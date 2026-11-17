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
    private readonly DispatcherTimer _watchTimer;
    private readonly DispatcherTimer _backdropTimer;
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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, (IntPtr)style);
        }
        catch (Exception ex)
        {
            Log.Write("Dock style setup failed: " + ex.Message);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyZoneLayout();
        Reposition();
        UpdateBackdrop();

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
        if (ActualWidth < 1 || ActualHeight < 1)
            return;

        Left = work.Left + (work.Width - ActualWidth) / 2;
        if (!_hidden)
        {
            BeginAnimation(TopProperty, null);
            Top = VisibleTop();
        }
    }

    private void RefreshScreenGeometry()
    {
        System.Windows.Forms.Screen screen = ScreenGeometry.Target(_vm.Settings.MonitorIndex);
        _workAreaDip = ScreenGeometry.WorkAreaDip(screen);
        _physicalWorkArea = screen.WorkingArea;
        _screenBounds = screen.Bounds;
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
            or nameof(Models.AppSettings.DockZoneDividers))
        {
            ApplyZoneLayout();
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

        string[] requested = (settings.DockZoneOrder ?? "apps,system,actions")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length != 3 || requested.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            requested.Any(key => !zones.ContainsKey(key)))
            requested = new[] { "apps", "system", "actions" };

        ZoneHost.Children.Clear();
        bool first = true;
        foreach (string key in requested)
        {
            bool visible = key switch
            {
                "apps" => settings.DockAppsZone,
                "system" => settings.DockSystemZone &&
                            (settings.ShowStats || settings.ShowBattery || settings.ShowClock),
                _ => settings.DockActionsZone &&
                     (settings.EnableTabs || settings.ShowSearch || settings.ShowWidgets ||
                      settings.ShowQuickControls || settings.ShowSettingsButton)
            };

            FrameworkElement zone = zones[key];
            zone.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                continue;

            if (!first && settings.DockZoneDividers)
            {
                var divider = new Border
                {
                    Width = 1,
                    Height = 24,
                    Margin = new Thickness(settings.DockZoneSpacing / 2, 0, settings.DockZoneSpacing / 2, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                divider.SetResourceReference(BackgroundProperty, "C.StrokeSoft");
                ZoneHost.Children.Add(divider);
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
            zone.Margin = first
                ? new Thickness(0)
                : new Thickness(settings.DockZoneSpacing / 2, 0, 0, 0);
            ZoneHost.Children.Add(zone);
            first = false;
        }
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

        return _vm.Settings.DockOnTop
            ? point.Y <= _physicalWorkArea.Top + 3
            : point.Y >= _physicalWorkArea.Bottom - 3;
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
