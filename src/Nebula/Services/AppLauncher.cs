using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Utilities;

namespace Nebula.Services;

/// <summary>
/// Single entry point used by the dock, the launcher and the Start panel.
/// System components are often not plain executables (Settings is a UWP app,
/// shell: targets are folder namespaces), so a raw Process.Start is not enough.
/// </summary>
internal static class AppLauncher
{
    public static bool Launch(string? rawPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            error = "Percorso non valido.";
            return false;
        }

        string path = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        try
        {
            // Settings is an immersive app whose real path is not SystemSettings.exe
            // in the Windows root. Opening the URI is the only stable route.
            if (path.EndsWith("SystemSettings.exe", StringComparison.OrdinalIgnoreCase))
                return OpenShellTarget("ms-settings:", out error);

            if (path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
                return OpenShellTarget(path, out error);

            if (path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return OpenShellTarget(path, out error);

            // AppUserModelID of a packaged app, e.g. Microsoft.WindowsCalculator_...!App
            if (path.Contains('!') && !File.Exists(path) && !Directory.Exists(path))
                return OpenShellTarget("shell:AppsFolder\\" + path, out error);

            if (Directory.Exists(path))
                return Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true }, out error);

            // .lnk/.url documents must be resolved by the shell so the target can
            // request its own elevation or activation context.
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            {
                if (Start(new ProcessStartInfo(path) { UseShellExecute = true }, out _))
                    return true;

                string? target = ShortcutResolver.ResolveExecutable(path);
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                    return Launch(target, out error);

                error = "Il collegamento non punta a un programma avviabile.";
                return false;
            }

            if (File.Exists(path))
            {
                var info = new ProcessStartInfo(path) { UseShellExecute = true };
                if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    info.WorkingDirectory = Path.GetDirectoryName(path) ?? "";

                // Task Manager, Registry Editor and the MMC snap-ins carry a
                // requireAdministrator manifest. Asking the shell explicitly
                // makes the UAC prompt appear reliably even when the dock runs
                // from a shortcut with a different activation context.
                if (NeedsElevation(path))
                    info.Verb = "runas";

                if (Start(info, out error))
                    return true;

                // A cancelled UAC prompt must not be retried through a second
                // shell activation: that would show the prompt twice.
                if (NeedsElevation(path) &&
                    error?.StartsWith("Avvio annullato", StringComparison.Ordinal) == true)
                    return false;

                // The shell activation path can still succeed where the direct
                // CreateProcess call was denied (auto-elevated system tools).
                if (OpenShellTarget(path, out string? shellError))
                    return true;

                error = shellError ?? error;
                return false;
            }

            // Commands such as "cmd" or registered protocol names.
            if (!path.Contains('\\') && !path.Contains('/'))
                return Start(new ProcessStartInfo(path) { UseShellExecute = true }, out error);

            error = "Il file non esiste più: " + path;
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Avvio annullato (richiesta di privilegi).";
            return false;
        }
        catch (Exception ex)
        {
            error = "Impossibile aprire: " + ex.Message;
            Log.Write($"AppLauncher failed for '{rawPath}': {ex}");
            return false;
        }
    }

    /// <summary>
    /// Launches an app from a bar that sits on a specific monitor and pulls the
    /// new window onto that monitor. Windows decides where a window opens, so
    /// the placement is applied as soon as the process owns a top-level window;
    /// apps without a window (shell targets, URIs) are simply launched.
    /// </summary>
    public static bool LaunchOnMonitor(string? rawPath, int monitorIndex, out string? error)
    {
        string? executable = ResolveExecutable(rawPath);
        HashSet<int> existing = SnapshotProcessIds(executable);

        if (!Launch(rawPath, out error))
            return false;

        if (executable is null || monitorIndex < 0 || monitorIndex >= ScreenGeometry.Count)
            return true;

        _ = Task.Run(() => MoveNewWindow(executable, existing, monitorIndex));
        return true;
    }

    private static string? ResolveExecutable(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        string path = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
            path = ShortcutResolver.ResolveExecutable(path) ?? path;

        return File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private static HashSet<int> SnapshotProcessIds(string? executable)
    {
        var ids = new HashSet<int>();
        if (executable is null)
            return ids;

        try
        {
            foreach (Process process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
            {
                ids.Add(process.Id);
                process.Dispose();
            }
        }
        catch
        {
            // Enumeration is best effort; a missing snapshot only means the
            // placement waits for the first new window it sees.
        }

        return ids;
    }

    private static void MoveNewWindow(string executable, HashSet<int> existing, int monitorIndex)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        System.Windows.Forms.Screen target = ScreenGeometry.Target(monitorIndex);

        for (int attempt = 0; attempt < 40; attempt++)
        {
            Thread.Sleep(150);
            try
            {
                foreach (Process process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (existing.Contains(process.Id))
                            continue;

                        process.Refresh();
                        IntPtr handle = process.MainWindowHandle;
                        if (handle == IntPtr.Zero)
                            continue;

                        PlaceOnScreen(handle, target);
                        return;
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // The process can exit between enumeration and inspection.
            }
        }
    }

    private static void PlaceOnScreen(IntPtr handle, System.Windows.Forms.Screen target)
    {
        if (handle == IntPtr.Zero)
            return;

        if (string.Equals(System.Windows.Forms.Screen.FromHandle(handle).DeviceName,
                target.DeviceName, StringComparison.OrdinalIgnoreCase))
            return;

        var rect = new NativeMethods.RECT();
        if (!NativeMethods.GetWindowRect(handle, out rect))
            return;

        bool maximized = NativeMethods.IsZoomed(handle);
        if (maximized)
            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);

        int width = Math.Max(320, rect.Right - rect.Left);
        int height = Math.Max(240, rect.Bottom - rect.Top);
        System.Drawing.Rectangle work = target.WorkingArea;
        int x = work.Left + Math.Max(0, (work.Width - width) / 2);
        int y = work.Top + Math.Max(0, (work.Height - height) / 2);

        NativeMethods.SetWindowPos(handle, IntPtr.Zero, x, y, width, height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        if (maximized)
            NativeMethods.ShowWindow(handle, NativeMethods.SW_SHOWMAXIMIZED);
    }

    /// <summary>Opens the containing folder and selects the file when possible.</summary>
    public static bool RevealInExplorer(string path, out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
                {
                    UseShellExecute = true
                });
                return true;
            }

            string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                error = "Cartella non disponibile.";
                return false;
            }

            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = "Impossibile aprire Esplora file: " + ex.Message;
            return false;
        }
    }

    public static void RecordRecent(AppSettings settings, string? path)
    {
        if (settings is null || string.IsNullOrWhiteSpace(path))
            return;

        // Shell namespaces and settings pages are not user documents and would
        // only push the useful recent apps out of the list.
        if (path.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            return;

        settings.RecentApps.RemoveAll(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentApps.Insert(0, path);
        while (settings.RecentApps.Count > 12)
            settings.RecentApps.RemoveAt(settings.RecentApps.Count - 1);
    }

    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        if (path.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
            return "Impostazioni di Windows";

        string name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        int separator = path.LastIndexOf('\\');
        return separator >= 0 ? path[(separator + 1)..] : path;
    }

    private static bool OpenShellTarget(string target, out string? error)
    {
        if (target.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
        {
            if (Start(new ProcessStartInfo(target) { UseShellExecute = true }, out error))
                return true;

            return Start(new ProcessStartInfo("explorer.exe", Quote(target)) { UseShellExecute = true }, out error);
        }

        // explorer.exe understands shell:AppsFolder and also launches shell
        // targets that Process.Start rejects with a bare Win32 error.
        if (Start(new ProcessStartInfo("explorer.exe", Quote(target)) { UseShellExecute = true }, out error))
            return true;

        return Start(new ProcessStartInfo(target) { UseShellExecute = true }, out error);
    }

    private static bool Start(ProcessStartInfo info, out string? error)
    {
        error = null;
        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Avvio annullato (richiesta di privilegi).";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool NeedsElevation(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("Taskmgr.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("regedit.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("eventvwr.msc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("services.msc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("devmgmt.msc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("diskmgmt.msc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("taskschd.msc", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("resmon.exe", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("msinfo32.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "") + "\"";
}
