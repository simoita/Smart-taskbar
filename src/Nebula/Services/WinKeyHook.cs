using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Nebula.Services;

/// <summary>
/// Intercepts a lone press of the Windows key and raises <see cref="WinPressed"/>
/// instead of letting Explorer open the native Start menu. Win+key chords are
/// left untouched (Win+E, Win+D, Win+Shift+S, ...).
/// </summary>
public sealed class WinKeyHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint LlkhfInjected = 0x10;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;
    private bool _winDown;
    private bool _chord;
    private uint _winVirtualKey;
    private uint _winScanCode;
    private bool _disposed;

    public WinKeyHook()
    {
        _callback = HookCallback;
    }

    public event Action? WinPressed;

    public bool IsInstalled => _hook != IntPtr.Zero;

    public bool Install()
    {
        if (_disposed || _hook != IntPtr.Zero)
            return _hook != IntPtr.Zero;

        try
        {
            IntPtr module = GetModuleHandle(null);
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, module, 0);
            if (_hook == IntPtr.Zero)
            {
                // A low-level hook owned by this executable also accepts a null
                // module handle. Retrying covers hosts where GetModuleHandle
                // returns a value the hook API does not accept.
                _hook = SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
            }

            if (_hook == IntPtr.Zero)
                Log.Write("Windows key hook unavailable (error " + Marshal.GetLastWin32Error() +
                          "); native Start menu remains active.");
            return _hook != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Write("Windows key hook failed: " + ex.Message);
            return false;
        }
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero)
            return;

        try
        {
            UnhookWindowsHookEx(_hook);
        }
        catch
        {
            // The OS releases the hook at process exit.
        }

        _hook = IntPtr.Zero;
        _winDown = false;
        _chord = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(_hook, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if ((data.flags & LlkhfInjected) != 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            int message = wParam.ToInt32();
            bool winKey = data.vkCode is VkLWin or VkRWin;

            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (winKey)
                {
                    _winDown = true;
                    _chord = false;
                    _winVirtualKey = data.vkCode;
                    _winScanCode = data.scanCode;
                    // Do not let Explorer see the physical Windows down. If
                    // it did, releasing the key would open the native Start
                    // menu; suppressing only the release also leaves Windows
                    // believing that Win is still held down.
                    return (IntPtr)1;
                }
                else if (_winDown && !_chord)
                {
                    // A chord was pressed. The physical Win down was blocked,
                    // so replay Win and the chord key as injected input before
                    // letting the rest of the chord through. This preserves
                    // Win+E, Win+D and every other native shortcut.
                    _chord = true;
                    InjectKey(_winVirtualKey, _winScanCode);
                    InjectKey(data.vkCode, data.scanCode);
                    return (IntPtr)1;
                }
            }
            else if ((message == WmKeyUp || message == WmSysKeyUp) && winKey && _winDown)
            {
                _winDown = false;
                if (!_chord)
                {
                    // Explorer opens Start on the key-up. Swallowing it is what
                    // makes the Nebula panel replace the native menu.
                    Application.Current?.Dispatcher.BeginInvoke(
                        DispatcherPriority.Input, new Action(() => WinPressed?.Invoke()));
                    return (IntPtr)1;
                }

                // The chord was replayed as injected input; let the matching
                // Win-up reach Windows so the modifier state is released.
            }
        }
        catch
        {
            // Never break the keyboard pipeline because of a malformed event.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Uninstall();
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private static void InjectKey(uint virtualKey, uint scanCode)
    {
        try
        {
            keybd_event((byte)virtualKey, (byte)scanCode, 0, UIntPtr.Zero);
        }
        catch
        {
            // UIPI can refuse injection from a lower-integrity process.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
