using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using Nebula.Interop;

namespace Nebula.Services;

public sealed class WindowCapture
{
    public IntPtr Handle { get; init; }
    public uint ProcessId { get; init; }
    public string ExePath { get; init; } = "";
    public string Title { get; init; } = "";
    public NativeMethods.WINDOWPLACEMENT Placement { get; init; }
    public ImageSource? Icon { get; init; }
}

public sealed class WindowCandidate
{
    public IntPtr Handle { get; init; }
    public string Title { get; init; } = "";
    public string ProcessName { get; init; } = "";
    public ImageSource? Icon { get; init; }
    public string Subtitle => ProcessName;
}

/// <summary>
/// Validation and native operations for windows owned by other applications.
/// The tab service only accepts windows it can actually hide, show and restore.
/// </summary>
public static class WindowAccess
{
    private static readonly string[] IgnoredClasses =
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "ApplicationFrameWindow", "Windows.UI.Core.CoreWindow",
        "NebulaDock", "NebulaOverlay", "NebulaTabBar"
    };

    public static bool TryCapture(IntPtr hwnd, out WindowCapture? capture, out string reason)
    {
        capture = null;
        reason = "";

        try
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                reason = "la finestra non esiste più";
                return false;
            }

            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                reason = "la finestra non è visibile";
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == Environment.ProcessId)
            {
                reason = "appartiene a Nebula";
                return false;
            }

            string title = ReadText(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                reason = "la finestra non ha un titolo";
                return false;
            }

            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT) != hwnd)
            {
                reason = "non è una finestra principale";
                return false;
            }

            long exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0)
            {
                reason = "è una finestra di servizio";
                return false;
            }

            int cloaked = 0;
            if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 &&
                cloaked != 0)
            {
                reason = "è una finestra UWP protetta o nascosta";
                return false;
            }

            string className = ReadClass(hwnd);
            if (IgnoredClasses.Contains(className, StringComparer.Ordinal))
            {
                reason = "è una finestra di sistema o UWP protetta";
                return false;
            }

            var rect = new NativeMethods.RECT();
            if (!NativeMethods.GetWindowRect(hwnd, out rect) ||
                rect.Right - rect.Left < 30 || rect.Bottom - rect.Top < 24)
            {
                reason = "la finestra è troppo piccola o non ha un'area valida";
                return false;
            }

            IntPtr process = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process == IntPtr.Zero)
            {
                reason = "richiede privilegi superiori o è protetta";
                return false;
            }

            try
            {
                if (!TryGetProcessPath(process, out string exePath))
                {
                    reason = "non è possibile leggere il processo della finestra";
                    return false;
                }

                if (HasHigherIntegrity(process))
                {
                    reason = "richiede privilegi superiori a Nebula";
                    return false;
                }

                var placement = new NativeMethods.WINDOWPLACEMENT
                {
                    length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
                };
                if (!NativeMethods.GetWindowPlacement(hwnd, ref placement))
                {
                    reason = "Windows non consente di memorizzarne la posizione";
                    return false;
                }

                string processName = Path.GetFileNameWithoutExtension(exePath);
                ImageSource? icon = IconExtractor.FromWindow(hwnd, exePath);

                capture = new WindowCapture
                {
                    Handle = hwnd,
                    ProcessId = pid,
                    ExePath = exePath,
                    Title = title.Trim(),
                    Placement = placement,
                    Icon = icon
                };
                return true;
            }
            finally
            {
                NativeMethods.CloseHandle(process);
            }
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static IReadOnlyList<WindowCandidate> EnumerateCandidates()
    {
        var found = new List<WindowCandidate>();
        var seen = new HashSet<IntPtr>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (!seen.Add(hwnd))
                return true;

            if (!TryCapture(hwnd, out WindowCapture? capture, out string _reason) || capture is null)
                return true;

            found.Add(new WindowCandidate
            {
                Handle = capture.Handle,
                Title = capture.Title,
                ProcessName = Path.GetFileNameWithoutExtension(capture.ExePath),
                Icon = capture.Icon
            });
            return true;
        }, IntPtr.Zero);

        return found
            .OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool TryGetWindowRect(IntPtr hwnd, out System.Drawing.Rectangle rect)
    {
        rect = System.Drawing.Rectangle.Empty;
        if (!NativeMethods.IsWindow(hwnd))
            return false;

        if (NativeMethods.IsIconic(hwnd))
        {
            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
            };
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                var normal = placement.rcNormalPosition;
                rect = System.Drawing.Rectangle.FromLTRB(normal.Left, normal.Top, normal.Right, normal.Bottom);
                return rect.Width > 0 && rect.Height > 0;
            }
        }

        var native = new NativeMethods.RECT();
        if (!NativeMethods.GetWindowRect(hwnd, out native))
            return false;

        rect = System.Drawing.Rectangle.FromLTRB(native.Left, native.Top, native.Right, native.Bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    public static void Hide(IntPtr hwnd)
    {
        if (NativeMethods.IsWindow(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    public static void ShowAndActivate(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
            return;

        if (NativeMethods.IsIconic(hwnd))
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        else
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

        ForceForeground(hwnd);
    }

    /// <summary>Restores the exact native placement captured when the tab joined.</summary>
    public static bool RestorePlacement(WindowCapture capture)
    {
        if (!NativeMethods.IsWindow(capture.Handle))
            return true;

        NativeMethods.WINDOWPLACEMENT placement = capture.Placement;
        placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
        bool placed = NativeMethods.SetWindowPlacement(capture.Handle, ref placement);
        if (!placed || !NativeMethods.IsWindowVisible(capture.Handle))
            NativeMethods.ShowWindow(capture.Handle, NativeMethods.SW_SHOW);

        return NativeMethods.IsWindowVisible(capture.Handle);
    }

    public static void Close(IntPtr hwnd)
    {
        if (NativeMethods.IsWindow(hwnd))
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
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

    public static string ReadText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string ReadClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(160);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static bool TryGetProcessPath(IntPtr process, out string path)
    {
        path = "";
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (!NativeMethods.QueryFullProcessImageName(process, 0, sb, ref size))
            return false;

        path = sb.ToString();
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool HasHigherIntegrity(IntPtr process)
    {
        int target = ReadIntegrity(process);
        int current = ReadIntegrity(NativeMethods.GetCurrentProcess());

        if (target >= 0 && current >= 0)
            return target > current;

        // Token metadata is normally available; this fallback still rejects an
        // elevated target when Nebula itself runs at medium integrity.
        bool targetElevated = ReadElevation(process);
        bool currentElevated = ReadElevation(NativeMethods.GetCurrentProcess());
        return targetElevated && !currentElevated;
    }

    private static int ReadIntegrity(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token))
                return -1;

            NativeMethods.GetTokenInformation(
                token, NativeMethods.TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
                return -1;

            buffer = Marshal.AllocHGlobal(length);
            if (!NativeMethods.GetTokenInformation(
                    token, NativeMethods.TokenIntegrityLevel, buffer, length, out _))
                return -1;

            var label = Marshal.PtrToStructure<NativeMethods.TOKEN_MANDATORY_LABEL>(buffer);
            IntPtr sid = label.Label.Sid;
            if (sid == IntPtr.Zero)
                return -1;

            int count = Marshal.ReadByte(NativeMethods.GetSidSubAuthorityCount(sid));
            if (count <= 0)
                return -1;

            return Marshal.ReadInt32(NativeMethods.GetSidSubAuthority(sid, count - 1));
        }
        catch
        {
            return -1;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero)
                NativeMethods.CloseHandle(token);
        }
    }

    private static bool ReadElevation(IntPtr process)
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out token))
                return false;

            int size = Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
            buffer = Marshal.AllocHGlobal(size);
            if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, buffer, size, out _))
                return false;

            var elevation = Marshal.PtrToStructure<NativeMethods.TOKEN_ELEVATION>(buffer);
            return elevation.TokenIsElevated != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero)
                NativeMethods.CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(
        uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(
        IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool altTab);
}
