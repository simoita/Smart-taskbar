using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Nebula.Services;

public sealed record FileSearchResult(string Name, string Path, string Folder, long Size, DateTime Modified,
    bool IsFolder = false)
{
    // Ranking touches every indexed file on each keystroke: lower-case the
    // strings once when the index is built instead of thousands of times.
    public string NameLower { get; } = Name.ToLowerInvariant();
    public string FolderLower { get; } = Folder.ToLowerInvariant();

    public string SizeText => Size switch
    {
        >= 1_073_741_824 => $"{Size / 1_073_741_824.0:0.#} GB",
        >= 1_048_576 => $"{Size / 1_048_576.0:0.#} MB",
        >= 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size} B"
    };
}

/// <summary>
/// Lightweight index over the user's shell folders. It is intentionally not a
/// full-disk index: it answers "where is that file?" in milliseconds without a
/// background service or elevated access.
/// </summary>
public sealed class FileSearchService
{
    private static readonly string[] SkippedFolders =
    {
        "AppData", "Application Data", "Local Settings", "node_modules", ".git", ".vs",
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "Temp", "tmp"
    };

    private readonly object _gate = new();
    private List<FileSearchResult> _index = new();
    private DateTime _builtUtc = DateTime.MinValue;
    private int _loading;

    public bool IsReady => _builtUtc != DateTime.MinValue;

    /// <summary>Raised after a scan completes; surfaces refresh open searches.</summary>
    public event Action? IndexReady;

    public int Count
    {
        get
        {
            lock (_gate)
                return _index.Count;
        }
    }

    public void StartLoadIfNeeded(bool force = false)
    {
        if (!force && IsReady && DateTime.UtcNow - _builtUtc < TimeSpan.FromMinutes(10))
            return;

        if (Interlocked.Exchange(ref _loading, 1) != 0)
            return;

        Task.Run(() =>
        {
            try
            {
                List<FileSearchResult> scanned = Scan();
                lock (_gate)
                {
                    _index = scanned;
                    _builtUtc = DateTime.UtcNow;
                }

                Log.Write($"File index ready: {scanned.Count} files.");
                IndexReady?.Invoke();
            }
            catch (Exception ex)
            {
                Log.Write("File index failed: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _loading, 0);
            }
        });
    }

    public IReadOnlyList<FileSearchResult> Search(string? query, int limit = 8) =>
        SearchCore(query, limit, CancellationToken.None);

    /// <summary>
    /// Same search, off the UI thread. The index can hold tens of thousands of
    /// files, and ranking them is what made typing stall when it ran inline.
    /// </summary>
    public Task<IReadOnlyList<FileSearchResult>> SearchAsync(string? query, int limit = 8,
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<FileSearchResult>>(
            () => SearchCore(query, limit, cancellationToken), cancellationToken);

    private IReadOnlyList<FileSearchResult> SearchCore(string? query, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<FileSearchResult>();

        // A query that starts with a shell variable searches that folder live,
        // even when it is outside the index (AppData is deliberately skipped).
        string trimmed = query.Trim();
        if (trimmed.StartsWith('%'))
        {
            IReadOnlyList<FileSearchResult> special = SearchShellPath(trimmed, limit);
            if (special.Count > 0)
                return special;
        }

        StartLoadIfNeeded();
        string q = query.Trim().ToLowerInvariant();
        List<FileSearchResult> snapshot;
        lock (_gate)
            snapshot = _index;

        // A one-letter query matches thousands of files. Only the best ranked
        // ones can ever be shown, so stop collecting once the pool is deep
        // enough instead of sorting the whole index on every keystroke.
        int cap = Math.Max(240, limit * 32);
        var pool = new List<(int Rank, DateTime Modified, FileSearchResult File)>(Math.Min(cap, 512));
        foreach (FileSearchResult file in snapshot)
        {
            if ((pool.Count & 0x3F) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int rank = Rank(file, q);
            if (rank < 0)
                continue;

            pool.Add((rank, file.Modified, file));
            if (pool.Count >= cap)
                break;
        }

        if (pool.Count == 0)
            return Array.Empty<FileSearchResult>();

        return pool
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Modified)
            .Take(limit)
            .Select(item => item.File)
            .ToList();
    }

    private static readonly Dictionary<string, string> ShellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["localdata"] = "%LOCALAPPDATA%",
        ["localappdata"] = "%LOCALAPPDATA%",
        ["appdata"] = "%APPDATA%",
        ["roaming"] = "%APPDATA%",
        ["userprofile"] = "%USERPROFILE%",
        ["profile"] = "%USERPROFILE%",
        ["user"] = "%USERPROFILE%",
        ["home"] = "%USERPROFILE%",
        ["desktop"] = "%USERPROFILE%\\Desktop",
        ["documents"] = "%USERPROFILE%\\Documents",
        ["downloads"] = "%USERPROFILE%\\Downloads",
        ["pictures"] = "%USERPROFILE%\\Pictures",
        ["music"] = "%USERPROFILE%\\Music",
        ["videos"] = "%USERPROFILE%\\Videos",
        ["temp"] = "%TEMP%",
        ["tmp"] = "%TEMP%",
        ["windir"] = "%WINDIR%",
        ["windows"] = "%WINDIR%",
        ["programfiles"] = "%PROGRAMFILES%",
        ["programdata"] = "%PROGRAMDATA%"
    };

    /// <summary>
    /// Resolves queries such as <c>%appdata%</c>, <c>%localappdata%\Nebula</c> or
    /// <c>%temp%</c> into a real folder and returns what is inside it. The folder
    /// itself comes first, so Enter opens it in Explorer.
    /// </summary>
    private static IReadOnlyList<FileSearchResult> SearchShellPath(string query, int limit)
    {
        int close = query.IndexOf('%', 1);
        if (close < 1)
            return Array.Empty<FileSearchResult>();

        string token = query[1..close].Trim();
        if (token.Length == 0)
            return Array.Empty<FileSearchResult>();

        string value = ShellAliases.TryGetValue(token, out string? alias)
            ? alias
            : "%" + token + "%";
        string expanded = Environment.ExpandEnvironmentVariables(value);
        if (string.Equals(expanded, value, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<FileSearchResult>();

        string rest = query[(close + 1)..].Trim().Trim('"');
        string combined = (expanded + rest).Replace('/', '\\').TrimEnd('\\');

        string root;
        string needle;
        try
        {
            if (Directory.Exists(combined))
            {
                root = combined;
                needle = "";
            }
            else
            {
                root = Path.GetDirectoryName(combined) ?? combined;
                needle = Path.GetFileName(combined);
            }
        }
        catch
        {
            return Array.Empty<FileSearchResult>();
        }

        if (!Directory.Exists(root))
            return Array.Empty<FileSearchResult>();

        var results = new List<FileSearchResult>(Math.Max(4, limit));
        if (needle.Length == 0)
        {
            var folder = new DirectoryInfo(root);
            results.Add(new FileSearchResult(
                string.IsNullOrWhiteSpace(folder.Name) ? root : folder.Name,
                folder.FullName,
                folder.Parent?.FullName ?? root,
                0,
                folder.LastWriteTime,
                true));
        }

        CollectShellEntries(root, needle, results, Math.Max(4, limit), 1);
        return results;
    }

    private static void CollectShellEntries(string directory, string needle, List<FileSearchResult> results,
        int limit, int depth)
    {
        if (results.Count >= limit)
            return;

        try
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                return;

            var entries = new List<FileSystemInfo>();
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (FileSystemInfo child in info.EnumerateFileSystemInfos("*", options))
            {
                if (needle.Length == 0 || child.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    entries.Add(child);
            }

            // Folders first, then names that start with what was typed.
            foreach (FileSystemInfo child in entries
                         .OrderByDescending(child => child is DirectoryInfo)
                         .ThenByDescending(child => child.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                         .ThenBy(child => child.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                results.Add(new FileSearchResult(
                    child.Name,
                    child.FullName,
                    directory,
                    child is FileInfo file ? file.Length : 0,
                    child.LastWriteTime,
                    child is DirectoryInfo));

                if (results.Count >= limit)
                    return;
            }

            if (depth <= 0 || needle.Length == 0)
                return;

            foreach (DirectoryInfo sub in info.EnumerateDirectories("*", options))
            {
                CollectShellEntries(sub.FullName, needle, results, limit, depth - 1);
                if (results.Count >= limit)
                    return;
            }
        }
        catch
        {
            // Unreadable folder: keep the other results.
        }
    }

    private static int Rank(FileSearchResult file, string query)
    {
        string name = file.NameLower;
        if (name.StartsWith(query, StringComparison.Ordinal))
            return 0;
        if (name.Contains(' ') && name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => word.StartsWith(query, StringComparison.Ordinal)))
            return 1;
        if (name.Contains(query, StringComparison.Ordinal))
            return 2;
        if (file.FolderLower.Contains(query, StringComparison.Ordinal))
            return 3;
        return -1;
    }

    private static List<FileSearchResult> Scan()
    {
        var results = new List<FileSearchResult>(20000);
        var queue = new Queue<(string Directory, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.MyVideos
                 })
        {
            string path = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                queue.Enqueue((path, 0));
        }

        // Downloads is a normal shell folder but not part of SpecialFolder.
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads))
            queue.Enqueue((downloads, 0));

        const int maxFiles = 24000;
        const int maxDepth = 5;

        while (queue.Count > 0 && results.Count < maxFiles)
        {
            (string directory, int depth) = queue.Dequeue();
            if (!seen.Add(directory))
                continue;

            try
            {
                var info = new DirectoryInfo(directory);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                    continue;

                foreach (FileInfo file in info.EnumerateFiles())
                {
                    if (results.Count >= maxFiles)
                        break;

                    try
                    {
                        if ((file.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                            continue;
                        if (file.Name.StartsWith("~$", StringComparison.Ordinal) ||
                            file.Length > 8L * 1024 * 1024 * 1024)
                            continue;

                        results.Add(new FileSearchResult(
                            file.Name, file.FullName, file.DirectoryName ?? directory, file.Length, file.LastWriteTime));
                    }
                    catch
                    {
                        // A file can disappear while the index is built.
                    }
                }

                if (depth >= maxDepth)
                    continue;

                foreach (DirectoryInfo child in info.EnumerateDirectories())
                {
                    if (SkippedFolders.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0)
                        continue;

                    queue.Enqueue((child.FullName, depth + 1));
                }
            }
            catch
            {
                // Locked or removed folder: keep scanning the rest.
            }
        }

        return results;
    }
}
