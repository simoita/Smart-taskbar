using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Nebula.Interop;

namespace Nebula.Services;

/// <summary>
/// Loads shell icons on a worker thread and applies them on the dispatcher, so
/// typing in a search box never waits for the shell. Requests are de-duplicated
/// by path: while the result list changes on every keystroke, the icon work is
/// paid once per program and never twice at the same time.
/// </summary>
internal static class IconLoader
{
    private static readonly HashSet<string> InFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Misses = new(StringComparer.OrdinalIgnoreCase);

    public static void Request(AppEntry entry)
    {
        if (entry.Icon is not null || entry.WindowHandle != IntPtr.Zero)
            return;

        string path = entry.Path;
        Request(path, icon => entry.Icon = icon);
    }

    public static void Request(string? path, Action<ImageSource> apply)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (InFlight)
        {
            if (Misses.Contains(path) || !InFlight.Add(path))
                return;
        }

        _ = Task.Run(() =>
        {
            ImageSource? icon = null;
            try
            {
                // The extractor also resolves shell names ("shell:AppsFolder\\…",
                // "ms-settings:…"), so the work never probes the UI thread.
                icon = IconExtractor.FromPath(path);
            }
            catch
            {
                // A missing or locked file simply has no icon.
            }

            Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() =>
            {
                lock (InFlight)
                {
                    InFlight.Remove(path);
                    if (icon is null && Misses.Count < 4096)
                        Misses.Add(path);
                }

                if (icon is not null)
                    apply(icon);
            }));
        });
    }
}
