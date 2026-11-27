using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Threading.Tasks;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;

namespace Nebula.Views;

public partial class DockWindow : Window
{
    private const double ShadowTop = 22;
    private const double ShadowBottom = 26;
    private const int WmDisplayChange = 0x007E;

    /// <summary>
    /// La taskbar a tutta larghezza appoggia sul bordo basso del monitor: parte
    /// dal bordo dello schermo, senza trasparenza sotto, e conserva solo gli
    /// angoli superiori arrotondati. Se l'utente sceglie un margine dal bordo la
    /// barra resta staccata e arrotondata anche sotto.
    /// </summary>
    private bool FlushTaskbar =>
        _vm.Settings.ReplaceWindowsTaskbar &&
        _vm.Settings.TaskbarFullWidth &&
        !_vm.Settings.DockOnTop &&
        _vm.Settings.EdgeMargin <= 0.5;

    /// <summary>Trasparenza sotto la barra: zero quando tocca il bordo dello schermo.</summary>
    private double BottomPad => FlushTaskbar ? 0 : ShadowBottom;

    private readonly DockViewModel _vm;
    private readonly SettingsStore _store;
    private readonly int? _forcedMonitor;
    private readonly TaskbarAppBar _appBar = new();
    private readonly DispatcherTimer _watchTimer;
    private readonly DispatcherTimer _backdropTimer;
    private HwndSource? _hwndSource;
    private uint _taskbarCreatedMessage;
    private bool _hidden;
    private bool _animating;
    private bool _userHidden;
    private bool _hiddenForTaskbarRestore;
    private int _tick;
    private bool _revealing;
    private readonly HashSet<string> _shownItemKeys = new(StringComparer.OrdinalIgnoreCase);
    private int _followMonitor = -1;
    private int _followTicks;
    private Rect _workAreaDip;
    private System.Drawing.Rectangle _physicalWorkArea;
    private System.Drawing.Rectangle _screenBounds;

    private const string DockItemDragFormat = "Nebula.DockItem";
    private const string TaskbarWidgetDragFormat = "Nebula.TaskbarWidget";
    private Point _itemDragStart;
    private DockItemViewModel? _itemDragSource;
    private bool _itemDragging;
    private bool _suppressItemClick;

    // Hover preview of every window of a group, like the Windows taskbar.
    private readonly DispatcherTimer _previewShowTimer;
    private readonly DispatcherTimer _previewHideTimer;
    private WindowPreviewWindow? _preview;
    private Button? _previewAnchor;
    private DockItemViewModel? _previewItem;

    private Point _widgetDragStart;
    private TaskbarWidgetViewModel? _widgetDragSource;
    private bool _widgetDragging;
    private bool _suppressWidgetClick;

    public DockWindow(DockViewModel vm, SettingsStore store, int? monitor = null)
    {
        _vm = vm;
        _store = store;
        _forcedMonitor = monitor;

        InitializeComponent();
        DataContext = vm;

        // Fullscreen, AppBar and auto-hide checks do not need a 6.7 Hz UI
        // timer while the dock is idle. 500 ms still hides promptly on a
        // foreground change and keeps auto-hide responsive.
        _watchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Start();

        // A failed re-apply after ABN_POSCHANGED must reach the guard, otherwise
        // the dock would believe the strip is still reserved.
        _appBar.ReapplyFailed += () => AppBarApplySucceeded = false;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        DpiChanged += (_, _) => Reposition();
        Closed += (_, _) =>
        {
            CloseWindowPreview();
            _watchTimer.Stop();
            try
            {
                _hwndSource?.RemoveHook(WndProc);
            }
            catch
            {
                // The source may already be disposing with the window.
            }
            _hwndSource = null;
            _appBar.Dispose();
            _vm.Settings.PropertyChanged -= OnSettingsChanged;
            _vm.ItemsChanged -= OnDockItemsChanged;
        };
        _vm.Settings.PropertyChanged += OnSettingsChanged;
        _vm.ItemsChanged += OnDockItemsChanged;

        _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _backdropTimer.Tick += (_, _) =>
        {
            _backdropTimer.Stop();
            UpdateBackdrop();
        };

        // A short rest on the icon opens the previews; moving away gives the
        // pointer time to reach the flyout before it disappears.
        _previewShowTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };
        _previewShowTimer.Tick += (_, _) =>
        {
            _previewShowTimer.Stop();
            ShowWindowPreview();
        };

        _previewHideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(340)
        };
        _previewHideTimer.Tick += (_, _) =>
        {
            _previewHideTimer.Stop();
            if (_preview is { IsMouseOver: false } && _previewAnchor?.IsMouseOver != true)
                CloseWindowPreview();
        };
    }

    public bool IsHidden => _hidden;
    public bool IsAppBarRegistered => _appBar.IsRegistered;
    public bool AppBarApplySucceeded { get; private set; }

    /// <summary>Monitor this bar draws on: its own index on multi-monitor bars.</summary>
    public int MonitorIndex => _forcedMonitor ?? ScreenGeometry.Clamp(_vm.Settings.MonitorIndex);

    /// <summary>
    /// True only on the bar that actually hosts the dockbar. With a bar on every
    /// screen, reserving the capsule's slot on the other monitors left an empty
    /// hole in their bars and pulled the selected bar's layout off-centre.
    /// </summary>
    public bool HostsIsland =>
        _vm.IslandInBar && MonitorIndex == ScreenGeometry.Clamp(_vm.Settings.MonitorIndex);

    /// <summary>Shows the dockbar slot only on the bar drawn on the capsule's monitor.</summary>
    private void UpdateIslandSlot()
    {
        if (IslandSlot is null)
            return;

        Visibility wanted = HostsIsland ? Visibility.Visible : Visibility.Collapsed;
        if (IslandSlot.Visibility != wanted)
            IslandSlot.Visibility = wanted;
    }

    /// <summary>Shell rectangle in window DIPs, excluding the transparent shadow.</summary>
    public Rect ShellBoundsDip
    {
        get
        {
            Point origin = Shell.TranslatePoint(new Point(0, 0), this);
            return new Rect(origin, Shell.RenderSize);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, (IntPtr)style);

            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
            _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        }
        catch (Exception ex)
        {
            Log.Write("Dock style setup failed: " + ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && msg == (int)_taskbarCreatedMessage)
        {
            // Explorer restarted: the AppBar registration died with it. Re-arm
            // the reservation on the new shell.
            _appBar.Remove();
            Dispatcher.InvokeAsync(UpdateAppBar, DispatcherPriority.Background);
        }

        if (msg == WmDisplayChange)
        {
            // A monitor was attached, removed or reconfigured. Rebuild the
            // geometry before the next guard tick works with stale bounds.
            Dispatcher.InvokeAsync(OnDisplayChanged, DispatcherPriority.Background);
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyZoneLayout();
        Reposition();
        UpdateBackdrop();
        CenterLiquidSheen(false);
        Dispatcher.InvokeAsync(UpdateAppBar, DispatcherPriority.Loaded);
        RevealDock();
    }

    /// <summary>
    /// La barra entra in scena tutta insieme: parte oltre il bordo dello schermo
    /// e sale al suo posto mentre l'opacità la accompagna. Prima il contenuto
    /// faceva una seconda animazione dentro una finestra già ferma, e le icone
    /// che arrivavano dopo comparivano di colpo "dal nulla".
    /// </summary>
    private void RevealDock()
    {
        double target = VisibleTop();

        _shownItemKeys.Clear();
        foreach (DockItemViewModel item in _vm.Items)
            _shownItemKeys.Add(item.Key);

        if (!_vm.Settings.AnimationsEnabled)
        {
            _revealing = false;
            BeginAnimation(TopProperty, null);
            Top = target;
            Root.Opacity = 1;
            return;
        }

        _revealing = true;
        double speed = Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);

        BeginAnimation(TopProperty, null);
        Top = HiddenTop();
        Root.Opacity = 0;
        UpdateLayout();

        var rise = new DoubleAnimation(Top, target,
            TimeSpan.FromMilliseconds(380 / speed))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        rise.Completed += (_, _) =>
        {
            _revealing = false;
            BeginAnimation(TopProperty, null);
            Top = target;
            Reposition();
        };
        BeginAnimation(TopProperty, rise);

        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210 / speed))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_hidden && !_animating && !_revealing)
            Reposition();

        _backdropTimer.Stop();
        _backdropTimer.Start();
    }

    public void Reposition()
    {
        RefreshScreenGeometry();
        Rect work = _workAreaDip;

        // The window keeps 26 px of transparent shadow on each side; the shell
        // itself must still reach the requested edge margin.
        const double horizontalShadow = 52;
        double maxWidth = Math.Max(360, work.Width + horizontalShadow);
        double maxHeight = Math.Max(140, work.Height - Math.Max(0, _vm.Settings.EdgeMargin) * 2);
        if (Math.Abs(MaxWidth - maxWidth) > 0.5)
            MaxWidth = maxWidth;
        if (Math.Abs(MaxHeight - maxHeight) > 0.5)
            MaxHeight = maxHeight;

        bool fullWidth = _vm.Settings.ReplaceWindowsTaskbar && _vm.Settings.TaskbarFullWidth;

        // A horizontal StackPanel measures its children with infinite width, so
        // without a cap the widget ScrollViewer would grow past the screen and
        // never scroll. Keep the widget zone inside the space the full-width
        // grid actually gives it.
        if (WidgetScroller is not null)
        {
            double cap = Math.Max(260, work.Width * 0.55);
            if (fullWidth && ZoneHost.ColumnDefinitions.Count == 3)
            {
                // With the dockbar in the bar the widget zone lives in a side
                // column (the centre one only holds Start and the capsule), so
                // the strip is capped by the room that side really has. That is
                // what keeps it from running over the neighbouring zones.
                double column = HostsIsland
                    ? Math.Min(ZoneHost.ColumnDefinitions[0].ActualWidth,
                               ZoneHost.ColumnDefinitions[2].ActualWidth)
                    : ZoneHost.ColumnDefinitions[1].ActualWidth;
                if (column > 1)
                    cap = Math.Min(cap, Math.Max(200, column - 6));
            }

            // The cap must live on the ScrollViewer itself: a horizontal
            // StackPanel would still measure it with infinite width and the
            // widget strip would overflow instead of scrolling.
            if (double.IsNaN(WidgetScroller.MaxWidth) || Math.Abs(WidgetScroller.MaxWidth - cap) > 0.5)
                WidgetScroller.MaxWidth = cap;
        }

        if (fullWidth)
        {
            double target = Math.Max(360,
                work.Width - Math.Max(0, _vm.Settings.EdgeMargin) * 2 + horizontalShadow);
            if (Math.Abs(Width - target) > 0.5)
                Width = target;
        }
        else if (!double.IsNaN(Width))
        {
            Width = double.NaN;
        }

        UpdateLayout();
        if (ActualWidth < 1 || ActualHeight < 1)
            return;

        if (fullWidth)
        {
            double desiredShell = Math.Max(320, work.Width - Math.Max(0, _vm.Settings.EdgeMargin) * 2);
            // The window may keep a non-client frame even with WindowStyle=None.
            // Absorb it in the transparent side margin so the *shell* keeps the
            // exact requested width and edge distance on every DPI.
            double side = Math.Max(0, (ActualWidth - desiredShell) / 2);
            Root.Margin = new Thickness(side, ShadowTop, side, BottomPad);
        }
        else if (Math.Abs(Root.Margin.Left - 26) > 0.5 ||
                 Math.Abs(Root.Margin.Bottom - BottomPad) > 0.5)
        {
            Root.Margin = new Thickness(26, ShadowTop, 26, BottomPad);
        }

        Left = work.Left + (work.Width - ActualWidth) / 2;
        if (!_hidden && !_revealing)
        {
            BeginAnimation(TopProperty, null);
            Top = VisibleTop();
        }

        UpdateShellBottomEdge();
        UpdateAppBar();
        UpdateIslandSlot();

        // The docked dockbar is anchored to the slot next to Start, so it has to
        // follow the bar whenever the bar itself moves or changes size. Only the
        // hosting bar does it: the other screens' bars moved a capsule they do
        // not own.
        if (HostsIsland)
            Dispatcher.InvokeAsync(() => App.Instance?.RepositionIsland(), DispatcherPriority.Background);
    }

    /// <summary>
    /// Keeps the bar on a screen that still exists after a hot-plug or a
    /// resolution change, then re-applies its position and AppBar strip.
    /// </summary>
    private void OnDisplayChanged()
    {
        if (_forcedMonitor is int forced && forced >= ScreenGeometry.Count)
        {
            // The screen this bar lived on is gone; the app owns the lifetime.
            _hiddenForTaskbarRestore = true;
            Hide();
            return;
        }

        if (_forcedMonitor is null)
        {
            int clamped = ScreenGeometry.Clamp(_vm.Settings.MonitorIndex);
            if (clamped != _vm.Settings.MonitorIndex)
                _vm.Settings.MonitorIndex = clamped;
        }

        _workAreaDip = default;
        _followMonitor = -1;
        _followTicks = 0;
        Reposition();
    }

    private void RefreshScreenGeometry()
    {
        System.Windows.Forms.Screen screen = ScreenGeometry.Target(MonitorIndex);
        _workAreaDip = _vm.Settings.ReplaceWindowsTaskbar && _vm.Settings.ReserveTaskbarSpace
            ? ScreenGeometry.BoundsDip(screen)
            : ScreenGeometry.WorkAreaDip(screen);
        _physicalWorkArea = screen.WorkingArea;
        _screenBounds = screen.Bounds;
    }

    /// <summary>
    /// Reserves the bar through the Windows AppBar API. This is what makes the
    /// replacement behave like a real taskbar: maximized windows stop at it.
    /// </summary>
    private void UpdateAppBar()
    {
        if (!IsVisible || _hiddenForTaskbarRestore)
            return;

        if (!_vm.Settings.ReplaceWindowsTaskbar || !_vm.Settings.ReserveTaskbarSpace)
        {
            _appBar.Remove();
            AppBarApplySucceeded = false;
            return;
        }

        double height = Math.Max(28, ActualHeight - ShadowTop - BottomPad + _vm.Settings.EdgeMargin);
        AppBarApplySucceeded = _appBar.Apply(this, _vm.Settings.DockOnTop, height, MonitorIndex);

        // Windows can grant a strip that is not flush with the monitor edge
        // (the bottom edge is shared with the native bar). Follow the granted
        // rectangle instead of the edge the user asked for.
        if (AppBarApplySucceeded && !_hidden && IsLoaded && !_revealing)
        {
            BeginAnimation(TopProperty, null);
            Top = VisibleTop();
        }
    }

    private Rect CurrentWorkArea()
    {
        if (_workAreaDip.Width < 1)
            RefreshScreenGeometry();

        return _workAreaDip;
    }

    public void ApplySettings()
    {
        ApplyZoneLayout();
        Reposition();
        UpdateBackdrop();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Models.AppSettings.DockAppsZone)
            or nameof(Models.AppSettings.DockSystemZone)
            or nameof(Models.AppSettings.DockActionsZone)
            or nameof(Models.AppSettings.DockZoneOrder)
            or nameof(Models.AppSettings.DockZoneSpacing)
            or nameof(Models.AppSettings.DockAppsScale)
            or nameof(Models.AppSettings.DockSystemScale)
            or nameof(Models.AppSettings.DockActionsScale)
            or nameof(Models.AppSettings.DockZoneDividers)
            or nameof(Models.AppSettings.DockZonesSeparate)
            or nameof(Models.AppSettings.IslandPlacement)
            or nameof(Models.AppSettings.ShowIsland)
            or nameof(Models.AppSettings.TaskbarWidgets)
            or nameof(Models.AppSettings.TaskbarFullWidth))
        {
            ApplyZoneLayout();
            Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(Models.AppSettings.ReserveTaskbarSpace)
            or nameof(Models.AppSettings.ReplaceWindowsTaskbar)
            or nameof(Models.AppSettings.DockOnTop)
            or nameof(Models.AppSettings.MonitorIndex)
            or nameof(Models.AppSettings.MonitorFollowCursor)
            or nameof(Models.AppSettings.EdgeMargin)
            or nameof(Models.AppSettings.DockHeight))
        {
            if (e.PropertyName is nameof(Models.AppSettings.MonitorFollowCursor))
            {
                _followMonitor = -1;
                _followTicks = 0;
            }

            Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// "Active screen" mode: when the pointer rests on another monitor for about
    /// a second the bar, its AppBar strip and the island move there.
    /// </summary>
    private void UpdateMonitorFollow()
    {
        // Bars on every monitor and a single bar that follows the pointer are
        // mutually exclusive: otherwise two bars would share one screen.
        if (_forcedMonitor is not null || _vm.Settings.TaskbarAllMonitors)
            return;

        if (!_vm.Settings.MonitorFollowCursor ||
            !NativeMethods.GetCursorPos(out NativeMethods.POINT point))
            return;

        int index = ScreenGeometry.IndexForPoint(point.X, point.Y);
        if (index < 0 || index == _vm.Settings.MonitorIndex)
        {
            _followMonitor = -1;
            _followTicks = 0;
            return;
        }

        if (index != _followMonitor)
        {
            _followMonitor = index;
            _followTicks = 0;
        }

        // Two 500 ms ticks: crossing a monitor edge does not relocate the bar.
        if (++_followTicks < 2)
            return;

        _followMonitor = -1;
        _followTicks = 0;
        _vm.Settings.MonitorIndex = index;
    }

    /// <summary>Rebuilds the dock from independent app/system/action zones.</summary>
    /// <summary>
    /// A layout fault must never abort the startup (which used to restore the
    /// native bar and leave the app in an error state after an update). The
    /// core keeps the bar usable; a failure is logged and leaves an empty but
    /// interactive shell instead of taking the whole app down.
    /// </summary>
    private void ApplyZoneLayout()
    {
        try
        {
            ApplyZoneLayoutCore();
        }
        catch (Exception ex)
        {
            Log.Write("Zone layout failed; keeping the bar usable: " + ex);
            try
            {
                ZoneHost.Children.Clear();
                ZoneHost.ColumnDefinitions.Clear();
            }
            catch
            {
                // Nothing else can be done for this layout pass.
            }
        }
    }

    private void ApplyZoneLayoutCore()
    {
        if (ZoneHost is null || AppsZone is null || SystemZone is null || ActionsZone is null)
            return;

        var settings = _vm.Settings;
        ApplyZoneChrome();
        UpdateIslandSlot();
        var zones = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["apps"] = AppsZone,
            ["system"] = SystemZone,
            ["actions"] = ActionsZone
        };

        // Hidden zones stay in a detached container when they are removed from
        // the host; free every zone before reparenting any of them.
        foreach (FrameworkElement zone in zones.Values)
        {
            if (zone.Parent is Panel parent)
                parent.Children.Remove(zone);
        }

        string[] requested = (settings.DockZoneOrder ?? "apps,system,actions")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length != 3 || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            requested.Any(key => !zones.ContainsKey(key)))
            requested = new[] { "apps", "system", "actions" };

        bool separate = settings.DockZonesSeparate;
        double spacing = separate
            ? Math.Max(6, settings.DockZoneSpacing)
            : settings.DockZoneSpacing;
        // Separate shells need a real gap so the pills read as distinct blocks;
        // a single bar keeps the original half-spacing rhythm.
        double gap = separate ? Math.Max(8, spacing) : spacing / 2;

        var entries = new List<(FrameworkElement Zone, Border? Divider, string Key)>();
        bool first = true;
        foreach (string key in requested)
        {
            bool visible = key switch
            {
                "apps" => settings.DockAppsZone,
                "system" => settings.DockSystemZone &&
                            settings.TaskbarWidgets is { Count: > 0 },
                _ => settings.DockActionsZone &&
                     (settings.EnableTabs || settings.ShowSearch || settings.ShowWidgets ||
                      settings.ShowQuickControls || settings.ShowSettingsButton)
            };

            FrameworkElement zone = zones[key];
            zone.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                continue;

            Border? divider = null;
            if (!separate && !first && settings.DockZoneDividers)
            {
                divider = new Border
                {
                    Width = 1,
                    // The divider follows the icons: a fixed 24 px line was
                    // taller than the bar itself on a short dock.
                    Height = _vm.SeparatorHeight,
                    Margin = new Thickness(settings.DockZoneSpacing / 2, 0, settings.DockZoneSpacing / 2, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                divider.SetResourceReference(BackgroundProperty, "C.Separator");
            }

            double scale = key switch
            {
                "apps" => settings.DockAppsScale,
                "system" => settings.DockSystemScale,
                _ => settings.DockActionsScale
            };
            zone.LayoutTransform = Math.Abs(scale - 1) < 0.01
                ? Transform.Identity
                : new ScaleTransform(scale, scale);
            zone.Margin = new Thickness(0);
            entries.Add((zone, divider, key));
            first = false;
        }

        // Reparenting a zone is only legal after detaching it from the previous
        // container; otherwise WPF throws "element already has a logical parent".
        foreach ((FrameworkElement zone, Border? divider, _) in entries)
        {
            if (zone.Parent is Panel zoneParent)
                zoneParent.Children.Remove(zone);
            if (divider?.Parent is Panel dividerParent)
                dividerParent.Children.Remove(divider);
        }

        ZoneHost.Children.Clear();
        ZoneHost.ColumnDefinitions.Clear();

        bool fullWidth = settings.ReplaceWindowsTaskbar && settings.TaskbarFullWidth;

        var blocks = new List<FrameworkElement>();
        var blockKeys = new List<string>();
        for (int i = 0; i < entries.Count; i++)
        {
            (FrameworkElement zone, Border? divider, string blockKey) = entries[i];
            var container = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (divider is not null)
                container.Children.Add(divider);
            container.Children.Add(zone);

            Thickness margin = default;
            if (!fullWidth)
                margin = i == 0 ? new Thickness(0) : new Thickness(gap, 0, 0, 0);
            else if (entries.Count >= 3 && i == 1)
                margin = new Thickness(spacing, 0, spacing, 0);

            FrameworkElement block = separate
                ? CreateZonePill(container)
                : container;
            block.Margin = margin;
            blocks.Add(block);
            blockKeys.Add(blockKey);
        }

        if (!fullWidth || blocks.Count == 0)
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(blocks[i], i);
                ZoneHost.Children.Add(blocks[i]);
            }

            return;
        }

        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (blocks.Count == 1)
        {
            Grid.SetColumn(blocks[0], 0);
            Grid.SetColumnSpan(blocks[0], 3);
            blocks[0].HorizontalAlignment = HorizontalAlignment.Center;
            ZoneHost.Children.Add(blocks[0]);
            return;
        }

        if (blocks.Count == 2)
        {
            Grid.SetColumn(blocks[0], 0);
            ZoneHost.Children.Add(blocks[0]);
            Grid.SetColumn(blocks[1], 2);
            blocks[1].HorizontalAlignment = HorizontalAlignment.Right;
            ZoneHost.Children.Add(blocks[1]);
            return;
        }

        // With the dockbar docked inside the bar it sits next to Start, so the
        // whole app group (Start, capsule, icons) is centred on the bar. Every
        // other zone still follows the order the user chose: the zones asked
        // for before apps sit on the left, the ones after it on the right, so
        // widgets and action buttons can live on the left even with the
        // capsule inside the bar. Only the bar that hosts the capsule does
        // this; the bars on the other monitors use the full width.
        int appsAt = HostsIsland ? blockKeys.FindIndex(key => key == "apps") : -1;
        if (appsAt >= 0)
        {
            FrameworkElement appsBlock = blocks[appsAt];
            appsBlock.HorizontalAlignment = HorizontalAlignment.Center;
            appsBlock.Margin = new Thickness(0);

            // Each side lives in a wrapper that is clipped to its own column:
            // a wide widget strip or a long action row can never paint over the
            // centred app group (and the capsule inside it).
            var leftHost = new Grid
            {
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            var left = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            leftHost.Children.Add(left);

            var rightHost = new Grid
            {
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            var right = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            rightHost.Children.Add(right);

            for (int i = 0; i < blocks.Count; i++)
            {
                if (i == appsAt)
                    continue;

                if (i < appsAt)
                {
                    blocks[i].Margin = new Thickness(0, 0, gap, 0);
                    left.Children.Add(blocks[i]);
                }
                else
                {
                    blocks[i].Margin = new Thickness(gap, 0, 0, 0);
                    right.Children.Add(blocks[i]);
                }
            }

            ZoneHost.ColumnDefinitions.Clear();
            ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // The apps block was never added before this branch: adding it here
            // is what used to crash the startup with "element already a child"
            // when the island lived inside the bar.
            Grid.SetColumn(appsBlock, 1);
            ZoneHost.Children.Add(appsBlock);

            // A side only gets its container when it actually holds a zone.
            if (left.Children.Count > 0)
            {
                Grid.SetColumn(leftHost, 0);
                ZoneHost.Children.Add(leftHost);
            }

            if (right.Children.Count > 0)
            {
                Grid.SetColumn(rightHost, 2);
                ZoneHost.Children.Add(rightHost);
            }

            return;
        }

        Grid.SetColumn(blocks[0], 0);
        ZoneHost.Children.Add(blocks[0]);

        Grid.SetColumn(blocks[1], 1);
        blocks[1].HorizontalAlignment = HorizontalAlignment.Center;
        ZoneHost.Children.Add(blocks[1]);

        Grid.SetColumn(blocks[2], 2);
        blocks[2].HorizontalAlignment = HorizontalAlignment.Right;
        ZoneHost.Children.Add(blocks[2]);
    }

    /// <summary>
    /// Wraps one zone in its own glass shell so the three zones read as separate
    /// capsules instead of sections of a single bar.
    /// </summary>
    private Border CreateZonePill(FrameworkElement content)
    {
        var pill = new Border
        {
            VerticalAlignment = VerticalAlignment.Center,
            // Same corner and same gutter as the bar: the separate capsules are
            // the bar cut into blocks, not a second visual language.
            CornerRadius = _vm.ShellRadius,
            Padding = new Thickness(_vm.ShellInset, 2, _vm.ShellInset, 2),
            BorderThickness = new Thickness(_vm.DockFinish == "flat" ? 0 : 1),
            Child = content
        };

        // The simple finishes stay monochrome even with separate zone capsules.
        if (_vm.DockFinish == "glass")
            pill.SetResourceReference(BackgroundProperty, "C.DockShell");
        else
            pill.Background = _vm.FlatFillBrush;

        // A soft stroke disappeared on light presets; DockStroke is the same
        // theme-aware hairline the shell itself uses.
        pill.SetResourceReference(BorderBrushProperty, "C.DockStroke");
        return pill;
    }

    /// <summary>Hides the continuous bar chrome while the zones are separate shells.</summary>
    private void ApplyZoneChrome()
    {
        if (Shell is null || ShellChrome is null)
            return;

        bool separate = _vm.Settings.DockZonesSeparate;
        ShellChrome.Visibility = separate ? Visibility.Collapsed : Visibility.Visible;

        // The flat finish is a single full colour: no border, no glass shell.
        UpdateShellBottomEdge();
        if (_vm.DockFinish == "glass" && !separate)
            Shell.SetResourceReference(BackgroundProperty, "C.DockShell");
        else
            Shell.Background = Brushes.Transparent;
    }

    /// <summary>
    /// Il bordo della barra. Sulla taskbar a filo dello schermo il lato
    /// inferiore non esiste: una linea da 1 px sul bordo del monitor si
    /// vedrebbe come una cornice sotto la barra.
    /// </summary>
    private void UpdateShellBottomEdge()
    {
        if (Shell is null)
            return;

        bool borderless = _vm.Settings.DockZonesSeparate || _vm.DockFinish == "flat";
        Thickness thickness = borderless
            ? new Thickness(0)
            : FlushTaskbar ? new Thickness(1, 1, 1, 0) : new Thickness(1);

        if (Shell.BorderThickness != thickness)
            Shell.BorderThickness = thickness;
    }

    private double VisibleTop()
    {
        double margin = _vm.Settings.EdgeMargin;

        // Prefer the strip Windows actually reserved: on the bottom edge the
        // shell can move it above the space still claimed by the native bar, and
        // a dock drawn from the monitor edge would end up off screen.
        if (_vm.Settings.ReplaceWindowsTaskbar && _vm.Settings.ReserveTaskbarSpace &&
            _appBar.HasAppliedBounds && AppBarApplySucceeded)
        {
            System.Drawing.Rectangle strip = _appBar.AppliedBounds;
            double scale = Math.Max(0.5, ScreenGeometry.Scale(ScreenGeometry.Target(MonitorIndex)));
            double stripTop = strip.Top / scale;
            double stripBottom = strip.Bottom / scale;
            double shellHeight = Math.Max(1, ActualHeight - ShadowTop - BottomPad);

            return _vm.Settings.DockOnTop
                ? stripTop + margin - ShadowTop
                : stripBottom - margin - shellHeight - ShadowTop;
        }

        Rect work = CurrentWorkArea();
        return _vm.Settings.DockOnTop
            ? work.Top - ShadowTop + margin
            : work.Bottom + BottomPad - margin - ActualHeight;
    }

    private double HiddenTop()
    {
        Rect work = CurrentWorkArea();
        return _vm.Settings.DockOnTop
            ? work.Top - ActualHeight - 48
            : work.Bottom + 40;
    }

    private void UpdateBackdrop() =>
        _vm.SetDockSize(Math.Max(1, ActualWidth - 52), Math.Max(1, ActualHeight - ShadowTop - BottomPad));

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_hiddenForTaskbarRestore)
            return;

        _tick++;
        UpdateMonitorFollow();

        // A click on any window of the topmost band can push the docked island
        // behind the bar; keep it on top while it lives inside the bar.
        if (_tick % 4 == 0 && HostsIsland && !_hidden)
            App.Instance?.RaiseIslandAboveDock();

        bool fullscreen = false;

        if (_vm.Settings.HideInFullscreen && (_tick % 4 == 0 || _hidden))
        {
            try
            {
                if (_screenBounds.Width < 1)
                    RefreshScreenGeometry();

                // Any fullscreen window on *this* monitor counts, not only the
                // foreground one: a game on a second screen must still take the
                // bar on that screen out of the way while the user works here.
                fullscreen = _vm.Tracker.IsFullscreenOnScreen(_screenBounds);
            }
            catch
            {
                fullscreen = false;
            }
        }


        if (!_hidden && _tick % 8 == 0 &&
            _vm.Settings.ReplaceWindowsTaskbar && _vm.Settings.ReserveTaskbarSpace &&
            (!_appBar.IsRegistered || !AppBarApplySucceeded))
        {
            if (!AppBarApplySucceeded)
            {
                // Explorer restarted: the old HWND registration died with the
                // shell, but our local flag did not. Force ABM_NEW again.
                _appBar.Remove();
            }
            UpdateAppBar();
        }

        // Fullscreen content puts the bar into auto-hide mode: it slides away
        // instead of covering the content and comes back when the pointer reaches
        // the edge, or when the fullscreen window closes. Only the bar on the
        // screen with the fullscreen content does it, because the check runs
        // against this bar's own bounds.
        bool autoHide = _vm.Settings.AutoHideDock || fullscreen;
        bool nearEdge = autoHide && IsCursorNearEdge();
        bool shouldHide = _userHidden || (autoHide && !IsMouseOver && !nearEdge);

        // A bar without an AppBar reservation can sit above a maximized window;
        // never hide it silently while it is the only bar on that screen.
        if (shouldHide && _vm.Settings.ReplaceWindowsTaskbar &&
            _vm.Settings.ReserveTaskbarSpace && !AppBarApplySucceeded)
            shouldHide = _userHidden;

        if (shouldHide && !_hidden)
            HideDock();
        else if (!shouldHide && _hidden)
            ShowDock();
    }

    private bool IsCursorNearEdge()
    {
        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT point))
            return false;

        if (_physicalWorkArea.Width < 1)
            RefreshScreenGeometry();

        bool reserved = _vm.Settings.ReplaceWindowsTaskbar && _vm.Settings.ReserveTaskbarSpace;
        int topEdge = reserved ? _screenBounds.Top : _physicalWorkArea.Top;
        int bottomEdge = reserved ? _screenBounds.Bottom : _physicalWorkArea.Bottom;
        return _vm.Settings.DockOnTop
            ? point.Y <= topEdge + 3
            : point.Y >= bottomEdge - 3;
    }

    public void HideDock()
    {
        if (_hidden || _animating)
            return;

        CloseWindowPreview();
        _hidden = true;
        double target = HiddenTop();

        if (!_vm.Settings.AnimationsEnabled)
        {
            BeginAnimation(TopProperty, null);
            Top = target;
            return;
        }

        _animating = true;
        double seconds = 0.24 / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);
        var animation = new DoubleAnimation(Top, target, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            Top = target;
            BeginAnimation(TopProperty, null);
            _animating = false;
        };
        BeginAnimation(TopProperty, animation);
    }

    public void ShowDock()
    {
        if (!_hidden || _animating)
            return;

        _hidden = false;
        double target = VisibleTop();

        if (!_vm.Settings.AnimationsEnabled)
        {
            BeginAnimation(TopProperty, null);
            Top = target;
            return;
        }

        _animating = true;
        double seconds = 0.32 / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);
        var animation = new DoubleAnimation(Top, target, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        animation.Completed += (_, _) =>
        {
            Top = target;
            BeginAnimation(TopProperty, null);
            _animating = false;
        };
        BeginAnimation(TopProperty, animation);
    }

    /// <summary>Removes the dock completely while the native taskbar is in charge again.</summary>
    public void HideForTaskbarRestore()
    {
        _hiddenForTaskbarRestore = true;
        _hidden = false;
        _animating = false;
        BeginAnimation(TopProperty, null);
        _appBar.Remove();
        CloseOpenTooltips();

        if (IsVisible)
            Hide();

        Log.Write("Dock hidden while the native taskbar is active.");
    }

    /// <summary>Brings the dock back when taskbar replacement is enabled again.</summary>
    public void ShowForTaskbarReplace()
    {
        _hiddenForTaskbarRestore = false;
        _userHidden = false;

        if (!IsVisible)
            Show();

        _hidden = false;
        Reposition();
        UpdateBackdrop();

        // Showing the bar puts it at the top of the topmost band, above the
        // island that is docked inside it. Restore the island above the bar.
        App.Instance?.RaiseIslandAboveDock();
    }

    private void CloseOpenTooltips()
    {
        try
        {
            foreach (PresentationSource source in PresentationSource.CurrentSources)
            {
                if (source.RootVisual is FrameworkElement root && root.TemplatedParent is ToolTip tip)
                    tip.IsOpen = false;
            }
        }
        catch
        {
            // Tooltips also disappear when their owner window is hidden.
        }
    }

    public void ToggleUserHidden()
    {
        if (!IsVisible)
        {
            ShowForTaskbarReplace();
            return;
        }

        _userHidden = !_userHidden;
        if (_userHidden)
            HideDock();
        else
            ShowDock();
    }

    private void OnStartClick(object sender, RoutedEventArgs e) => App.Instance?.ToggleStartPanel();

    private void OnSearchClick(object sender, RoutedEventArgs e) => App.Instance?.ShowLauncher();

    private void OnWidgetsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowWidgets();

    private void OnControlsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowControls();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowSettings();

    private void OnTabsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowGroupPicker();

    private void OnTaskbarWidgetClick(object sender, RoutedEventArgs e)
    {
        // The mouse-up that ends a reorder drag is not a click on the widget.
        if (_suppressWidgetClick)
        {
            _suppressWidgetClick = false;
            return;
        }

        if (sender is not FrameworkElement { DataContext: TaskbarWidgetViewModel widget })
            return;

        try
        {
            RunQuickAction(widget.Id);
        }
        catch (Exception ex)
        {
            // A missing optional app (Windows Terminal, Settings URI) must not
            // take the bar down; the next click simply tries again.
            Log.Write($"Taskbar widget action '{widget.Id}' failed: {ex.Message}");
        }
    }

    private void OnDockButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DockButtonViewModel button })
            return;

        try
        {
            RunQuickAction(button.Id);
        }
        catch (Exception ex)
        {
            Log.Write($"Dock button '{button.Id}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Esegue una voce del catalogo. Widget della barra e pulsanti della dock
    /// passano entrambi di qui: una sola implementazione per ogni funzione, così
    /// le due superfici non possono divergere.
    /// </summary>
    /// <summary>Voce pubblica per la barra intelligente, che vive in un'altra finestra.</summary>
    public void RunDockAction(string id) => RunQuickAction(id);

    private void RunQuickAction(string id)
    {
        switch (id)
        {
            case "search":
                App.Instance?.ShowLauncher();
                break;
            case "clock":
            case "date":
            case "widgets":
            case "cpu":
            case "ram":
            case "stats":
            case "ramdetail":
            case "disk":
            case "uptime":
            case "battery":
                // Every read-only system widget opens the panel with the
                // full detail instead of doing nothing.
                App.Instance?.ShowWidgets();
                break;
            case "network":
            case "wifi":
            case "bluetooth":
                App.Instance?.ShowNetworkPanel();
                break;
            case "controls":
            case "brightness":
                App.Instance?.ShowControls();
                break;
            case "volume":
                _vm.Controls.ToggleMute(out _, out _);
                _vm.Controls.RefreshVolumeOnly();
                _vm.UpdateTaskbarWidgetValues();
                break;
            case "tabs":
                App.Instance?.ShowGroupPicker();
                break;
            case "settings":
                App.Instance?.ShowSettings();
                break;
            case "media":
                App.Instance?.ToggleMediaPlayback();
                break;
            case "notifications":
                _vm.Controls.SendWinShortcut(0x4E); // Win+N
                break;
            case "quicksettings":
                _vm.Controls.SendWinShortcut(0x41); // Win+A
                break;
            case "taskview":
                _vm.Controls.SendWinShortcut(0x09); // Win+Tab
                break;
            case "showdesktop":
                _vm.Controls.SendWinShortcut(0x44); // Win+D
                break;
            case "screenshot":
                _vm.Controls.SendWinShortcut(0x53, shift: true); // Win+Shift+S
                break;
            case "clipboard":
                _vm.Controls.SendWinShortcut(0x56); // Win+V
                break;
            case "emoji":
                _vm.Controls.SendWinShortcut(0xBE); // Win+.
                break;
            case "lock":
                _vm.Controls.SendWinShortcut(0x4C); // Win+L
                break;
            case "explorer":
                OpenShellTarget("explorer.exe");
                break;
            case "terminal":
                OpenTerminal();
                break;
            case "taskmgr":
                OpenShellTarget("taskmgr.exe");
                break;
            case "recycle":
                OpenShellTarget("shell:RecycleBinFolder");
                break;
        }
    }

    /// <summary>
    /// Widgets are reordered by dragging them along the bar: pressing one
    /// records the pointer, a real movement starts the drop, and the widget
    /// lands in front of the one under the cursor.
    /// </summary>
    private void OnTaskbarWidgetDragStart(object sender, MouseButtonEventArgs e)
    {
        _widgetDragSource = null;
        _suppressWidgetClick = false;

        if (e.ClickCount != 1 ||
            sender is not FrameworkElement { DataContext: TaskbarWidgetViewModel widget })
            return;

        _widgetDragStart = e.GetPosition(this);
        _widgetDragSource = widget;
    }

    private void OnTaskbarWidgetDragMove(object sender, MouseEventArgs e)
    {
        if (_widgetDragging || _widgetDragSource is null ||
            e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _widgetDragStart.X) < 6 &&
            Math.Abs(current.Y - _widgetDragStart.Y) < 6)
            return;

        TaskbarWidgetViewModel source = _widgetDragSource;
        _widgetDragSource = null;
        _widgetDragging = true;
        _suppressWidgetClick = true;

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject(TaskbarWidgetDragFormat, source.Id), DragDropEffects.Move);
        }
        finally
        {
            _widgetDragging = false;
            if (sender is FrameworkElement element)
                element.Opacity = 1;
        }
    }

    private void OnTaskbarWidgetDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TaskbarWidgetDragFormat))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        if (sender is FrameworkElement element)
            element.Opacity = 0.55;
    }

    private void OnTaskbarWidgetDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.Opacity = 1;
    }

    private void OnTaskbarWidgetDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TaskbarWidgetViewModel target } element)
        {
            element.Opacity = 1;

            if (e.Data.GetDataPresent(TaskbarWidgetDragFormat) &&
                e.Data.GetData(TaskbarWidgetDragFormat) is string fromId)
            {
                e.Handled = true;
                _vm.MoveTaskbarWidget(fromId, target.Id);
            }
        }

        _widgetDragSource = null;
    }

    /// <summary>
    /// Expanded widgets grow a little under the pointer. The transform lives on
    /// the content grid and only runs when the widget is not in icon mode.
    /// </summary>
    private void OnTaskbarWidgetPointerEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { DataContext: TaskbarWidgetViewModel widget } button &&
            widget.IsExpandedMode &&
            widget.Kind != TaskbarWidgetKind.Separator)
            AnimateWidgetScale(button, 1.16);
    }

    private void OnTaskbarWidgetPointerLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button button)
            AnimateWidgetScale(button, 1.0);
    }

    private void AnimateWidgetScale(Button button, double target)
    {
        if (button.Content is not FrameworkElement { RenderTransform: ScaleTransform scale })
            return;

        Panel.SetZIndex(button, target > 1 ? 20 : 0);

        if (!_vm.Settings.AnimationsEnabled)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = target;
            scale.ScaleY = target;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(140);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(target, duration) { EasingFunction = ease });
    }

    private void OnWidgetScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.ScrollableWidth <= 0)
            return;

        viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private static void OpenShellTarget(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private void OpenTerminal()
    {
        try
        {
            OpenShellTarget("wt.exe");
        }
        catch
        {
            OpenShellTarget("powershell.exe");
        }
    }

    private void OnShellMouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.Settings.LiquidSheen || Shell.ActualWidth <= 0)
            return;

        double x = e.GetPosition(Shell).X - LiquidSheen.Width / 2;
        SheenShift.X = Math.Clamp(x, 0, Math.Max(0, Shell.ActualWidth - LiquidSheen.Width));
    }

    private void OnShellMouseLeave(object sender, MouseEventArgs e)
    {
        CenterLiquidSheen(_vm.Settings.AnimationsEnabled);
    }

    private void CenterLiquidSheen(bool animated)
    {
        if (Shell.ActualWidth <= 0)
            return;

        double target = Math.Max(0, (Shell.ActualWidth - LiquidSheen.Width) / 2);
        if (!animated)
        {
            SheenShift.BeginAnimation(TranslateTransform.XProperty, null);
            SheenShift.X = target;
            return;
        }

        SheenShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(target, TimeSpan.FromSeconds(
                0.35 / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    /// <summary>
    /// The bar and a docked island are both topmost windows: a click on the bar
    /// can lift it above the capsule, which then looks like the island simply
    /// disappeared. Put the island back on top straight after the click.
    /// </summary>
    private void OnShellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HostsIsland)
            Dispatcher.InvokeAsync(() => App.Instance?.RaiseIslandAboveDock(), DispatcherPriority.Input);
    }

    /// <summary>Handle of the bar window, used to stack the island right above it.</summary>
    public IntPtr NativeHandle => _hwndSource?.Handle ?? IntPtr.Zero;

    /// <summary>
    /// The bar's real screen rectangle in physical pixels. Windows can grant an
    /// AppBar strip that is not flush with the monitor edge (the native bar still
    /// claims the bottom band for a moment), so the docked capsule must follow
    /// this rectangle instead of the edge the user selected. It is deliberately
    /// device pixels: converting it with this bar's DPI and then again with the
    /// capsule's put the capsule on the wrong monitor on mixed-DPI setups.
    /// </summary>
    public bool TryGetBarScreenRect(out Rect rect)
    {
        rect = Rect.Empty;
        if (Shell is null || !IsVisible || Shell.ActualWidth < 1 || Shell.ActualHeight < 1)
            return false;

        try
        {
            Point topLeft = Shell.PointToScreen(new Point(0, 0));
            Point bottomRight = Shell.PointToScreen(new Point(Shell.ActualWidth, Shell.ActualHeight));

            double width = Math.Max(0, bottomRight.X - topLeft.X);
            double height = Math.Max(0, bottomRight.Y - topLeft.Y);
            if (width < 1 || height < 1)
                return false;

            rect = new Rect(topLeft.X, topLeft.Y, width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Screen X in physical pixels where the docked capsule must be centred: the
    /// empty slot the bar reserves right next to the Start button. Only the bar
    /// that actually hosts the capsule answers.
    /// </summary>
    public bool TryGetIslandSlotCenter(out double centerX)
    {
        centerX = 0;
        if (!HostsIsland || IslandSlot is null || IslandSlot.Visibility != Visibility.Visible ||
            !IsVisible || IslandSlot.ActualWidth < 1)
            return false;

        try
        {
            Point point = IslandSlot.PointToScreen(
                new Point(IslandSlot.ActualWidth / 2, IslandSlot.ActualHeight / 2));
            centerX = point.X;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnDockItemEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not DockItemViewModel item)
            return;

        if (_vm.Settings.HoverMagnify)
        {
            // The icon grows above its neighbours; the bar no longer clips it
            // and the higher Z-order keeps it in front of the other buttons.
            Panel.SetZIndex(button, 10);
            AnimateItem(button, 1.35, 0.18, true);
        }

        // The previews replace the tooltip for a group: two popups on top of
        // each other would cover the thumbnails.
        if (item.Group is { Windows.Count: > 1 })
            ToolTipService.SetIsEnabled(button, false);

        ScheduleWindowPreview(button, item);
    }

    private void OnDockItemLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Button button)
            return;

        Panel.SetZIndex(button, 0);
        AnimateItem(button, 1, 0.18, false);
        ToolTipService.SetIsEnabled(button, true);

        if (ReferenceEquals(_previewAnchor, button))
        {
            _previewShowTimer.Stop();
            _previewHideTimer.Start();
        }
    }

    /// <summary>
    /// Waits for the pointer to rest on an icon whose app has more than one
    /// window, then opens the per-window previews. A single-window app keeps the
    /// normal click behaviour and no flyout.
    /// </summary>
    private void ScheduleWindowPreview(Button button, DockItemViewModel item)
    {
        _previewHideTimer.Stop();

        if (item.Group is not { Windows.Count: > 1 })
        {
            _previewShowTimer.Stop();
            CloseWindowPreview();
            return;
        }

        if (_preview is { IsVisible: true } && ReferenceEquals(_previewAnchor, button))
            return;

        _previewShowTimer.Stop();
        _previewAnchor = button;
        _previewItem = item;
        _previewShowTimer.Start();
    }

    private void ShowWindowPreview()
    {
        if (!IsVisible || _hidden || _previewAnchor is not { IsMouseOver: true } anchor ||
            _previewItem is not { } item || item.Group is not { Windows.Count: > 1 } group)
            return;

        CloseWindowPreview(keepAnchor: true);

        try
        {
            var cards = new List<WindowPreviewItem>();
            foreach (TrackedWindow window in group.Windows.Take(6))
            {
                cards.Add(new WindowPreviewItem
                {
                    Handle = window.Handle,
                    Title = string.IsNullOrWhiteSpace(window.Title) ? item.Name : window.Title,
                    Icon = item.Icon
                });
            }

            if (cards.Count < 2)
                return;

            _preview = new WindowPreviewWindow(
                cards,
                handle => _vm.Tracker.ActivateWindow(handle),
                () => _previewAnchor?.IsMouseOver == true)
            {
                ShowActivated = false
            };
            _preview.Closed += (_, _) => _preview = null;
            _preview.Show();

            double dpiScale = Math.Max(0.5, VisualTreeHelper.GetDpi(anchor).DpiScaleX);
            Point origin = anchor.PointToScreen(new Point(0, 0));
            var anchorRect = new Rect(
                origin.X / dpiScale,
                origin.Y / dpiScale,
                anchor.ActualWidth,
                anchor.ActualHeight);
            Rect screen = ScreenGeometry.BoundsDip(ScreenGeometry.Target(MonitorIndex));
            _preview.Place(anchorRect, screen, above: !_vm.Settings.DockOnTop);

            // The flyout is already on screen with placeholders; each picture
            // arrives from a worker so an unresponsive app cannot stall the bar.
            _ = LoadPreviewThumbnailsAsync(_preview, cards);
        }
        catch (Exception ex)
        {
            Log.Write("Window preview failed: " + ex.Message);
            CloseWindowPreview();
        }
    }

    private async Task LoadPreviewThumbnailsAsync(
        WindowPreviewWindow preview, IReadOnlyList<WindowPreviewItem> items)
    {
        foreach (WindowPreviewItem item in items)
        {
            try
            {
                IntPtr handle = item.Handle;
                var image = await Task.Run(() => WindowThumbnail.Capture(handle, 170, 102));
                if (!ReferenceEquals(_preview, preview))
                    return;

                item.Thumbnail = image;
            }
            catch (Exception ex)
            {
                Log.Write("Preview thumbnail failed: " + ex.Message);
            }
        }
    }

    private void CloseWindowPreview(bool keepAnchor = false)
    {
        _previewShowTimer.Stop();
        _previewHideTimer.Stop();
        if (!keepAnchor)
        {
            _previewAnchor = null;
            _previewItem = null;
        }

        if (_preview is null)
            return;

        WindowPreviewWindow preview = _preview;
        _preview = null;
        try
        {
            preview.Close();
        }
        catch
        {
            // The flyout may already be closing with the bar.
        }
    }

    private void AnimateItem(Button button, double to, double seconds, bool elastic)
    {
        if (button.Template.FindName("ItemScale", button) is not ScaleTransform scale)
            return;

        TranslateTransform? lift = button.Template.FindName("ItemLift", button) as TranslateTransform;
        if (!_vm.Settings.AnimationsEnabled)
        {
            scale.ScaleX = to;
            scale.ScaleY = to;
            if (lift is not null)
                lift.Y = to > 1 ? -10 : 0;
            return;
        }

        double adjusted = seconds / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);
        IEasingFunction easing = elastic
            ? new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(adjusted)) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);

        if (lift is not null)
        {
            lift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(to > 1 ? -10 : 0, TimeSpan.FromSeconds(adjusted))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    /// <summary>Staggered entrance for the dock icons; skipped when motion is off.</summary>
    /// <summary>
    /// Le icone che arrivano dopo (un'app appena aperta, un pin aggiunto) entrano
    /// con la stessa dissolvenza invece di apparire di colpo accanto alle altre.
    /// </summary>
    private void OnDockItemsChanged()
    {
        Dispatcher.InvokeAsync(AnimateNewDockItems, DispatcherPriority.Loaded);
    }

    private void AnimateNewDockItems()
    {
        if (DockItems is null || !IsVisible)
            return;

        DockItems.UpdateLayout();
        double speed = Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);

        foreach (DockItemViewModel item in _vm.Items)
        {
            if (_shownItemKeys.Contains(item.Key))
                continue;

            if (DockItems.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container)
                continue;

            _shownItemKeys.Add(item.Key);
            if (!_vm.Settings.AnimationsEnabled)
                continue;

            container.Opacity = 0;
            var shift = new TranslateTransform(0, 6);
            container.RenderTransform = shift;
            container.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150 / speed))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(190 / speed))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private void OnDockBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source) is not null)
            return;

        e.Handled = true;
        var menu = new ContextMenu();

        menu.Items.Add(MenuFactory.Item("Raggruppa finestre in schede…", "\uE7C4",
            () => App.Instance?.ShowGroupPicker()));
        menu.Items.Add(MenuFactory.Item("Cerca app e finestre…", "\uE721",
            () => App.Instance?.ShowLauncher()));
        menu.Items.Add(MenuFactory.Item("Apri widget", "\uE71D",
            () => App.Instance?.ShowWidgets()));
        menu.Items.Add(MenuFactory.Item("Apri controlli rapidi", "\uE945",
            () => App.Instance?.ShowControls()));

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuFactory.Item("Impostazioni Nebula", "\uE713",
            () => App.Instance?.ShowSettings()));

        menu.PlacementTarget = Shell;
        menu.IsOpen = true;
    }

    private static Button? FindButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button button)
                return button;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        // The drop that ends a drag must not count as a click and launch the app.
        if (_suppressItemClick)
        {
            _suppressItemClick = false;
            return;
        }

        if (sender is not FrameworkElement element || element.DataContext is not DockItemViewModel item)
            return;

        CloseWindowPreview();
        Pulse(element);

        if (item.Group is { Windows.Count: > 0 } group)
        {
            if (group.Windows.Count == 1)
            {
                _vm.Tracker.ActivateOrMinimize(group);
                return;
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            int index = group.Windows.FindIndex(w => w.Handle == foreground);
            TrackedWindow next = index >= 0
                ? group.Windows[(index + 1) % group.Windows.Count]
                : group.Windows[0];
            _vm.Tracker.ActivateWindow(next.Handle);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.LaunchPath))
            Launch(item.LaunchPath, MonitorIndex);
    }

    /// <summary>
    /// Anchors must be draggable: pressing an icon records the pointer, and a
    /// real drag (not a click) starts the reordering drag.
    /// </summary>
    private void OnItemDragStart(object sender, MouseButtonEventArgs e)
    {
        _suppressItemClick = false;
        _itemDragSource = null;

        if (e.ClickCount != 1 || sender is not FrameworkElement { DataContext: DockItemViewModel item })
            return;

        _itemDragSource = item;
        _itemDragStart = e.GetPosition(this);
    }

    private void OnItemDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _itemDragSource is null || _itemDragging)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _itemDragStart.X) < 6 && Math.Abs(current.Y - _itemDragStart.Y) < 6)
            return;

        DockItemViewModel source = _itemDragSource;
        _itemDragSource = null;
        _itemDragging = true;
        _suppressItemClick = true;

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject(DockItemDragFormat, source.Key), DragDropEffects.Move);
        }
        finally
        {
            _itemDragging = false;
            if (sender is FrameworkElement element)
                element.Opacity = 1;
        }
    }

    private void OnItemDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DockItemDragFormat))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;

        // The icon under the pointer reads as the drop target.
        if (sender is FrameworkElement element)
            element.Opacity = 0.55;
    }

    private void OnItemDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement element)
            element.Opacity = 1;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DockItemViewModel target } element)
        {
            element.Opacity = 1;

            if (e.Data.GetDataPresent(DockItemDragFormat) &&
                e.Data.GetData(DockItemDragFormat) is string fromKey)
            {
                e.Handled = true;
                _vm.MoveItem(fromKey, target.Key);
            }
        }

        _itemDragSource = null;
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseWindowPreview();
        if (sender is not FrameworkElement element || element.DataContext is not DockItemViewModel item)
            return;

        var menu = new ContextMenu();
        int monitor = MonitorIndex;

        if (item.Group is { } group)
        {
            // The windows of the app come first, each with the app's own icon:
            // the list reads as "these are the open windows", then the actions.
            foreach (TrackedWindow window in group.Windows.Take(6))
            {
                var entry = new MenuItem
                {
                    Header = window.Title.Length > 46 ? window.Title[..46] + "…" : window.Title,
                    Icon = item.Icon is { } appIcon
                        ? new Image { Source = appIcon, Width = 16, Height = 16 }
                        : MenuFactory.Icon("\uE737")
                };
                entry.Click += (_, _) => _vm.Tracker.ActivateWindow(window.Handle);
                menu.Items.Add(entry);
            }

            if (group.Windows.Count > 1)
                menu.Items.Add(MenuFactory.Item("Riduci a icona tutte", "\uE921",
                    () => _vm.Tracker.MinimizeAll(group)));

            menu.Items.Add(MenuFactory.Item("Raggruppa in schede…", "\uE7C4",
                () => App.Instance?.ShowGroupPicker(group.Windows.Select(w => w.Handle))));

            menu.Items.Add(new Separator());

            menu.Items.Add(MenuFactory.Item("Chiudi le finestre", "\uE8BB",
                () => _vm.Tracker.CloseAll(group)));

            // Ends the app the way Task Manager does: close request first, then
            // terminate the processes that ignore it.
            menu.Items.Add(MenuFactory.Item("Termina attività", "\uE7BA",
                () => _vm.Tracker.EndTask(group)));
        }
        else
        {
            menu.Items.Add(MenuFactory.Item("Apri", "\uE8A7",
                () => Launch(item.LaunchPath, monitor)));
        }

        if (!string.IsNullOrWhiteSpace(item.LaunchPath) || !string.IsNullOrWhiteSpace(item.TargetPath))
        {
            string path = item.LaunchPath;

            menu.Items.Add(MenuFactory.Item("Apri come amministratore", "\uE7EF", () =>
            {
                AppActions.RunAsAdmin(path, out string? error);
                Report(error);
            }));

            menu.Items.Add(MenuFactory.Item("Apri percorso file", "\uE838", () =>
            {
                string target = item.TargetPath ?? path;
                AppLauncher.RevealInExplorer(target, out string? error);
                Report(error);
            }));

            menu.Items.Add(MenuFactory.Item("Apri con…", "\uE8E5", () =>
            {
                AppActions.OpenWith(path, out string? error);
                Report(error);
            }));
        }

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuFactory.Item(
            item.IsPinned ? "Rimuovi dalla taskbar" : "Fissa alla taskbar",
            item.IsPinned ? "\uE77A" : "\uE718",
            () =>
            {
                if (item.IsPinned)
                    _vm.Settings.PinnedPaths.RemoveAll(p =>
                        string.Equals(p, item.LaunchPath, StringComparison.OrdinalIgnoreCase));
                else if (!string.IsNullOrWhiteSpace(item.LaunchPath))
                    _vm.Settings.PinnedPaths.Add(item.LaunchPath);

                _store.MarkDirty();
                _vm.RefreshItems();
            }));

        // Destructive action last, on its own, so it is never clicked by reflex.
        if (!string.IsNullOrWhiteSpace(item.LaunchPath) || !string.IsNullOrWhiteSpace(item.TargetPath))
        {
            string uninstallPath = item.LaunchPath;
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuFactory.Item("Disinstalla", "\uE74D", () =>
            {
                if (MessageBox.Show($"Disinstallare “{item.Name}”?", "Nebula",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                AppActions.Uninstall(item.TargetPath ?? uninstallPath, item.Name, out string? error);
                Report(error);
            }));
        }

        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    /// <summary>Shows a shell failure in the tray instead of a blocking dialog.</summary>
    private static void Report(string? error)
    {
        if (!string.IsNullOrWhiteSpace(error))
            Log.Write("Dock action: " + error);
    }

    private void Pulse(FrameworkElement element)
    {
        if (!_vm.Settings.LaunchPulse || !_vm.Settings.AnimationsEnabled)
            return;

        if (element is not Button button || button.Template.FindName("ItemScale", button) is not ScaleTransform scale)
            return;

        var animation = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(340))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut }
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void Launch(string path, int monitor)
    {
        // A bar on a specific screen opens the app there, even when the monitor
        // that would normally host the new window is another one.
        if (AppLauncher.LaunchOnMonitor(path, monitor, out string? error))
        {
            App.Instance?.RecordRecent(path);
            return;
        }

        Log.Write($"Launch failed for '{path}': {error}");
        MessageBox.Show(error ?? "Impossibile aprire l'app.", "Nebula",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
