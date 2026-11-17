using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Nebula.Utilities;

/// <summary>
/// Resolves the selected monitor and its DPI so the dock can be placed on that
/// screen instead of always assuming the primary one.
/// </summary>
public static class ScreenGeometry
{
    public static Forms.Screen Target(int index)
    {
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
            return Forms.Screen.PrimaryScreen ?? throw new InvalidOperationException("No screens available.");

        return screens[Math.Clamp(index, 0, screens.Length - 1)];
    }

    public static double Scale(Forms.Screen screen)
    {
        try
        {
            var point = new Point32
            {
                X = screen.Bounds.Left + screen.Bounds.Width / 2,
                Y = screen.Bounds.Top + screen.Bounds.Height / 2
            };

            IntPtr monitor = MonitorFromPoint(point, 2);
            if (monitor != IntPtr.Zero &&
                GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch
        {
            // Fall through to 100%.
        }

        return 1.0;
    }

    public static Rect WorkAreaDip(Forms.Screen screen)
    {
        double scale = Scale(screen);
        var work = screen.WorkingArea;
        return new Rect(work.Left / scale, work.Top / scale, work.Width / scale, work.Height / scale);
    }

    /// <summary>Full monitor rectangle in DIPs, ignoring taskbar reservations.</summary>
    public static Rect BoundsDip(Forms.Screen screen)
    {
        double scale = Scale(screen);
        var bounds = screen.Bounds;
        return new Rect(bounds.Left / scale, bounds.Top / scale, bounds.Width / scale, bounds.Height / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point32 point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
