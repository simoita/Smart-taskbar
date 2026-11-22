using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Nebula.Utilities;

/// <summary>
/// Makes the surfaces Nebula opens movable with the mouse. Popup cards move
/// their own window; full-screen overlays (Start and search) move only the
/// visible card, so the dim and the screen edges stay where they are.
/// </summary>
public static class PanelDrag
{
    /// <summary>
    /// Drags a popup window by a header element. Buttons and other interactive
    /// children keep receiving their own clicks.
    /// </summary>
    public static bool MoveWindow(Window window, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            IsInteractive(e.OriginalSource as DependencyObject))
            return false;

        double left = window.Left;
        double top = window.Top;

        try
        {
            window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove throws when the button is released before the drag
            // starts; the position simply does not change.
            return false;
        }

        return Math.Abs(window.Left - left) > 0.5 || Math.Abs(window.Top - top) > 0.5;
    }

    /// <summary>
    /// Attaches a card inside a full-screen overlay to the mouse. The offset is
    /// clamped to the window so a panel can never be dragged off the desktop.
    /// </summary>
    /// <param name="handle">Visible part the user presses.</param>
    /// <param name="card">Element that actually moves (usually the whole panel).</param>
    public static void AttachCard(FrameworkElement handle, FrameworkElement card, TranslateTransform offset)
    {
        Window? window = null;
        Point start = default;
        Point origin = default;
        bool dragging = false;

        handle.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left ||
                IsInteractive(e.OriginalSource as DependencyObject))
                return;

            window = Window.GetWindow(handle);
            if (window is null)
                return;

            start = e.GetPosition(window);
            origin = new Point(offset.X, offset.Y);
            dragging = true;
            handle.CaptureMouse();
        };

        handle.PreviewMouseMove += (_, e) =>
        {
            if (!dragging || window is null || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point current = e.GetPosition(window);
            offset.X = origin.X + current.X - start.X;
            offset.Y = origin.Y + current.Y - start.Y;
            Clamp(card, window, offset);
        };

        void Finish()
        {
            if (!dragging)
                return;

            dragging = false;
            if (handle.IsMouseCaptured)
                handle.ReleaseMouseCapture();
        }

        handle.PreviewMouseLeftButtonUp += (_, _) => Finish();
        handle.LostMouseCapture += (_, _) => dragging = false;
    }

    /// <summary>Keeps the translated card inside the overlay window.</summary>
    public static void Clamp(FrameworkElement card, Window window, TranslateTransform offset)
    {
        if (window.ActualWidth < 4 || window.ActualHeight < 4)
            return;

        Point position = card.TranslatePoint(new Point(0, 0), window);
        double width = card.ActualWidth;
        double height = card.ActualHeight;

        if (position.X < 0)
            offset.X -= position.X;
        else if (position.X + width > window.ActualWidth)
            offset.X -= position.X + width - window.ActualWidth;

        if (position.Y < 0)
            offset.Y -= position.Y;
        else if (position.Y + height > window.ActualHeight)
            offset.Y -= position.Y + height - window.ActualHeight;
    }

    private static bool IsInteractive(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or TextBoxBase or ComboBox or ScrollBar or Thumb or Slider)
                return true;

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
