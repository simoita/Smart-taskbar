using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Nebula.Interop;

namespace Nebula.Services;

public sealed class TrackedWindow
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
}

public sealed class AppGroup
{
    public string ExePath { get; init; } = "";
    public string Name { get; init; } = "";
    public List<TrackedWindow> Windows { get; } = new();
    public ImageSource? Icon { get; set; }
    public bool IsActive { get; set; }

    public TrackedWindow? Primary => Windows.Count > 0 ? Windows[0] : null;
}

public sealed class WindowTracker
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;

    private static readonly string[] IgnoredClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", "NebulaDock", "NebulaOverlay"
    };

    private readonly List<AppGroup> _groups = new();
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string> _exeCache = new();
    private readonly List<IntPtr> _eventHooks = new();
    private readonly int _ownPid = Environment.ProcessId;
    private NativeMethods.WinEventDelegate? _eventCallback;
    private int _refreshScheduled;
    private bool _disposed;
    private string _signature = "";
    private int _refreshes;

    public WindowTracker()
    {
        _eventCallback = OnDesktopEvent;

        // Polling every 1.4 s re-enumerates every top-level window even when
        // nothing changed. Use shell events for the common changes and keep
        // the App timer only as a reconciliation fallback.
        bool foreground = InstallHook(EventSystemForeground, EventSystemForeground);
        bool windows = InstallHook(EventObjectCreate, EventObjectHide);
        bool titles = InstallHook(EventObjectNameChange, EventObjectNameChange);
        bool minimized = InstallHook(EventSystemMinimizeStart, EventSystemMinimizeEnd);
        EventDriven = foreground && windows && titles && minimized;
        if (!EventDriven)
            Log.Write("Window events unavailable; falling back to the reconciliation poll.");
    }

    public IReadOnlyList<AppGroup> Groups => _groups;

    public bool EventDriven { get; }

    public event Action? Updated;

    public void Refresh()
    {
        if (_disposed)
            return;
        // Bound the PID cache: Windows can reuse an exited PID for another
        // executable during a long session.
        _refreshes++;
        if (_refreshes % 60 == 0)
            _exeCache.Clear();

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var groups = new List<AppGroup>();
        var byPath = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        var visible = new List<(IntPtr Hwnd, string Title, string Class, string Exe, bool Iconic)>();

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                    return true;

                long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
                    return true;

                int cloaked = 0;
                NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
                if (cloaked != 0)
                    return true;

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                if (pid == _ownPid)
                    return true;

                string title = ReadWindowText(hwnd);
                if (string.IsNullOrWhiteSpace(title))
                    return true;

                // A minimized window still belongs on the taskbar. Windows
                // reports its rectangle at -32000 with the 160x28 iconic size,
                // so the minimum-size filter must only run on restored windows.
                bool iconic = NativeMethods.IsIconic(hwnd);
                if (!iconic)
                {
                    var rect = new NativeMethods.RECT();
                    if (!NativeMethods.GetWindowRect(hwnd, out rect) ||
                        rect.Right - rect.Left < 40 || rect.Bottom - rect.Top < 30)
                        return true;
                }

                string cls = ReadClass(hwnd);
                if (IgnoredClasses.Contains(cls, StringComparer.Ordinal))
                    return true;

                string? exe = GetProcessPathCached(pid);
                if (string.IsNullOrWhiteSpace(exe))
                    return true;

                visible.Add((hwnd, title.Trim(), cls, exe, iconic));
            }
            catch
            {
                // Skip problematic windows, keep enumerating.
            }

            return true;
        }, IntPtr.Zero);

        // Rebuilding the dock for an unchanged desktop is the most expensive
        // work this poll can do: it re-renders every icon and tooltip. Compare
        // a cheap signature first and stay idle when nothing changed.
        var signature = new StringBuilder(visible.Count * 48 + 16);
        signature.Append(foreground.ToInt64()).Append('|');
        foreach (var window in visible)
        {
            signature.Append(window.Exe).Append('\u001f')
                .Append(window.Hwnd.ToInt64()).Append('\u001f')
                .Append(window.Iconic ? '1' : '0').Append('\u001f')
                .Append(window.Title).Append('\n');
        }

        string current = signature.ToString();
        if (string.Equals(current, _signature, StringComparison.Ordinal))
            return;

        _signature = current;

        foreach (var window in visible)
        {
            if (!byPath.TryGetValue(window.Exe, out AppGroup? group))
            {
                group = new AppGroup { ExePath = window.Exe, Name = GetDisplayName(window.Exe) };
                byPath[window.Exe] = group;
                groups.Add(group);
            }

            group.Windows.Add(new TrackedWindow
            {
                Handle = window.Hwnd,
                Title = window.Title,
                ClassName = window.Class
            });
        }

        foreach (AppGroup group in groups)
        {
            group.IsActive = group.Windows.Any(w => w.Handle == foreground);

            if (!_iconCache.TryGetValue(group.ExePath, out ImageSource? icon))
            {
                icon = IconExtractor.FromWindow(group.Windows[0].Handle, group.ExePath);
                _iconCache[group.ExePath] = icon;
            }

            group.Icon = icon;
        }

        _groups.Clear();
        _groups.AddRange(groups);
        Updated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (IntPtr hook in _eventHooks)
            NativeMethods.UnhookWinEvent(hook);
        _eventHooks.Clear();
        _eventCallback = null;
    }

    private bool InstallHook(uint eventMin, uint eventMax)
    {
        IntPtr hook = NativeMethods.SetWinEventHook(
            eventMin, eventMax, IntPtr.Zero, _eventCallback!, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (hook == IntPtr.Zero)
            return false;

        _eventHooks.Add(hook);
        return true;
    }

    private void OnDesktopEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        // Only top-level window events affect the dock. This filters out the
        // high-frequency child-control events that a broad hook would receive.
        if (_disposed || hwnd == IntPtr.Zero || idObject != 0)
            return;

        switch (eventType)
        {
            case EventSystemForeground:
            case EventSystemMinimizeStart:
            case EventSystemMinimizeEnd:
            case EventObjectCreate:
            case EventObjectDestroy:
            case EventObjectShow:
            case EventObjectHide:
            case EventObjectNameChange:
                ScheduleRefresh();
                break;
        }
    }

    private void ScheduleRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshScheduled, 1) != 0)
            return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _refreshScheduled, 0);
            if (!_disposed)
                Refresh();
        }));
    }

    public void ActivateOrMinimize(AppGroup group)
    {
        TrackedWindow? window = group.Primary;
        if (window is null)
            return;

        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (NativeMethods.IsIconic(window.Handle))
        {
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_RESTORE);
            ForceForeground(window.Handle);
            return;
        }

        if (foreground == window.Handle)
        {
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
            return;
        }

        ForceForeground(window.Handle);
    }

    public void ActivateWindow(IntPtr hwnd)
    {
        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        ForceForeground(hwnd);
    }

    public void MinimizeAll(AppGroup group)
    {
        foreach (TrackedWindow window in group.Windows)
            NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_MINIMIZE);
    }

    public void CloseAll(AppGroup group)
    {
        foreach (TrackedWindow window in group.Windows)
            PostClose(window.Handle);
    }

    public static void PostClose(IntPtr hwnd)
    {
        const uint WM_CLOSE = 0x0010;
        NativeMethods.SendMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Ends the app like Task Manager does: every window gets WM_CLOSE first so
    /// the program can save, then the remaining processes are terminated. The
    /// kill runs on a worker thread because waiting on a hung window would block
    /// the bar.
    /// </summary>
    public void EndTask(AppGroup group)
    {
        var processIds = new HashSet<uint>();
        foreach (TrackedWindow window in group.Windows)
        {
            uint pid;
            NativeMethods.GetWindowThreadProcessId(window.Handle, out pid);
            if (pid != 0 && pid != _ownPid)
                processIds.Add(pid);

            PostClose(window.Handle);
        }

        if (processIds.Count == 0)
            return;

        _ = Task.Run(() =>
        {
            Thread.Sleep(1200);
            foreach (uint pid in processIds)
            {
                if (pid == (uint)_ownPid)
                    continue;

                try
                {
                    using Process process = Process.GetProcessById((int)pid);
                    if (!process.HasExited)
                        process.Kill();
                }
                catch
                {
                    // The window closed cleanly or the process is protected.
                }
            }
        });
    }

    public bool IsForegroundFullscreen(System.Drawing.Rectangle? targetBounds = null)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        uint pid;
        NativeMethods.GetWindowThreadProcessId(foreground, out pid);
        if (pid == _ownPid)
            return false;

        string cls = ReadClass(foreground);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd")
            return false;

        System.Drawing.Rectangle bounds = targetBounds
            ?? System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var rect = new NativeMethods.RECT();
        if (!NativeMethods.GetWindowRect(foreground, out rect))
            return false;

        return rect.Left <= bounds.Left + 1 &&
               rect.Top <= bounds.Top + 1 &&
               rect.Right >= bounds.Right - 1 &&
               rect.Bottom >= bounds.Bottom - 1;
    }

    /// <summary>
    /// True when a visible top-level window covers this whole monitor, even if
    /// it is not the foreground one: a fullscreen game on a second screen must
    /// still put the bar on that screen into auto-hide while the user works on
    /// another monitor.
    /// </summary>
    public bool IsFullscreenOnScreen(System.Drawing.Rectangle bounds)
    {
        if (IsForegroundFullscreen(bounds))
            return true;

        var rect = new NativeMethods.RECT();
        foreach (AppGroup group in _groups)
        {
            foreach (TrackedWindow window in group.Windows)
            {
                if (!NativeMethods.IsWindowVisible(window.Handle) || NativeMethods.IsIconic(window.Handle))
                    continue;

                if (!NativeMethods.GetWindowRect(window.Handle, out rect))
                    continue;

                if (rect.Left <= bounds.Left + 1 && rect.Top <= bounds.Top + 1 &&
                    rect.Right >= bounds.Right - 1 && rect.Bottom >= bounds.Bottom - 1)
                    return true;
            }
        }

        return false;
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(320);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(160);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private string GetDisplayName(string exePath)
    {
        if (_nameCache.TryGetValue(exePath, out string? cached))
            return cached;

        string name = Path.GetFileNameWithoutExtension(exePath);
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
                name = info.FileDescription!.Trim();
            else if (!string.IsNullOrWhiteSpace(info.ProductName))
                name = info.ProductName!.Trim();
        }
        catch
        {
            // File metadata unavailable, keep the file name.
        }

        _nameCache[exePath] = name;
        return name;
    }

    /// <summary>
    /// Process paths do not change during a session: resolving them once per
    /// PID removes an OpenProcess + QueryFullProcessImageName round trip for
    /// every visible window on every poll.
    /// </summary>
    private string? GetProcessPathCached(uint pid)
    {
        if (_exeCache.TryGetValue(pid, out string? cached))
            return cached;

        string? path = GetProcessPath(pid);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_exeCache.Count > 1024)
            _exeCache.Clear();

        _exeCache[pid] = path;
        return path;
    }

    private static string? GetProcessPath(uint pid)
    {
        IntPtr handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size))
                return null;
            return sb.ToString();
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();

        bool attached = false;
        if (foregroundThread != 0 && foregroundThread != currentThread)
            attached = AttachThreadInput(currentThread, foregroundThread, true);

        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            if (!NativeMethods.SetForegroundWindow(hwnd))
                SwitchToThisWindow(hwnd, true);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAltTab);
}
