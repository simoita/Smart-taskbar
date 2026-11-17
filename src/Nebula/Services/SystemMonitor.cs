using System.Collections.ObjectModel;
using System.IO;
using System.Net.NetworkInformation;
using Nebula.Interop;
using Nebula.Models;

namespace Nebula.Services;

public sealed class SystemMonitor : ObservableObject
{
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _primed;
    private double _cpu;
    private double _ram;
    private string _cpuText = "--";
    private string _ramText = "--";
    private string _batteryText = "--";
    private bool _hasBattery;
    private bool _isCharging;
    private string _uptime = "--";
    private string _ramDetail = "";
    private string _diskText = "";
    private string _networkText = "Rete";
    private bool _networkAvailable;

    public ObservableCollection<double> CpuHistory { get; } = new();

    public double Cpu
    {
        get => _cpu;
        private set => SetProperty(ref _cpu, value);
    }

    public double Ram
    {
        get => _ram;
        private set => SetProperty(ref _ram, value);
    }

    public string CpuText
    {
        get => _cpuText;
        private set => SetProperty(ref _cpuText, value);
    }

    public string RamText
    {
        get => _ramText;
        private set => SetProperty(ref _ramText, value);
    }

    public string RamDetail
    {
        get => _ramDetail;
        private set => SetProperty(ref _ramDetail, value);
    }

    public string BatteryText
    {
        get => _batteryText;
        private set => SetProperty(ref _batteryText, value);
    }

    public bool HasBattery
    {
        get => _hasBattery;
        private set => SetProperty(ref _hasBattery, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => SetProperty(ref _isCharging, value);
    }

    public string Uptime
    {
        get => _uptime;
        private set => SetProperty(ref _uptime, value);
    }

    public string DiskText
    {
        get => _diskText;
        private set => SetProperty(ref _diskText, value);
    }

    public string NetworkText
    {
        get => _networkText;
        private set => SetProperty(ref _networkText, value);
    }

    public bool NetworkAvailable
    {
        get => _networkAvailable;
        private set => SetProperty(ref _networkAvailable, value);
    }

    public SystemMonitor()
    {
        Update();
    }

    public void Update()
    {
        UpdateCpu();
        UpdateMemory();
        UpdatePower();
        UpdateDisk();
        UpdateNetwork();

        Uptime = FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private void UpdateCpu()
    {
        if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            return;

        ulong idleTicks = ToUlong(idle);
        ulong kernelTicks = ToUlong(kernel);
        ulong userTicks = ToUlong(user);

        if (!_primed)
        {
            _prevIdle = idleTicks;
            _prevKernel = kernelTicks;
            _prevUser = userTicks;
            _primed = true;
            return;
        }

        ulong idleDelta = idleTicks - _prevIdle;
        ulong kernelDelta = kernelTicks - _prevKernel;
        ulong userDelta = userTicks - _prevUser;
        ulong total = kernelDelta + userDelta;

        _prevIdle = idleTicks;
        _prevKernel = kernelTicks;
        _prevUser = userTicks;

        if (total == 0)
            return;

        double usage = Math.Clamp((1.0 - idleDelta / (double)total) * 100.0, 0, 100);
        Cpu = usage;
        CpuText = $"{usage:0}%";

        CpuHistory.Add(Cpu);
        while (CpuHistory.Count > 48)
            CpuHistory.RemoveAt(0);
    }

    private void UpdateMemory()
    {
        try
        {
            var mem = new NativeMethods.MEMORYSTATUSEX();
            if (!NativeMethods.GlobalMemoryStatusEx(mem))
                return;

            Ram = mem.dwMemoryLoad;
            RamText = $"{mem.dwMemoryLoad}%";
            double total = mem.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
            double used = (mem.ullTotalPhys - mem.ullAvailPhys) / 1024.0 / 1024.0 / 1024.0;
            RamDetail = $"{used:0.0} / {total:0.0} GB";
        }
        catch
        {
            // Monitoring is decorative; never crash the dock.
        }
    }

    private void UpdatePower()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
            return;

        HasBattery = status.BatteryFlag != 128 && status.BatteryLifePercent != 255;
        if (!HasBattery)
        {
            BatteryText = "AC";
            IsCharging = true;
            return;
        }

        IsCharging = status.ACLineStatus == 1;
        BatteryText = $"{status.BatteryLifePercent}%";
    }

    private void UpdateDisk()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            double free = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            double total = drive.TotalSize / 1024.0 / 1024.0 / 1024.0;
            DiskText = $"{free:0} GB liberi di {total:0} GB";
        }
        catch
        {
            DiskText = "";
        }
    }

    private void UpdateNetwork()
    {
        try
        {
            NetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
            NetworkInterface? active = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(nic => nic.OperationalStatus == OperationalStatus.Up &&
                                       nic.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                                       nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel);

            NetworkText = !NetworkAvailable
                ? "Offline"
                : active is null
                    ? "Online"
                    : active.Name;
        }
        catch
        {
            NetworkText = "Rete";
            NetworkAvailable = false;
        }
    }

    private static ulong ToUlong(NativeMethods.FILETIME time) =>
        ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;

    private static string FormatUptime(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}g {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1)
            return $"{span.Hours}h {span.Minutes}m";
        return $"{span.Minutes}m";
    }
}
