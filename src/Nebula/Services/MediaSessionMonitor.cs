using System.Windows;
using System.Windows.Threading;

namespace Nebula.Services;

/// <summary>What the active media session is playing right now.</summary>
public sealed record MediaSessionInfo(string Title, string Artist, bool IsPlaying, string Source);

/// <summary>
/// Polls the Windows media session manager so the taskbar media widget can show
/// the current track. The work runs off the UI thread; listeners are notified on
/// the dispatcher, and the last known state stays available to the widget.
/// </summary>
public sealed class MediaSessionMonitor : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _cts = new();
    private global::Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager? _manager;
    private MediaSessionInfo? _current;
    private bool _started;
    private bool _disposed;

    public MediaSessionInfo? Current => _current;

    public event Action? Changed;

    public void Start()
    {
        if (_started || _disposed)
            return;

        _started = true;
        _ = Task.Run(() => PollAsync(_cts.Token));
    }

    private async Task PollAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            MediaSessionInfo? info = null;
            try
            {
                _manager ??= await global::Windows.Media.Control
                    .GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                global::Windows.Media.Control.GlobalSystemMediaTransportControlsSession? session =
                    _manager?.GetCurrentSession();
                if (session is not null)
                {
                    global::Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackInfo playback =
                        session.GetPlaybackInfo();
                    global::Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties? properties =
                        await session.TryGetMediaPropertiesAsync();

                    string title = properties?.Title?.Trim() ?? "";
                    string artist = properties?.Artist?.Trim() ?? "";
                    if (title.Length > 0)
                    {
                        info = new MediaSessionInfo(title, artist,
                            playback?.PlaybackStatus ==
                            global::Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                            session.SourceAppUserModelId ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                // No session, a player that does not implement SMTC, or a shell
                // restart: all of them simply leave the widget without a track.
                Log.Write("Media session query failed: " + ex.Message);
            }

            bool changed = !Equals(_current, info);
            _current = info;
            if (changed)
                Dispatch(Changed);

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static void Dispatch(Action? action)
    {
        if (action is null)
            return;

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Shutting down anyway.
        }

        _cts.Dispose();
    }
}
