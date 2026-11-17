using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private bool _canHide;

    public bool IsApplied { get; private set; }
    public int OriginalState { get; private set; }

    public void Apply(bool replace)
    {
        if (!replace)
        {
            Restore();
            return;
        }

        if (IsApplied)
        {
            if (_canHide)
            {
                HideTray();
                EnsureProtection();
            }
            else
            {
                TryEnableProtection();
            }

            return;
        }

        OriginalState = GetState();

        if (!WritePending())
        {
            Log.Write("Taskbar recovery state could not be written; using safe auto-hide fallback.");
            SetState(AbsAutoHide);
            IsApplied = true;
            return;
        }

        if (!StartWatchdog())
        {
            Log.Write("Taskbar watchdog unavailable; using safe auto-hide fallback.");
            TryDelete(StateFile);
            SetState(AbsAutoHide);
            IsApplied = true;
            return;
        }

        _canHide = true;
        SetState(AbsAutoHide);
        HideTray();
        IsApplied = true;
        Log.Write("Native taskbar hidden; crash watchdog armed.");
    }

    /// <summary>Keeps the native bar hidden and the watchdog alive while replacement is active.</summary>
    public void EnsureProtection()
    {
        if (!IsApplied || !_canHide)
            return;

        if (_watchdog is { HasExited: false })
        {
            HideTray();
            return;
        }

        Log.Write("Taskbar watchdog is no longer running; restarting it.");
        if (!WritePending() || !StartWatchdog())
        {
            Log.Write("Taskbar watchdog could not be restarted; restoring the native taskbar.");
            Restore();
            return;
        }

        HideTray();
    }

    public void Restore()
    {
        if (!IsApplied)
            return;

        ShowTray();
        SetState(OriginalState);
        IsApplied = false;
        _canHide = false;
        TryDelete(StateFile);
        Log.Write("Native taskbar restored.");
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

    private void TryEnableProtection()
    {
        if (!WritePending())
            return;

        if (!StartWatchdog())
        {
            TryDelete(StateFile);
            return;
        }

        _canHide = true;
        SetState(AbsAutoHide);
        HideTray();
        Log.Write("Taskbar watchdog enabled; native taskbar hidden.");
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
            var start = new ProcessStartInfo(exe, $"{Environment.ProcessId} {_session}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory
            };

            _watchdog = Process.Start(start);
            if (_watchdog is null)
                return false;

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

    private static void HideTray()
    {
        try
        {
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
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
            IntPtr tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
                NativeMethods.ShowWindow(tray, NativeMethods.SW_SHOW);
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar show failed: " + ex.Message);
        }
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
