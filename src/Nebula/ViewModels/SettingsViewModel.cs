using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Threading.Tasks;
using System.Windows.Media;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;

namespace Nebula.ViewModels;

public sealed class StyleCardViewModel : ObservableObject
{
    private bool _isSelected;

    public StyleCardViewModel(ThemePreset preset)
    {
        Preset = preset;

        var preview = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        preview.GradientStops.Add(new GradientStop(ColorUtil.Parse(preset.GlassTop), 0));
        preview.GradientStops.Add(new GradientStop(ColorUtil.Parse(preset.GlassBottom), 1));
        preview.Freeze();
        Preview = preview;

        var accent = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };
        accent.GradientStops.Add(new GradientStop(ColorUtil.Parse(preset.AccentA), 0));
        accent.GradientStops.Add(new GradientStop(ColorUtil.Parse(preset.AccentB), 1));
        accent.Freeze();
        Accent = accent;
    }

    public ThemePreset Preset { get; }
    public string Name => Preset.Name;
    public string Tagline => Preset.Tagline;
    public Brush Preview { get; }
    public Brush Accent { get; }
    public bool IsLight => Preset.IsLight;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class AccentSwatchViewModel : ObservableObject
{
    private bool _isSelected;

    public AccentSwatchViewModel(string hex)
    {
        Hex = hex;
        Brush = new SolidColorBrush(string.IsNullOrEmpty(hex)
            ? Color.FromRgb(139, 92, 246)
            : ColorUtil.Parse(hex));
        Brush.Freeze();
    }

    public string Hex { get; }
    public Brush Brush { get; }
    public bool IsAuto => string.IsNullOrEmpty(Hex);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class PinnedAppViewModel : ObservableObject
{
    private readonly AppSettings _settings;

    public PinnedAppViewModel(AppSettings settings, string path)
    {
        _settings = settings;
        Path = path;
        Name = System.IO.Path.GetFileNameWithoutExtension(path);
        Target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ShortcutResolver.Resolve(path)
            : path;
        Icon = IconExtractor.FromPath(Target ?? path);
    }

    public string Path { get; }
    public string Name { get; }
    public string? Target { get; }
    public ImageSource? Icon { get; }

    public string SelectedStyle
    {
        get => _settings.CustomIconStyles.TryGetValue(Path, out string? style) ? style : "auto";
        set
        {
            if (string.Equals(SelectedStyle, value, StringComparison.Ordinal))
                return;

            var updated = new Dictionary<string, string>(_settings.CustomIconStyles, StringComparer.OrdinalIgnoreCase);
            if (value == "auto")
                updated.Remove(Path);
            else
                updated[Path] = value;
            _settings.CustomIconStyles = updated;
            OnPropertyChanged();
        }
    }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ThemeEngine _theme;
    private readonly TabGroupService _tabs;
    private readonly UpdateCoordinator _updates;
    private string _accentInput = "";
    private string _pinStatus = "";
    private string _updateStatus = "";
    private string _updateNotes = "";
    private bool _updateAvailable;

    public SettingsViewModel(AppSettings settings, DockViewModel preview, ThemeEngine theme,
        SystemMonitor monitor, TabGroupService tabs, SystemIconService systemIcons,
        UpdateCoordinator updates)
    {
        Settings = settings;
        Preview = preview;
        _theme = theme;
        Monitor = monitor;
        _tabs = tabs;
        _updates = updates;
        _updateStatus = updates.State.Message;
        _updateNotes = updates.State.Notes;
        _updateAvailable = updates.State.HasUpdate;
        updates.StateChanged += OnUpdateStateChanged;
        _tabs.GroupsChanged += OnTabsChanged;
        SystemIcons = new SystemIconsViewModel(settings, systemIcons);

        foreach (ThemePreset preset in ThemePreset.All)
            StyleCards.Add(new StyleCardViewModel(preset));

        Accents.Add(new AccentSwatchViewModel(""));
        foreach (string hex in new[]
                 {
                     "#8B5CF6", "#22D3EE", "#34D399", "#FBBF24", "#FB7185",
                     "#60A5FA", "#F472B6", "#A3E635", "#F97316", "#E5E7EB"
                 })
            Accents.Add(new AccentSwatchViewModel(hex));

        foreach (WidgetDefinition widget in WidgetCatalog.All)
            WidgetOptions.Add(new WidgetOptionViewModel(settings, widget));
        SyncWidgetOptions();
        SyncTaskbarWidgets();

        AccentInput = settings.AccentHex;

        RefreshScreens();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        RebuildPins();
        RebuildShortcuts();
        SyncSelection();
        _theme.Changed += SyncSelection;
        Settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnTabsChanged()
    {
        OnPropertyChanged(nameof(TabsSummary));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AccentHex))
        {
            AccentInput = Settings.AccentHex;
            SyncSelection();
        }
        else if (e.PropertyName == nameof(AppSettings.ThemeId))
        {
            OnPropertyChanged(nameof(ActiveThemeName));
            OnPropertyChanged(nameof(ActiveThemeTagline));
        }
        else if (e.PropertyName is nameof(AppSettings.SurfaceStyle) or nameof(AppSettings.TabsStyle))
        {
            OnPropertyChanged(nameof(ActiveSurfaceStyleName));
        }
        else if (e.PropertyName == nameof(AppSettings.PanelWidgets))
        {
            SyncWidgetOptions();
        }
        else         if (e.PropertyName == nameof(AppSettings.TaskbarWidgets) ||
            e.PropertyName == nameof(AppSettings.TaskbarWidgetModes))
        {
            SyncTaskbarWidgets();
        }
        else if (e.PropertyName == nameof(AppSettings.IconSize))
        {
            OnPropertyChanged(nameof(IconSizePreset));
        }
    }

    public AppSettings Settings { get; }
    public SystemIconsViewModel SystemIcons { get; }
    public DockViewModel Preview { get; }
    public SystemMonitor Monitor { get; }
    public ObservableCollection<StyleCardViewModel> StyleCards { get; } = new();
    public ObservableCollection<AccentSwatchViewModel> Accents { get; } = new();
    public ObservableCollection<PinnedAppViewModel> Pins { get; } = new();
    public ObservableCollection<ScreenOptionViewModel> Screens { get; } = new();
    public ObservableCollection<WidgetOptionViewModel> WidgetOptions { get; } = new();
    public ObservableCollection<TaskbarWidgetOptionViewModel> ActiveTaskbarWidgets { get; } = new();
    public ObservableCollection<TaskbarWidgetOptionViewModel> AvailableTaskbarWidgets { get; } = new();
    public ObservableCollection<ShortcutInfoViewModel> DesktopShortcuts { get; } = new();
    public IReadOnlyList<TabStyleOptionViewModel> TabStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("glass", "Vetro"),
        new("minimal", "Minimal"),
        new("neon", "Neon"),
        new("soft", "Soft"),
        new("mono", "Mono"),
        new("liquid", "Liquid")
    };
    public IReadOnlyList<TabStyleOptionViewModel> SurfaceStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("glass", "Vetro"),
        new("minimal", "Minimal"),
        new("neon", "Neon"),
        new("soft", "Soft"),
        new("mono", "Mono"),
        new("liquid", "Liquid")
    };
    public IReadOnlyList<TabPositionOptionViewModel> TabPositionOptions { get; } = new List<TabPositionOptionViewModel>
    {
        new("top", "Sopra la finestra"),
        new("bottom", "Sotto la finestra")
    };
    public IReadOnlyList<TabStyleOptionViewModel> IslandStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("capsule", "Capsula"),
        new("pill", "Pillola"),
        new("minimal", "Minimal")
    };

    /// <summary>Bar finishes: glass, or a simple solid monochrome bar.</summary>
    public IReadOnlyList<TabStyleOptionViewModel> DockFinishOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("glass", "Vetro (bagliori e riflessi)"),
        new("flat", "Monocolore piatto"),
        new("outline", "Monocolore con bordo")
    };
    public IReadOnlyList<TabStyleOptionViewModel> IslandPlacementOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("floating", "Flottante in alto"),
        new("taskbar", "Dentro la taskbar")
    };
    public IReadOnlyList<IconSizeOptionViewModel> IconSizeOptions { get; } = new List<IconSizeOptionViewModel>
    {
        new("small", "Piccole (36 px)"),
        new("medium", "Medie (42 px)"),
        new("large", "Grandi (48 px)")
    };

    /// <summary>Three explicit icon sizes, independent of the bar height.</summary>
    public string IconSizePreset
    {
        get => Settings.IconSize >= 46 ? "large" : Settings.IconSize >= 39 ? "medium" : "small";
        set
        {
            Settings.IconSize = value switch
            {
                "large" => 48,
                "medium" => 42,
                _ => 36
            };
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ZoneOrderOptionViewModel> ZoneOrderOptions { get; } = new List<ZoneOrderOptionViewModel>
    {
        new("apps,system,actions", "App · Sistema · Azioni"),
        new("apps,actions,system", "App · Azioni · Sistema"),
        new("system,apps,actions", "Sistema · App · Azioni"),
        new("system,actions,apps", "Sistema · Azioni · App"),
        new("actions,apps,system", "Azioni · App · Sistema"),
        new("actions,system,apps", "Azioni · Sistema · App")
    };
    public IReadOnlyList<TabStyleOptionViewModel> IndicatorStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("dot", "Punto"),
        new("pill", "Pillola"),
        new("bar", "Barra lunga"),
        new("frame", "Tacca") 
    };
    public IReadOnlyList<TabStyleOptionViewModel> IconStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("native", "Originale"),
        new("tile", "Riquadro"),
        new("circle", "Cerchio"),
        new("mono", "Monocromatico"),
        new("neon", "Neon")
    };
    public IReadOnlyList<TabStyleOptionViewModel> IconShapeOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("rounded", "Arrotondata"),
        new("squircle", "Squircle"),
        new("circle", "Cerchio"),
        new("square", "Quadrata")
    };
    public IReadOnlyList<TabStyleOptionViewModel> PerIconStyleOptions { get; } = new List<TabStyleOptionViewModel>
    {
        new("auto", "Segui lo stile globale"),
        new("native", "Originale"),
        new("tile", "Riquadro"),
        new("circle", "Cerchio"),
        new("mono", "Monocromatico"),
        new("neon", "Neon")
    };

    public string Version => _updates.CurrentVersion;

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public string UpdateNotes
    {
        get => _updateNotes;
        private set => SetProperty(ref _updateNotes, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    private void OnUpdateStateChanged(UpdateState state)
    {
        UpdateStatus = state.Message;
        UpdateNotes = state.Notes;
        UpdateAvailable = state.HasUpdate;
    }

    public async Task CheckUpdatesAsync()
    {
        await _updates.CheckNowAsync(false);
    }

    public async Task InstallUpdateAsync()
    {
        if (!_updates.State.HasUpdate)
        {
            UpdateStatus = "Verifica prima la disponibilità di un aggiornamento.";
            return;
        }

        void OnProgress(UpdateProgress progress) =>
            UpdateStatus = "Download del pacchetto: " + progress.Describe();

        _updates.DownloadProgress += OnProgress;
        try
        {
            await _updates.InstallPendingAsync();
        }
        finally
        {
            _updates.DownloadProgress -= OnProgress;
        }
    }
    public string TabsSummary
    {
        get
        {
            int groups = _tabs.Groups.Count;
            int tabs = _tabs.Groups.Sum(g => g.Members.Count);
            return groups == 0
                ? "Nessun gruppo attivo"
                : groups == 1
                    ? $"1 gruppo · {tabs} finestre"
                    : $"{groups} gruppi · {tabs} finestre";
        }
    }

    public void CreateTabGroup() => App.Instance?.ShowGroupPicker();
    public void CloseTabGroups() => App.Instance?.CloseAllTabGroups();
    public string ActiveThemeName => ThemePreset.Find(Settings.ThemeId).Name;
    public string ActiveThemeTagline => ThemePreset.Find(Settings.ThemeId).Tagline;
    public string ActiveSurfaceStyleName =>
        SurfaceStyleOptions.FirstOrDefault(o => o.Id == Settings.SurfaceStyle)?.Name ?? "Vetro";
    public Visibility PinsVisibility => Pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoPinsVisibility => Pins.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string HotkeyText => "Ctrl + Alt + Spazio";
    public string WidgetHotkeyText => "Ctrl + Alt + W";
    public string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula", "settings.json");

    public string AccentInput
    {
        get => _accentInput;
        set => SetProperty(ref _accentInput, value);
    }

    public string PinStatus
    {
        get => _pinStatus;
        set => SetProperty(ref _pinStatus, value);
    }

    public string AccentDescription => string.IsNullOrWhiteSpace(Settings.AccentHex)
        ? "Colore automatico dello stile"
        : Settings.AccentHex;

    public void SyncSelection()
    {
        OnPropertyChanged(nameof(ActiveThemeName));
        OnPropertyChanged(nameof(ActiveThemeTagline));

        foreach (StyleCardViewModel card in StyleCards)
            card.IsSelected = card.Preset.Id == Settings.ThemeId;

        foreach (AccentSwatchViewModel swatch in Accents)
            swatch.IsSelected = string.Equals(
                swatch.Hex, Settings.AccentHex, StringComparison.OrdinalIgnoreCase);
    }

    public void ApplyAccentInput()
    {
        string value = AccentInput.Trim();
        if (string.IsNullOrEmpty(value))
        {
            Settings.AccentHex = "";
            AccentInput = "";
            return;
        }

        string normalized = value.StartsWith('#') ? value : "#" + value;
        if (normalized.Length == 7 && normalized[1..].All(Uri.IsHexDigit))
        {
            Settings.AccentHex = normalized.ToUpperInvariant();
            AccentInput = Settings.AccentHex;
        }
        else
        {
            AccentInput = Settings.AccentHex;
        }
    }

    public void SetAccent(string hex)
    {
        Settings.AccentHex = hex;
        AccentInput = hex;
        SyncSelection();
    }

    public void RebuildPins()
    {
        Pins.Clear();
        foreach (string path in Settings.PinnedPaths)
        {
            if (File.Exists(path) || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                Pins.Add(new PinnedAppViewModel(Settings, path));
        }

        OnPropertyChanged(nameof(PinsVisibility));
        OnPropertyChanged(nameof(NoPinsVisibility));
    }

    private void SyncWidgetOptions()
    {
        var selected = new HashSet<string>(Settings.PanelWidgets ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (WidgetOptionViewModel option in WidgetOptions)
            option.SetEnabledFromSettings(selected.Contains(option.Id));
        OnPropertyChanged(nameof(WidgetCountText));
    }

    private void RebuildShortcuts()
    {
        DesktopShortcuts.Clear();
        foreach (string folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                 })
        {
            try
            {
                if (!Directory.Exists(folder))
                    continue;

                foreach (string path in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (DesktopShortcuts.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    DesktopShortcuts.Add(new ShortcutInfoViewModel(name, path));
                }
            }
            catch (Exception ex)
            {
                Log.Write("Shortcuts scan failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Re-reads the monitors so a display attached while Studio is open appears
    /// in the selector; the current choice stays when that screen still exists.
    /// </summary>
    public void RefreshScreens()
    {
        System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
        while (Screens.Count > screens.Length)
            Screens.RemoveAt(Screens.Count - 1);

        for (int i = 0; i < screens.Length; i++)
        {
            var bounds = screens[i].Bounds;
            string suffix = screens[i].Primary ? " · principale" : "";
            var option = new ScreenOptionViewModel(i,
                $"Schermo {i + 1} · {bounds.Width}×{bounds.Height}{suffix}");
            if (i < Screens.Count)
                Screens[i] = option;
            else
                Screens.Add(option);
        }

        int clamped = ScreenGeometry.Clamp(Settings.MonitorIndex);
        if (clamped != Settings.MonitorIndex)
            Settings.MonitorIndex = clamped;

        OnPropertyChanged(nameof(ScreenCountText));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.InvokeAsync(RefreshScreens);

    public string ScreenCountText => Screens.Count == 1
        ? "1 schermo rilevato"
        : $"{Screens.Count} schermi rilevati";

    public string WidgetCountText =>
        $"{Settings.PanelWidgets.Count} widget attivi · scegli dal catalogo";

    public string TaskbarWidgetCountText =>
        $"{Settings.TaskbarWidgets?.Count ?? 0} di {TaskbarWidgetCatalog.MaxWidgets} widget nella barra · " +
        "frecce per l'ordine · rotellina sulla barra per scorrere";

    public Visibility NoTaskbarWidgetsVisibility =>
        Settings.TaskbarWidgets is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;

    private void SyncTaskbarWidgets()
    {
        ActiveTaskbarWidgets.Clear();
        AvailableTaskbarWidgets.Clear();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in Settings.TaskbarWidgets ?? new List<string>())
        {
            TaskbarWidgetDefinition? definition = TaskbarWidgetCatalog.Find(id);
            if (definition is null || !used.Add(definition.Id))
                continue;
            ActiveTaskbarWidgets.Add(new TaskbarWidgetOptionViewModel(
                definition, TaskbarWidgetMode(definition.Id)));
        }

        foreach (TaskbarWidgetDefinition definition in TaskbarWidgetCatalog.All)
        {
            if (!used.Contains(definition.Id))
                AvailableTaskbarWidgets.Add(new TaskbarWidgetOptionViewModel(definition));
        }

        OnPropertyChanged(nameof(TaskbarWidgetCountText));
        OnPropertyChanged(nameof(NoTaskbarWidgetsVisibility));
    }

    private string TaskbarWidgetMode(string id) =>
        Settings.TaskbarWidgetModes is { } modes &&
        modes.TryGetValue(id, out string? mode)
            ? mode
            : "expanded";

    /// <summary>Switches one widget between the rich and the icon-only layout.</summary>
    public void ToggleTaskbarWidgetMode(TaskbarWidgetOptionViewModel option)
    {
        // Shortcut widgets only have an icon layout: there is nothing to toggle.
        if (!option.CanExpand)
            return;

        var modes = new Dictionary<string, string>(
            Settings.TaskbarWidgetModes ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);

        if (option.Mode == "icon")
            modes.Remove(option.Id);
        else
            modes[option.Id] = "icon";

        Settings.TaskbarWidgetModes = modes;
        SyncTaskbarWidgets();
    }

    public void AddTaskbarWidget(TaskbarWidgetOptionViewModel option)
    {
        var widgets = new List<string>(Settings.TaskbarWidgets ?? new List<string>());
        if (widgets.Contains(option.Id, StringComparer.OrdinalIgnoreCase) ||
            widgets.Count >= TaskbarWidgetCatalog.MaxWidgets)
            return;

        widgets.Add(option.Id);
        Settings.TaskbarWidgets = widgets;
        SyncTaskbarWidgets();
    }

    /// <summary>Fills the bar with the whole catalog in one click.</summary>
    public void AddAllTaskbarWidgets()
    {
        var widgets = new List<string>();
        foreach (TaskbarWidgetDefinition definition in TaskbarWidgetCatalog.All)
        {
            if (widgets.Count >= TaskbarWidgetCatalog.MaxWidgets)
                break;
            widgets.Add(definition.Id);
        }

        Settings.TaskbarWidgets = widgets;
        SyncTaskbarWidgets();
    }

    public void RemoveTaskbarWidget(TaskbarWidgetOptionViewModel option)
    {
        var widgets = new List<string>(Settings.TaskbarWidgets ?? new List<string>());
        widgets.RemoveAll(id => string.Equals(id, option.Id, StringComparison.OrdinalIgnoreCase));
        Settings.TaskbarWidgets = widgets;
        SyncTaskbarWidgets();
    }

    public void MoveTaskbarWidget(TaskbarWidgetOptionViewModel option, int delta)
    {
        var widgets = new List<string>(Settings.TaskbarWidgets ?? new List<string>());
        int index = widgets.FindIndex(id => string.Equals(id, option.Id, StringComparison.OrdinalIgnoreCase));
        int target = index + delta;
        if (index < 0 || target < 0 || target >= widgets.Count)
            return;

        (widgets[index], widgets[target]) = (widgets[target], widgets[index]);
        Settings.TaskbarWidgets = widgets;
        SyncTaskbarWidgets();
    }

    /// <summary>
    /// Moves a widget in front of another one. The Studio list and the bar both
    /// use this for drag-and-drop reordering.
    /// </summary>
    public void ReorderTaskbarWidget(string fromId, string toId)
    {
        var widgets = new List<string>(Settings.TaskbarWidgets ?? new List<string>());
        int from = widgets.FindIndex(id => string.Equals(id, fromId, StringComparison.OrdinalIgnoreCase));
        int to = widgets.FindIndex(id => string.Equals(id, toId, StringComparison.OrdinalIgnoreCase));
        if (from < 0 || to < 0 || from == to)
            return;

        string moved = widgets[from];
        widgets.RemoveAt(from);
        to = widgets.FindIndex(id => string.Equals(id, toId, StringComparison.OrdinalIgnoreCase));
        widgets.Insert(to < 0 ? widgets.Count : to, moved);

        Settings.TaskbarWidgets = widgets;
        SyncTaskbarWidgets();
    }

    public void AddPin(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            Settings.PinnedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        Settings.PinnedPaths.Add(path);
        RebuildPins();
        Preview.RefreshItems();
        PinStatus = "Aggiunta: " + Path.GetFileNameWithoutExtension(path);
    }

    public void RemovePin(PinnedAppViewModel pin)
    {
        Settings.PinnedPaths.RemoveAll(p => string.Equals(p, pin.Path, StringComparison.OrdinalIgnoreCase));
        RebuildPins();
        Preview.RefreshItems();
        PinStatus = "Rimossa: " + pin.Name;
    }

    public void Dispose()
    {
        _theme.Changed -= SyncSelection;
        Settings.PropertyChanged -= OnSettingsChanged;
        _tabs.GroupsChanged -= OnTabsChanged;
        _updates.StateChanged -= OnUpdateStateChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}

public sealed record ScreenOptionViewModel(int Index, string DisplayName);
public sealed record TabStyleOptionViewModel(string Id, string Name);
public sealed record TabPositionOptionViewModel(string Id, string Name);
public sealed record ZoneOrderOptionViewModel(string Id, string Name);
public sealed record IconSizeOptionViewModel(string Id, string Name);
public sealed record ShortcutInfoViewModel(string Name, string Path);

public sealed class TaskbarWidgetOptionViewModel
{
    private readonly string _mode;

    public TaskbarWidgetOptionViewModel(TaskbarWidgetDefinition definition, string mode = "expanded")
    {
        Definition = definition;
        CanExpand = definition.Expandable;
        _mode = CanExpand && !string.Equals(mode, "icon", StringComparison.OrdinalIgnoreCase)
            ? "expanded"
            : "icon";
    }

    public TaskbarWidgetDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Glyph => Definition.Glyph;

    /// <summary>False for the shortcut widgets, which only have an icon layout.</summary>
    public bool CanExpand { get; }
    public string Mode => _mode;
    public string ModeLabel => Mode == "icon" ? "Icona" : "Espanso";
    public string ModeToolTip => !CanExpand
        ? "Questo widget mostra solo l'icona: non ha una versione espansa."
        : Mode == "icon"
            ? "Passa alla versione espansa con valori e barre"
            : "Riduci a icona nella barra";
    public Visibility ModeToggleVisibility => CanExpand ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class WidgetOptionViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _isEnabled;
    private bool _syncing;

    public WidgetOptionViewModel(AppSettings settings, WidgetDefinition definition)
    {
        _settings = settings;
        Definition = definition;
    }

    public WidgetDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Glyph => Definition.Glyph;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value) || _syncing)
                return;

            var widgets = new List<string>(_settings.PanelWidgets ?? new List<string>());
            widgets.RemoveAll(id => string.Equals(id, Id, StringComparison.OrdinalIgnoreCase));
            if (value)
                widgets.Add(Id);
            _settings.PanelWidgets = widgets;
        }
    }

    public void SetEnabledFromSettings(bool enabled)
    {
        _syncing = true;
        IsEnabled = enabled;
        _syncing = false;
    }
}
