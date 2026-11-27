using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.ViewModels;

namespace Nebula.Views;

public partial class TabBarWindow : Window
{
    private const double FloatingGap = 6;
    private const double MinimumCaptionBand = 26;

    private readonly TabGroup _group;
    private readonly TabGroupService _service;
    private readonly AppSettings _settings;
    private readonly ThemeEngine _theme;
    private readonly TabBarViewModel _vm;
    private readonly DispatcherTimer _followTimer;

    private Point _dragStart;
    private TabChipViewModel? _dragChip;
    private Border? _dragBorder;
    private bool _dragged;
    private bool _closing;
    private bool _loadedAnimated;
    private int _lastX = int.MinValue;
    private int _lastY = int.MinValue;
    private int _lastWidth;
    private int _lastHeight;
    private bool _barHidden;
    private bool _coverMode;
    private bool _zoomedMode;
    private string? _captionState;

    public TabBarWindow(TabGroup group, TabGroupService service, AppSettings settings, ThemeEngine theme)
    {
        _group = group;
        _service = service;
        _settings = settings;
        _theme = theme;
        _vm = new TabBarViewModel(group, service, settings);

        // The bar follows the window it belongs to. The group service ticks at
        // 300 ms, which is fine for the tabs but makes a dragged window leave
        // its own bar behind, so the geometry is refreshed far more often while
        // the bar exists.
        _followTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _followTimer.Tick += (_, _) => UpdatePosition();

        InitializeComponent();
        DataContext = _vm;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _settings.PropertyChanged += OnSettingsChanged;
        _theme.Changed += OnThemeChanged;
    }

    public TabGroup Group => _group;

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
            Log.Write("Tab bar style setup failed: " + ex.Message);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStyle();
        Refresh();
        AnimateIn();
        _followTimer.Start();

        Log.Write(
            $"Tab bar rendered for group {_group.Id[..6]} in " +
            $"{Stopwatch.GetElapsedTime(_group.CreatedTimestamp).TotalMilliseconds:0} ms.");
    }

    public void Refresh()
    {
        if (_closing || !IsVisible)
            return;

        _vm.Sync();
        UpdatePosition();
    }

    public void AnimateClose()
    {
        if (_closing)
            return;

        _closing = true;
        _followTimer.Stop();
        IsHitTestVisible = false;

        if (!_settings.AnimationsEnabled)
        {
            Close();
            return;
        }

        double seconds = 0.13 / Math.Clamp(_settings.AnimationSpeed, 0.5, 2);
        var fade = new DoubleAnimation(Root.Opacity, 0, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
        BarSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(BarSlide.Y, -5, TimeSpan.FromSeconds(seconds)));
    }

    private void AnimateIn()
    {
        if (_loadedAnimated)
            return;

        _loadedAnimated = true;
        if (!_settings.AnimationsEnabled)
        {
            Root.Opacity = 1;
            BarSlide.Y = 0;
            return;
        }

        double seconds = 0.16 / Math.Clamp(_settings.AnimationSpeed, 0.5, 2);
        Root.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        BarSlide.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(7, 0, TimeSpan.FromSeconds(seconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void ApplyStyle()
    {
        string style = _settings.TabsStyle;
        Bar.Background = FindBrush("C.TooltipBg", Brushes.Black);
        Bar.BorderBrush = FindBrush("C.DockStroke", Brushes.White);

        switch (style)
        {
            case "minimal":
                break;

            case "neon":
                Bar.Background = FindBrush("C.Bg", Brushes.Black);
                Bar.BorderBrush = FindBrush("C.Accent", Brushes.MediumPurple);
                break;

            case "soft":
                Bar.BorderBrush = FindBrush("C.AccentFaint", Brushes.MediumPurple);
                break;

            case "liquid":
                Bar.Background = FindBrush("C.DockGlass", Brushes.Black);
                break;

            case "mono":
                Bar.Background = FindBrush("C.Bg", Brushes.Black);
                Bar.BorderBrush = FindBrush("C.StrokeSoft", Brushes.Gray);
                break;
        }

        ApplyBarMode();
    }

    /// <summary>
    /// Quando la barra copre la vecchia barra del titolo ha gli angoli alti
    /// arrotondati come la finestra; quando invece resta staccata sopra la
    /// finestra torna una pillola.
    /// </summary>
    private void ApplyBarMode()
    {
        if (Bar is null)
            return;

        CornerRadius radius;
        if (_coverMode)
        {
            radius = !_zoomedMode && Environment.OSVersion.Version.Build >= 22000
                ? new CornerRadius(8, 8, 0, 0)
                : new CornerRadius(0);
        }
        else
        {
            radius = new CornerRadius(14);
        }

        Bar.CornerRadius = radius;
        Bar.BorderThickness = _coverMode ? new Thickness(0, 0, 0, 1) : new Thickness(1);
    }

    /// <summary>
    /// Geometria della barra. In alto, se la finestra ha una barra del titolo
    /// classica, la barra la copre esattamente: stessa larghezza, stessa altezza
    /// della fascia, stesso bordo. Una finestra con cornice disegnata da sé
    /// (niente fascia non client) lascia invece la barra staccata, perché
    /// coprirne l'intestazione taglierebbe il contenuto dell'app.
    /// </summary>
    private void UpdatePosition()
    {
        if (_closing)
            return;

        TabMember? active = _group.ActiveMember;
        if (active is null || !NativeMethods.IsWindow(active.Handle))
            return;

        IntPtr barHandle = new WindowInteropHelper(this).Handle;
        if (barHandle == IntPtr.Zero)
            return;

        if (NativeMethods.IsIconic(active.Handle) || !NativeMethods.IsWindowVisible(active.Handle))
        {
            if (!_barHidden)
            {
                NativeMethods.ShowWindow(barHandle, NativeMethods.SW_HIDE);
                _barHidden = true;
            }

            return;
        }

        if (_barHidden)
        {
            NativeMethods.ShowWindow(barHandle, NativeMethods.SW_SHOWNOACTIVATE);
            _barHidden = false;
        }

        System.Drawing.Rectangle window = VisibleWindowRect(active.Handle);
        if (window.Width < 2 || window.Height < 2)
            return;

        System.Windows.Forms.Screen screen =
            System.Windows.Forms.Screen.FromHandle(active.Handle) ??
            System.Windows.Forms.Screen.PrimaryScreen!;
        System.Drawing.Rectangle work = screen.WorkingArea;

        bool zoomed = NativeMethods.IsZoomed(active.Handle);
        double scale = DpiScale(active.Handle);
        int band = CaptionBand(active.Handle, zoomed ? work.Top : window.Top);

        bool cover =
            string.Equals(_settings.TabsBarPosition, "top", StringComparison.OrdinalIgnoreCase) &&
            band >= MinimumCaptionBand;

        int x;
        int y;
        int widthPx;
        int heightPx;

        if (cover)
        {
            x = zoomed ? work.Left : window.Left;
            y = zoomed ? work.Top : window.Top;
            widthPx = zoomed ? work.Width : window.Width;
            heightPx = band;
        }
        else
        {
            widthPx = Math.Max(240, window.Width);
            heightPx = (int)Math.Round(Math.Clamp(_settings.TabsBarHeight, 34, 64) * scale);
            x = window.Left;

            bool below = string.Equals(
                _settings.TabsBarPosition, "bottom", StringComparison.OrdinalIgnoreCase);
            int gap = (int)Math.Round(FloatingGap * scale);
            y = below ? window.Bottom + gap : window.Top - heightPx - gap;

            if (y + heightPx > work.Bottom - 4)
                y = window.Top - heightPx - gap;
            if (y < work.Top + 4)
                y = window.Bottom + gap;

            x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - widthPx - 4));
            y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - heightPx - 4));
        }

        widthPx = Math.Max(220, widthPx);
        heightPx = Math.Max(28, heightPx);

        if (_coverMode != cover || _zoomedMode != zoomed || _captionState is null)
        {
            _coverMode = cover;
            _zoomedMode = zoomed;
            ApplyBarMode();
        }

        _vm.SetBarHeight(heightPx / scale);
        UpdateCaptionState(zoomed);

        double dipWidth = widthPx / scale;
        double dipHeight = heightPx / scale;
        if (Math.Abs(Width - dipWidth) > 0.5)
            Width = dipWidth;
        if (Math.Abs(Height - dipHeight) > 0.5)
            Height = dipHeight;

        if (x == _lastX && y == _lastY && widthPx == _lastWidth && heightPx == _lastHeight)
            return;

        _lastX = x;
        _lastY = y;
        _lastWidth = widthPx;
        _lastHeight = heightPx;

        NativeMethods.SetWindowPos(
            barHandle, (IntPtr)NativeMethods.HWND_TOPMOST, x, y, widthPx, heightPx,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>Rettangolo visibile della finestra, senza i bordi trasparenti di DWM.</summary>
    private static System.Drawing.Rectangle VisibleWindowRect(IntPtr handle)
    {
        if (NativeMethods.DwmGetWindowAttributeRect(
                handle, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out NativeMethods.RECT visible, Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            visible.Right > visible.Left && visible.Bottom > visible.Top)
        {
            return System.Drawing.Rectangle.FromLTRB(
                visible.Left, visible.Top, visible.Right, visible.Bottom);
        }

        if (NativeMethods.GetWindowRect(handle, out NativeMethods.RECT rect))
            return System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);

        return System.Drawing.Rectangle.Empty;
    }

    /// <summary>Altezza in pixel della fascia fra il bordo visibile e il contenuto.</summary>
    private static int CaptionBand(IntPtr handle, int visibleTop)
    {
        if (!NativeMethods.GetClientRect(handle, out _))
            return 0;

        var origin = new NativeMethods.POINT();
        if (!NativeMethods.ClientToScreen(handle, ref origin))
            return 0;

        int band = origin.Y - visibleTop;
        return band is > 0 and < 160 ? band : 0;
    }

    private static double DpiScale(IntPtr handle)
    {
        uint dpi = NativeMethods.GetDpiForWindow(handle);
        return dpi >= 48 ? dpi / 96.0 : 1.0;
    }

    private void UpdateCaptionState(bool zoomed)
    {
        string state = zoomed ? "restore" : "maximize";
        if (string.Equals(_captionState, state, StringComparison.Ordinal))
            return;

        _captionState = state;
        MaximizeGlyph.Text = zoomed ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = zoomed ? "Ripristina" : "Ingrandisci";
        AutomationProperties.SetName(MaximizeButton, zoomed ? "Ripristina" : "Ingrandisci");
    }

    private IntPtr ActiveHandle
    {
        get
        {
            TabMember? active = _group.ActiveMember;
            return active is not null && NativeMethods.IsWindow(active.Handle)
                ? active.Handle
                : IntPtr.Zero;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.TabsStyle)
            or nameof(AppSettings.TabsBarHeight)
            or nameof(AppSettings.TabsBarPosition))
        {
            ApplyStyle();
            _vm.Sync();
            UpdatePosition();
        }
    }

    private void OnThemeChanged()
    {
        ApplyStyle();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _followTimer.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
        _theme.Changed -= OnThemeChanged;
    }

    private Brush FindBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private void OnBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInteractive(e.OriginalSource as DependencyObject))
            return;

        IntPtr handle = ActiveHandle;
        if (handle == IntPtr.Zero)
            return;

        if (e.ClickCount == 2)
        {
            SendSystemCommand(NativeMethods.IsZoomed(handle)
                ? NativeMethods.SC_RESTORE
                : NativeMethods.SC_MAXIMIZE);
            e.Handled = true;
            return;
        }

        int hit = _coverMode ? ResizeHitTest(e.GetPosition(Bar)) : NativeMethods.HTCAPTION;

        // The click belongs to the window below: hand it the caption hit so
        // Windows runs its own move loop (snap, maximize on drag, resize).
        NativeMethods.ReleaseCapture();
        NativeMethods.SendMessage(handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        e.Handled = true;
    }

    private void OnBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_coverMode)
            return;

        Bar.Cursor = ResizeHitTest(e.GetPosition(Bar)) switch
        {
            NativeMethods.HTTOP => Cursors.SizeNS,
            NativeMethods.HTLEFT or NativeMethods.HTRIGHT => Cursors.SizeWE,
            NativeMethods.HTTOPLEFT or NativeMethods.HTBOTTOMRIGHT => Cursors.SizeNWSE,
            NativeMethods.HTTOPRIGHT or NativeMethods.HTBOTTOMLEFT => Cursors.SizeNESW,
            _ => null
        };
    }

    private void OnBarMouseLeave(object sender, MouseEventArgs e) => Bar.Cursor = null;

    private int ResizeHitTest(Point position)
    {
        const double edge = 5;
        bool top = position.Y <= edge;
        bool left = position.X <= edge;
        bool right = position.X >= Bar.ActualWidth - edge;

        if (top && left)
            return NativeMethods.HTTOPLEFT;
        if (top && right)
            return NativeMethods.HTTOPRIGHT;
        if (top)
            return NativeMethods.HTTOP;
        if (left)
            return NativeMethods.HTLEFT;
        if (right)
            return NativeMethods.HTRIGHT;

        return NativeMethods.HTCAPTION;
    }

    private void OnBarRightClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject))
            return;

        IntPtr handle = ActiveHandle;
        if (handle == IntPtr.Zero)
            return;

        e.Handled = true;

        var menu = new ContextMenu();
        menu.Items.Add(CommandItem("Ripristina", NativeMethods.SC_RESTORE));
        menu.Items.Add(NativeMethods.IsZoomed(handle)
            ? CommandItem("Riduci a icona", NativeMethods.SC_MINIMIZE)
            : CommandItem("Ingrandisci", NativeMethods.SC_MAXIMIZE));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("Sposta", NativeMethods.SC_MOVE));
        menu.Items.Add(CommandItem("Dimensiona", NativeMethods.SC_SIZE));
        menu.Items.Add(new Separator());

        var close = new MenuItem { Header = "Chiudi finestra" };
        close.Click += (_, _) =>
        {
            IntPtr current = ActiveHandle;
            if (current != IntPtr.Zero)
                NativeMethods.PostMessage(current, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        };
        menu.Items.Add(close);

        menu.Items.Add(new Separator());
        var closeGroup = new MenuItem { Header = "Chiudi gruppo e ripristina le finestre" };
        closeGroup.Click += (_, _) => _vm.CloseGroup();
        menu.Items.Add(closeGroup);

        menu.PlacementTarget = Bar;
        menu.IsOpen = true;
    }

    private MenuItem CommandItem(string header, int command)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => SendSystemCommand(command);
        return item;
    }

    private void SendSystemCommand(int command)
    {
        IntPtr handle = ActiveHandle;
        if (handle != IntPtr.Zero)
            NativeMethods.PostMessage(handle, NativeMethods.WM_SYSCOMMAND, (IntPtr)command, IntPtr.Zero);
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) =>
        SendSystemCommand(NativeMethods.SC_MINIMIZE);

    private void OnMaximizeWindow(object sender, RoutedEventArgs e) =>
        SendSystemCommand(NativeMethods.IsZoomed(ActiveHandle)
            ? NativeMethods.SC_RESTORE
            : NativeMethods.SC_MAXIMIZE);

    private void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        IntPtr handle = ActiveHandle;
        if (handle != IntPtr.Zero)
            NativeMethods.PostMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnTabScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource as DependencyObject))
            return;

        if (sender is not FrameworkElement element || element.DataContext is not TabChipViewModel chip)
            return;

        _dragStart = e.GetPosition(this);
        _dragChip = chip;
        _dragBorder = element as Border;
        _dragged = false;
    }

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragChip is null || _dragBorder is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point current = e.GetPosition(this);
        if (Math.Abs(current.X - _dragStart.X) < 7 && Math.Abs(current.Y - _dragStart.Y) < 7)
            return;

        _dragged = true;
        TabChipViewModel chip = _dragChip;
        Border source = _dragBorder;
        _dragChip = null;
        _dragBorder = null;

        try
        {
            DragDrop.DoDragDrop(source, new DataObject("NebulaTab", chip), DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Log.Write("Tab drag failed: " + ex.Message);
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        TabChipViewModel? chip = _dragChip;
        bool dragged = _dragged;
        _dragChip = null;
        _dragBorder = null;
        _dragged = false;

        if (chip is null || dragged)
            return;

        _vm.Select(chip);
        PulseTab(chip);
        Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("NebulaTab") ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData("NebulaTab") is not TabChipViewModel dragged)
            return;

        TabChipViewModel? target = FindChip(e.OriginalSource as DependencyObject);
        int targetIndex = target is null ? _vm.Tabs.Count - 1 : _vm.Tabs.IndexOf(target);
        if (targetIndex >= 0)
            _vm.Move(dragged, targetIndex);

        e.Handled = true;
    }

    private void PulseTab(TabChipViewModel chip)
    {
        if (!_settings.AnimationsEnabled)
            return;

        try
        {
            TabItems.UpdateLayout();
            if (TabItems.ItemContainerGenerator.ContainerFromItem(chip) is not DependencyObject container)
                return;

            Border? border = FindVisual<Border>(container, "TabRoot");
            if (border is null)
                return;

            var scale = new ScaleTransform(1, 1);
            border.RenderTransformOrigin = new Point(0.5, 0.5);
            border.RenderTransform = scale;

            double seconds = 0.22 / Math.Clamp(_settings.AnimationSpeed, 0.5, 2);
            var animation = new DoubleAnimation(1, 1.045, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }
        catch
        {
            // Animation is cosmetic; never break tab selection for it.
        }
    }

    private void OnCloseTabClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: TabChipViewModel chip })
            _vm.Close(chip);
    }

    private void OnTabRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement element || element.DataContext is not TabChipViewModel chip)
            return;

        var menu = new ContextMenu();

        var select = new MenuItem { Header = "Seleziona" };
        select.Click += (_, _) => _vm.Select(chip);
        menu.Items.Add(select);

        var detach = new MenuItem { Header = "Stacca scheda" };
        detach.Click += (_, _) =>
        {
            _vm.Detach(chip);
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        };
        menu.Items.Add(detach);

        var close = new MenuItem { Header = "Chiudi finestra" };
        close.Click += (_, _) => _vm.Close(chip);
        menu.Items.Add(close);

        menu.Items.Add(new Separator());

        var closeGroup = new MenuItem { Header = "Chiudi gruppo e ripristina le finestre" };
        closeGroup.Click += (_, _) => _vm.CloseGroup();
        menu.Items.Add(closeGroup);

        menu.PlacementTarget = element;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Vero per tutto ciò che deve ricevere il clic (schede, pulsanti, barre di
    /// scorrimento): solo lo spazio vuoto della barra sposta la finestra.
    /// </summary>
    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or ScrollBar or TextBoxBase)
                return true;

            if (element is FrameworkElement { DataContext: TabChipViewModel })
                return true;

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private static TabChipViewModel? FindChip(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: TabChipViewModel chip })
                return chip;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button)
                return true;
            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static T? FindVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name)
                return typed;
            T? found = FindVisual<T>(child, name);
            if (found is not null)
                return found;
        }

        return null;
    }
}
