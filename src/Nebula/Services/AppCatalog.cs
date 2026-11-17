using System.IO;
using System.Windows.Media;
using Nebula.Interop;

namespace Nebula.Services;

public sealed class AppEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string? Glyph { get; init; }
    public string? ActionId { get; init; }
    public IntPtr WindowHandle { get; init; }
    public bool IsWindow => WindowHandle != IntPtr.Zero;
    public ImageSource? Icon { get; set; }

    private string? _searchKey;

    public string SearchKey => _searchKey ??= Name.ToLowerInvariant();

    public void EnsureIcon()
    {
        if (Icon is not null || string.IsNullOrWhiteSpace(Path))
            return;

        Icon = IconExtractor.FromPath(Path);
    }
}

public sealed class AppCatalog
{
    private readonly object _gate = new();
    private List<AppEntry> _apps = new();
    private bool _loading;

    public IReadOnlyList<AppEntry> Apps => _apps;

    public bool IsLoaded { get; private set; }

    public event Action? Loaded;

    public void LoadAsync()
    {
        if (_loading)
            return;

        _loading = true;
        Task.Run(() =>
        {
            List<AppEntry> scanned = Scan();
            lock (_gate)
                _apps = scanned;
            IsLoaded = true;
            _loading = false;
            Loaded?.Invoke();
        });
    }

    public IReadOnlyList<AppEntry> Search(string? query, int limit = 8)
    {
        List<AppEntry> apps;
        lock (_gate)
            apps = _apps;

        if (string.IsNullOrWhiteSpace(query))
            return apps.Take(limit).ToList();

        string q = query.Trim().ToLowerInvariant();
        return apps
            .Select(a => new { App = a, Rank = Rank(a, q) })
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.App.Name)
            .Take(limit)
            .Select(x => x.App)
            .ToList();
    }

    private static int Rank(AppEntry app, string query)
    {
        string name = app.SearchKey;
        if (name.StartsWith(query, StringComparison.Ordinal))
            return 0;
        if (name.Split(' ').Any(w => w.StartsWith(query, StringComparison.Ordinal)))
            return 1;
        if (name.Contains(query, StringComparison.Ordinal))
            return 2;
        return -1;
    }

    private static List<AppEntry> Scan()
    {
        var found = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string path, string? glyph = null, string? action = null)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name + "|" + path))
                return;
            found.Add(new AppEntry { Name = name, Path = path, Glyph = glyph, ActionId = action });
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Add("Esplora file", Path.Combine(windows, "explorer.exe"));
        Add("Impostazioni di Windows", Path.Combine(windows, "SystemSettings.exe"));
        Add("Task Manager", Path.Combine(windows, "System32", "Taskmgr.exe"));
        Add("Pannello di controllo", Path.Combine(windows, "System32", "control.exe"));
        Add("Prompt dei comandi", Path.Combine(windows, "System32", "cmd.exe"));
        Add("Impostazioni Nebula", "", "\uE713", "settings");
        Add("Pannello widget", "", "\uE71D", "widgets");
        Add("Controlli rapidi", "", "\uE9E9", "controls");
        Add("Blocca PC", "", "\uE72E", "lock");

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        foreach (string root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (ShouldSkip(name))
                        continue;

                    Add(name, file);
                    if (found.Count > 600)
                        break;
                }
            }
            catch
            {
                // A locked Start Menu folder must not block the catalog.
            }
        }

        return found
            .OrderBy(a => a.ActionId is null ? 1 : 0)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool ShouldSkip(string name)
    {
        string[] blocked =
        {
            "uninstall", "disinstalla", "readme", "read me", "release notes", "help", "guida",
            "website", "sito web", "license", "licenza", "documentation", "modifica"
        };
        string lower = name.ToLowerInvariant();
        return blocked.Any(lower.Contains);
    }
}
