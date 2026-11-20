using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nebula.Services;

public static class UpdateDefaults
{
    public static string Repository { get; } = ReadRepository();

    public static string ManifestUrl { get; } = BuildManifestUrl(Repository);

    private static string ReadRepository()
    {
        string? value = typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "Nebula.UpdateRepository", StringComparison.Ordinal))
            ?.Value?.Trim();
        // Even a local/portable build ships with a usable update channel. The
        // release workflow overrides this value with the actual repository.
        if (string.IsNullOrWhiteSpace(value))
            return "simoita/Smart-taskbar";

        value = value.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        // An invalid or missing compile-time value must still leave the app
        // with a working update channel: the user never has to type a manifest.
        return Regex.IsMatch(value, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
            ? value
            : "simoita/Smart-taskbar";
    }

    private static string BuildManifestUrl(string repository) => repository.Length == 0
        ? ""
        : $"https://github.com/{repository}/releases/latest/download/update.json";
}

public sealed record UpdateManifest(string Version, string DownloadUrl, string Sha256, string Notes);

/// <summary>
/// Live download metrics: bytes received, the package size announced by the
/// server and the current average speed.
/// </summary>
public sealed record UpdateProgress(long Downloaded, long? Total, double BytesPerSecond)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");

    public double? Percent => Total is > 0
        ? Math.Clamp(Downloaded * 100.0 / Total.Value, 0, 100)
        : null;

    /// <summary>"12,4 MB di 73,9 MB · 5,2 MB/s"</summary>
    public string Describe()
    {
        string done = Size(Downloaded);
        string speed = Size((long)Math.Max(0, BytesPerSecond)) + "/s";
        return Total is > 0
            ? $"{done} di {Size(Total.Value)} · {speed}"
            : $"{done} · {speed}";
    }

    private static string Size(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00", Culture) + " GB";
        if (bytes >= 1024 * 1024)
            return (bytes / 1024.0 / 1024).ToString("0.0", Culture) + " MB";
        return (bytes / 1024.0).ToString("0", Culture) + " KB";
    }
}

public sealed record UpdateCheckResult(bool HasUpdate, string Version, string Notes, UpdateManifest? Manifest,
    string? Error, bool Suppressed = false);

public sealed record PendingUpdate(string Token, string TargetVersion);

internal sealed class PendingUpdateState
{
    public string Token { get; set; } = "";
    public string TargetVersion { get; set; } = "";
    public string TargetSha256 { get; set; } = "";
    public string AppDirectory { get; set; } = "";
    public string PreviousDirectory { get; set; } = "";
    public string UpdatedDirectory { get; set; } = "";
    public string Phase { get; set; } = "prepared";
    public string CreatedUtc { get; set; } = "";
}

internal sealed class FailedUpdateState
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string FailedUtc { get; set; } = "";
}

public sealed class UpdateService
{
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string RunOnceValue = "NebulaUpdateRecovery";

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient DownloadHttp = CreateClient(TimeSpan.FromMinutes(10));

    private static string AssemblyVersion =>
        typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";

    public string CurrentVersion { get; } = AssemblyVersion;

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            !IsAllowedUpdateUri(uri))
        {
            return new UpdateCheckResult(false, CurrentVersion, "", null,
                "Inserisci un URL https del manifest aggiornamenti.");
        }

        try
        {
            string json = await Http.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) ||
                string.IsNullOrWhiteSpace(manifest.DownloadUrl) || string.IsNullOrWhiteSpace(manifest.Sha256))
                return new UpdateCheckResult(false, CurrentVersion, "", null, "Manifest non valido.");

            bool hasUpdate = Version.TryParse(manifest.Version.TrimStart('v', 'V'), out Version? remote) &&
                             Version.TryParse(CurrentVersion, out Version? current) && remote > current;
            if (hasUpdate && IsFailedPackage(manifest))
            {
                Log.Write("Update package suppressed after a failed start: " + manifest.Version +
                          " sha256=" + NormalizeHash(manifest.Sha256));
                return new UpdateCheckResult(false, manifest.Version, manifest.Notes ?? "", manifest, null, true);
            }

            return new UpdateCheckResult(hasUpdate, manifest.Version, manifest.Notes ?? "", manifest, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, CurrentVersion, "", null, "Verifica non riuscita: " + ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> InstallAsync(UpdateManifest manifest,
        CancellationToken cancellationToken = default, IProgress<UpdateProgress>? progress = null)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            !IsAllowedUpdateUri(uri))
            return (false, "URL del pacchetto non valido: è richiesto HTTPS.");

        string root = UpdatesDirectory;
        string staging = Path.Combine(root, "staging");
        string zipPath = Path.Combine(root, "update.zip");
        bool scriptStarted = false;

        try
        {
            Directory.CreateDirectory(root);
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);

            using (HttpResponseMessage response = await DownloadHttp
                       .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(zipPath);

                var buffer = new byte[81920];
                long downloaded = 0;
                var clock = Stopwatch.StartNew();
                TimeSpan lastReport = TimeSpan.Zero;

                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;

                    // Four updates per second are enough for a smooth bar and do
                    // not flood the dispatcher while the file is being written.
                    TimeSpan elapsed = clock.Elapsed;
                    if (elapsed - lastReport < TimeSpan.FromMilliseconds(150))
                        continue;

                    lastReport = elapsed;
                    progress?.Report(new UpdateProgress(downloaded, total,
                        downloaded / Math.Max(0.001, elapsed.TotalSeconds)));
                }

                progress?.Report(new UpdateProgress(downloaded, total ?? downloaded,
                    downloaded / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
            }

            string actualHash = await Sha256Async(zipPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actualHash, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return (false, "Hash del pacchetto non valido: aggiornamento annullato.");

            Directory.CreateDirectory(staging);
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        return (false, "Pacchetto non valido: percorso non sicuro.");
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, true);
                }
            }

            if (!File.Exists(Path.Combine(staging, "Nebula.exe")))
                return (false, "Pacchetto non valido: Nebula.exe non è nella radice dello ZIP.");

            // The updater only needs the extracted staging folder. Removing
            // the archive here means a later swap failure cannot leave a
            // 65 MB download behind forever.
            TryDeleteFile(zipPath);

            string scriptPath = Path.Combine(root, "apply-update.ps1");
            string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string token = Guid.NewGuid().ToString("N");
            TryDeleteFile(ConfirmationPath);
            string targetVersion = manifest.Version.Trim().TrimStart('v', 'V');
            string targetSha = NormalizeHash(manifest.Sha256);
            await File.WriteAllTextAsync(RecoveryScriptPath,
                UpdateScripts.Recover(appDirectory, token, targetVersion, targetSha), Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);
            WritePendingState(manifest, appDirectory, token);
            await File.WriteAllTextAsync(scriptPath,
                UpdateScripts.Apply(appDirectory, staging, token, targetVersion, targetSha), Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                // The app may have been started with its install folder as the
                // working directory (shortcuts use that). Never let the updater
                // inherit it: Windows will not rename a process's current folder.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            System.Diagnostics.Process.Start(start);
            scriptStarted = true;
            return (true, "Aggiornamento pronto: Nebula si chiuderà e ripartirà con la nuova versione.");
        }
        catch (Exception ex)
        {
            return (false, "Download non riuscito: " + ex.Message);
        }
        finally
        {
            if (!scriptStarted)
            {
                TryDeleteFile(zipPath);
                TryDeleteDirectory(staging);
                ClearPendingTransaction();
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write("Update cleanup failed for " + path + ": " + ex.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Write("Update cleanup failed for " + path + ": " + ex.Message);
        }
    }

    /// <summary>
    /// The manifest carries both the download URL and its hash, so plain HTTP
    /// would let anyone on the network replace the package and the checksum.
    /// Loopback HTTP stays available for the documented local test flow.
    /// </summary>
    private static bool IsAllowedUpdateUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    public static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nebula", "updates");

    public static string PendingStatePath => Path.Combine(UpdatesDirectory, "update-state.json");
    public static string ConfirmationPath => Path.Combine(UpdatesDirectory, "update-confirmed.txt");
    public static string RecoveryScriptPath => Path.Combine(UpdatesDirectory, "recover-update.ps1");
    public static string FailedUpdatePath => Path.Combine(UpdatesDirectory, "failed-update.json");

    private static string FailureReportPath => Path.Combine(UpdatesDirectory, "update-failed.txt");

    /// <summary>
    /// Reads and removes the report written when the directory swap could not
    /// complete. The previous build restarts on its own, so this is how the user
    /// learns that the update failed instead of wondering why nothing changed.
    /// </summary>
    public static PendingUpdate? ReadPendingUpdate()
    {
        try
        {
            if (!File.Exists(PendingStatePath))
                return null;

            PendingUpdateState? state = JsonSerializer.Deserialize<PendingUpdateState>(
                File.ReadAllText(PendingStatePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (state is null || string.IsNullOrWhiteSpace(state.Token) ||
                string.IsNullOrWhiteSpace(state.TargetVersion))
                return null;

            return new PendingUpdate(state.Token, state.TargetVersion);
        }
        catch (Exception ex)
        {
            Log.Write("Pending update state could not be read: " + ex.Message);
            return null;
        }
    }

    public static bool VersionMatches(string left, string right)
    {
        string a = (left ?? "").Trim().TrimStart('v', 'V');
        string b = (right ?? "").Trim().TrimStart('v', 'V');
        if (Version.TryParse(a, out Version? va) && Version.TryParse(b, out Version? vb))
        {
            // "1.6.13" and "1.6.13.0" are the same release: the receipt must not
            // fail just because one side carries the revision component.
            return va.Major == vb.Major && va.Minor == vb.Minor && va.Build == vb.Build;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHash(string hash) => (hash ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// A package that was rolled back must not be offered again unchanged. The
    /// receipt is keyed by both version and SHA, so publishing a corrected
    /// package with the same version remains installable.
    /// </summary>
    public static bool IsFailedPackage(UpdateManifest manifest)
    {
        try
        {
            if (!File.Exists(FailedUpdatePath))
                return false;

            FailedUpdateState? failed = JsonSerializer.Deserialize<FailedUpdateState>(
                File.ReadAllText(FailedUpdatePath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (failed is null || string.IsNullOrWhiteSpace(failed.Version) || string.IsNullOrWhiteSpace(failed.Sha256))
                return false;

            return VersionMatches(failed.Version, manifest.Version) &&
                   string.Equals(NormalizeHash(failed.Sha256), NormalizeHash(manifest.Sha256), StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Log.Write("Failed update receipt could not be read: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Written by the new build only after its startup health gate passes. The
    /// updater and the next-start recovery script use this receipt to decide
    /// whether the swap is final or must be rolled back.
    /// </summary>
    public static bool ConfirmHealthyUpdate(string token)
    {
        try
        {
            PendingUpdate? pending = ReadPendingUpdate();
            if (pending is null || !string.Equals(pending.Token, token, StringComparison.Ordinal))
                return false;
            if (!VersionMatches(pending.TargetVersion, AssemblyVersion))
                return false;

            string temp = ConfirmationPath + ".tmp";
            File.WriteAllText(temp, token + Environment.NewLine + AssemblyVersion + Environment.NewLine);
            File.Move(temp, ConfirmationPath, true);
            TryDeleteFile(FailedUpdatePath);
            Log.Write("Update startup health confirmed: " + pending.TargetVersion);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Update confirmation could not be written: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// A power loss can leave the old directory and a confirmation receipt
    /// behind after the new build has already proven itself. Finish that
    /// transaction on the next start without touching the live install.
    /// </summary>
    public static void CleanupConfirmedUpdateAtStartup()
    {
        try
        {
            PendingUpdate? pending = ReadPendingUpdate();
            if (pending is null || !File.Exists(ConfirmationPath))
                return;
            if (!VersionMatches(pending.TargetVersion, AssemblyVersion))
                return;

            string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            TryDeleteDirectory(appDirectory + ".old");
            TryDeleteDirectory(appDirectory + ".new");
            TryDeleteDirectory(appDirectory + ".failed");
            TryDeleteFile(PendingStatePath);
            TryDeleteFile(ConfirmationPath);
            TryDeleteFile(FailedUpdatePath);
            ClearRecoveryRunOnce();
            Log.Write("Finalized previously confirmed update at startup.");
        }
        catch (Exception ex)
        {
            Log.Write("Confirmed update cleanup failed: " + ex.Message);
        }
    }

    private static void WritePendingState(UpdateManifest manifest, string appDirectory, string token)
    {
        Directory.CreateDirectory(UpdatesDirectory);
        var state = new PendingUpdateState
        {
            Token = token,
            TargetVersion = manifest.Version.Trim().TrimStart('v', 'V'),
            TargetSha256 = NormalizeHash(manifest.Sha256),
            AppDirectory = appDirectory,
            PreviousDirectory = appDirectory + ".old",
            UpdatedDirectory = appDirectory + ".new",
            Phase = "prepared",
            CreatedUtc = DateTime.UtcNow.ToString("O")
        };

        string temp = PendingStatePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, PendingStatePath, true);
        SetRecoveryRunOnce();
    }

    private static void SetRecoveryRunOnce()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunOnceKey, true)
            ?? throw new InvalidOperationException("RunOnce non disponibile.");
        string command = "\"powershell.exe\" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" +
                         RecoveryScriptPath + "\" -Recover";
        key.SetValue(RunOnceValue, command, RegistryValueKind.String);
    }

    private static void ClearRecoveryRunOnce()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKey, true);
            key?.DeleteValue(RunOnceValue, false);
        }
        catch (Exception ex)
        {
            Log.Write("Update RunOnce cleanup failed: " + ex.Message);
        }
    }

    private static void ClearPendingTransaction()
    {
        TryDeleteFile(PendingStatePath);
        TryDeleteFile(ConfirmationPath);
        ClearRecoveryRunOnce();
    }

    public static string? ConsumeFailureReport()
    {
        try
        {
            string path = FailureReportPath;
            if (!File.Exists(path))
                return null;

            string text = File.ReadAllText(path).Trim();
            File.Delete(path);
            return text.Length == 0 ? "L'aggiornamento non è stato applicato." : text;
        }
        catch (Exception ex)
        {
            Log.Write("Update failure report could not be read: " + ex.Message);
            return null;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nebula-Updater/1.1");
        return client;
    }
}