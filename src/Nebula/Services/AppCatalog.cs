using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using Nebula.Interop;

namespace Nebula.Services;

public enum AppEntryKind
{
    Application,
    BuiltIn,
    Setting,
    Action,
    Window,
    File
}

public sealed class AppEntry : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string? _searchKey;
    private string? _nameLower;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Keywords { get; init; } = "";
    public string? Glyph { get; init; }
    public string? ActionId { get; init; }
    public IntPtr WindowHandle { get; init; }
    public AppEntryKind Kind { get; init; }
    public string? FolderPath { get; init; }
    public bool IsWindow => WindowHandle != IntPtr.Zero;
    public bool IsFile => Kind == AppEntryKind.File;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Icons arrive from a worker thread; the row refreshes when they do.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;

            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>Cached lowercase name: ranking runs on every keystroke.</summary>
    public string NameLower => _nameLower ??= Name.ToLowerInvariant();

    /// <summary>Cached words of the name, so a query never splits it again.</summary>
    public string[] NameWords => _nameWords ??=
        NameLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private string[]? _nameWords;

    public string SearchKey => _searchKey ??=
        (Name + " " + Keywords).Trim().ToLowerInvariant();

    public void EnsureIcon()
    {
        if (Icon is not null || string.IsNullOrWhiteSpace(Path))
            return;

        if (!File.Exists(Path))
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

        // Plain loop over the cached lowercase names: this runs on every
        // keystroke and must not allocate an object per catalog entry.
        var matches = new List<(int Rank, AppEntry App)>(Math.Min(apps.Count, limit * 4));
        foreach (AppEntry app in apps)
        {
            int rank = Rank(app, q);
            if (rank >= 0)
                matches.Add((rank, app));
        }

        if (matches.Count == 0)
            return Array.Empty<AppEntry>();

        return matches
            .OrderBy(match => match.Rank)
            .ThenBy(match => match.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(limit)
            .Select(match => match.App)
            .ToList();
    }

    public AppEntry? FindByName(string name)
    {
        List<AppEntry> apps;
        lock (_gate)
            apps = _apps;

        return apps.FirstOrDefault(app =>
            string.Equals(app.Name, name, StringComparison.CurrentCultureIgnoreCase));
    }

    public AppEntry? FindByPath(string path)
    {
        List<AppEntry> apps;
        lock (_gate)
            apps = _apps;

        return apps.FirstOrDefault(app =>
            string.Equals(app.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    private static int Rank(AppEntry app, string query)
    {
        string name = app.NameLower;
        if (name.StartsWith(query, StringComparison.Ordinal))
            return 0;
        if (app.NameWords.Any(word => word.StartsWith(query, StringComparison.Ordinal)))
            return 1;
        if (name.Contains(query, StringComparison.Ordinal))
            return 2;
        if (app.SearchKey.Contains(query, StringComparison.Ordinal))
            return 3;
        return -1;
    }

    private static List<AppEntry> Scan()
    {
        var found = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, string path, string subtitle = "", string keywords = "",
            string? glyph = null, string? action = null, AppEntryKind kind = AppEntryKind.Application)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name + "|" + path))
                return;

            // A Start-menu shortcut and the packaged app can describe the same
            // program; the first (more specific) entry wins.
            if (kind != AppEntryKind.Action && !seenNames.Add(name))
                return;

            found.Add(new AppEntry
            {
                Name = name,
                Path = path,
                Subtitle = subtitle,
                Keywords = keywords,
                Glyph = glyph,
                ActionId = action,
                Kind = kind
            });
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Never rely on SpecialFolder.System here: in a 32-bit process it points
        // to SysWOW64, where tools such as Taskmgr.exe do not exist, so the
        // system app would silently disappear from the catalog and dock.
        string system = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(windows, "Sysnative")
            : Path.Combine(windows, "System32");
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void AddTool(string name, string path, string keywords, string? glyph = null)
        {
            if (File.Exists(path))
                Add(name, path, "Programma di sistema", keywords, glyph, kind: AppEntryKind.BuiltIn);
        }

        AddTool("Esplora file", Path.Combine(windows, "explorer.exe"),
            "cartelle file explorer", "\uE8B7");
        Add("Impostazioni di Windows", "ms-settings:", "Impostazioni del sistema",
            "settings impostazioni pannello configurazione", "\uE713", kind: AppEntryKind.Setting);
        AddTool("Gestione attività", Path.Combine(system, "Taskmgr.exe"),
            "task manager processi cpu memoria avvio", "\uE9D9");
        AddTool("Pannello di controllo", Path.Combine(system, "control.exe"),
            "control panel sistema", "\uE770");
        AddTool("Prompt dei comandi", Path.Combine(system, "cmd.exe"),
            "cmd terminale console", "\uE756");
        AddTool("PowerShell", Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
            "terminale comandi script", "\uE756");
        AddTool("Blocco note", Path.Combine(system, "notepad.exe"),
            "note testo editor", "\uE70F");
        AddTool("Calcolatrice", Path.Combine(system, "calc.exe"),
            "calcolo matematica", "\uE8EF");
        AddTool("Paint", Path.Combine(system, "mspaint.exe"),
            "disegno immagini", "\uE790");
        AddTool("Strumento di cattura", Path.Combine(system, "SnippingTool.exe"),
            "screenshot cattura schermo", "\uE722");
        AddTool("Lente d'ingrandimento", Path.Combine(system, "magnify.exe"),
            "zoom accessibilità", "\uE71E");
        AddTool("Tastiera su schermo", Path.Combine(system, "osk.exe"),
            "accessibilità tastiera", "\uE765");
        AddTool("Mappa caratteri", Path.Combine(system, "charmap.exe"),
            "simboli caratteri", "\uE8D2");
        AddTool("Editor del Registro", Path.Combine(system, "regedit.exe"),
            "registro sistema regedit", "\uE74C");
        AddTool("Visualizzatore eventi", Path.Combine(system, "eventvwr.msc"),
            "log eventi diagnostica", "\uE9D5");
        AddTool("Servizi", Path.Combine(system, "services.msc"),
            "servizi sistema", "\uE9F5");
        AddTool("Gestione dispositivi", Path.Combine(system, "devmgmt.msc"),
            "driver hardware dispositivi", "\uE977");
        AddTool("Gestione disco", Path.Combine(system, "diskmgmt.msc"),
            "partizioni dischi volumi", "\uEDA2");
        AddTool("Utilità di pianificazione", Path.Combine(system, "taskschd.msc"),
            "attività pianificate scheduler", "\uE823");
        AddTool("Informazioni di sistema", Path.Combine(system, "msinfo32.exe"),
            "specifiche pc sistema", "\uE946");
        AddTool("Monitoraggio risorse", Path.Combine(system, "resmon.exe"),
            "prestazioni cpu rete disco", "\uE9D9");
        AddTool("DXDiag", Path.Combine(system, "dxdiag.exe"),
            "directx diagnostica video", "\uE7F4");

        Add("Scarica", Path.Combine(user, "Downloads"), "Cartella Download",
            "download scaricati cartella file", "\uE896", kind: AppEntryKind.BuiltIn);
        Add("Desktop", Path.Combine(user, "Desktop"), "Cartella Desktop",
            "scrivania cartella file", "\uE8B7", kind: AppEntryKind.BuiltIn);
        Add("Documenti", Path.Combine(user, "Documents"), "Cartella Documenti",
            "documenti cartella file", "\uE8A5", kind: AppEntryKind.BuiltIn);

        // Windows settings pages that users actually search by name.
        var settings = new (string Name, string Uri, string Keywords)[]
        {
            ("Wi-Fi e rete", "ms-settings:network", "wifi rete internet ethernet connessione"),
            ("Bluetooth e dispositivi", "ms-settings:bluetooth", "bluetooth dispositivi"),
            ("Schermo", "ms-settings:display", "monitor risoluzione luminosità display"),
            ("Suono", "ms-settings:sound", "audio volume altoparlanti"),
            ("Aggiornamenti di Windows", "ms-settings:windowsupdate", "update aggiornamenti windows"),
            ("App installate", "ms-settings:appsfeatures", "programmi applicazioni disinstalla"),
            ("Data e ora", "ms-settings:dateandtime", "orologio fuso orario"),
            ("Alimentazione e batteria", "ms-settings:batterysaver", "risparmio energetico batteria"),
            ("Privacy", "ms-settings:privacy", "permessi privacy"),
            ("Account", "ms-settings:yourinfo", "utente account profilo"),
            ("Mouse", "ms-settings:mousetouchpad", "mouse touchpad puntatore"),
            ("Tastiera", "ms-settings:keyboard", "tastiera input"),
            ("Notifiche", "ms-settings:notifications", "notifiche avvisi"),
            ("Archiviazione", "ms-settings:storagesense", "disco spazio archiviazione"),
            ("Multitasking", "ms-settings:multitasking", "finestre desktop virtuali"),
            ("Barra delle applicazioni", "ms-settings:taskbar", "taskbar barra applicazioni"),
            ("Reti e Internet", "ms-settings:network-status", "rete stato connessione")
        };

        foreach ((string name, string uri, string keywords) in settings)
            Add(name, uri, "Impostazioni di Windows", keywords, "\uE713", kind: AppEntryKind.Setting);

        Add("Impostazioni Nebula", "", "Studio di Nebula", "studio impostazioni nebula",
            "\uE713", "settings", AppEntryKind.Action);
        Add("Start di Nebula", "", "Pannello applicazioni", "start menu pannello app avvio",
            "\uE700", "start", AppEntryKind.Action);
        Add("Pannello widget", "", "Widget e calendario", "widget calendario pannello",
            "\uE71D", "widgets", AppEntryKind.Action);
        Add("Controlli rapidi", "", "Volume, media e rete", "controlli volume audio rete",
            "\uE9E9", "controls", AppEntryKind.Action);
        Add("Crea gruppo di schede", "", "Raggruppa finestre", "schede tab gruppo finestre",
            "\uE7C4", "tabs", AppEntryKind.Action);
        Add("Blocca PC", "", "Sicurezza", "blocca sessione sicurezza",
            "\uE72E", "lock", AppEntryKind.Action);
        Add("Sospendi PC", "", "Risparmio energetico", "sospendi stop sleep",
            "\uE708", "sleep", AppEntryKind.Action);
        Add("Riavvia PC", "", "Spegnimento", "riavvia restart",
            "\uE777", "restart", AppEntryKind.Action);
        Add("Spegni PC", "", "Spegnimento", "spegni shutdown",
            "\uE7E8", "shutdown", AppEntryKind.Action);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        };

        // Localized Windows installs keep a "Programmi" junction inside the
        // Start Menu that denies directory reads. Recursing blindly threw there
        // and dropped every shortcut of that root from the catalog, leaving the
        // panel and the search without the installed apps. Ignore inaccessible
        // folders and never follow junctions/symlinks.
        var startMenuOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (string root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", startMenuOptions))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (ShouldSkip(name))
                        continue;

                    Add(name, file, "App installata", "", null, null, AppEntryKind.Application);
                    if (found.Count > 800)
                        break;
                }
            }
            catch
            {
                // A locked Start Menu folder must not block the catalog.
            }
        }

        // Packaged apps (Store/UWP) do not have a .lnk file. The shell namespace
        // is the only public list that contains them.
        foreach (ShellApp app in ShellApps.Enumerate())
        {
            if (ShouldSkip(app.Name))
                continue;

            Add(app.Name, app.LaunchTarget, "App installata", "", "\uE71D", null,
                AppEntryKind.Application);
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
