using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;
using Forms = System.Windows.Forms;

namespace Nebula.Views;

/// <summary>
/// Floating top-center capsule. It stays compact at rest and expands only on
/// pointer enter or click; no frame timer runs while idle. Clock and metrics are
/// bound to the existing DockViewModel/SystemMonitor, so the island does not add
/// another polling loop.
/// </summary>
public partial class IslandWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private readonly DockViewModel _dock;
    private readonly QuickControls _controls = new();
    private readonly DispatcherTimer _hideTimer;
    private HwndSource? _source;

    private bool _expanded;
    private bool _pinned;
    private double _compactWidth = 220;
    private double _compactHeight = 40;
    private double _expandedWidth = 400;
    private double _expandedHeight = 190;

    public IslandWindow(DockViewModel dock)
    {
        _dock = dock;
        InitializeComponent();
        DataContext = dock;

        _hideTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(320)
        };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_pinned && !Capsule.IsMouseOver)
                SetExpanded(false, true);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(PositionIsland, DispatcherPriority.Background);
        Closed += OnClosed;
        _dock.Settings.PropertyChanged += OnSettingsChanged;
        _dock.Theme.Changed += OnThemeChanged;
    }

    public bool IsExpanded => _expanded;
    public bool IsPinned => _pinned;
    public string AppliedStyleKey { get; private set; } = "capsule";
    public double CompactWidth => _compactWidth;
    public double CompactHeight => _compactHeight;
    public double ExpandedWidth => _expandedWidth;
    public double ExpandedHeight => _expandedHeight;
    public double CapsuleWidth => Capsule.ActualWidth;
    public double CapsuleHeight => Capsule.ActualHeight;
    public double CapsuleCornerRadius => Capsule.CornerRadius.TopLeft;
    public Brush? CapsuleBackground => Capsule.Background;

    public Rect CapsuleScreenRect
    {
        get
        {
            Point topLeft = Capsule.PointToScreen(new Point(0, 0));
            Point bottomRight = Capsule.PointToScreen(new Point(Capsule.ActualWidth, Capsule.ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
    }

    public bool IsPointInteractive(Point screenPoint)
    {
        if (!IsVisible || Capsule.ActualWidth < 1 || Capsule.ActualHeight < 1)
            return false;

        try
        {
            Point local = Capsule.PointFromScreen(screenPoint);
            return local.X >= 0 && local.Y >= 0 &&
                   local.X <= Capsule.ActualWidth && local.Y <= Capsule.ActualHeight;
        }
        catch
        {
            return false;
        }
    }

    public void ApplySettings()
    {
        ApplyAppearance();
        if (IsLoaded)
            PositionIsland();
    }

    /// <summary>Used by the self-test and capture harness; no animation.</summary>
    public void ExpandForTest()
    {
        _pinned = true;
        SetExpanded(true, false);
    }

    public void CollapseForTest()
    {
        _pinned = false;
        SetExpanded(false, false);
    }

    /// <summary>Used by the real-session capture harness to show the spring animation.</summary>
    public void ExpandAnimated()
    {
        _pinned = true;
        SetExpanded(true, true);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            long style = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            style |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, (IntPtr)style);

            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Log.Write("Island style setup failed: " + ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            try
            {
                System.Drawing.Point cursor = Forms.Cursor.Position;
                if (!IsPointInteractive(new Point(cursor.X, cursor.Y)))
                {
                    // The window is larger than the capsule for the spring
                    // animation. Outside the capsule the OS must pass the click
                    // to the window underneath, or the island would eat desktop
                    // clicks inside an invisible rectangle.
                    handled = true;
                    return (IntPtr)HTTRANSPARENT;
                }
            }
            catch
            {
                // Fall through to the default hit test.
            }
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAppearance();
        PositionIsland();
        AnimateIn();
    }

    private void AnimateIn()
    {
        if (!_dock.Settings.AnimationsEnabled)
        {
            Root.Opacity = 1;
            IslandSlide.Y = 0;
            return;
        }

        double seconds = 0.28 / Math.Clamp(_dock.Settings.AnimationSpeed, 0.5, 2);
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        IslandSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-8, 0, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ApplyAppearance()
    {
        AppSettings settings = _dock.Settings;
        string style = settings.IslandStyle;
        if (style is not ("capsule" or "pill" or "minimal"))
            style = "capsule";

        double scale = Math.Clamp(settings.IslandScale, 0.8, 1.3);
        _compactWidth = 220 * scale;
        _compactHeight = 40 * scale;
        _expandedWidth = 400 * scale;
        _expandedHeight = 190 * scale;
        AppliedStyleKey = style;

        Capsule.BeginAnimation(WidthProperty, null);
        Capsule.BeginAnimation(HeightProperty, null);
        Capsule.Width = _expanded ? _expandedWidth : _compactWidth;
        Capsule.Height = _expanded ? _expandedHeight : _compactHeight;

        double radius = style switch
        {
            "pill" => _expanded ? 30 * scale : _compactHeight / 2,
            "minimal" => (_expanded ? 14 : 10) * scale,
            _ => (_expanded ? 24 : 20) * scale
        };

        Capsule.CornerRadius = new CornerRadius(radius);
        IslandGlow.CornerRadius = new CornerRadius(radius);
        IslandGlow2.CornerRadius = new CornerRadius(radius);

        Capsule.Background = style == "minimal"
            ? FindBrush("C.Bg", Brushes.Black)
            : FindBrush("C.TooltipBg", Brushes.Black);
        Capsule.BorderBrush = style == "minimal"
            ? FindBrush("C.StrokeSoft", Brushes.Gray)
            : FindBrush("C.DockStroke", Brushes.White);
        Capsule.BorderThickness = new Thickness(style == "minimal" ? 1 : 1.2);

        bool glow = style != "minimal" && settings.ShowGlow;
        IslandGlow.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;
        IslandGlow2.Visibility = glow ? Visibility.Visible : Visibility.Collapsed;

        CompactView.Opacity = _expanded ? 0 : 1;
        ExpandedView.Opacity = _expanded ? 1 : 0;
        ExpandedView.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetExpanded(bool expand, bool animate)
    {
        if (_expanded == expand &&
            (expand ? ExpandedView.Visibility == Visibility.Visible : ExpandedView.Visibility != Visibility.Visible))
            return;

        _expanded = expand;
        _hideTimer.Stop();

        double width = expand ? _expandedWidth : _compactWidth;
        double height = expand ? _expandedHeight : _compactHeight;
        double radius = AppliedStyleKey switch
        {
            "pill" => expand ? 30 * Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3)
                             : _compactHeight / 2,
            "minimal" => (expand ? 14 : 10) * Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3),
            _ => (expand ? 24 : 20) * Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3)
        };

        if (expand)
            ExpandedView.Visibility = Visibility.Visible;

        if (expand)
            RefreshControls();

        bool motion = animate && _dock.Settings.AnimationsEnabled;
        if (!motion)
        {
            Capsule.BeginAnimation(WidthProperty, null);
            Capsule.BeginAnimation(HeightProperty, null);
            Capsule.Width = width;
            Capsule.Height = height;
            CompactView.BeginAnimation(OpacityProperty, null);
            ExpandedView.BeginAnimation(OpacityProperty, null);
            CompactView.Opacity = expand ? 0 : 1;
            ExpandedView.Opacity = expand ? 1 : 0;
            if (!expand)
                ExpandedView.Visibility = Visibility.Collapsed;
        }
        else
        {
            double speed = Math.Clamp(_dock.Settings.AnimationSpeed, 0.5, 2);
            double seconds = 0.26 / speed;
            var sizeEase = new BackEase { Amplitude = 0.10, EasingMode = EasingMode.EaseOut };
            Capsule.BeginAnimation(WidthProperty,
                new DoubleAnimation(width, TimeSpan.FromSeconds(seconds)) { EasingFunction = sizeEase });
            Capsule.BeginAnimation(HeightProperty,
                new DoubleAnimation(height, TimeSpan.FromSeconds(seconds)) { EasingFunction = sizeEase });

            CompactView.BeginAnimation(OpacityProperty,
                new DoubleAnimation(expand ? 0 : 1, TimeSpan.FromSeconds(0.14))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });

            var expandedFade = new DoubleAnimation(expand ? 1 : 0, TimeSpan.FromSeconds(0.18))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            expandedFade.Completed += (_, _) =>
            {
                if (!_expanded)
                    ExpandedView.Visibility = Visibility.Collapsed;
            };
            ExpandedView.BeginAnimation(OpacityProperty, expandedFade);
        }

        Capsule.CornerRadius = new CornerRadius(radius);
        IslandGlow.CornerRadius = new CornerRadius(radius);
        IslandGlow2.CornerRadius = new CornerRadius(radius);

    }

    private void PositionIsland()
    {
        Forms.Screen screen = ScreenGeometry.Target(_dock.Settings.MonitorIndex);
        Rect bounds = ScreenGeometry.BoundsDip(screen);
        double scale = Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3);

        Width = Math.Max(_expandedWidth + 40, 480 * scale);
        Height = Math.Max(_expandedHeight + 30, 260 * scale);
        Left = bounds.Left + (bounds.Width - Width) / 2;

        double top = bounds.Top + _dock.Settings.IslandTopMargin;
        if (_dock.Settings.ReplaceWindowsTaskbar && _dock.Settings.DockOnTop)
            top = bounds.Top + _dock.Settings.DockHeight + 20 + _dock.Settings.IslandTopMargin;
        Top = top;
    }

    private void RefreshControls()
    {
        try
        {
            _controls.Refresh();
            VolumeText.Text = _controls.VolumeSupported ? $"{_controls.VolumePercent}%" : "—";
            IslandStatus.Text = _controls.VolumeSupported ? "Audio di Windows" : "Volume di sistema";
        }
        catch (Exception ex)
        {
            Log.Write("Island controls refresh failed: " + ex.Message);
            VolumeText.Text = "—";
            IslandStatus.Text = "Controlli non disponibili";
        }
    }

    private void ChangeVolume(int delta)
    {
        if (!_controls.VolumeSupported)
        {
            IslandStatus.Text = "Volume non disponibile";
            return;
        }

        int target = Math.Clamp(_controls.VolumePercent + delta, 0, 100);
        if (!_controls.SetVolume(target, out string? error))
        {
            IslandStatus.Text = error ?? "Volume non disponibile";
            return;
        }

        VolumeText.Text = $"{_controls.VolumePercent}%";
        IslandStatus.Text = "Volume aggiornato";
    }

    private void OnCapsuleEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hideTimer.Stop();
        SetExpanded(true, true);
    }

    private void OnCapsuleLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pinned || !_expanded)
            return;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnCapsuleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (FindButton(e.OriginalSource as DependencyObject) is not null)
            return;

        _pinned = !_pinned;
        SetExpanded(_pinned, true);
        if (!_pinned)
            _hideTimer.Stop();
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (!_controls.ToggleMute(out bool muted, out string? error))
        {
            IslandStatus.Text = error ?? "Mute non disponibile";
            return;
        }

        IslandStatus.Text = muted ? "Audio disattivato" : "Audio riattivato";
        VolumeText.Text = _controls.VolumeSupported ? $"{_controls.VolumePercent}%" : "—";
    }

    private void OnVolumeDown(object sender, RoutedEventArgs e) => ChangeVolume(-5);

    private void OnVolumeUp(object sender, RoutedEventArgs e) => ChangeVolume(5);

    private void OnPreviousMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.Previous);

    private void OnPlayPauseMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.PlayPause);

    private void OnNextMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.Next);

    private void SendMedia(MediaCommand command)
    {
        if (!_controls.SendMedia(command))
        {
            IslandStatus.Text = "Comando media non accettato";
            return;
        }

        IslandStatus.Text = command switch
        {
            MediaCommand.Previous => "Precedente inviato",
            MediaCommand.Next => "Successiva inviata",
            _ => "Play/Pausa inviato"
        };
    }

    private void OnOpenSearch(object sender, RoutedEventArgs e)
    {
        CollapseAfterAction();
        App.Instance?.ShowLauncher();
    }

    private void OnOpenWidgets(object sender, RoutedEventArgs e)
    {
        CollapseAfterAction();
        App.Instance?.ShowWidgets();
    }

    private void OnOpenControls(object sender, RoutedEventArgs e)
    {
        CollapseAfterAction();
        App.Instance?.ShowControls();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        CollapseAfterAction();
        App.Instance?.ShowSettings();
    }

    private void CollapseAfterAction()
    {
        _pinned = false;
        SetExpanded(false, true);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.IslandStyle)
            or nameof(AppSettings.IslandScale)
            or nameof(AppSettings.IslandTopMargin))
        {
            ApplyAppearance();
            if (IsLoaded)
                PositionIsland();
        }
        else if (e.PropertyName is nameof(AppSettings.MonitorIndex)
                 or nameof(AppSettings.DockOnTop)
                 or nameof(AppSettings.DockHeight)
                 or nameof(AppSettings.ReplaceWindowsTaskbar))
        {
            if (IsLoaded)
                PositionIsland();
        }
    }

    private void OnThemeChanged()
    {
        if (IsLoaded)
            Dispatcher.InvokeAsync(ApplyAppearance, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hideTimer.Stop();
        try
        {
            _source?.RemoveHook(WndProc);
        }
        catch
        {
            // The source may already be disposing with the window.
        }
        _source = null;
        _controls.Dispose();
        _dock.Settings.PropertyChanged -= OnSettingsChanged;
        _dock.Theme.Changed -= OnThemeChanged;
        DataContext = null;
    }

    private Brush FindBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

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
}
