using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nebula.Watchdog;

/// <summary>
/// Lives only as long as the Nebula process. If Nebula disappears for any reason
/// (including taskkill /F), this restores the shell taskbar instead of leaving the
/// user without one. It is intentionally dependency-free and does not use WPF.
/// </summary>
internal static class Program
{
    private const int Synchronize = 0x00100000;
    private const int Terminate = 0x0001;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitTimeout = 0x00000102;
    private const int SwShow = 5;
    private const uint AbmSetState = 0x0000000A;
    private const uint AbsAlwaysOnTop = 0x00000002;

    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula");
    private static readonly string PendingPath = Path.Combine(StateDir, "taskbar.pending");
    private static readonly string TabsPendingPath = Path.Combine(StateDir, "tabs.pending");
    private static readonly string LogPath = Path.Combine(StateDir, "watchdog.log");

    [STAThread]
    private static void Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || !int.TryParse(args[0], out int parentPid) || string.IsNullOrWhiteSpace(args[1]))
            {
                Log("watchdog started without a valid parent/session");
                return;
            }

            string session = args[1];
            int timeoutSeconds = 0;
            if (args.Length >= 3 && int.TryParse(args[2], out int parsedTimeout))
                timeoutSeconds = Math.Clamp(parsedTimeout, 5, 600);

            Log($"watchdog armed pid={parentPid} session={session[..Math.Min(8, session.Length)]}" +
                (timeoutSeconds > 0 ? $" timeout={timeoutSeconds}s" : ""));

            IntPtr handle = OpenProcess(Synchronize | Terminate, false, parentPid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    uint wait = timeoutSeconds > 0 ? (uint)(timeoutSeconds * 1000) : Infinite;
                    uint result = WaitForSingleObject(handle, wait);
                    if (result == WaitTimeout && timeoutSeconds > 0)
                    {
                        Log("selftest/emergency timeout reached; terminating the parent for a guaranteed restore");
                        TerminateProcess(handle, 3);
                        Thread.Sleep(120);
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            else
            {
                Log("parent handle unavailable; restoring immediately");
            }

            // Give a clean shutdown the chance to restore first.
            Thread.Sleep(180);
            RestoreTabs();
            RestorePending(session);
        }
        catch (Exception ex)
        {
            Log("watchdog failure: " + ex);
        }
    }

    private static void RestorePending(string session)
    {
        (string Session, int State)? pending = ReadPending();
        if (pending is null)
        {
            Log("no pending state; nothing to restore");
            return;
        }

        if (!string.Equals(pending.Value.Session, session, StringComparison.Ordinal))
        {
            Log("pending state belongs to another session; leaving it alone");
            return;
        }

        // A crash restore always leaves the bar on screen. Auto-hide would hide it
        // again and defeat the safety guarantee; keep the always-on-top bit only.
        int visibleState = pending.Value.State & (int)AbsAlwaysOnTop;
        RestoreTaskbar(visibleState);
        TryDeletePending();
        Log($"taskbar restored after parent exit (state={visibleState})");
    }

    private static void RestoreTabs()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!File.Exists(TabsPendingPath))
                {
                    Log("no pending tab groups; nothing to restore");
                    return;
                }

                string[] lines = File.ReadAllLines(TabsPendingPath);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "NebulaTabs v1", StringComparison.Ordinal))
                {
                    Log("tabs.pending has an unknown format; leaving it for the app");
                    return;
                }

                int restored = 0;
                int failed = 0;
                foreach (string line in lines.Skip(1))
                {
                    if (!TryParseTab(line, out IntPtr hwnd, out WindowPlacement placement))
                        continue;

                    if (!IsWindow(hwnd))
                        continue;

                    placement.length = Marshal.SizeOf<WindowPlacement>();
                    bool placed = SetWindowPlacement(hwnd, ref placement);
                    if (!placed || !IsWindowVisible(hwnd))
                    {
                        // SetWindowPlacement can report success yet leave a window
                        // hidden (observed with a member that was foreground at kill).
                        ShowWindow(hwnd, SwShow);
                    }

                    if (IsWindowVisible(hwnd))
                        restored++;
                    else
                        failed++;
                }

                if (failed == 0)
                {
                    try { File.Delete(TabsPendingPath); }
                    catch { }
                }

                Log($"restored {restored} grouped window(s) after parent exit" +
                    (failed > 0 ? $"; {failed} could not be restored" : ""));
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Log("tab restore failure: " + ex.Message);
                return;
            }
        }
    }

    private static bool TryParseTab(string line, out IntPtr hwnd, out WindowPlacement placement)
    {
        hwnd = IntPtr.Zero;
        placement = default;
        string[] parts = line.Split('|');
        if (parts.Length < 12 || parts[0] != "window")
            return false;

        if (!long.TryParse(parts[1], out long handle) ||
            !int.TryParse(parts[2], out int flags) ||
            !int.TryParse(parts[3], out int show))
            return false;

        int[] rest = new int[8];
        for (int i = 0; i < rest.Length; i++)
        {
            if (!int.TryParse(parts[i + 4], out rest[i]))
                return false;
        }

        placement = new WindowPlacement
        {
            length = Marshal.SizeOf<WindowPlacement>(),
            flags = flags,
            showCmd = show,
            ptMinPosition = new Point { X = rest[0], Y = rest[1] },
            ptMaxPosition = new Point { X = rest[2], Y = rest[3] },
            rcNormalPosition = new SmallRect
            {
                Left = rest[4],
                Top = rest[5],
                Right = rest[6],
                Bottom = rest[7]
            }
        };
        hwnd = (IntPtr)handle;
        return true;
    }

    private static (string Session, int State)? ReadPending()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (!File.Exists(PendingPath))
                    return null;

                string[] lines = File.ReadAllLines(PendingPath);
                if (lines.Length >= 2)
                {
                    string session = lines[0].Trim();
                    if (int.TryParse(lines[1].Trim(), out int state))
                        return (session, state);
                }
                else if (lines.Length == 1 && int.TryParse(lines[0].Trim(), out int oldState))
                {
                    // Compatibility with the pre-watchdog single-value file.
                    return ("legacy", oldState);
                }
            }
            catch (IOException)
            {
                // The app may still be writing the file.
            }

            Thread.Sleep(50);
        }

        return null;
    }

    private static void RestoreTaskbar(int state)
    {
        for (int attempt = 0; attempt < 24; attempt++)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                ShowWindow(tray, SwShow);
                SetState(tray, state);
                if (IsWindowVisible(tray))
                    return;
            }

            Thread.Sleep(125);
        }
    }

    private static void SetState(IntPtr tray, int state)
    {
        var data = new AppBarData
        {
            cbSize = Marshal.SizeOf<AppBarData>(),
            hWnd = tray,
            lParam = (IntPtr)state
        };
        SHAppBarMessage(AbmSetState, ref data);
    }

    private static void TryDeletePending()
    {
        try
        {
            if (File.Exists(PendingPath))
                File.Delete(PendingPath);
        }
        catch
        {
            // The next launch also performs recovery.
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Never let logging prevent a restore.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int length;
        public int flags;
        public int showCmd;
        public Point ptMinPosition;
        public Point ptMaxPosition;
        public SmallRect rcNormalPosition;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref AppBarData data);
}
