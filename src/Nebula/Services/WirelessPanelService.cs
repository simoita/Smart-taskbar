using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace Nebula.Services;

public sealed record WifiNetworkItem(
    string Name,
    int Signal,
    string Detail,
    string ActionLabel,
    bool IsConnected,
    bool IsSaved,
    bool IsSecure);

public sealed record BluetoothDeviceItem(string Name, string Detail, bool IsConnected);

public readonly record struct RadioStateInfo(bool Supported, bool On);

/// <summary>
/// Quick Wi-Fi and Bluetooth surface. Radios are switched through the WinRT
/// Radio API (no elevation) and Wi-Fi scanning/connection uses the built-in
/// netsh client, which is the same mechanism Windows' own flyout drives.
/// Every call is defensive: a missing adapter, a denied radio or a localized
/// netsh output must only leave the panel without data, never break the app.
/// </summary>
public sealed class WirelessPanelService
{
    private static readonly Regex SsidLine = new(
        @"^\s*SSID\s+\d+\s*:\s*(?<name>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InterfaceSsidLine = new(
        @"^\s*SSID\s*:\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PercentLine = new(
        @"(?<value>\d{1,3})\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, WifiNetworkItem> _lastScan = new(StringComparer.OrdinalIgnoreCase);
    private Radio? _wifiRadio;
    private Radio? _bluetoothRadio;
    private bool _wifiAccessLogged;
    private bool _bluetoothAccessLogged;

    public string LastMessage { get; private set; } = "";

    public async Task<RadioStateInfo> GetWifiStateAsync()
    {
        Radio? radio = await GetRadioAsync(RadioKind.WiFi);
        if (radio is null)
        {
            // The Radio API may not expose a radio even when the adapter is
            // present. Try the scan anyway: netsh reports the real state and
            // the panel can still list networks.
            bool adapter = HasWifiAdapter();
            return new RadioStateInfo(adapter, adapter);
        }

        try
        {
            return new RadioStateInfo(true, radio.State != RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Write("Wi-Fi radio state failed: " + ex.Message);
            return new RadioStateInfo(true, false);
        }
    }

    public async Task<RadioStateInfo> GetBluetoothStateAsync()
    {
        Radio? radio = await GetRadioAsync(RadioKind.Bluetooth);
        if (radio is null)
            return new RadioStateInfo(false, false);

        try
        {
            return new RadioStateInfo(true, radio.State != RadioState.Off);
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth radio state failed: " + ex.Message);
            return new RadioStateInfo(true, false);
        }
    }

    public async Task<bool> SetWifiAsync(bool on)
    {
        Radio? radio = await GetRadioAsync(RadioKind.WiFi);
        if (radio is null)
        {
            LastMessage = "Wi-Fi non controllabile da qui: nessuna radio rilevata.";
            return false;
        }

        return await SetRadioAsync(radio, on, "Wi-Fi");
    }

    public async Task<bool> SetBluetoothAsync(bool on)
    {
        Radio? radio = await GetRadioAsync(RadioKind.Bluetooth);
        if (radio is null)
        {
            LastMessage = "Bluetooth non controllabile da qui: nessuna radio rilevata.";
            return false;
        }

        return await SetRadioAsync(radio, on, "Bluetooth");
    }

    public async Task<IReadOnlyList<WifiNetworkItem>> ScanWifiAsync()
    {
        NetshResult scan = await RunNetshAsync("wlan show networks mode=bssid");
        NetshResult profiles = await RunNetshAsync("wlan show profiles");
        NetshResult interfaces = await RunNetshAsync("wlan show interfaces");

        var saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in profiles.Output.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || line.TrimStart().StartsWith("Gruppo", StringComparison.OrdinalIgnoreCase))
                continue;

            string name = line[(colon + 1)..].Trim();
            if (name.Length > 0 &&
                !name.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !int.TryParse(name, out _))
            {
                saved.Add(name);
            }
        }

        string current = ReadCurrentSsid(interfaces.Output);
        List<WifiNetworkItem> networks = ParseNetworks(scan.Output, saved, current);
        _lastScan.Clear();
        foreach (WifiNetworkItem network in networks)
            _lastScan[network.Name] = network;

        if (networks.Count == 0)
        {
            if (scan.Output.Contains("wlansvc", StringComparison.OrdinalIgnoreCase) ||
                scan.Output.Contains("non è in esecuzione", StringComparison.OrdinalIgnoreCase) ||
                scan.Output.Contains("not running", StringComparison.OrdinalIgnoreCase))
            {
                LastMessage = "Il servizio Wi-Fi di Windows non è attivo. Avvialo o riavvia il PC.";
            }
            else if (scan.ExitCode != 0)
            {
                LastMessage = "Windows non ha restituito reti Wi-Fi. Controlla che il Wi-Fi sia attivo.";
            }
        }

        return networks;
    }

    public async Task<string> ConnectWifiAsync(WifiNetworkItem network)
    {
        if (network.IsConnected)
            return $"Sei già connesso a {network.Name}.";

        if (network.IsSaved)
        {
            NetshResult result = await RunNetshAsync(
                $"wlan connect name=\"{CleanNetshName(network.Name)}\" ssid=\"{CleanNetshName(network.Name)}\"");
            return result.ExitCode == 0
                ? $"Connessione a {network.Name} richiesta."
                : "Windows non ha accettato la connessione: apri le impostazioni Wi-Fi.";
        }

        if (!network.IsSecure)
        {
            string profilePath = Path.Combine(Path.GetTempPath(),
                "nebula-wifi-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                await File.WriteAllTextAsync(profilePath, BuildOpenProfile(network.Name), Encoding.UTF8);
                NetshResult added = await RunNetshAsync(
                    $"wlan add profile filename=\"{profilePath}\" user=current");
                if (added.ExitCode != 0)
                    return "Windows non ha creato il profilo per la rete aperta.";

                NetshResult connected = await RunNetshAsync(
                    $"wlan connect name=\"{CleanNetshName(network.Name)}\" ssid=\"{CleanNetshName(network.Name)}\"");
                return connected.ExitCode == 0
                    ? $"Connessione a {network.Name} richiesta."
                    : "Windows non ha accettato la connessione: apri le impostazioni Wi-Fi.";
            }
            catch (Exception ex)
            {
                Log.Write("Open Wi-Fi connect failed: " + ex.Message);
                return "Connessione non riuscita: apri le impostazioni Wi-Fi di Windows.";
            }
            finally
            {
                try
                {
                    if (File.Exists(profilePath))
                        File.Delete(profilePath);
                }
                catch
                {
                    // Best effort temp cleanup.
                }
            }
        }

        OpenWifiSettings();
        return "Questa rete è protetta: inserisci la password nella finestra di Windows appena aperta.";
    }

    /// <summary>
    /// Paired classic Bluetooth devices with their current connection state.
    /// Windows has no generic "connect this device" API for every profile, so
    /// the panel shows the state and hands pairing/management to the system.
    /// </summary>
    public async Task<IReadOnlyList<BluetoothDeviceItem>> GetBluetoothDevicesAsync()
    {
        var result = new List<BluetoothDeviceItem>();
        try
        {
            string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DeviceInformation device in devices)
            {
                string name = device.Name?.Trim() ?? "";
                if (name.Length == 0 || !seen.Add(name))
                    continue;

                bool connected = false;
                if (device.Properties.TryGetValue("System.Devices.Aep.IsConnected", out object? value))
                    connected = value is bool boolean && boolean;

                result.Add(new BluetoothDeviceItem(name, connected ? "Connesso" : "Associato", connected));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth device list failed: " + ex.Message);
            LastMessage = "Elenco Bluetooth non disponibile: apri le impostazioni di Windows.";
        }

        return result
            .OrderByDescending(device => device.IsConnected)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool OpenWifiSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network-wifi") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Wi-Fi settings could not open: " + ex.Message);
            return false;
        }
    }

    public static bool OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Bluetooth settings could not open: " + ex.Message);
            return false;
        }
    }

    private async Task<Radio?> GetRadioAsync(RadioKind kind)
    {
        Radio? cached = kind == RadioKind.WiFi ? _wifiRadio : _bluetoothRadio;
        if (cached is not null)
            return cached;

        try
        {
            RadioAccessStatus access = await Radio.RequestAccessAsync();
            bool logged = kind == RadioKind.WiFi ? _wifiAccessLogged : _bluetoothAccessLogged;
            if (!logged)
            {
                Log.Write($"Wireless radio access ({kind}): {access}.");
                if (kind == RadioKind.WiFi)
                    _wifiAccessLogged = true;
                else
                    _bluetoothAccessLogged = true;
            }
            if (access != RadioAccessStatus.Allowed)
            {
                LastMessage = "Windows non consente a questa app di cambiare la radio; usa le impostazioni di sistema.";
                return null;
            }

            IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
            Radio? radio = radios.FirstOrDefault(candidate => candidate.Kind == kind);
            if (radio is null)
                return null;

            if (kind == RadioKind.WiFi)
                _wifiRadio = radio;
            else
                _bluetoothRadio = radio;

            return radio;
        }
        catch (Exception ex)
        {
            Log.Write("Wireless radio access failed: " + ex.Message);
            return null;
        }
    }

    private async Task<bool> SetRadioAsync(Radio radio, bool on, string label)
    {
        try
        {
            RadioAccessStatus status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            if (status != RadioAccessStatus.Allowed)
            {
                LastMessage = $"Windows ha rifiutato il cambio di stato {label}.";
                return false;
            }

            LastMessage = on ? $"{label} attivato." : $"{label} disattivato.";
            return true;
        }
        catch (Exception ex)
        {
            LastMessage = $"Impossibile cambiare lo stato {label}: " + ex.Message;
            Log.Write($"{label} state change failed: " + ex.Message);
            return false;
        }
    }

    private static bool HasWifiAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(nic =>
                nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch
        {
            return false;
        }
    }

    private static List<WifiNetworkItem> ParseNetworks(
        string output, HashSet<string> saved, string current)
    {
        var networks = new Dictionary<string, WifiNetworkItem>(StringComparer.OrdinalIgnoreCase);
        string? ssid = null;
        string auth = "";
        string encryption = "";
        int signal = 0;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(ssid))
                return;

            bool open = auth.Contains("open", StringComparison.OrdinalIgnoreCase) ||
                        auth.Contains("apri", StringComparison.OrdinalIgnoreCase) ||
                        auth.Contains("aperta", StringComparison.OrdinalIgnoreCase) ||
                        auth.Contains("nessuna", StringComparison.OrdinalIgnoreCase) ||
                        auth.Contains("none", StringComparison.OrdinalIgnoreCase) ||
                        encryption.Contains("nessuna", StringComparison.OrdinalIgnoreCase) ||
                        encryption.Contains("none", StringComparison.OrdinalIgnoreCase);
            bool isSaved = saved.Contains(ssid);
            bool connected = string.Equals(ssid, current, StringComparison.OrdinalIgnoreCase);
            string action = connected ? "Connesso" : isSaved || open ? "Connetti" : "Password";
            string detail = $"{Math.Clamp(signal, 0, 100)}% · " +
                            $"{(open ? "Aperta" : "Protetta")}" +
                            (isSaved ? " · salvata" : "");

            var item = new WifiNetworkItem(ssid.Trim(), Math.Clamp(signal, 0, 100),
                detail, action, connected, isSaved, !open);

            if (!networks.TryGetValue(ssid, out WifiNetworkItem? existing) ||
                item.Signal > existing.Signal)
                networks[ssid] = item;
        }

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            Match match = SsidLine.Match(line);
            if (match.Success)
            {
                Flush();
                ssid = match.Groups["name"].Value;
                auth = "";
                encryption = "";
                signal = 0;
                continue;
            }

            if (ssid is null)
                continue;

            if (line.Contains("Autenticazione", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0)
                    auth = line[(colon + 1)..].Trim();
                continue;
            }

            if (line.Contains("Crittografia", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Encryption", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0)
                    encryption = line[(colon + 1)..].Trim();
                continue;
            }

            Match percent = PercentLine.Match(line);
            if (percent.Success && int.TryParse(percent.Groups["value"].Value, out int value))
                signal = Math.Max(signal, Math.Min(100, value));
        }

        Flush();
        return networks.Values
            .OrderByDescending(network => network.IsConnected)
            .ThenByDescending(network => network.Signal)
            .ThenBy(network => network.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string ReadCurrentSsid(string interfacesOutput)
    {
        foreach (string line in interfacesOutput.Split('\n'))
        {
            Match match = InterfaceSsidLine.Match(line);
            if (match.Success)
                return match.Groups["name"].Value.Trim();
        }

        return "";
    }

    private static string BuildOpenProfile(string ssid)
    {
        string name = SecurityElement.Escape(ssid) ?? "";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>{name}</name>
              <SSIDConfig>
                <SSID>
                  <name>{name}</name>
                </SSID>
              </SSIDConfig>
              <connectionType>ESS</connectionType>
              <connectionMode>auto</connectionMode>
              <MSM>
                <security>
                  <authEncryption>
                    <authentication>open</authentication>
                    <encryption>none</encryption>
                    <useOneX>false</useOneX>
                  </authEncryption>
                </security>
              </MSM>
            </WLANProfile>
            """;
    }

    private static string CleanNetshName(string name) => name.Replace("\"", "").Trim();

    private static Task<NetshResult> RunNetshAsync(string arguments) => Task.Run(() =>
    {
        try
        {
            var start = new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using Process? process = Process.Start(start);
            if (process is null)
                return new NetshResult(-1, "");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit(6000);
            return new NetshResult(process.HasExited ? process.ExitCode : -1, output + error);
        }
        catch (Exception ex)
        {
            Log.Write("netsh failed: " + ex.Message);
            return new NetshResult(-1, "");
        }
    });

    private readonly record struct NetshResult(int ExitCode, string Output);
}
