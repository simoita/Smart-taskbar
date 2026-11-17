using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;

namespace Nebula.ViewModels;

public sealed class DockViewModel : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _monitorTimer;
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iconsLoading = new(StringComparer.OrdinalIgnoreCase);
    private string _timeText = "";
    private string _dateText = "";
    private ImageBrush? _backdrop;
    private double _lastAspect;
    private Brush _tintBrush = Brushes.Transparent;
    private Brush _strokeBrush = Brushes.Transparent;
    private Brush _glowBrush = Brushes.Transparent;

    public DockViewModel(AppSettings settings, ThemeEngine theme, WindowTracker tracker, SystemMonitor monitor)
    {
        Settings = settings;
        Theme = theme;
        Tracker = tracker;
        Monitor = monitor;

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += (_, _) =>
        {
            Monitor.Update();
            UpdateTaskbarWidgetValues();
        };

        Settings.PropertyChanged += OnSettingsChanged;
        Theme.Changed += RefreshTheme;
        Tracker.Updated += RefreshItems;

        RefreshTheme();
        UpdateClock();
        RefreshTaskbarWidgets();
        _clockTimer.Start();
        _monitorTimer.Start();
    }

    public AppSettings Settings { get; }
    public ThemeEngine Theme { get; }
    public WindowTracker Tracker { get; }
    public SystemMonitor Monitor { get; }

    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    /// <summary>Widgets rendered directly in the taskbar, in user order.</summary>
    public ObservableCollection<TaskbarWidgetViewModel> TaskbarWidgetItems { get; } = new();

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => SetProperty(ref _dateText, value);
    }

    public Brush TintBrush
    {
        get => _tintBrush;
        private set => SetProperty(ref _tintBrush, value);
    }

    public Brush StrokeBrush
    {
        get => _strokeBrush;
        private set => SetProperty(ref _strokeBrush, value);
    }

    public Brush GlowBrush
    {
        get => _glowBrush;
        private set => SetProperty(ref _glowBrush, value);
    }

    public ImageBrush? Backdrop
    {
        get => _backdrop;
        private set
        {
            if (SetProperty(ref _backdrop, value))
                OnPropertyChanged(nameof(BackdropVisibility));
        }
    }

    public Visibility BackdropVisibility => _backdrop is null ? Visibility.Collapsed : Visibility.Visible;

    public double ItemSize => Math.Max(28, Settings.DockHeight - 14);
    public double IconSize => Settings.IconSize;
    public Thickness ItemMargin => new(Settings.ItemSpacing / 2, 0, Settings.ItemSpacing / 2, 0);

    public CornerRadius ShellRadius
    {
        get
        {
            double radius = Math.Min(
                Settings.CornerRadius * Theme.Preset.RadiusBias + 8,
                Settings.DockHeight / 2 + 4);

            // A full-width taskbar reads better with Windows 11-like corners
            // instead of becoming one giant pill.
            if (Settings.ReplaceWindowsTaskbar && Settings.TaskbarFullWidth)
                radius = Math.Min(radius, 18);

            return new CornerRadius(radius);
        }
    }

    public CornerRadius ItemRadius => new(Math.Min(ItemSize / 3.4, 16));

    public bool ShowPinned => Settings.ShowPinned;
    public bool ShowRunning => Settings.ShowRunning;

    public event Action? ItemsChanged;

    public void RefreshTheme()
    {
        TintBrush = Theme.TintBrush;
        StrokeBrush = Theme.DockStrokeBrush;
        GlowBrush = Theme.GlowBrush;
        OnPropertyChanged(nameof(ShellRadius));
        OnPropertyChanged(nameof(ItemRadius));
        OnPropertyChanged(nameof(ItemSize));
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(ItemMargin));
        if (_lastAspect > 0.1)
            Backdrop = BackdropFactory.Get(_lastAspect);
    }

    public void SetDockSize(double width, double height)
    {
        if (width < 2 || height < 2)
            return;

        double aspect = width / height;
        if (Math.Abs(aspect - _lastAspect) < 0.01)
            return;

        _lastAspect = aspect;
        Backdrop = BackdropFactory.Get(aspect);
    }

    public void UpdateClock()
    {
        DateTime now = DateTime.Now;
        string format = Settings.Clock24Hour
            ? (Settings.ShowSeconds ? "HH:mm:ss" : "HH:mm")
            : (Settings.ShowSeconds ? "h:mm:ss tt" : "h:mm tt");

        TimeText = now.ToString(format, Culture);

        string date = now.ToString("ddd d MMM", Culture);
        DateText = date.Length > 1
            ? char.ToUpper(date[0], Culture) + date[1..]
            : date;

        UpdateTaskbarWidgetValues();
    }

    public void RefreshTaskbarWidgets()
    {
        TaskbarWidgetItems.Clear();
        foreach (string id in Settings.TaskbarWidgets ?? new List<string>())
        {
            TaskbarWidgetDefinition? definition = TaskbarWidgetCatalog.Find(id);
            if (definition is null)
                continue;

            var widget = new TaskbarWidgetViewModel(definition);
            TaskbarWidgetItems.Add(widget);
        }

        UpdateTaskbarWidgetValues();
    }

    public void UpdateTaskbarWidgetValues()
    {
        foreach (TaskbarWidgetViewModel widget in TaskbarWidgetItems)
            widget.Apply(this);
    }

    public void RefreshItems()
    {
        List<AppGroup> groups = Tracker.Groups.ToList();
        var usedGroups = new HashSet<AppGroup>();
        var specs = new List<(string Key, DockItemViewModel? Existing, PinSpec? Pin, AppGroup? Group)>();

        if (Settings.ShowPinned)
        {
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PinSpec pin in BuildPins())
            {
                if (pin.Target is not null && !seenTargets.Add(pin.Target))
                    continue;

                AppGroup? match = groups.FirstOrDefault(g =>
                    !usedGroups.Contains(g) &&
                    pin.Target is not null &&
                    string.Equals(g.ExePath, pin.Target, StringComparison.OrdinalIgnoreCase));

                match ??= groups.FirstOrDefault(g =>
                    !usedGroups.Contains(g) &&
                    string.Equals(g.Name, pin.Name, StringComparison.OrdinalIgnoreCase));

                if (match is not null)
                    usedGroups.Add(match);

                specs.Add(("pin:" + pin.Path, Items.FirstOrDefault(i => i.Key == "pin:" + pin.Path), pin, match));
            }
        }

        if (Settings.ShowRunning)
        {
            foreach (AppGroup group in groups)
            {
                if (usedGroups.Contains(group))
                    continue;
                specs.Add(("run:" + group.ExePath, Items.FirstOrDefault(i => i.Key == "run:" + group.ExePath), null, group));
            }
        }

        bool rebuild = specs.Count != Items.Count;
        if (!rebuild)
        {
            for (int i = 0; i < specs.Count; i++)
            {
                if (Items[i].Key != specs[i].Key)
                {
                    rebuild = true;
                    break;
                }
            }
        }

        if (rebuild)
        {
            Items.Clear();
            foreach (var spec in specs)
            {
                DockItemViewModel item = spec.Pin is not null
                    ? DockItemViewModel.CreatePinned(
                        Settings,
                        spec.Pin.Path, spec.Pin.Name, spec.Pin.Target, spec.Group, spec.Existing?.Icon)
                    : DockItemViewModel.CreateRunning(Settings, spec.Group!);

                if (item.Icon is null)
                    item.Icon = FindCachedIcon(item);

                Items.Add(item);
                _ = LoadIconAsync(item);
            }

            ItemsChanged?.Invoke();
            return;
        }

        for (int i = 0; i < specs.Count; i++)
        {
            DockItemViewModel item = Items[i];
            AppGroup? group = specs[i].Group;
            item.UpdateGroup(group);
            if (item.Icon is null)
                item.Icon = FindCachedIcon(item);
            if (item.Icon is null)
                _ = LoadIconAsync(item);
        }
    }

    private async Task LoadIconAsync(DockItemViewModel item)
    {
        if (item.Icon is not null)
            return;

        List<string> candidates = IconCandidates(item);
        if (candidates.Count == 0)
            return;

        if (!_iconsLoading.Add(candidates[0]))
            return;

        try
        {
            foreach (string path in candidates)
            {
                ImageSource? icon = await Task.Run(() => IconExtractor.FromPath(path)).ConfigureAwait(true);
                if (icon is null)
                {
                    Log.Write("Icon unavailable: " + path);
                    continue;
                }

                _iconCache[path] = icon;
                foreach (DockItemViewModel current in Items.Where(i => i.Key == item.Key))
                    current.Icon = icon;
                return;
            }
        }
        finally
        {
            _iconsLoading.Remove(candidates[0]);
        }
    }

    private ImageSource? FindCachedIcon(DockItemViewModel item)
    {
        foreach (string path in IconCandidates(item))
        {
            if (_iconCache.TryGetValue(path, out ImageSource? icon))
                return icon;
        }

        return null;
    }

    private List<string> IconCandidates(DockItemViewModel item)
    {
        var candidates = new List<string>();
        foreach (KeyValuePair<string, string> pair in Settings.CustomIcons)
        {
            if (string.Equals(pair.Key, item.Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, item.LaunchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, item.TargetPath, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(pair.Value);
                break;
            }
        }
        if (!string.IsNullOrWhiteSpace(item.TargetPath))
            candidates.Add(item.TargetPath);

        if (item.IsPinned)
        {
            string? iconLocation = ShortcutResolver.ResolveIconLocation(item.LaunchPath);
            if (!string.IsNullOrWhiteSpace(iconLocation))
                candidates.Add(iconLocation);
        }

        if (!string.IsNullOrWhiteSpace(item.IconPath))
            candidates.Add(item.IconPath);

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void RefreshIconOverrides()
    {
        _iconCache.Clear();
        _iconsLoading.Clear();
        foreach (DockItemViewModel item in Items)
            item.Icon = null;
        RefreshItems();
    }

    private List<PinSpec> BuildPins()
    {
        var pins = new List<PinSpec>();
        foreach (string path in Settings.PinnedPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string name = Path.GetFileNameWithoutExtension(path);
            string? target = ShortcutResolver.ResolveExecutable(path);

            pins.Add(new PinSpec(path, name, target));
        }

        return pins;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DockHeight):
            case nameof(AppSettings.CornerRadius):
            case nameof(AppSettings.IconSize):
            case nameof(AppSettings.ItemSpacing):
                OnPropertyChanged(nameof(ItemSize));
                OnPropertyChanged(nameof(IconSize));
                OnPropertyChanged(nameof(ItemMargin));
                OnPropertyChanged(nameof(ShellRadius));
                OnPropertyChanged(nameof(ItemRadius));
                ItemsChanged?.Invoke();
                break;
            case nameof(AppSettings.TaskbarFullWidth):
            case nameof(AppSettings.ReserveTaskbarSpace):
            case nameof(AppSettings.ReplaceWindowsTaskbar):
                OnPropertyChanged(nameof(ShellRadius));
                break;
            case nameof(AppSettings.ShowPinned):
            case nameof(AppSettings.ShowRunning):
                RefreshItems();
                break;
            case nameof(AppSettings.ShowOpenIndicator):
            case nameof(AppSettings.ShowMinimizedIndicator):
            case nameof(AppSettings.ShowClosedIndicator):
            case nameof(AppSettings.DockIndicatorStyle):
            case nameof(AppSettings.MinimizedIconOpacity):
            case nameof(AppSettings.ClosedIconOpacity):
                foreach (DockItemViewModel item in Items)
                    item.RefreshPresentation();
                break;
            case nameof(AppSettings.IconStyle):
            case nameof(AppSettings.IconShape):
            case nameof(AppSettings.CustomIconStyles):
                foreach (DockItemViewModel item in Items)
                    item.RefreshPresentation();
                break;
            case nameof(AppSettings.CustomIcons):
                RefreshIconOverrides();
                break;
            case nameof(AppSettings.ShowSeconds):
            case nameof(AppSettings.Clock24Hour):
                UpdateClock();
                break;
            case nameof(AppSettings.ShowDate):
                UpdateTaskbarWidgetValues();
                break;
            case nameof(AppSettings.TaskbarWidgets):
                RefreshTaskbarWidgets();
                break;
            case nameof(AppSettings.ShowGlow):
            case nameof(AppSettings.ShowGrain):
                OnPropertyChanged(nameof(Settings));
                break;
        }
    }
}

internal sealed record PinSpec(string Path, string Name, string? Target);
