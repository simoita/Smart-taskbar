using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Nebula.Models;

namespace Nebula.Services;

public sealed record UpdateState(string Message, string Notes, bool HasUpdate);

/// <summary>
/// Owns the single update check used by both the automatic timer and the Studio page.
/// Automatic checks are deliberately delayed until the app is ready, then repeat at a
/// low frequency; the network never runs on the UI thread.
/// </summary>
public sealed class UpdateCoordinator : IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(15);

    private readonly AppSettings _settings;
    private readonly UpdateService _service = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DispatcherTimer? _timer;
    private UpdateManifest? _pendingUpdate;
    private UpdateState _state;
    private bool _disposed;

    public UpdateCoordinator(AppSettings settings)
    {
        _settings = settings;
        string manifest = string.IsNullOrWhiteSpace(settings.UpdateManifestUrl)
            ? UpdateDefaults.ManifestUrl
            : settings.UpdateManifestUrl;
        _state = new UpdateState(
            string.IsNullOrWhiteSpace(manifest)
                ? "Configura il manifest per ricevere gli aggiornamenti automatici."
                : "Controllo automatico attivo.",
            "",
            false);
    }

    public UpdateState State => _state;
    public string CurrentVersion => _service.CurrentVersion;

    public event Action<UpdateState>? StateChanged;
    public event Action<UpdateManifest>? UpdateAvailable;
    public event Action? UpdateReady;

    public void Start()
    {
        if (_timer is not null || _disposed)
            return;

        _timer = new DispatcherTimer { Interval = StartupDelay };
        _timer.Tick += async (_, _) =>
        {
            if (_timer is not null)
                _timer.Interval = PollInterval;
            await CheckNowAsync(true);
        };
        _timer.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
    }

    /// <summary>
    /// Checks the configured manifest. With <paramref name="automatic"/> true the
    /// update window is offered when AutoUpdate is enabled; the package is
    /// downloaded only after the user confirms. Manual checks never install on their own.
    /// </summary>
    public async Task<bool> CheckNowAsync(bool automatic, CancellationToken cancellationToken = default)
    {
        if (_disposed || (automatic && !_settings.AutoUpdate))
            return false;

        string url = (_settings.UpdateManifestUrl ?? "").Trim();
        if (url.Length == 0)
            url = UpdateDefaults.ManifestUrl;
        if (url.Length == 0)
        {
            if (!automatic)
                SetState("Inserisci l'URL del manifest aggiornamenti.", "", false);
            return false;
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
            return false;

        try
        {
            SetState(automatic ? "Controllo aggiornamenti…" : "Verifica in corso…", "", false);
            UpdateCheckResult result = await _service.CheckAsync(url, cancellationToken).ConfigureAwait(true);
            if (result.Error is not null)
            {
                SetState(result.Error, "", false);
                if (automatic)
                    Log.Write("Automatic update check failed: " + result.Error);
                return false;
            }

            _pendingUpdate = result.HasUpdate ? result.Manifest : null;
            if (!result.HasUpdate || result.Manifest is null)
            {
                if (result.Suppressed)
                {
                    SetState($"La versione {result.Version} è stata bloccata dopo un avvio non riuscito; serve un pacchetto corretto.", "", false);
                    Log.Write("Update suppressed after a failed start: " + result.Version);
                }
                else
                {
                    SetState($"Nebula è aggiornata ({CurrentVersion}).", "", false);
                }
                return false;
            }

            SetState($"Disponibile {result.Version} (installata {CurrentVersion}).", result.Notes, true);

            // The package is downloaded and applied only after the user confirms
            // in the update window. The popup appears for automatic checks and
            // for manual ones, so a new release is never only a line in Studio.
            Log.Write((automatic ? "Automatic" : "Manual") + " update available: " + result.Version);
            UpdateAvailable?.Invoke(result.Manifest);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            string message = "Controllo aggiornamenti non riuscito: " + ex.Message;
            SetState(message, "", false);
            Log.Write(message);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Installs the update found by the last check, without checking again.</summary>
    public async Task<(bool Ok, string Message)> InstallPendingAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingUpdate is null)
            return (false, "Verifica prima la disponibilità di un aggiornamento.");

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(true))
            return (false, "Un controllo aggiornamenti è già in corso.");

        try
        {
            SetState("Download e verifica del pacchetto…", _state.Notes, true);
            (bool ok, string message) = await _service.InstallAsync(_pendingUpdate, cancellationToken)
                .ConfigureAwait(true);
            SetState(message, _state.Notes, !ok);
            if (ok)
            {
                Log.Write("Manual update ready.");
                UpdateReady?.Invoke();
            }
            return (ok, message);
        }
        catch (OperationCanceledException)
        {
            return (false, "Installazione annullata.");
        }
        catch (Exception ex)
        {
            string message = "Download non riuscito: " + ex.Message;
            SetState(message, _state.Notes, true);
            Log.Write(message);
            return (false, message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void SetState(string message, string notes, bool hasUpdate)
    {
        _state = new UpdateState(message, notes, hasUpdate);
        StateChanged?.Invoke(_state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer?.Stop();
        _timer = null;
    }
}
