using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        if (string.IsNullOrWhiteSpace(value))
            return "";

        value = value.Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .Trim('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];

        return Regex.IsMatch(value, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ? value : "";
    }

    private static string BuildManifestUrl(string repository) => repository.Length == 0
        ? ""
        : $"https://github.com/{repository}/releases/latest/download/update.json";
}

public sealed record UpdateManifest(string Version, string DownloadUrl, string Sha256, string Notes);

public sealed record UpdateCheckResult(bool HasUpdate, string Version, string Notes, UpdateManifest? Manifest, string? Error);

public sealed class UpdateService
{
    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(20));
    private static readonly HttpClient DownloadHttp = CreateClient(TimeSpan.FromMinutes(10));

    public string CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";

    public async Task<UpdateCheckResult> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifestUrl?.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return new UpdateCheckResult(false, CurrentVersion, "", null,
                "Inserisci un URL http/https del manifest aggiornamenti.");
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
            return new UpdateCheckResult(hasUpdate, manifest.Version, manifest.Notes ?? "", manifest, null);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, CurrentVersion, "", null, "Verifica non riuscita: " + ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> InstallAsync(UpdateManifest manifest,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (false, "URL del pacchetto non valido.");

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nebula", "updates");
        string staging = Path.Combine(root, "staging");
        string zipPath = Path.Combine(root, "update.zip");

        try
        {
            Directory.CreateDirectory(root);
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);

            await using (Stream source = await DownloadHttp.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false))
            await using (var target = File.Create(zipPath))
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

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

            string scriptPath = Path.Combine(root, "apply-update.ps1");
            string appDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            await File.WriteAllTextAsync(scriptPath, UpdaterScript(appDirectory, staging), Encoding.UTF8,
                cancellationToken).ConfigureAwait(false);

            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            System.Diagnostics.Process.Start(start);
            return (true, "Aggiornamento pronto: Nebula si chiuderà e ripartirà con la nuova versione.");
        }
        catch (Exception ex)
        {
            return (false, "Download non riuscito: " + ex.Message);
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string UpdaterScript(string appDirectory, string staging)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        int pid = Environment.ProcessId;
        string logPath = Path.Combine(Path.GetDirectoryName(staging)!, "apply-update.log");
        return string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$log = " + Quote(logPath),
            "$pidToWait = " + pid,
            "$source = " + Quote(staging),
            "$destination = " + Quote(appDirectory),
            "while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }",
            "try {",
            "  Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force",
            "  Start-Process -FilePath (Join-Path $destination 'Nebula.exe')",
            "  Remove-Item -LiteralPath " + Quote(Path.Combine(Path.GetDirectoryName(staging)!, "update.zip")) + " -Force -ErrorAction SilentlyContinue",
            "} catch {",
            "  ($_ | Out-String) | Out-File -LiteralPath $log -Encoding utf8",
            "}"
        });
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Nebula-Updater/1.1");
        return client;
    }
}