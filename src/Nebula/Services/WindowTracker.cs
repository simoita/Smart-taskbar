using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
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
    private static readonly string[] IgnoredClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow", "NebulaDock", "NebulaOverlay"
    };

    private readonly List<AppGroup> _groups = new();
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _ownPid = Environment.ProcessId;

    public IReadOnlyList<AppGroup> Groups => _groups;

    public event Action? Updated;

    public void Refresh()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        var groups = new List<AppGroup>();
        var byPath = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        var visible = new List<(IntPtr Hwnd, string Title, string Class, string Exe)>();

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

                var rect = new NativeMethods.RECT();
                if (!NativeMethods.GetWindowRect(hwnd, out rect) || rect.Right - rect.Left < 40 || rect.Bottom - rect.Top < 30)
                    return true;

                string cls = ReadClass(hwnd);
                if (IgnoredClasses.Contains(cls, StringComparer.Ordinal))
                    return true;

                string? exe = GetProcessPath(pid);
                if (string.IsNullOrWhiteSpace(exe))
                    return true;

                visible.Add((hwnd, title.Trim(), cls, exe));
            }
            catch
            {
                // Skip problematic windows, keep enumerating.
            }

            return true;
        }, IntPtr.Zero);

        foreach ((IntPtr hwnd, string title, string cls, string exe) in visible)
        {
            if (!byPath.TryGetValue(exe, out AppGroup? group))
            {
                group = new AppGroup { ExePath = exe, Name = GetDisplayName(exe) };
                byPath[exe] = group;
                groups.Add(group);
            }

            group.Windows.Add(new TrackedWindow { Handle = hwnd, Title = title, ClassName = cls });
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
