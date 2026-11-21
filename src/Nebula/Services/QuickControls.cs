using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Nebula.Interop;

namespace Nebula.Services;

public enum MediaCommand
{
    PlayPause,
    Previous,
    Next
}

/// <summary>
/// Lightweight native bridge for the quick-controls panel. It uses the
/// existing desktop audio endpoint, system media keys and the monitor DDC API,
/// so no extra process or background service is needed.
/// </summary>
public sealed class QuickControls : IDisposable
{
    private const int ClsctxAll = 23;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint KeyeventfKeyup = 0x0002;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioEndpointVolume? _volume;
    private IntPtr _physicalMonitor;
    private uint _brightnessMin;
    private uint _brightnessMax;
    private bool _brightnessInitialized;
    private bool _disposed;

    public bool VolumeSupported { get; private set; }
    public int VolumePercent { get; private set; }
    public bool Muted { get; private set; }
    public bool BrightnessSupported { get; private set; }
    public int BrightnessPercent { get; private set; } = -1;
    public string NetworkText { get; private set; } = "Rilevatore rete…";
    public bool NetworkAvailable { get; private set; }

    public QuickControls(bool autoRefresh = true)
    {
        if (autoRefresh)
            Refresh();
    }

    public void Refresh()
    {
        if (_disposed)
            return;

        RefreshVolume();
        RefreshBrightness();
        RefreshNetwork();
    }

    /// <summary>
    /// Lightweight refresh used by the taskbar widgets: reading the default
    /// audio endpoint does not touch the monitor DDC channel or enumerate
    /// network adapters, so it is cheap enough for a periodic poll.
    /// </summary>
    public void RefreshVolumeOnly()
    {
        if (!_disposed)
            RefreshVolume();
    }

    /// <summary>Refreshes just the DDC brightness reading.</summary>
    public void RefreshBrightnessOnly()
    {
        if (!_disposed)
            RefreshBrightness();
    }

    /// <summary>
    /// Sends a Windows key chord (for example Win+A for the quick settings
    /// panel). Keys are released in reverse order so the shell never keeps a
    /// modifier stuck down.
    /// </summary>
    public bool SendWinShortcut(byte virtualKey, bool shift = false)
    {
        const byte leftWin = 0x5B;
        const byte shiftKey = 0x10;

        try
        {
            keybd_event(leftWin, 0, 0, UIntPtr.Zero);
            if (shift)
                keybd_event(shiftKey, 0, 0, UIntPtr.Zero);

            keybd_event(virtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(virtualKey, 0, KeyeventfKeyup, UIntPtr.Zero);

            if (shift)
                keybd_event(shiftKey, 0, KeyeventfKeyup, UIntPtr.Zero);
            keybd_event(leftWin, 0, KeyeventfKeyup, UIntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Windows shortcut failed: " + ex.Message);
            return false;
        }
    }

    public bool SetVolume(int percent, out string? error)
    {
        error = null;
        percent = Math.Clamp(percent, 0, 100);

        if (!EnsureVolume())
        {
            error = "Il controllo volume di Windows non è disponibile.";
            return false;
        }

        try
        {
            Guid context = Guid.Empty;
            int hr = _volume!.SetMasterVolumeLevelScalar(percent / 100f, ref context);
            if (hr != 0)
            {
                error = "Windows ha rifiutato il cambio di volume.";
                VolumeSupported = false;
                return false;
            }

            VolumePercent = percent;
            if (_volume.GetMute(out int muted) == 0)
                Muted = muted != 0;
            return true;
        }
        catch (Exception ex)
        {
            error = "Volume non disponibile: " + ex.Message;
            VolumeSupported = false;
            return false;
        }
    }

    public bool ToggleMute(out bool muted, out string? error)
    {
        muted = Muted;
        error = null;

        if (!EnsureVolume())
        {
            error = "Il controllo volume di Windows non è disponibile.";
            return false;
        }

        try
        {
            bool next = !Muted;
            Guid context = Guid.Empty;
            int hr = _volume!.SetMute(next ? 1 : 0, ref context);
            if (hr != 0)
            {
                error = "Windows ha rifiutato il comando mute.";
                return false;
            }

            Muted = next;
            muted = next;
            return true;
        }
        catch (Exception ex)
        {
            error = "Mute non disponibile: " + ex.Message;
            return false;
        }
    }

    public bool SendMedia(MediaCommand command)
    {
        byte key = command switch
        {
            MediaCommand.PlayPause => 0xB3,
            MediaCommand.Next => 0xB0,
            MediaCommand.Previous => 0xB1,
            _ => 0xB3
        };

        try
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            keybd_event(key, 0, KeyeventfKeyup, UIntPtr.Zero);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Media key failed: " + ex.Message);
            return false;
        }
    }

    public bool OpenNetworkSettings(out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = "Impossibile aprire le impostazioni di rete: " + ex.Message;
            return false;
        }
    }

    public bool OpenSoundSettings(out string? error)
    {
        error = null;
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:sound") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = "Impossibile aprire le impostazioni audio: " + ex.Message;
            return false;
        }
    }

    public bool SetBrightness(int percent, out string? error)
    {
        error = null;
        percent = Math.Clamp(percent, 0, 100);

        if (!EnsureBrightness())
        {
            error = "Luminosità non controllabile su questo monitor. Windows espone il controllo solo per alcuni display integrati.";
            return false;
        }

        try
        {
            uint value = _brightnessMin +
                         (uint)Math.Round((_brightnessMax - _brightnessMin) * (percent / 100.0));
            if (!SetMonitorBrightness(_physicalMonitor, value))
            {
                error = "Il monitor ha rifiutato il cambio di luminosità.";
                BrightnessSupported = false;
                return false;
            }

            BrightnessPercent = percent;
            return true;
        }
        catch (Exception ex)
        {
            error = "Luminosità non disponibile: " + ex.Message;
            BrightnessSupported = false;
            return false;
        }
    }

    private bool EnsureVolume()
    {
        if (_volume is not null)
            return true;

        try
        {
            _enumerator ??= (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            int hr = _enumerator.GetDefaultAudioEndpoint(0, 1, out _device);
            if (hr != 0 || _device is null)
                return false;

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            hr = _device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object endpoint);
            if (hr != 0 || endpoint is not IAudioEndpointVolume volume)
                return false;

            _volume = volume;
            VolumeSupported = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Audio endpoint unavailable: " + ex.Message);
            VolumeSupported = false;
            return false;
        }
    }

    private void RefreshVolume()
    {
        if (!EnsureVolume())
            return;

        try
        {
            Guid context = Guid.Empty;
            if (_volume!.GetMasterVolumeLevelScalar(out float level) == 0)
                VolumePercent = (int)Math.Round(level * 100);

            if (_volume.GetMute(out int muted) == 0)
                Muted = muted != 0;

            VolumeSupported = true;
        }
        catch (Exception ex)
        {
            Log.Write("Volume refresh failed: " + ex.Message);
            VolumeSupported = false;
        }
    }

    private bool EnsureBrightness()
    {
        if (_brightnessInitialized)
            return BrightnessSupported;

        _brightnessInitialized = true;
        try
        {
            var point = new NativeMethods.POINT { X = 0, Y = 0 };
            IntPtr monitor = MonitorFromPoint(point, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero ||
                !GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out uint count) ||
                count == 0)
            {
                return false;
            }

            var monitors = new PHYSICAL_MONITOR[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors))
                return false;

            _physicalMonitor = monitors[0].hPhysicalMonitor;
            if (_physicalMonitor == IntPtr.Zero)
                return false;

            if (!GetMonitorBrightness(_physicalMonitor, out _brightnessMin,
                    out uint current, out _brightnessMax) ||
                _brightnessMax <= _brightnessMin)
            {
                DestroyPhysicalMonitors(count, monitors);
                _physicalMonitor = IntPtr.Zero;
                return false;
            }

            BrightnessPercent = (int)Math.Round(
                (current - _brightnessMin) * 100.0 / (_brightnessMax - _brightnessMin));
            BrightnessSupported = true;
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Brightness endpoint unavailable: " + ex.Message);
            return false;
        }
    }

    private void RefreshBrightness()
    {
        if (!EnsureBrightness())
            return;

        try
        {
            if (GetMonitorBrightness(_physicalMonitor, out _brightnessMin,
                    out uint current, out _brightnessMax) &&
                _brightnessMax > _brightnessMin)
            {
                BrightnessPercent = (int)Math.Round(
                    (current - _brightnessMin) * 100.0 / (_brightnessMax - _brightnessMin));
                BrightnessSupported = true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Brightness refresh failed: " + ex.Message);
            BrightnessSupported = false;
        }
    }

    private void RefreshNetwork()
    {
        try
        {
            NetworkInterface[] active = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0
                    : n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 1
                    : 2)
                .ThenByDescending(n => n.Speed)
                .ToArray();

            NetworkAvailable = active.Length > 0;
            if (!NetworkAvailable)
            {
                NetworkText = "Nessuna connessione attiva";
                return;
            }

            NetworkInterface fastest = active[0];
            string type = fastest.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet => "Ethernet",
                _ => fastest.NetworkInterfaceType.ToString()
            };
            string speed = fastest.Speed switch
            {
                >= 1_000_000_000 => $" · {fastest.Speed / 1_000_000_000.0:0.#} Gbps",
                > 0 => $" · {fastest.Speed / 1_000_000} Mbps",
                _ => ""
            };
            string label = fastest.Name.Contains(type, StringComparison.OrdinalIgnoreCase)
                ? fastest.Name
                : $"{fastest.Name} · {type}";
            NetworkText = label + speed;
        }
        catch (Exception ex)
        {
            NetworkText = "Stato rete non disponibile";
            NetworkAvailable = false;
            Log.Write("Network refresh failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_physicalMonitor != IntPtr.Zero)
        {
            try
            {
                uint count = 1;
                var monitors = new PHYSICAL_MONITOR[1];
                monitors[0].hPhysicalMonitor = _physicalMonitor;
                DestroyPhysicalMonitors(count, monitors);
            }
            catch
            {
                // Native cleanup is best effort.
            }

            _physicalMonitor = IntPtr.Zero;
        }

        if (_volume is not null && Marshal.IsComObject(_volume))
            Marshal.ReleaseComObject(_volume);
        if (_device is not null && Marshal.IsComObject(_device))
            Marshal.ReleaseComObject(_device);
        if (_enumerator is not null && Marshal.IsComObject(_enumerator))
            Marshal.ReleaseComObject(_enumerator);

        _volume = null;
        _device = null;
        _enumerator = null;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativeMethods.POINT pt, uint dwFlags);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr hMonitor, uint arraySize, [Out] PHYSICAL_MONITOR[] physicalMonitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(
        IntPtr hMonitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr hMonitor, uint newBrightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(
        uint arraySize, [In] PHYSICAL_MONITOR[] physicalMonitors);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IntPtr notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute(int mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute(out int muted);
    }
}
