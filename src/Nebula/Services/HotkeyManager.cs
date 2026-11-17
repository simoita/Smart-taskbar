using System.Windows.Interop;
using Nebula.Interop;

namespace Nebula.Services;

public sealed class HotkeyManager : IDisposable
{
    private const int LauncherId = 0x4E42;
    private const int WidgetsId = 0x4E43;

    private readonly HwndSource _source;
    private bool _disposed;

    public HotkeyManager()
    {
        var parameters = new HwndSourceParameters("NebulaHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        uint mods = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        LauncherRegistered = NativeMethods.RegisterHotKey(_source.Handle, LauncherId, mods, 0x20 /* Space */);
        WidgetsRegistered = NativeMethods.RegisterHotKey(_source.Handle, WidgetsId, mods, 0x57 /* W */);
    }

    public bool LauncherRegistered { get; }
    public bool WidgetsRegistered { get; }

    public event Action? LauncherRequested;
    public event Action? WidgetsRequested;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == LauncherId)
            {
                LauncherRequested?.Invoke();
                handled = true;
            }
            else if (id == WidgetsId)
            {
                WidgetsRequested?.Invoke();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        NativeMethods.UnregisterHotKey(_source.Handle, LauncherId);
        NativeMethods.UnregisterHotKey(_source.Handle, WidgetsId);
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
