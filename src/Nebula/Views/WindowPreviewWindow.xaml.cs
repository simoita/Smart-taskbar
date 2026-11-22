using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;

namespace Nebula.Views;

/// <summary>One window of a taskbar group, with its captured picture.</summary>
public sealed class WindowPreviewItem : ObservableObject
{
    private ImageSource? _thumbnail;

    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";

    /// <summary>Filled asynchronously so a slow window never blocks the bar.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public ImageSource? Icon { get; init; }

    /// <summary>Generic window glyph shown until (or instead of) the capture.</summary>
    public string Glyph => "\uE737";
}

/// <summary>
/// Hover flyout of a bar icon with more than one window. Every window of the
/// group gets its own preview, exactly like the Windows taskbar, and a click
/// brings that window to the front.
/// </summary>
public partial class WindowPreviewWindow : Window
{
    private readonly Action<IntPtr> _activate;
    private readonly Func<bool> _anchorHovered;
    private readonly DispatcherTimer _leaveTimer;

    public WindowPreviewWindow(
        IReadOnlyList<WindowPreviewItem> items,
        Action<IntPtr> activate,
        Func<bool> anchorHovered)
    {
        InitializeComponent();

        _activate = activate;
        _anchorHovered = anchorHovered;
        CardList.ItemsSource = items;

        // Explicit size: SizeToContent measures the flyout before the captured
        // images exist and clipped the last card on some DPI scales.
        Width = Math.Min(6, Math.Max(1, items.Count)) * 190 + 60;
        Height = 196;

        _leaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(320)
        };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!IsMouseOver && !_anchorHovered())
                Close();
        };

        MouseEnter += (_, _) => _leaveTimer.Stop();
        MouseLeave += (_, _) => _leaveTimer.Start();
        SourceInitialized += OnSourceInitialized;
    }

    public int ItemCount => CardList.Items.Count;
    public FrameworkElement CardElement => Card;

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
            Log.Write("Window preview style failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Places the flyout right above (bar at the bottom) or below (bar at the
    /// top) the bar, centred on the icon and clamped to the screen.
    /// </summary>
    public void Place(Rect anchorDip, Rect screenDip, bool above)
    {
        UpdateLayout();
        double width = ActualWidth;
        double height = ActualHeight;

        double left = anchorDip.Left + anchorDip.Width / 2 - width / 2;
        double top = above
            ? anchorDip.Top - height - 10
            : anchorDip.Bottom + 10;

        left = Math.Clamp(left, screenDip.Left + 8,
            Math.Max(screenDip.Left + 8, screenDip.Right - width - 8));
        top = Math.Clamp(top, screenDip.Top + 8,
            Math.Max(screenDip.Top + 8, screenDip.Bottom - height - 8));

        Left = left;
        Top = top;
    }

    private void OnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WindowPreviewItem item })
            return;

        e.Handled = true;
        try
        {
            _activate(item.Handle);
        }
        catch (Exception ex)
        {
            Log.Write("Window preview activation failed: " + ex.Message);
        }

        Close();
    }
}
