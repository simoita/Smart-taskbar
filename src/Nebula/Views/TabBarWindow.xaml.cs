using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private readonly TabGroup _group;
    private readonly TabGroupService _service;
    private readonly AppSettings _settings;
    private readonly ThemeEngine _theme;
    private readonly TabBarViewModel _vm;

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

    public TabBarWindow(TabGroup group, TabGroupService service, AppSettings settings, ThemeEngine theme)
    {
        _group = group;
        _service = service;
        _settings = settings;
        _theme = theme;
        _vm = new TabBarViewModel(group, service, settings);

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
        Bar.Height = _vm.BarHeight;
        Bar.Background = FindBrush("C.TooltipBg", Brushes.Black);
        Bar.BorderBrush = FindBrush("C.DockStroke", Brushes.White);
        Bar.BorderThickness = new Thickness(1);
        Bar.CornerRadius = new CornerRadius(14);
        Bar.Effect = null;

        switch (style)
        {
            case "minimal":
                Bar.CornerRadius = new CornerRadius(6);
                Bar.BorderThickness = new Thickness(1);
                Bar.Effect = null;
                break;

            case "neon":
                Bar.CornerRadius = new CornerRadius(22);
                Bar.Background = FindBrush("C.Bg", Brushes.Black);
                Bar.BorderBrush = FindBrush("C.Accent", Brushes.MediumPurple);
                Bar.BorderThickness = new Thickness(1.5);
                break;

            case "soft":
                Bar.CornerRadius = new CornerRadius(20);
                Bar.Background = FindBrush("C.TooltipBg", Brushes.Black);
                Bar.BorderBrush = FindBrush("C.AccentFaint", Brushes.MediumPurple);
                Bar.BorderThickness = new Thickness(1);
                break;

            case "mono":
                Bar.CornerRadius = new CornerRadius(4);
                Bar.Background = FindBrush("C.Bg", Brushes.Black);
                Bar.BorderBrush = FindBrush("C.StrokeSoft", Brushes.Gray);
                Bar.BorderThickness = new Thickness(1);
                break;

            default:
                Bar.BorderBrush = FindBrush("C.DockStroke", Brushes.White);
                break;
        }
    }

    private void UpdatePosition()
    {
        TabMember? active = _group.ActiveMember;
        if (active is null || !NativeMethods.IsWindow(active.Handle))
            return;

        if (!WindowAccess.TryGetWindowRect(active.Handle, out System.Drawing.Rectangle target))
            return;

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        UpdateLayout();
        var barRect = new NativeMethods.RECT();
        if (!NativeMethods.GetWindowRect(handle, out barRect))
            return;

        int width = Math.Max(180, barRect.Right - barRect.Left);
        int height = Math.Max(34, barRect.Bottom - barRect.Top);
        System.Windows.Forms.Screen screen =
            System.Windows.Forms.Screen.FromHandle(active.Handle) ??
            System.Windows.Forms.Screen.PrimaryScreen!;
        System.Drawing.Rectangle work = screen.WorkingArea;

        int x = target.Left + 8;
        int y = string.Equals(_settings.TabsBarPosition, "bottom", StringComparison.OrdinalIgnoreCase)
            ? target.Bottom + 6
            : target.Top - height - 6;

        if (string.Equals(_settings.TabsBarPosition, "bottom", StringComparison.OrdinalIgnoreCase) &&
            y + height > work.Bottom - 4)
        {
            y = target.Bottom - height - 6;
        }
        else if (!string.Equals(_settings.TabsBarPosition, "bottom", StringComparison.OrdinalIgnoreCase) &&
                 y < work.Top + 4)
        {
            y = target.Top + 6;
        }

        x = Math.Clamp(x, work.Left + 4, Math.Max(work.Left + 4, work.Right - width - 4));
        y = Math.Clamp(y, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - height - 4));

        if (x == _lastX && y == _lastY && width == _lastWidth && height == _lastHeight)
            return;

        _lastX = x;
        _lastY = y;
        _lastWidth = width;
        _lastHeight = height;

        NativeMethods.SetWindowPos(
            handle, (IntPtr)NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
        _settings.PropertyChanged -= OnSettingsChanged;
        _theme.Changed -= OnThemeChanged;
    }

    private Brush FindBrush(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

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

    private void OnCloseGroupClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _vm.CloseGroup();
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
