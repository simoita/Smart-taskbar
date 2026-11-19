using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Nebula.Interop;
using Nebula.Models;

namespace Nebula.Services;

public sealed class ShelfItem : ObservableObject
{
    private string _name = "";

    public string Id { get; init; } = "";
    public string StoredPath { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public bool IsFolder { get; init; }
    public int FileCount { get; init; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public long Size { get; init; }
    public DateTime AddedUtc { get; init; } = DateTime.UtcNow;
    public ImageSource? Icon { get; init; }

    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";

    public string SizeText => IsFolder
        ? FileCount == 0
            ? "Cartella vuota"
            : Size > 0
                ? $"Cartella · {FileCountText} · {FormatSize(Size)}"
                : $"Cartella · {FileCountText}"
        : FormatSize(Size);

    private string FileCountText => FileCount == 1 ? "1 file" : $"{FileCount} file";

    public string AddedText => AddedUtc.ToLocalTime().ToString("HH:mm");

    internal static string FormatSize(long size) => size switch
    {
        >= 1_073_741_824 => $"{size / 1_073_741_824.0:0.#} GB",
        >= 1_048_576 => $"{size / 1_048_576.0:0.#} MB",
        >= 1024 => $"{size / 1024.0:0.#} KB",
        _ => $"{size} B"
    };
}

/// <summary>
/// A temporary drawer for files and folders: dropping them on the island stores
/// a copy in %APPDATA%\Nebula\shelf and keeps it there until it is dragged out,
/// moved to another folder or removed.
/// </summary>
public sealed class FileShelf : ObservableObject, IDisposable
{
    private readonly ActivityCenter _activities;
    private readonly object _gate = new();

    public FileShelf(ActivityCenter activities)
    {
        _activities = activities;
        Directory.CreateDirectory(ShelfDirectory);
        Load();
    }

    public static string ShelfDirectory => Path.Combine(SettingsStore.DataDirectory, "shelf");

    private static string IndexPath => Path.Combine(SettingsStore.DataDirectory, "shelf.json");

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public event Action? Changed;

    public async Task<(int Added, int Skipped, string? Error)> AddFilesAsync(IEnumerable<string> paths)
    {
        List<string> sources = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        int skipped = sources.Count(path => !Exists(path) || IsInsideShelf(path));
        sources = sources.Where(path => Exists(path) && !IsInsideShelf(path)).ToList();
        if (sources.Count == 0)
            return (0, skipped, "Nessun file o cartella valido.");

        SystemActivity? activity = _activities.BeginOperation(
            ActivityKind.Copy,
            sources.Count == 1 ? "Aggiunta al cassetto" : $"{sources.Count} elementi al cassetto",
            SourceName(sources[0]));

        int added = 0;
        string? error = null;

        try
        {
            for (int index = 0; index < sources.Count; index++)
            {
                string source = sources[index];
                try
                {
                    bool isFolder = Directory.Exists(source);
                    string destination = UniqueDestination(SourceName(source), isFolder);
                    if (isFolder)
                        await Task.Run(() => CopyDirectory(source, destination));
                    else
                        await Task.Run(() => CopyWithoutProgress(source, destination));

                    ShelfItem item = CreateItem(destination, source);
                    Items.Insert(0, item);
                    added++;
                    _activities.UpdateOperation(activity,
                        (index + 1) * 100.0 / sources.Count,
                        $"{index + 1}/{sources.Count} · {item.Name}");
                }
                catch (Exception ex)
                {
                    error = "Un elemento non è stato copiato: " + ex.Message;
                    Log.Write("Shelf copy failed: " + ex);
                }
            }
        }
        finally
        {
            Save();
            Changed?.Invoke();
            if (added > 0)
                _activities.CompleteOperation(activity, added == 1 ? "Elemento conservato" : $"{added} elementi conservati");
            else
                _activities.CompleteOperation(activity, "Nessun elemento copiato");
        }

        return (added, skipped, error);
    }

    public bool Remove(ShelfItem item)
    {
        try
        {
            DeleteStored(item.StoredPath);
        }
        catch (Exception ex)
        {
            Log.Write("Shelf delete failed: " + ex.Message);
            return false;
        }

        Items.Remove(item);
        Save();
        Changed?.Invoke();
        return true;
    }

    public bool MoveTo(ShelfItem item, string destinationFolder)
    {
        if (!Directory.Exists(destinationFolder))
            return false;

        try
        {
            bool isFolder = Directory.Exists(item.StoredPath);
            string target = UniqueInFolder(destinationFolder, item.Name, isFolder);
            if (isFolder)
                Directory.Move(item.StoredPath, target);
            else
                File.Move(item.StoredPath, target);

            Items.Remove(item);
            Save();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Shelf move failed: " + ex.Message);
            return false;
        }
    }

    public bool Open(ShelfItem item) => AppLauncher.Launch(item.StoredPath, out _);

    public bool Reveal(ShelfItem item) => AppLauncher.RevealInExplorer(item.StoredPath, out _);

    public void Clear()
    {
        foreach (ShelfItem item in Items.ToList())
        {
            try
            {
                DeleteStored(item.StoredPath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        Items.Clear();
        Save();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Save();
    }

    private ShelfItem CreateItem(string storedPath, string originalPath)
    {
        bool isFolder = Directory.Exists(storedPath);
        if (isFolder)
        {
            (long size, int files) = MeasureDirectory(storedPath);
            return new ShelfItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = SourceName(storedPath),
                StoredPath = storedPath,
                OriginalPath = originalPath,
                IsFolder = true,
                Size = size,
                FileCount = files,
                AddedUtc = DateTime.UtcNow
            };
        }

        var info = new FileInfo(storedPath);
        return new ShelfItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = SourceName(storedPath),
            StoredPath = storedPath,
            OriginalPath = originalPath,
            Size = info.Length,
            AddedUtc = DateTime.UtcNow,
            Icon = IconExtractor.FromPath(storedPath)
        };
    }

    private static string SourceName(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool DestinationExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsInsideShelf(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(ShelfDirectory);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyWithoutProgress(string source, string destination)
    {
        try
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, read);

            output.Flush(true);
        }
        catch
        {
            try
            {
                if (File.Exists(destination))
                    File.Delete(destination);
            }
            catch
            {
                // Ignore cleanup failure.
            }

            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
            CopyWithoutProgress(file, Path.Combine(destination, Path.GetFileName(file)));

        foreach (string directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);
            // Junctions and symlinks are not followed: they can point back into
            // the tree being copied and would never finish.
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                continue;

            CopyDirectory(directory, Path.Combine(destination, info.Name));
        }
    }

    private static (long Size, int Files) MeasureDirectory(string path)
    {
        long size = 0;
        int files = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    size += new FileInfo(file).Length;
                    files++;
                }
                catch (IOException)
                {
                    // A file disappeared meanwhile: the rest is still counted.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shelf folder measure failed: " + ex.Message);
        }

        return (size, files);
    }

    private static void DeleteStored(string path)
    {
        if (Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);

                Directory.Delete(path, true);
            }

            return;
        }

        if (!File.Exists(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static string UniqueInFolder(string folder, string name, bool isFolder)
    {
        string candidate = Path.Combine(folder, name);
        if (!DestinationExists(candidate))
            return candidate;

        int index = 2;
        if (isFolder)
        {
            do
            {
                candidate = Path.Combine(folder, $"{name} ({index++})");
            } while (DestinationExists(candidate));
        }
        else
        {
            string baseName = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            do
            {
                candidate = Path.Combine(folder, $"{baseName} ({index++}){extension}");
            } while (DestinationExists(candidate));
        }

        return candidate;
    }

    private string UniqueDestination(string name, bool isFolder)
    {
        string safe = string.IsNullOrWhiteSpace(name) ? (isFolder ? "Cartella" : "file") : name;
        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        string candidate = Path.Combine(ShelfDirectory, safe);
        if (!DestinationExists(candidate))
            return candidate;

        int index = 2;
        if (isFolder)
        {
            do
            {
                candidate = Path.Combine(ShelfDirectory, $"{safe} ({index++})");
            } while (DestinationExists(candidate));
        }
        else
        {
            string baseName = Path.GetFileNameWithoutExtension(safe);
            string extension = Path.GetExtension(safe);
            do
            {
                candidate = Path.Combine(ShelfDirectory, $"{baseName} ({index++}){extension}");
            } while (DestinationExists(candidate));
        }

        return candidate;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return;

            var data = JsonSerializer.Deserialize<ShelfData>(File.ReadAllText(IndexPath));
            if (data?.Items is null)
                return;

            foreach (ShelfItemData item in data.Items)
            {
                string stored = item.StoredPath;
                if (string.IsNullOrWhiteSpace(stored))
                    continue;

                bool isFolder = Directory.Exists(stored);
                if (!isFolder && !File.Exists(stored))
                    continue;

                Items.Add(new ShelfItem
                {
                    Id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id,
                    Name = SourceName(stored),
                    StoredPath = stored,
                    OriginalPath = item.OriginalPath ?? "",
                    IsFolder = isFolder,
                    Size = isFolder ? item.Size : new FileInfo(stored).Length,
                    FileCount = isFolder ? item.FileCount : 0,
                    AddedUtc = item.AddedUtc == default ? DateTime.UtcNow : item.AddedUtc,
                    Icon = isFolder ? null : IconExtractor.FromPath(stored)
                });
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shelf load failed: " + ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            lock (_gate)
            {
                var data = new ShelfData
                {
                    Items = Items.Select(item => new ShelfItemData
                    {
                        Id = item.Id,
                        StoredPath = item.StoredPath,
                        OriginalPath = item.OriginalPath,
                        IsFolder = item.IsFolder,
                        Size = item.Size,
                        FileCount = item.FileCount,
                        AddedUtc = item.AddedUtc
                    }).ToList()
                };

                Directory.CreateDirectory(SettingsStore.DataDirectory);
                File.WriteAllText(IndexPath,
                    JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Shelf save failed: " + ex.Message);
        }
    }

    private sealed class ShelfData
    {
        public List<ShelfItemData> Items { get; set; } = new();
    }

    private sealed class ShelfItemData
    {
        public string Id { get; set; } = "";
        public string StoredPath { get; set; } = "";
        public string? OriginalPath { get; set; }
        public bool IsFolder { get; set; }
        public long Size { get; set; }
        public int FileCount { get; set; }
        public DateTime AddedUtc { get; set; }
    }
}
