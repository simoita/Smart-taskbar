using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;
using Forms = System.Windows.Forms;

namespace Nebula.Views;

/// <summary>
/// Floating top-center capsule. Compact at rest, it expands into a small system
/// dashboard with live activities, media controls and a temporary file drawer.
/// </summary>
public partial class IslandWindow : Window
{
    private const int WM_NCHITTEST = 0x0084;
    private const int HTTRANSPARENT = -1;

    private readonly DockViewModel _dock;
    private readonly ActivityCenter _activities;
    private readonly FileShelf _shelf;
    private readonly QuickControls _controls = new();
    private readonly DispatcherTimer _hideTimer;
    private HwndSource? _source;

    private bool _expanded;
    private bool _pinned;
    private bool _dragHover;
    private double _compactWidth = 250;
    private double _compactHeight = 42;
    private double _expandedWidth = 440;
    private double _expandedHeight = 500;

    private Point _shelfDragStart;
    private ShelfItem? _shelfDragItem;

    public IslandWindow(DockViewModel dock, ActivityCenter activities, FileShelf shelf)
    {
        _dock = dock;
        _activities = activities;
        _shelf = shelf;

        InitializeComponent();
        DataContext = dock;

        ActivityList.ItemsSource = _activities.Activities;
        ShelfList.ItemsSource = _shelf.Items;

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
        _activities.Changed += RefreshActivitySummary;
        _activities.Activities.CollectionChanged += OnActivitiesCollectionChanged;
        _shelf.Changed += RefreshShelfSummary;

        RefreshActivitySummary();
        RefreshShelfSummary();
    }

    /// <summary>True when the capsule is docked inside the Nebula bar instead of floating.</summary>
    private bool InTaskbar =>
        _dock.Settings.IslandPlacement == "taskbar" &&
        _dock.Settings.ReplaceWindowsTaskbar &&
        _dock.Settings.TaskbarFullWidth;

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
        RefreshControls();
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
        bool inBar = InTaskbar;
        _compactWidth = (inBar ? 208 : 250) * scale;
        _compactHeight = inBar
            ? Math.Clamp(settings.DockHeight - 18, 26, 44)
            : 42 * scale;

        _expandedWidth = 440 * scale;
        _expandedHeight = ComputeExpandedHeight();
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
        if (inBar)
            radius = _expanded ? 24 * scale : _compactHeight / 2;

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

        UpdateSections();
        RefreshActivitySummary();
        RefreshShelfSummary();
    }

    /// <summary>Only sections with something to show take space in the island.</summary>
    private bool ActivitiesVisible =>
        _dock.Settings.IslandShowActivities && _activities.Activities.Count > 0;

    private bool ShelfVisible =>
        _dock.Settings.IslandShowShelf && (_shelf.Items.Count > 0 || _dragHover);

    private double ComputeExpandedHeight()
    {
        double scale = Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3);
        double content = 150;

        // Sections grow with what they actually contain: one download does not
        // reserve room for four, and an empty drawer reserves nothing.
        if (ActivitiesVisible)
            content += 34 + Math.Min(196, _activities.Activities.Count * 68);

        if (ShelfVisible)
        {
            int rows = Math.Max(1, (int)Math.Ceiling(_shelf.Items.Count / 2.0));
            content += 46 + Math.Min(150, rows * 42);
        }

        if (_dock.Settings.IslandShowControls)
            content += 42;

        // The minimum only guards the controls row: an island with nothing to
        // report stays short instead of showing empty space.
        return Math.Max(210, content) * scale;
    }

    /// <summary>
    /// Hides the activity list when nothing is running and the file drawer when
    /// it is empty, so the expanded capsule never shows empty zones.
    /// </summary>
    private void UpdateSections()
    {
        if (ActivitiesSection is null || ShelfSection is null)
            return;

        ActivitiesSection.Visibility = ActivitiesVisible ? Visibility.Visible : Visibility.Collapsed;
        ShelfSection.Visibility = ShelfVisible ? Visibility.Visible : Visibility.Collapsed;

        if (ActivityEmpty is not null)
            ActivityEmpty.Visibility = Visibility.Collapsed;
        if (ShelfEmptyText is not null)
            ShelfEmptyText.Visibility = Visibility.Collapsed;

        _expandedHeight = ComputeExpandedHeight();
        if (_expanded && IsLoaded)
        {
            Capsule.BeginAnimation(HeightProperty, null);
            Capsule.Height = _expandedHeight;
            PositionIsland();
        }
    }

    private void SetExpanded(bool expand, bool animate)
    {
        if (_expanded == expand &&
            (expand ? ExpandedView.Visibility == Visibility.Visible : ExpandedView.Visibility != Visibility.Visible))
            return;

        _expanded = expand;
        _hideTimer.Stop();

        if (expand)
            UpdateSections();

        double width = expand ? _expandedWidth : _compactWidth;
        double height = expand ? _expandedHeight : _compactHeight;
        double scale = Math.Clamp(_dock.Settings.IslandScale, 0.8, 1.3);
        double radius = AppliedStyleKey switch
        {
            "pill" => expand ? 30 * scale : _compactHeight / 2,
            "minimal" => (expand ? 14 : 10) * scale,
            _ => (expand ? 24 : 20) * scale
        };
        if (InTaskbar)
            radius = expand ? 24 * scale : _compactHeight / 2;

        if (expand)
        {
            UpdateSections();
            ExpandedView.Visibility = Visibility.Visible;
            RefreshControls();
        }

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
            double seconds = 0.28 / speed;
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
        Height = Math.Max(_expandedHeight + 30, 300 * scale);
        Left = bounds.Left + (bounds.Width - Width) / 2;

        AppSettings settings = _dock.Settings;
        bool inBar = InTaskbar;

        // Without a reserved work area the bar sits above the native taskbar,
        // exactly like the dock itself does.
        Rect barArea = settings.ReplaceWindowsTaskbar && settings.ReserveTaskbarSpace
            ? bounds
            : ScreenGeometry.WorkAreaDip(screen);

        // Anchoring the capsule to the bottom keeps its screen position stable
        // while the panel grows upwards, which is what a bar at the bottom of
        // the screen needs.
        Capsule.VerticalAlignment = inBar && !settings.DockOnTop
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;

        if (inBar)
        {
            double barHeight = Math.Max(44, settings.DockHeight);
            double barTop = settings.DockOnTop
                ? barArea.Top + settings.EdgeMargin
                : barArea.Bottom - settings.EdgeMargin - barHeight;

            if (settings.DockOnTop)
            {
                Top = barTop + (barHeight - _compactHeight) / 2;
            }
            else
            {
                double capsuleBottom = barTop + (barHeight + _compactHeight) / 2;
                Top = capsuleBottom - Height;
            }

            // The bar and the island are both topmost windows: moving the
            // capsule into the bar does not raise it, so without this it stays
            // hidden behind the bar.
            BringAboveDock();
            return;
        }

        double top = bounds.Top + settings.IslandTopMargin;
        if (settings.ReplaceWindowsTaskbar && settings.DockOnTop)
            top = bounds.Top + settings.DockHeight + 20 + settings.IslandTopMargin;
        Top = top;
    }

    /// <summary>
    /// Keeps the capsule above the Nebula bar while it is docked inside it.
    /// Both windows are topmost, so a bar that was raised later would otherwise
    /// cover the island completely.
    /// </summary>
    public void BringAboveDock()
    {
        if (!InTaskbar || !IsVisible)
            return;

        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;

            NativeMethods.SetWindowPos(handle, (IntPtr)NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Write("Island topmost failed: " + ex.Message);
        }
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

    private void OnActivitiesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        RefreshActivitySummary();

    private void RefreshActivitySummary()
    {
        if (_activities is null || ActivityCountText is null)
            return;

        int active = _activities.Activities.Count(activity => !activity.IsFinished);
        bool enabled = _dock.Settings.IslandShowActivities;
        CompactActivityBadge.Visibility = enabled && active > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        SystemActivity? first = _activities.Activities.FirstOrDefault(activity => !activity.IsFinished);
        CompactActivityText.Text = active switch
        {
            0 => "",
            1 => first?.Title ?? "1 attività",
            _ => $"{active} attività"
        };

        ActivityCountText.Text = active switch
        {
            0 => "nessuna attività",
            1 => "1 attività",
            _ => $"{active} attività"
        };

        // The list and the capsule height follow the real state of the PC.
        UpdateSections();
    }

    private void RefreshShelfSummary()
    {
        if (ShelfCountText is null)
            return;

        ShelfCountText.Text = _shelf.Items.Count switch
        {
            0 => "",
            1 => _shelf.Items[0].IsFolder ? "1 cartella" : "1 file",
            _ => _shelf.Items.Any(item => item.IsFolder)
                ? $"{_shelf.Items.Count} elementi"
                : $"{_shelf.Items.Count} file"
        };

        UpdateSections();
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

    private void OnCapsuleEnter(object sender, MouseEventArgs e)
    {
        _hideTimer.Stop();
        SetExpanded(true, true);
    }

    private void OnCapsuleLeave(object sender, MouseEventArgs e)
    {
        if (_pinned || !_expanded)
            return;

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnCapsuleClick(object sender, MouseButtonEventArgs e)
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

    private void OnDismissActivity(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SystemActivity activity })
            _activities.Dismiss(activity);
    }

    private async void OnIslandDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await AddDroppedFilesAsync(e);
    }

    private async void OnShelfDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        await AddDroppedFilesAsync(e);
    }

    private async Task AddDroppedFilesAsync(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            return;

        IslandStatus.Text = "Copio nel cassetto…";
        (int added, int skipped, string? error) = await _shelf.AddFilesAsync(files);
        IslandStatus.Text = added switch
        {
            0 => error ?? "Nessun elemento aggiunto",
            1 => "Aggiunto al cassetto",
            _ => $"{added} elementi nel cassetto"
        };

        if (skipped > 0)
            IslandStatus.Text += $" · {skipped} saltati";

        ResetShelfHighlight();
        _dragHover = false;
        RefreshShelfSummary();
    }

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // The drawer appears while a file is over the island, even when it is
        // empty, and disappears again when the drag leaves.
        if (!_dragHover)
        {
            _dragHover = true;
            UpdateSections();
        }

        if (!_expanded)
            SetExpanded(true, true);
        HighlightShelf();
    }

    private void OnShelfDragOver(object sender, DragEventArgs e) => OnIslandDragOver(sender, e);

    private void OnIslandDragLeave(object sender, DragEventArgs e)
    {
        ResetShelfHighlight();
        if (_dragHover)
        {
            _dragHover = false;
            UpdateSections();
        }

        if (!_pinned && _expanded && !Capsule.IsMouseOver)
        {
            _hideTimer.Stop();
            _hideTimer.Start();
        }
    }

    private void OnShelfDragLeave(object sender, DragEventArgs e) => OnIslandDragLeave(sender, e);

    private void HighlightShelf()
    {
        ShelfZone.BorderBrush = FindBrush("C.Accent", Brushes.MediumPurple);
        ShelfZone.BorderThickness = new Thickness(1.5);
        ShelfDropHint.Text = "Rilascia qui: resterà nel cassetto finché non lo sposti.";
    }

    private void ResetShelfHighlight()
    {
        ShelfZone.BorderBrush = FindBrush("S.InnerBorder", Brushes.Transparent);
        ShelfZone.BorderThickness = new Thickness(1);
        ShelfDropHint.Text = "Trascina qui file o cartelle per conservarli temporaneamente.";
    }

    private void OnShelfItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindButton(e.OriginalSource as DependencyObject) is not null)
        {
            _shelfDragItem = null;
            return;
        }

        _shelfDragItem = (sender as FrameworkElement)?.DataContext as ShelfItem;
        _shelfDragStart = e.GetPosition(this);
    }

    private void OnShelfItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfDragItem is null)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _shelfDragStart.X) < 8 &&
            Math.Abs(current.Y - _shelfDragStart.Y) < 8)
            return;

        ShelfItem item = _shelfDragItem;
        _shelfDragItem = null;
        var data = new DataObject(DataFormats.FileDrop, new[] { item.StoredPath });
        DragDropEffects effect = DragDrop.DoDragDrop((DependencyObject)sender, data,
            DragDropEffects.Copy | DragDropEffects.Move);
        if (effect == DragDropEffects.Move)
        {
            // Explorer moved the stored copy: the drawer no longer owns it.
            _shelf.Remove(item);
            IslandStatus.Text = "Spostato fuori dal cassetto";
            RefreshShelfSummary();
        }
        else if (effect == DragDropEffects.Copy)
        {
            IslandStatus.Text = "Copia lasciata fuori dal cassetto";
        }
    }

    private void OnShelfMove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShelfItem item })
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Sposta dal cassetto",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (_shelf.MoveTo(item, dialog.FolderName))
        {
            IslandStatus.Text = "Elemento spostato";
            RefreshShelfSummary();
        }
        else
        {
            IslandStatus.Text = "Spostamento non riuscito";
        }
    }

    private void OnShelfRemove(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ShelfItem item })
            return;

        IslandStatus.Text = _shelf.Remove(item)
            ? "Rimosso dal cassetto"
            : "Impossibile rimuovere l'elemento";
        RefreshShelfSummary();
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

    private void OnPowerMenuClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();

        void Add(string header, string glyph, string action)
        {
            menu.Items.Add(MenuFactory.Item(header, glyph,
                () => App.Instance?.RunPowerAction(action)));
        }

        Add("Blocca", "\uE72E", "lock");
        Add("Sospendi", "\uE708", "sleep");
        menu.Items.Add(new Separator());
        Add("Riavvia il PC", "\uE777", "restart");
        Add("Spegni il PC", "\uE7E8", "shutdown");
        menu.Items.Add(new Separator());
        Add("Esci da Nebula", "\uE711", "exit");

        menu.PlacementTarget = sender as Button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
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
            or nameof(AppSettings.IslandTopMargin)
            or nameof(AppSettings.IslandPlacement)
            or nameof(AppSettings.IslandShowActivities)
            or nameof(AppSettings.IslandShowShelf)
            or nameof(AppSettings.IslandShowControls)
            or nameof(AppSettings.ReplaceWindowsTaskbar)
            or nameof(AppSettings.TaskbarFullWidth))
        {
            ApplyAppearance();
            if (IsLoaded)
                PositionIsland();
        }
        else if (e.PropertyName is nameof(AppSettings.MonitorIndex)
                 or nameof(AppSettings.DockOnTop)
                 or nameof(AppSettings.DockHeight)
                 or nameof(AppSettings.TaskbarFullWidth)
                 or nameof(AppSettings.EdgeMargin)
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
        _activities.Changed -= RefreshActivitySummary;
        _activities.Activities.CollectionChanged -= OnActivitiesCollectionChanged;
        _shelf.Changed -= RefreshShelfSummary;
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
