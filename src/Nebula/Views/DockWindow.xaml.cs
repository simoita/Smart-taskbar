using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    private readonly DockViewModel _vm;
    private readonly SettingsStore _store;
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
    private Rect _workAreaDip;
    private System.Drawing.Rectangle _physicalWorkArea;
    private System.Drawing.Rectangle _screenBounds;

    public DockWindow(DockViewModel vm, SettingsStore store)
    {
        _vm = vm;
        _store = store;

        InitializeComponent();
        DataContext = vm;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        DpiChanged += (_, _) => Reposition();
        Closed += (_, _) =>
        {
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
        };
        _vm.Settings.PropertyChanged += OnSettingsChanged;

        _watchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _watchTimer.Tick += OnWatchTick;
        _watchTimer.Start();

        _backdropTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _backdropTimer.Tick += (_, _) =>
        {
            _backdropTimer.Stop();
            UpdateBackdrop();
        };
    }

    public bool IsHidden => _hidden;
    public bool IsAppBarRegistered => _appBar.IsRegistered;
    public bool AppBarApplySucceeded { get; private set; }

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

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyZoneLayout();
        Reposition();
        UpdateBackdrop();
        Dispatcher.InvokeAsync(UpdateAppBar, DispatcherPriority.Loaded);

        if (!_vm.Settings.AnimationsEnabled)
        {
            Root.Opacity = 1;
            DockSlide.Y = 0;
            return;
        }

        double seconds = 0.45 / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);

        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        DockSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(24, 0, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_hidden && !_animating)
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
            Root.Margin = new Thickness(side, ShadowTop, side, ShadowBottom);
        }
        else if (Math.Abs(Root.Margin.Left - 26) > 0.5)
        {
            Root.Margin = new Thickness(26, ShadowTop, 26, ShadowBottom);
        }

        Left = work.Left + (work.Width - ActualWidth) / 2;
        if (!_hidden)
        {
            BeginAnimation(TopProperty, null);
            Top = VisibleTop();
        }

        UpdateAppBar();
    }

    private void RefreshScreenGeometry()
    {
        System.Windows.Forms.Screen screen = ScreenGeometry.Target(_vm.Settings.MonitorIndex);
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

        double height = Math.Max(28, ActualHeight - ShadowTop - ShadowBottom + _vm.Settings.EdgeMargin);
        AppBarApplySucceeded = _appBar.Apply(this, _vm.Settings.DockOnTop, height, _vm.Settings.MonitorIndex);
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
            or nameof(Models.AppSettings.TaskbarFullWidth))
        {
            ApplyZoneLayout();
            Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(Models.AppSettings.ReserveTaskbarSpace)
            or nameof(Models.AppSettings.ReplaceWindowsTaskbar)
            or nameof(Models.AppSettings.DockOnTop)
            or nameof(Models.AppSettings.MonitorIndex)
            or nameof(Models.AppSettings.EdgeMargin)
            or nameof(Models.AppSettings.DockHeight))
        {
            Dispatcher.InvokeAsync(Reposition, DispatcherPriority.Background);
        }
    }

    /// <summary>Rebuilds the dock from independent app/system/action zones.</summary>
    private void ApplyZoneLayout()
    {
        if (ZoneHost is null || AppsZone is null || SystemZone is null || ActionsZone is null)
            return;

        var settings = _vm.Settings;
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

        var entries = new List<(FrameworkElement Zone, Border? Divider)>();
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
            if (!first && settings.DockZoneDividers)
            {
                divider = new Border
                {
                    Width = 1,
                    Height = 24,
                    Margin = new Thickness(settings.DockZoneSpacing / 2, 0, settings.DockZoneSpacing / 2, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                divider.SetResourceReference(BackgroundProperty, "C.StrokeSoft");
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
            entries.Add((zone, divider));
            first = false;
        }

        // Reparenting a zone is only legal after detaching it from the previous
        // container; otherwise WPF throws "element already has a logical parent".
        foreach ((FrameworkElement zone, Border? divider) in entries)
        {
            if (zone.Parent is Panel zoneParent)
                zoneParent.Children.Remove(zone);
            if (divider?.Parent is Panel dividerParent)
                dividerParent.Children.Remove(divider);
        }

        ZoneHost.Children.Clear();
        ZoneHost.ColumnDefinitions.Clear();

        bool fullWidth = settings.ReplaceWindowsTaskbar && settings.TaskbarFullWidth;

        var containers = new List<StackPanel>();
        for (int i = 0; i < entries.Count; i++)
        {
            (FrameworkElement zone, Border? divider) = entries[i];
            var container = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (divider is not null)
                container.Children.Add(divider);
            container.Children.Add(zone);

            if (!fullWidth)
                container.Margin = i == 0
                    ? new Thickness(0)
                    : new Thickness(settings.DockZoneSpacing / 2, 0, 0, 0);
            else if (entries.Count >= 3 && i == 1)
                container.Margin = new Thickness(settings.DockZoneSpacing, 0, settings.DockZoneSpacing, 0);

            containers.Add(container);
        }

        if (!fullWidth || containers.Count == 0)
        {
            for (int i = 0; i < containers.Count; i++)
            {
                ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(containers[i], i);
                ZoneHost.Children.Add(containers[i]);
            }

            return;
        }

        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ZoneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (containers.Count == 1)
        {
            Grid.SetColumn(containers[0], 0);
            Grid.SetColumnSpan(containers[0], 3);
            containers[0].HorizontalAlignment = HorizontalAlignment.Center;
            ZoneHost.Children.Add(containers[0]);
            return;
        }

        Grid.SetColumn(containers[0], 0);
        ZoneHost.Children.Add(containers[0]);

        if (containers.Count == 2)
        {
            Grid.SetColumn(containers[1], 2);
            containers[1].HorizontalAlignment = HorizontalAlignment.Right;
            ZoneHost.Children.Add(containers[1]);
            return;
        }

        Grid.SetColumn(containers[1], 1);
        containers[1].HorizontalAlignment = HorizontalAlignment.Center;
        ZoneHost.Children.Add(containers[1]);

        Grid.SetColumn(containers[2], 2);
        containers[2].HorizontalAlignment = HorizontalAlignment.Right;
        ZoneHost.Children.Add(containers[2]);
    }

    private double VisibleTop()
    {
        Rect work = CurrentWorkArea();
        double margin = _vm.Settings.EdgeMargin;
        return _vm.Settings.DockOnTop
            ? work.Top - ShadowTop + margin
            : work.Bottom + ShadowBottom - margin - ActualHeight;
    }

    private double HiddenTop()
    {
        Rect work = CurrentWorkArea();
        return _vm.Settings.DockOnTop
            ? work.Top - ActualHeight - 48
            : work.Bottom + 40;
    }

    private void UpdateBackdrop() =>
        _vm.SetDockSize(Math.Max(1, ActualWidth - 52), Math.Max(1, ActualHeight - ShadowTop - ShadowBottom));

    private void OnWatchTick(object? sender, EventArgs e)
    {
        if (_hiddenForTaskbarRestore)
            return;

        _tick++;
        bool fullscreen = false;

        if (_vm.Settings.HideInFullscreen && (_tick % 4 == 0 || _hidden))
        {
            try
            {
                if (_screenBounds.Width < 1)
                    RefreshScreenGeometry();

                fullscreen = _vm.Tracker.IsForegroundFullscreen(_screenBounds);
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

        bool nearEdge = IsCursorNearEdge();
        bool shouldHide = _userHidden || fullscreen ||
                          (_vm.Settings.AutoHideDock && !IsMouseOver && !nearEdge);

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
        double seconds = 0.26 / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);
        var animation = new DoubleAnimation(Top, target, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
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

    private void OnStartClick(object sender, RoutedEventArgs e) => App.Instance?.ShowLauncher();

    private void OnSearchClick(object sender, RoutedEventArgs e) => App.Instance?.ShowLauncher();

    private void OnWidgetsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowWidgets();

    private void OnControlsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowControls();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowSettings();

    private void OnTabsClick(object sender, RoutedEventArgs e) => App.Instance?.ShowGroupPicker();

    private void OnTaskbarWidgetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarWidgetViewModel widget })
            return;

        switch (widget.Id)
        {
            case "search":
                App.Instance?.ShowLauncher();
                break;
            case "clock":
                App.Instance?.ShowWidgets();
                break;
            case "network":
            case "controls":
                App.Instance?.ShowControls();
                break;
            case "widgets":
                App.Instance?.ShowWidgets();
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
        }
    }

    private void OnDockItemEnter(object sender, MouseEventArgs e)
    {
        if (!_vm.Settings.HoverMagnify || sender is not Button button)
            return;

        Panel.SetZIndex(button, 1);
        AnimateItem(button, 1.13, 0.16, true);
    }

    private void OnDockItemLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Button button)
            return;

        Panel.SetZIndex(button, 0);
        AnimateItem(button, 1, 0.18, false);
    }

    private void AnimateItem(Button button, double to, double seconds, bool elastic)
    {
        if (button.Template.FindName("ItemScale", button) is not ScaleTransform scale)
            return;

        if (!_vm.Settings.AnimationsEnabled)
        {
            scale.ScaleX = to;
            scale.ScaleY = to;
            return;
        }

        double adjusted = seconds / Math.Clamp(_vm.Settings.AnimationSpeed, 0.5, 2);
        IEasingFunction easing = elastic
            ? new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var animation = new DoubleAnimation(to, TimeSpan.FromSeconds(adjusted)) { EasingFunction = easing };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private void OnDockBackgroundRightClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source) is not null)
            return;

        e.Handled = true;
        var menu = new ContextMenu();

        var group = new MenuItem { Header = "Raggruppa finestre in schede…" };
        group.Click += (_, _) => App.Instance?.ShowGroupPicker();
        menu.Items.Add(group);

        var search = new MenuItem { Header = "Cerca app e finestre…" };
        search.Click += (_, _) => App.Instance?.ShowLauncher();
        menu.Items.Add(search);

        var widgets = new MenuItem { Header = "Apri widget" };
        widgets.Click += (_, _) => App.Instance?.ShowWidgets();
        menu.Items.Add(widgets);

        var controls = new MenuItem { Header = "Apri controlli rapidi" };
        controls.Click += (_, _) => App.Instance?.ShowControls();
        menu.Items.Add(controls);

        menu.Items.Add(new Separator());

        var settings = new MenuItem { Header = "Impostazioni Nebula" };
        settings.Click += (_, _) => App.Instance?.ShowSettings();
        menu.Items.Add(settings);

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
        if (sender is not FrameworkElement element || element.DataContext is not DockItemViewModel item)
            return;

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
            Launch(item.LaunchPath);
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement element || element.DataContext is not DockItemViewModel item)
            return;

        var menu = new ContextMenu();

        if (item.Group is { } group)
        {
            var tabs = new MenuItem { Header = "Raggruppa schede…" };
            tabs.Click += (_, _) => App.Instance?.ShowGroupPicker(group.Windows.Select(w => w.Handle));
            menu.Items.Add(tabs);
            menu.Items.Add(new Separator());

            foreach (TrackedWindow window in group.Windows.Take(6))
            {
                var entry = new MenuItem
                {
                    Header = window.Title.Length > 46 ? window.Title[..46] + "…" : window.Title
                };
                entry.Click += (_, _) => _vm.Tracker.ActivateWindow(window.Handle);
                menu.Items.Add(entry);
            }

            if (group.Windows.Count > 1)
            {
                var minimize = new MenuItem { Header = "Riduci a icona tutte" };
                minimize.Click += (_, _) => _vm.Tracker.MinimizeAll(group);
                menu.Items.Add(minimize);
            }

            var close = new MenuItem { Header = "Chiudi" };
            close.Click += (_, _) => _vm.Tracker.CloseAll(group);
            menu.Items.Add(close);
        }
        else
        {
            var open = new MenuItem { Header = "Apri" };
            open.Click += (_, _) => Launch(item.LaunchPath);
            menu.Items.Add(open);
        }

        var pin = new MenuItem { Header = item.IsPinned ? "Rimuovi dalla dock" : "Fissa alla dock" };
        pin.Click += (_, _) =>
        {
            if (item.IsPinned)
                _vm.Settings.PinnedPaths.RemoveAll(p =>
                    string.Equals(p, item.LaunchPath, StringComparison.OrdinalIgnoreCase));
            else if (!string.IsNullOrWhiteSpace(item.LaunchPath))
                _vm.Settings.PinnedPaths.Add(item.LaunchPath);

            _store.MarkDirty();
            _vm.RefreshItems();
        };
        menu.Items.Add(pin);

        menu.PlacementTarget = element;
        menu.IsOpen = true;
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

    private static void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Launch failed for '{path}': {ex.Message}");
        }
    }
}
