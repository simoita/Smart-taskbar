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
    private readonly HashSet<string> _iconMisses = new(StringComparer.OrdinalIgnoreCase);
    // Running apps keep the order in which they first appeared. EnumWindows
    // returns windows in Z-order, which used to reshuffle the dock whenever an
    // app was opened or focused.
    private readonly List<string> _runningOrder = new();
    private string _timeText = "";
    private string _dateText = "";
    private ImageBrush? _backdrop;
    private double _lastAspect;
    private Brush _tintBrush = Brushes.Transparent;
    private Brush _strokeBrush = Brushes.Transparent;
    private Brush _glowBrush = Brushes.Transparent;
    private int _widgetSensorTick;

    public DockViewModel(AppSettings settings, ThemeEngine theme, WindowTracker tracker, SystemMonitor monitor)
    {
        Settings = settings;
        Theme = theme;
        Tracker = tracker;
        Monitor = monitor;
        Media = new MediaSessionMonitor();
        // Volume and brightness are read lazily: only a bar that actually has
        // those widgets pays for the COM/DDC calls.
        Controls = new QuickControls(false);

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();

        _monitorTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _monitorTimer.Tick += (_, _) =>
        {
            Monitor.Update();
            RefreshWidgetSensors();
            UpdateTaskbarWidgetValues();
        };

        Settings.PropertyChanged += OnSettingsChanged;
        Theme.Changed += RefreshTheme;
        Tracker.Updated += RefreshItems;

        RefreshTheme();
        UpdateClock();
        RefreshTaskbarWidgets();
        RefreshDockButtons();
        Media.Changed += UpdateTaskbarWidgetValues;
        Media.Start();
        _clockTimer.Start();
        _monitorTimer.Start();
    }

    public AppSettings Settings { get; }
    public ThemeEngine Theme { get; }
    public WindowTracker Tracker { get; }
    public SystemMonitor Monitor { get; }
    public MediaSessionMonitor Media { get; }
    public QuickControls Controls { get; }

    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    /// <summary>Widgets rendered directly in the taskbar, in user order.</summary>
    public ObservableCollection<TaskbarWidgetViewModel> TaskbarWidgetItems { get; } = new();

    /// <summary>Pulsanti extra della zona azioni, nell'ordine scelto.</summary>
    public ObservableCollection<DockButtonViewModel> DockButtons { get; } = new();

    public Visibility DockButtonsVisibility =>
        DockButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>glass, flat or outline.</summary>
    public string DockFinish =>
        Settings.DockFinish is "flat" or "outline" ? Settings.DockFinish : "glass";

    public bool IsSimpleFinish => DockFinish != "glass";

    /// <summary>Flat monochrome fill used by the simple finishes.</summary>
    public Brush FlatFillBrush
    {
        get
        {
            try
            {
                Color color = (Color)ColorConverter.ConvertFromString(Theme.Preset.GlassBottom);
                color.A = 0xFF;
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
            catch
            {
                return Brushes.Black;
            }
        }
    }

    public Visibility FlatFillVisibility => IsSimpleFinish ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChromeGlowVisibility =>
        !IsSimpleFinish && Settings.ShowGlow ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChromeGrainVisibility =>
        !IsSimpleFinish && Settings.ShowGrain ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChromeShimmerVisibility =>
        !IsSimpleFinish && Settings.ShowShimmer ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChromeSheenVisibility =>
        !IsSimpleFinish && Settings.LiquidSheen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only the outline finish keeps a border; flat is a clean solid bar.</summary>
    public Visibility ChromeBorderVisibility =>
        DockFinish == "flat" ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// The dockbar is a separate window drawn over the bar. When it is docked
    /// inside the bar the layout reserves exactly its compact width, so the
    /// capsule sits next to Start without covering an icon.
    /// </summary>
    public bool IslandInBar =>
        Settings.ShowIsland &&
        Settings.IslandPlacement == "taskbar" &&
        Settings.ReplaceWindowsTaskbar &&
        Settings.TaskbarFullWidth;

    public double IslandSlotWidth => IslandInBar ? CapsuleCompactWidth : 0;

    public Thickness IslandSlotMargin
    {
        get
        {
            // Same gutter on both sides of the capsule, tied to the bar's corners.
            double side = Math.Round(Math.Clamp(ShellInset * 0.75, 5, 8));
            return IslandInBar ? new Thickness(side, 0, side, 0) : new Thickness(0);
        }
    }

    public Visibility IslandSlotVisibility => IslandInBar
        ? Visibility.Visible
        : Visibility.Collapsed;

    // One metric set for the bar *and* for the dockbar docked inside it. Every
    // value is rounded: a fractional box, radius or border is what made the
    // edges look chewed at some sizes.
    public double ItemSize =>
        Math.Round(Math.Clamp(Settings.IconSize + 10, 28, Math.Max(28, Settings.DockHeight - 6)));

    /// <summary>Lane under the icons that hosts the open/minimized indicator.</summary>
    public double IndicatorLane => 5;
    public GridLength IndicatorLaneLength => new(IndicatorLane);

    // The glyph always leaves the indicator lane and a 2 px gap inside its own
    // slot. Before this a 36 px icon in a 44 px bar covered the indicator and
    // touched the neighbouring icon.
    public double IconSize =>
        Math.Round(Math.Min(Settings.IconSize, Math.Max(16, ItemSize - IndicatorLane - 2)));

    public Thickness ItemMargin
    {
        get
        {
            double half = Math.Round(Settings.ItemSpacing / 2);
            return new Thickness(half, 0, half, 0);
        }
    }

    public double ShellRadiusValue
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

            return Math.Round(Math.Clamp(radius, 6, 42));
        }
    }

    public CornerRadius ShellRadius => new(ShellRadiusValue);

    /// <summary>
    /// La barra che sostituisce la taskbar a tutta larghezza appoggia sul bordo
    /// basso del monitor: parte dal bordo dello schermo senza cornice sotto e
    /// conserva l'arrotondamento solo in alto. Con un margine dal bordo scelto
    /// dall'utente la barra resta staccata e arrotondata anche sotto.
    /// </summary>
    public bool IsFlushTaskbar =>
        Settings.ReplaceWindowsTaskbar &&
        Settings.TaskbarFullWidth &&
        !Settings.DockOnTop &&
        Settings.EdgeMargin <= 0.5;

    /// <summary>Angoli della barra: arrotondati in alto, squadrati sul bordo schermo.</summary>
    public CornerRadius ShellCornerRadius => IsFlushTaskbar
        ? new CornerRadius(ShellRadiusValue, ShellRadiusValue, 0, 0)
        : new CornerRadius(ShellRadiusValue);

    /// <summary>Concentric radius for anything painted just inside the 1 px stroke.</summary>
    public CornerRadius InnerRadius => IsFlushTaskbar
        ? new CornerRadius(Math.Max(0, ShellRadiusValue - 1), Math.Max(0, ShellRadiusValue - 1), 0, 0)
        : new CornerRadius(Math.Max(0, ShellRadiusValue - 1));

    /// <summary>Il bisello inferiore disegnerebbe una riga sul bordo dello schermo.</summary>
    public Visibility ChromeBottomLineVisibility =>
        IsFlushTaskbar ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Gutter between the shell edge and the first control. It follows the
    /// corner, so a bar with wide corners never shows icons squeezed into them.
    /// </summary>
    public double ShellInset => Math.Round(Math.Clamp(ShellRadiusValue * 0.45, 6, 10));
    public Thickness ZoneHostMargin => new(ShellInset, 0, ShellInset, 0);

    public double ItemRadiusValue => Math.Round(Math.Min(ItemSize / 3.4, 16));
    public CornerRadius ItemRadius => new(ItemRadiusValue);

    public double SeparatorHeight => Math.Round(Math.Clamp(ItemSize * 0.55, 16, 28));

    public Thickness SeparatorMargin
    {
        get
        {
            double side = Math.Round(Math.Clamp(Settings.ItemSpacing + 4, 6, 12));
            return new Thickness(side, 0, side, 0);
        }
    }

    public double ActionButtonSize => Math.Round(Math.Clamp(ItemSize * 0.8, 26, 34));
    public double StartGlyphSize => Math.Round(Math.Clamp(ItemSize * 0.42, 14, 20));

    // The dockbar docked in the bar is sized by the same metrics: it becomes the
    // icon row itself instead of a shorter pill with its own proportions.
    public double CapsuleHeight => Math.Max(26, Math.Round(ItemSize));

    public double CapsuleCompactWidth =>
        Math.Max(200, Math.Round(208 * Math.Clamp(Settings.IslandScale, 0.8, 1.3)));

    public double CapsuleRadiusValue =>
        Math.Round(Math.Min(ItemRadiusValue + 2, CapsuleHeight / 2));

    public bool ShowPinned => Settings.ShowPinned;
    public bool ShowRunning => Settings.ShowRunning;

    public event Action? ItemsChanged;

    public void RefreshTheme()
    {
        TintBrush = Theme.TintBrush;
        StrokeBrush = Theme.DockStrokeBrush;
        GlowBrush = Theme.GlowBrush;
        OnPropertyChanged(nameof(FlatFillBrush));
        RaiseBarMetrics();
        if (_lastAspect > 0.1)
            Backdrop = BackdropFactory.Get(_lastAspect);
    }

    /// <summary>
    /// Pushes every derived metric at once. The bar and the dockbar read the
    /// same values, so a change of height, icon size or corner cannot leave one
    /// surface with the geometry of the previous setting.
    /// </summary>
    private void RaiseBarMetrics()
    {
        OnPropertyChanged(nameof(ShellRadiusValue));
        OnPropertyChanged(nameof(ShellRadius));
        OnPropertyChanged(nameof(IsFlushTaskbar));
        OnPropertyChanged(nameof(ShellCornerRadius));
        OnPropertyChanged(nameof(InnerRadius));
        OnPropertyChanged(nameof(ChromeBottomLineVisibility));
        OnPropertyChanged(nameof(ShellInset));
        OnPropertyChanged(nameof(ZoneHostMargin));
        OnPropertyChanged(nameof(ItemSize));
        OnPropertyChanged(nameof(IndicatorLane));
        OnPropertyChanged(nameof(IndicatorLaneLength));
        OnPropertyChanged(nameof(IconSize));
        OnPropertyChanged(nameof(ItemMargin));
        OnPropertyChanged(nameof(ItemRadiusValue));
        OnPropertyChanged(nameof(ItemRadius));
        OnPropertyChanged(nameof(SeparatorHeight));
        OnPropertyChanged(nameof(SeparatorMargin));
        OnPropertyChanged(nameof(ActionButtonSize));
        OnPropertyChanged(nameof(StartGlyphSize));
        OnPropertyChanged(nameof(CapsuleHeight));
        OnPropertyChanged(nameof(CapsuleCompactWidth));
        OnPropertyChanged(nameof(CapsuleRadiusValue));
        OnPropertyChanged(nameof(IslandSlotWidth));
        OnPropertyChanged(nameof(IslandSlotMargin));

        double icon = IconSize;
        foreach (DockItemViewModel item in Items)
            item.SetVisualIconSize(icon);
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

            string mode = Settings.TaskbarWidgetModes is { } modes &&
                          modes.TryGetValue(definition.Id, out string? configured)
                ? configured
                : "expanded";
            var widget = new TaskbarWidgetViewModel(definition, mode);
            TaskbarWidgetItems.Add(widget);
        }

        UpdateTaskbarWidgetValues();
        RefreshWidgetSensors(force: true);
    }

    /// <summary>
    /// Rebuilds the extra dock buttons in the configured order. Unknown ids and
    /// duplicates (a hand-edited settings file) are ignored here too, so the dock
    /// never shows a button that does nothing.
    /// </summary>
    public void RefreshDockButtons()
    {
        DockButtons.Clear();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in Settings.DockButtons ?? new List<string>())
        {
            TaskbarWidgetDefinition? definition = DockButtonCatalog.Find(id);
            if (definition is null || !used.Add(definition.Id))
                continue;

            DockButtons.Add(new DockButtonViewModel(definition));
        }

        OnPropertyChanged(nameof(DockButtonsVisibility));
    }

    /// <summary>
    /// Reorders the bar by dragging one widget onto another. The dragged widget
    /// lands in front of the target, which matches the direction of the drag.
    /// </summary>
    public void MoveTaskbarWidget(string fromId, string toId)
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
    }

    /// <summary>
    /// Keeps the volume and brightness widgets current without polling the
    /// expensive endpoints when those widgets are not on the bar.
    /// </summary>
    private void RefreshWidgetSensors(bool force = false)
    {
        _widgetSensorTick++;
        try
        {
            if (HasWidget("volume") && (force || _widgetSensorTick % 3 == 0))
                Controls.RefreshVolumeOnly();
            if (HasWidget("brightness") && (force || _widgetSensorTick % 15 == 0))
                Controls.RefreshBrightnessOnly();
        }
        catch (Exception ex)
        {
            // A missing audio/brightness endpoint only blanks that widget.
            Log.Write("Widget sensor refresh failed: " + ex.Message);
        }
    }

    private bool HasWidget(string id) =>
        TaskbarWidgetItems.Any(widget =>
            string.Equals(widget.Id, id, StringComparison.OrdinalIgnoreCase));

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
            var byPath = groups.ToDictionary(group => group.ExePath, StringComparer.OrdinalIgnoreCase);
            var ordered = new List<AppGroup>();

            // Existing running entries keep their position even if Windows
            // reports the windows in a different Z-order now.
            foreach (string key in _runningOrder.ToList())
            {
                if (byPath.TryGetValue(key, out AppGroup? group) && !usedGroups.Contains(group))
                {
                    ordered.Add(group);
                    usedGroups.Add(group);
                }
            }

            // Brand-new apps append to the right instead of reordering the bar.
            foreach (AppGroup group in groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (usedGroups.Contains(group))
                    continue;

                ordered.Add(group);
                usedGroups.Add(group);
                _runningOrder.Add(group.ExePath);
            }

            var live = new HashSet<string>(groups.Select(group => group.ExePath), StringComparer.OrdinalIgnoreCase);
            _runningOrder.RemoveAll(path => !live.Contains(path));

            foreach (AppGroup group in ordered)
                specs.Add(("run:" + group.ExePath, Items.FirstOrDefault(i => i.Key == "run:" + group.ExePath), null, group));
        }

        // The order the user set by dragging wins over the automatic one; keys
        // that were never dragged keep the order computed above.
        if (Settings.DockItemOrder is { Count: > 0 } preferred)
        {
            var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < preferred.Count; index++)
                rank.TryAdd(preferred[index], index);

            specs = specs
                .Select((spec, index) => (spec, index))
                .OrderBy(pair => rank.TryGetValue(pair.spec.Key, out int position) ? position : int.MaxValue)
                .ThenBy(pair => pair.index)
                .Select(pair => pair.spec)
                .ToList();
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

                item.SetVisualIconSize(IconSize);
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
            item.SetVisualIconSize(IconSize);
            if (item.Icon is null)
                item.Icon = FindCachedIcon(item);
            if (item.Icon is null)
                _ = LoadIconAsync(item);
        }
    }

    /// <summary>
    /// Moves a dock icon where the user dropped it and saves the result, so the
    /// order survives restarts, app launches and the tracker refreshing items.
    /// </summary>
    public void MoveItem(string fromKey, string toKey)
    {
        if (string.IsNullOrWhiteSpace(fromKey) || string.IsNullOrWhiteSpace(toKey) ||
            string.Equals(fromKey, toKey, StringComparison.OrdinalIgnoreCase))
            return;

        int from = Items.ToList().FindIndex(item =>
            string.Equals(item.Key, fromKey, StringComparison.OrdinalIgnoreCase));
        if (from < 0)
            return;

        List<string> order = Items.Select(item => item.Key).ToList();
        string moved = order[from];
        order.RemoveAt(from);

        int target = order.FindIndex(key =>
            string.Equals(key, toKey, StringComparison.OrdinalIgnoreCase));
        if (target < 0)
            target = order.Count;

        order.Insert(target, moved);

        Settings.DockItemOrder = order;
        ReorderPins(order);
        RefreshItems();
    }

    /// <summary>Keeps the Start panel pins in the same order as the bar.</summary>
    private void ReorderPins(IReadOnlyList<string> order)
    {
        var pinned = order
            .Where(key => key.StartsWith("pin:", StringComparison.Ordinal))
            .Select(key => key[4..])
            .Where(path => Settings.PinnedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (pinned.Count == 0)
            return;

        var rest = Settings.PinnedPaths
            .Where(path => !pinned.Contains(path, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Settings.PinnedPaths.Clear();
        Settings.PinnedPaths.AddRange(pinned);
        Settings.PinnedPaths.AddRange(rest);
    }

    private async Task LoadIconAsync(DockItemViewModel item)
    {
        if (item.Icon is not null)
            return;

        List<string> candidates = IconCandidates(item)
            .Where(path => !_iconMisses.Contains(path))
            .ToList();
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
                    // The shell cannot resolve this icon: remember the miss so
                    // the next tracker tick does not retry it forever.
                    _iconMisses.Add(path);
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
        _iconMisses.Clear();
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

    public void RefreshFinish()
    {
        OnPropertyChanged(nameof(DockFinish));
        OnPropertyChanged(nameof(IsSimpleFinish));
        OnPropertyChanged(nameof(FlatFillBrush));
        OnPropertyChanged(nameof(FlatFillVisibility));
        OnPropertyChanged(nameof(ChromeGlowVisibility));
        OnPropertyChanged(nameof(ChromeGrainVisibility));
        OnPropertyChanged(nameof(ChromeShimmerVisibility));
        OnPropertyChanged(nameof(ChromeSheenVisibility));
        OnPropertyChanged(nameof(ChromeBorderVisibility));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DockHeight):
            case nameof(AppSettings.CornerRadius):
            case nameof(AppSettings.IconSize):
            case nameof(AppSettings.ItemSpacing):
                RaiseBarMetrics();
                ItemsChanged?.Invoke();
                break;
            case nameof(AppSettings.TaskbarFullWidth):
            case nameof(AppSettings.ReserveTaskbarSpace):
            case nameof(AppSettings.ReplaceWindowsTaskbar):
            case nameof(AppSettings.DockOnTop):
            case nameof(AppSettings.EdgeMargin):
                RaiseBarMetrics();
                OnPropertyChanged(nameof(IslandInBar));
                OnPropertyChanged(nameof(IslandSlotVisibility));
                break;
            case nameof(AppSettings.IslandPlacement):
            case nameof(AppSettings.ShowIsland):
            case nameof(AppSettings.IslandScale):
                OnPropertyChanged(nameof(IslandInBar));
                OnPropertyChanged(nameof(CapsuleCompactWidth));
                OnPropertyChanged(nameof(IslandSlotWidth));
                OnPropertyChanged(nameof(IslandSlotMargin));
                OnPropertyChanged(nameof(IslandSlotVisibility));
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
            case nameof(AppSettings.DockButtons):
                RefreshDockButtons();
                break;
            case nameof(AppSettings.TaskbarWidgets):
            case nameof(AppSettings.TaskbarWidgetModes):
                RefreshTaskbarWidgets();
                break;
            case nameof(AppSettings.ShowGlow):
            case nameof(AppSettings.ShowGrain):
                OnPropertyChanged(nameof(Settings));
                RefreshFinish();
                break;
            case nameof(AppSettings.ShowShimmer):
            case nameof(AppSettings.LiquidSheen):
            case nameof(AppSettings.DockFinish):
                RefreshFinish();
                break;
        }
    }
}

internal sealed record PinSpec(string Path, string Name, string? Target);
