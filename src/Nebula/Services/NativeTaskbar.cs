using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Nebula.Interop;

namespace Nebula.Services;

/// <summary>
/// Replaces the Windows shell bar: auto-hides it, takes it off screen and arms a
/// tiny watchdog process that restores it even when Nebula is force-killed.
/// </summary>
public sealed class NativeTaskbarController
{
    private const int AbsAutoHide = 1;
    private const int AbsAlwaysOnTop = 2;

    private static readonly string StateFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula", "taskbar.pending");

    private readonly string _session = Guid.NewGuid().ToString("N");
    private Process? _watchdog;
    private int? _watchdogTimeoutSeconds;
    private bool _canHide;
    private NativeMethods.WinEventDelegate? _showCallback;
    private IntPtr _showHook;

    private const int FullSweepTicks = 4;
    private int _protectionTicks;

    public bool IsApplied { get; private set; }
    public int OriginalState { get; private set; }

    /// <summary>
    /// Raised when the watchdog can no longer guarantee a restore. The app must
    /// stop the replacement instead of keeping the shell bar hidden.
    /// </summary>
    public event Action? ProtectionLost;

    public bool Apply(bool replace, int? watchdogTimeoutSeconds = null)
    {
        _watchdogTimeoutSeconds = watchdogTimeoutSeconds;
        if (!replace)
        {
            Restore();
            return true;
        }

        if (IsApplied)
        {
            if (_canHide)
            {
                HideTray();
                EnsureProtection();
            }
            else if (!TryEnableProtection())
            {
                Restore();
                return false;
            }

            return IsApplied && _canHide;
        }

        if (!Preflight(out string reason))
        {
            Log.Write("Taskbar replacement aborted; native taskbar left visible: " + reason);
            return false;
        }

        OriginalState = GetState();

        if (!WritePending())
        {
            Log.Write("Taskbar replacement aborted: recovery state could not be written.");
            return false;
        }

        if (!StartWatchdog())
        {
            Log.Write("Taskbar replacement aborted: watchdog unavailable.");
            TryDelete(StateFile);
            return false;
        }

        _canHide = true;
        InstallHideHook();
        IsApplied = true;
        SetState(AbsAutoHide);
        HideTray();

        if (!TrayHidden())
        {
            Log.Write("Taskbar replacement failed verification: a shell bar is still visible; restoring.");
            Restore();
            return false;
        }

        Log.Write("Native taskbar hidden; crash watchdog armed.");
        return true;
    }

    /// <summary>Keeps the native bar hidden and the watchdog alive while replacement is active.</summary>
    public void EnsureProtection()
    {
        if (!IsApplied || !_canHide)
            return;

        if (_watchdog is not { HasExited: false })
        {
            Log.Write("Taskbar watchdog is no longer running; restarting it.");
            if (!WritePending() || !StartWatchdog())
            {
                Log.Write("Taskbar watchdog could not be restarted; restoring the native taskbar.");
                Restore();
                ProtectionLost?.Invoke();
                return;
            }
        }

        InstallHideHook();

        // The WinEvent hook hides a bar as soon as Explorer shows it. The timer is
        // only the safety net, so most ticks do one cheap lookup; the full sweep
        // that covers secondary monitors runs every four seconds.
        _protectionTicks++;
        if (_protectionTicks % FullSweepTicks == 0)
        {
            HideTray();
            if (!TrayHidden())
            {
                Log.Write("Taskbar guard could not keep every shell bar hidden; restoring.");
                Restore();
                ProtectionLost?.Invoke();
            }
            return;
        }

        HidePrimaryTrayIfVisible();
    }

    public void Restore()
    {
        if (!IsApplied)
            return;

        // Disarm first: showing the tray while the hook is still armed would
        // make the callback hide it again immediately.
        RemoveHideHook();
        IsApplied = false;
        _canHide = false;
        ShowTray();
        // Never restore into auto-hide: the user must get a visible bar back.
        SetState(OriginalState & AbsAlwaysOnTop);
        TryDelete(StateFile);
        Log.Write("Native taskbar restored.");
    }

    /// <summary>
    /// Emergency path used by <c>--restore-taskbar</c>. It does not depend on
    /// this process having hidden the bar: it always makes the shell bars visible.
    /// </summary>
    public static void RestoreNow()
    {
        try
        {
            ShowTray();
            SetState(0);
            TryDelete(StateFile);
            Log.Write("Emergency restore: native taskbar shown and pending state cleared.");
        }
        catch (Exception ex)
        {
            Log.Write("Emergency taskbar restore failed: " + ex.Message);
        }
    }

    /// <summary>True when at least one primary/secondary shell bar is visible.</summary>
    public static bool AnyTrayVisible()
    {
        try
        {
            return TrayWindows().Any(NativeMethods.IsWindowVisible);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Refuses to hide the shell unless Explorer's taskbar exists, the watchdog
    /// is deployed beside the app and the recovery state can be written. This is
    /// the "do not hide if verification cannot pass" gate for normal startup too.
    /// </summary>
    public static bool Preflight(out string reason)
    {
        reason = "";
        try
        {
            if (NativeMethods.FindWindow("Shell_TrayWnd", null) == IntPtr.Zero)
            {
                reason = "Shell_TrayWnd non trovata (Explorer non è attivo).";
                return false;
            }

            string watchdog = Path.Combine(AppContext.BaseDirectory, "Nebula.Watchdog.exe");
            if (!File.Exists(watchdog))
            {
                reason = "Nebula.Watchdog.exe non trovato accanto all'eseguibile.";
                return false;
            }

            string dir = Path.GetDirectoryName(StateFile)!;
            Directory.CreateDirectory(dir);
            string probe = StateFile + ".probe";
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex)
        {
            reason = "Stato di recupero non scrivibile: " + ex.Message;
            return false;
        }
    }

    private static bool TrayHidden()
    {
        try
        {
            return TrayWindows().All(tray => !NativeMethods.IsWindowVisible(tray));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restores the taskbar if a previous session ended without cleaning up.</summary>
    public static void RecoverFromUncleanExit()
    {
        try
        {
            if (!File.Exists(StateFile))
                return;

            string[] lines = File.ReadAllLines(StateFile);
            int state = 0;
            if (lines.Length >= 2)
                int.TryParse(lines[1].Trim(), out state);
            else if (lines.Length == 1)
                int.TryParse(lines[0].Trim(), out state);

            // Safety first: after a crash leave the bar visible, not auto-hidden.
            ShowTray();
            SetState(state & AbsAlwaysOnTop);
            File.Delete(StateFile);
            Log.Write("Recovered taskbar visibility after unclean exit.");
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar recovery failed: " + ex.Message);
        }
    }

    public static int GetState()
    {
        try
        {
            var data = NewData();
            return (int)NativeMethods.SHAppBarMessage(NativeMethods.ABM_GETSTATE, ref data);
        }
        catch
        {
            return 0;
        }
    }

    private bool TryEnableProtection()
    {
        if (!Preflight(out string reason))
        {
            Log.Write("Taskbar protection could not be enabled: " + reason);
            return false;
        }

        if (!WritePending())
            return false;

        if (!StartWatchdog())
        {
            TryDelete(StateFile);
            return false;
        }

        _canHide = true;
        InstallHideHook();
        IsApplied = true;
        SetState(AbsAutoHide);
        HideTray();
        if (!TrayHidden())
        {
            Log.Write("Taskbar protection verification failed; restoring.");
            Restore();
            return false;
        }

        Log.Write("Taskbar watchdog enabled; native taskbar hidden.");
        return true;
    }

    /// <summary>
    /// Hides the native bar the instant the shell shows it again (for example
    /// when hovering the autohide edge or after an Explorer restart), instead
    /// of waiting for the next watchdog tick.
    /// </summary>
    private void InstallHideHook()
    {
        if (_showHook != IntPtr.Zero)
            return;

        _showCallback = OnWindowShown;
        _showHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
            IntPtr.Zero, _showCallback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);

        if (_showHook == IntPtr.Zero)
            Log.Write("Taskbar show hook unavailable; the guard timer will keep hiding it.");
    }

    private void RemoveHideHook()
    {
        if (_showHook == IntPtr.Zero)
            return;

        NativeMethods.UnhookWinEvent(_showHook);
        _showHook = IntPtr.Zero;
        _showCallback = null;
    }

    private void OnWindowShown(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        uint thread, uint time)
    {
        if (!IsApplied || !_canHide || hwnd == IntPtr.Zero)
            return;

        var name = new StringBuilder(64);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        string className = name.ToString();
        if (!string.Equals(className, "Shell_TrayWnd", StringComparison.Ordinal) &&
            !string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
            return;

        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
    }

    private bool StartWatchdog()
    {
        if (_watchdog is { HasExited: false })
            return true;

        _watchdog?.Dispose();
        _watchdog = null;

        string exe = Path.Combine(AppContext.BaseDirectory, "Nebula.Watchdog.exe");
        if (!File.Exists(exe))
        {
            Log.Write("Watchdog executable not found: " + exe);
            return false;
        }

        try
        {
            string timeoutArg = _watchdogTimeoutSeconds is int seconds ? " " + seconds : "";
            var start = new ProcessStartInfo(exe, $"{Environment.ProcessId} {_session}{timeoutArg}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _watchdog = Process.Start(start);
            if (_watchdog is null)
                return false;

            // A watchdog that dies immediately (missing runtime, unreadable
            // binary) must abort the replacement instead of leaving the shell
            // bar hidden without a way back.
            if (_watchdog.WaitForExit(400))
            {
                Log.Write($"Taskbar watchdog exited during startup (code {_watchdog.ExitCode}).");
                _watchdog.Dispose();
                _watchdog = null;
                return false;
            }

            Log.Write("Taskbar watchdog started.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar watchdog could not start: " + ex.Message);
            return false;
        }
    }

    private bool WritePending()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, _session + Environment.NewLine + OriginalState);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar recovery state write failed: " + ex.Message);
            return false;
        }
    }

    private static void SetState(int state)
    {
        try
        {
            var data = NewData();
            data.lParam = (IntPtr)state;
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETSTATE, ref data);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar state change failed: " + ex.Message);
        }
    }

    private static NativeMethods.APPBARDATA NewData()
    {
        return new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = NativeMethods.FindWindow("Shell_TrayWnd", null)
        };
    }

    private static void HidePrimaryTrayIfVisible()
    {
        try
        {
            IntPtr primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero && NativeMethods.IsWindowVisible(primary))
                NativeMethods.ShowWindow(primary, NativeMethods.SW_HIDE);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar hide failed: " + ex.Message);
        }
    }

    private static void HideTray()
    {
        try
        {
            foreach (IntPtr tray in TrayWindows())
                NativeMethods.ShowWindow(tray, NativeMethods.SW_HIDE);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar hide failed: " + ex.Message);
        }
    }

    private static void ShowTray()
    {
        try
        {
            foreach (IntPtr tray in TrayWindows())
                NativeMethods.ShowWindow(tray, NativeMethods.SW_SHOW);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar show failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Finds the primary taskbar and every secondary taskbar, so replacing the
    /// shell bar also works on multi-monitor setups.
    /// </summary>
    private static List<IntPtr> TrayWindows()
    {
        var handles = new List<IntPtr>();
        IntPtr primary = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero)
            handles.Add(primary);

        NativeMethods.EnumWindows((handle, _) =>
        {
            var name = new StringBuilder(64);
            NativeMethods.GetClassName(handle, name, name.Capacity);
            if (string.Equals(name.ToString(), "Shell_SecondaryTrayWnd", StringComparison.Ordinal))
                handles.Add(handle);
            return true;
        }, IntPtr.Zero);

        return handles;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
