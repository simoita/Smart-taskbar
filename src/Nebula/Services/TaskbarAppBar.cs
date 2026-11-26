using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Utilities;

namespace Nebula.Services;

/// <summary>
/// Registers the Nebula bar as a Windows AppBar. This is the same shell
/// mechanism the real taskbar uses: the reserved strip is removed from the
/// work area, so maximized windows stop at the bar instead of covering it.
/// </summary>
public sealed class TaskbarAppBar : IDisposable
{
    private const uint CallbackMessage = 0x0400 + 0x51;
    private const int NotificationPosChanged = 0x0001;

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private bool _top;
    private int _heightPixels;
    private int _monitorIndex = -1;
    private bool _reapplyPending;

    public bool IsRegistered => _registered;

    /// <summary>
    /// Raised when a re-apply triggered by the shell failed, so the dock can
    /// mark its reservation as unhealthy and retry on the next guard tick.
    /// </summary>
    public event Action? ReapplyFailed;

    /// <summary>
    /// The strip Windows actually granted, in physical pixels. The shell can move
    /// the rectangle the app asked for (the bottom edge is shared with the
    /// native bar), so the dock must position itself on this rectangle instead
    /// of assuming it owns the monitor edge.
    /// </summary>
    public System.Drawing.Rectangle AppliedBounds { get; private set; }

    public bool HasAppliedBounds => AppliedBounds.Width > 0 && AppliedBounds.Height > 0;

    public bool Apply(Window window, bool top, double heightDip, int monitorIndex)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return false;

        if (!_registered || handle != _handle)
        {
            Remove();
            if (!Register(handle))
                return false;
        }

        System.Windows.Forms.Screen screen = ScreenGeometry.Target(monitorIndex);
        double scale = ScreenGeometry.Scale(screen);
        int height = Math.Max(24, (int)Math.Round(Math.Max(24, heightDip) * scale));

        bool announce = _monitorIndex != monitorIndex || _top != top || _heightPixels != height;
        if (!Place(handle, top, height, monitorIndex))
            return false;

        _top = top;
        _heightPixels = height;
        _monitorIndex = monitorIndex;
        if (announce)
        {
            Log.Write($"AppBar reserved: edge={(top ? "top" : "bottom")}, {height}px on screen {monitorIndex + 1} " +
                      $"(granted {AppliedBounds.Top}..{AppliedBounds.Bottom}).");
        }

        return true;
    }

    /// <summary>
    /// Asks the shell for the strip and verifies that the work area really
    /// changed. ABM_SETPOS can answer success on a shell that has just restarted
    /// but has not re-registered this HWND, so the reply alone is not trusted.
    /// </summary>
    private bool Place(IntPtr handle, bool top, int height, int monitorIndex)
    {
        System.Windows.Forms.Screen screen = ScreenGeometry.Target(monitorIndex);
        System.Drawing.Rectangle bounds = screen.Bounds;

        var data = NewData(handle);
        data.uEdge = top ? NativeMethods.ABE_TOP : NativeMethods.ABE_BOTTOM;
        data.rc = new NativeMethods.RECT
        {
            Left = bounds.Left,
            Top = top ? bounds.Top : bounds.Bottom - height,
            Right = bounds.Right,
            Bottom = top ? bounds.Top + height : bounds.Bottom
        };

        // Ask the shell first, then keep the full monitor width and let it
        // decide the vertical offset it can actually grant.
        var requested = data.rc;
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
        data.rc.Left = requested.Left;
        data.rc.Right = requested.Right;

        UIntPtr setResult = NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);
        if (setResult == UIntPtr.Zero)
        {
            Log.Write("AppBar reservation failed: the shell refused ABM_SETPOS.");
            return false;
        }

        // ABM_SETPOS reports the rectangle the shell reserved. On the bottom
        // edge it can sit above the space the native bar still claims, which is
        // exactly why a dock drawn from the monitor edge disappeared.
        var granted = new System.Drawing.Rectangle(
            data.rc.Left, data.rc.Top,
            Math.Max(0, data.rc.Right - data.rc.Left),
            Math.Max(0, data.rc.Bottom - data.rc.Top));
        if (granted.Width < 1 || granted.Height < 1)
        {
            Log.Write("AppBar reservation returned an empty rectangle.");
            return false;
        }

        // GetMonitorInfo is live and per-monitor; Screen.WorkingArea may be
        // cached and SPI_GETWORKAREA is only the primary monitor.
        bool reserved = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var center = new NativeMethods.POINT
            {
                X = bounds.Left + bounds.Width / 2,
                Y = bounds.Top + bounds.Height / 2
            };
            IntPtr monitor = NativeMethods.MonitorFromPoint(center, 2);
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            System.Drawing.Rectangle work = monitor != IntPtr.Zero &&
                                              NativeMethods.GetMonitorInfo(monitor, ref info)
                ? System.Drawing.Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
                : screen.WorkingArea;
            reserved = top
                ? work.Top >= data.rc.Bottom - 2
                : work.Bottom <= data.rc.Top + 2;
            if (reserved)
                break;
            System.Threading.Thread.Sleep(10);
        }

        if (!reserved)
        {
            Log.Write($"AppBar reservation was not applied by the shell: edge={(top ? "top" : "bottom")}, rc={data.rc.Top}..{data.rc.Bottom}.");
            return false;
        }

        AppliedBounds = granted;
        return true;
    }

    /// <summary>
    /// Re-applies the cached strip after an ABN_POSCHANGED notification. The
    /// call is deferred to the dispatcher so the shell notification never
    /// re-enters ABM_SETPOS, and it is coalesced when several arrive together.
    /// </summary>
    private void ScheduleReapply()
    {
        if (!_registered || _source is null || _reapplyPending)
            return;

        _reapplyPending = true;
        _source.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _reapplyPending = false;
            if (!_registered || _heightPixels < 24)
                return;

            if (!Place(_handle, _top, _heightPixels, _monitorIndex))
            {
                Log.Write("AppBar re-apply after ABN_POSCHANGED failed; the dock guard will retry.");
                ReapplyFailed?.Invoke();
            }
        }));
    }

    private bool Register(IntPtr handle)
    {
        HwndSource? source = HwndSource.FromHwnd(handle);
        if (source is null)
        {
            Log.Write("AppBar registration failed: no window source.");
            return false;
        }

        var data = NewData(handle);
        data.uCallbackMessage = CallbackMessage;
        UIntPtr result = NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);
        if (result == UIntPtr.Zero)
        {
            Log.Write("AppBar registration was refused by the shell.");
            return false;
        }

        source.AddHook(WndProc);
        _source = source;
        _handle = handle;
        _registered = true;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)CallbackMessage)
        {
            if (wParam.ToInt32() == NotificationPosChanged)
            {
                // Another appbar changed the edge, or the shell moved or
                // resized ours: Microsoft requires a fresh QUERYPOS/SETPOS.
                ScheduleReapply();
            }

            return IntPtr.Zero;
        }

        // The AppBar protocol also expects the window to forward activation and
        // position changes, so the shell keeps the z-order of auto-hide appbars
        // on the same edge correct.
        switch (msg)
        {
            case NativeMethods.WM_ACTIVATE:
                NotifyShell(NativeMethods.ABM_ACTIVATE);
                break;
            case NativeMethods.WM_WINDOWPOSCHANGED:
                NotifyShell(NativeMethods.ABM_WINDOWPOSCHANGED);
                break;
        }

        return IntPtr.Zero;
    }

    private void NotifyShell(uint message)
    {
        if (!_registered)
            return;

        var data = NewData(_handle);
        NativeMethods.SHAppBarMessage(message, ref data);
    }

    private static NativeMethods.APPBARDATA NewData(IntPtr handle) => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.APPBARDATA>(),
        hWnd = handle
    };

    public void Remove()
    {
        if (_registered)
        {
            var data = NewData(_handle);
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
            Log.Write(
                $"AppBar released: edge={(_top ? "top" : "bottom")}, {_heightPixels}px.");
        }

        try
        {
            _source?.RemoveHook(WndProc);
        }
        catch
        {
            // The window source may already be torn down during shutdown.
        }
        _source = null;
        _handle = IntPtr.Zero;
        _registered = false;
        _reapplyPending = false;
        AppliedBounds = System.Drawing.Rectangle.Empty;
    }

    public void Dispose() => Remove();
}
