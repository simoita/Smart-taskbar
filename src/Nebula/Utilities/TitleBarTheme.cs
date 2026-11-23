using System.Windows;
using System.Windows.Interop;
using Nebula.Interop;

namespace Nebula.Utilities;

/// <summary>
/// Paints the standard Windows title bar of an app page dark, so a classic
/// frame does not show a white caption above the dark Nebula pages.
/// </summary>
public static class TitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;

    public static void Apply(Window window)
    {
        if (window.IsLoaded)
        {
            ApplyNow(window);
            return;
        }

        window.SourceInitialized += (_, _) => ApplyNow(window);
    }

    private static void ApplyNow(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int enabled = 1;
            int result = NativeMethods.DwmSetWindowAttribute(
                handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));

            // Windows 10 builds before 20H1 used the previous attribute number.
            if (result != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
        }
        catch
        {
            // An older shell simply keeps its own caption colours.
        }
    }
}
