using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Nebula.Models;

namespace Nebula.Services;

public enum ActivityKind
{
    Download,
    Upload,
    Copy,
    Move,
    Compression,
    Extraction,
    Media,
    /// <summary>A document or project being written by its own app.</summary>
    Save,
    Other
}

public sealed class SystemActivity : ObservableObject
{
    private string _title = "";
    private string _detail = "";
    private string _status = "";
    private double _progress = -1;
    private bool _isFinished;

    public string Id { get; init; } = "";
    public ActivityKind Kind { get; init; }
    public string? Path { get; set; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public bool IsMedia { get; init; }
    public long Bytes { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
                OnPropertyChanged(nameof(KindStatusLabel));
        }
    }

    /// <summary>0-100, or -1 while the total size is unknown.</summary>
    public double Progress
    {
        get => _progress;
        set
        {
            if (!SetProperty(ref _progress, value))
                return;
            OnPropertyChanged(nameof(ProgressValue));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    public double ProgressValue => _progress < 0 ? 0 : Math.Clamp(_progress, 0, 100);
    public bool IsIndeterminate => _progress < 0 && !_isFinished;

    public bool IsFinished
    {
        get => _isFinished;
        set
        {
            if (!SetProperty(ref _isFinished, value))
                return;
            OnPropertyChanged(nameof(IsIndeterminate));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(KindStatusLabel));
        }
    }

    public bool IsActive => !_isFinished;

    public string KindLabel => Kind switch
    {
        ActivityKind.Download => "Download",
        ActivityKind.Upload => "Caricamento",
        ActivityKind.Copy => "Copia file",
        ActivityKind.Move => "Spostamento",
        ActivityKind.Compression => "Compressione",
        ActivityKind.Extraction => "Estrazione",
        ActivityKind.Media => "In riproduzione",
        ActivityKind.Save => "Salvataggio",
        _ => "Attività"
    };

    /// <summary>Type plus live state, so the island says what is running and whether it still is.</summary>
    public string KindStatusLabel => IsFinished
        ? KindLabel
        : $"{KindLabel} · {Status.ToLowerInvariant()}";

    public string Glyph => Kind switch
    {
        ActivityKind.Download => "\uE896",
        ActivityKind.Upload => "\uE898",
        ActivityKind.Copy => "\uE8C8",
        ActivityKind.Move => "\uE8DE",
        ActivityKind.Compression => "\uE7B8",
        ActivityKind.Extraction => "\uE8F1",
        ActivityKind.Media => "\uE768",
        ActivityKind.Save => "\uE74E",
        _ => "\uE9D9"
    };

    public string StartedText => StartedUtc.ToLocalTime().ToString("HH:mm");
}

/// <summary>
/// Watches the user's shell folders and the running shell/media sessions and
/// turns what is happening into a small, shared activity list. It is a
/// heuristic observer: no driver, service or browser extension is installed.
/// </summary>
public sealed class ActivityCenter : IDisposable
{
    private static readonly string[] TempExtensions =
    {
        ".crdownload", ".part", ".download", ".opdownload", ".partial", ".tmp"
    };

    private static readonly string[] ArchiveExtensions =
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso", ".cab", ".wim"
    };

    private static readonly HashSet<string> ArchiverProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "7z", "7zfm", "7zg", "winrar", "winrarremont", "unrar", "winzip", "winzip64",
        "peazip", "bandizip", "ark", "haozip", "izarc"
    };

    private static readonly HashSet<string> MediaProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vlc", "mpv", "mpc-hc64", "mpc-be64", "potplayermini64", "potplayermini",
        "spotify", "music.ui", "microsoft.media.player", "quicktime", "wmplayer",
        "foobar2000", "aimp", "itunes", "amazon music"
    };

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, FileTrack> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SystemActivity> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _removeAfter = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _dismissedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastTouched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recentDeleteFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _recentCreates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _recentCreatePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _extractionLast = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _extractionCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _mediaIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _shelfRoot;
    private readonly string _downloadsRoot;
    private readonly string _oneDriveRoot;
    private bool _archiverRunning;
    private int _ticks;
    private bool _disposed;
    private bool _audioProbeUnavailable;

    public ActivityCenter(AppSettings settings)
    {
        _settings = settings;
        _shelfRoot = Path.Combine(SettingsStore.DataDirectory, "shelf");
        _oneDriveRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive");

        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _downloadsRoot = downloads;
        Watch(downloads);
        Watch(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Watch(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        if (Directory.Exists(_oneDriveRoot))
            Watch(_oneDriveRoot, true);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();
    }

    public ObservableCollection<SystemActivity> Activities { get; } = new();

    public event Action? Changed;

    public int ActiveCount => Activities.Count(activity => !activity.IsFinished);

    /// <summary>Used by the file shelf to surface its own copy operations.</summary>
    public SystemActivity BeginOperation(ActivityKind kind, string title, string detail)
    {
        var activity = new SystemActivity
        {
            Id = "op:" + Guid.NewGuid().ToString("N"),
            Kind = kind,
            Title = title,
            Detail = detail,
            Status = "In corso",
            Progress = -1
        };
        Add(activity);
        return activity;
    }

    public void UpdateOperation(SystemActivity activity, double progress, string detail)
    {
        if (_disposed)
            return;

        activity.Progress = progress;
        activity.Detail = detail;
    }

    public void CompleteOperation(SystemActivity activity, string detail = "Completato")
    {
        activity.IsFinished = true;
        activity.Progress = 100;
        activity.Detail = detail;
        activity.Status = "Completato";
        _removeAfter[activity.Id] = DateTime.UtcNow.AddSeconds(3.5);
        Changed?.Invoke();
    }

    public void Dismiss(SystemActivity activity)
    {
        if (activity is null)
            return;

        _dismissedUntil[activity.Id] = DateTime.UtcNow.AddSeconds(
            activity.IsMedia ? 60 : 30);
        Remove(activity.Id);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        foreach (FileSystemWatcher watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Watcher already gone.
            }
        }

        _watchers.Clear();
        _byId.Clear();
        Activities.Clear();
    }

    private void Watch(string path, bool oneDrive = false)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size |
                               NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 32 * 1024,
                EnableRaisingEvents = true
            };

            watcher.Created += (_, args) => OnFileEvent(args, true, false, oneDrive);
            watcher.Changed += (_, args) => OnFileEvent(args, false, false, oneDrive);
            watcher.Deleted += (_, args) => OnFileEvent(args, false, true, oneDrive);
            watcher.Renamed += (_, args) => OnRenamed(args.OldFullPath, args.FullPath, oneDrive);
            watcher.Error += (_, args) => Log.Write("Activity watcher error: " + args.GetException().Message);

            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            Log.Write($"Activity watcher unavailable for '{path}': {ex.Message}");
        }
    }

    private void OnFileEvent(FileSystemEventArgs args, bool created, bool deleted, bool oneDrive)
    {
        if (_disposed || string.IsNullOrWhiteSpace(args.FullPath))
            return;

        string path = args.FullPath;
        if (path.StartsWith(_shelfRoot, StringComparison.OrdinalIgnoreCase))
            return;

        Dispatch(() =>
        {
            if (deleted)
                OnDeleted(path);
            else
                TouchFile(path, created, oneDrive);
        });
    }

    private void OnRenamed(string oldPath, string newPath, bool oneDrive)
    {
        if (_disposed || string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            return;

        Dispatch(() =>
        {
            if (_files.Remove(oldPath, out FileTrack? track))
            {
                bool wasTemp = IsTempDownload(oldPath);
                if (_byId.TryGetValue(track.ActivityId, out SystemActivity? activity))
                {
                    activity.Title = Path.GetFileName(newPath);
                    activity.Path = newPath;
                    activity.Detail = wasTemp
                        ? "Download completato"
                        : "Rinominato";
                    activity.Progress = 100;
                    activity.IsFinished = true;
                    activity.Status = "Completato";
                    _removeAfter[activity.Id] = DateTime.UtcNow.AddSeconds(4);
                }

                if (wasTemp)
                {
                    // A completed download is not a second "copy" operation.
                    _files.Remove(newPath);
                    return;
                }
            }

            TouchFile(newPath, true, oneDrive);
        });
    }

    private void TouchFile(string path, bool created, bool oneDrive)
    {
        if (_disposed || !File.Exists(path) || IsShelfPath(path))
            return;

        DateTime now = DateTime.UtcNow;
        if (_lastTouched.TryGetValue(path, out DateTime last) &&
            (now - last).TotalMilliseconds < 220)
            return;
        _lastTouched[path] = now;

        long size;
        DateTime modified;
        try
        {
            var info = new FileInfo(path);
            size = info.Length;
            modified = info.LastWriteTimeUtc;
        }
        catch
        {
            return;
        }

        bool temp = IsTempDownload(path);
        bool archive = IsArchive(path);
        if (size == 0 && !temp)
            return;

        string? moveOrigin = ConsumeRecentDelete(path);
        if (moveOrigin is not null)
        {
            string id = "move:" + Guid.NewGuid().ToString("N");
            var move = new SystemActivity
            {
                Id = id,
                Kind = ActivityKind.Move,
                Title = Path.GetFileName(path),
                Detail = $"Spostato da {Path.GetFileName(moveOrigin)}",
                Path = path,
                Bytes = size,
                Progress = 100,
                IsFinished = true,
                Status = "Completato"
            };
            Add(move);
            _removeAfter[id] = now.AddSeconds(4);
            return;
        }

        if (!_files.TryGetValue(path, out FileTrack? track))
        {
            track = new FileTrack
            {
                Path = path,
                Size = size,
                LastSampleSize = size,
                LastSampleUtc = now,
                LastGrowthUtc = now,
                CreatedUtc = now
            };
            _files[path] = track;
        }

        // Extraction is a folder-level operation: one card for the whole
        // archive, not one card per extracted file.
        if (_archiverRunning && !temp && !archive)
        {
            TouchExtraction(Path.GetDirectoryName(path) ?? "", Path.GetFileName(path), now);
            return;
        }

        double seconds = Math.Max(0.2, (now - track.LastSampleUtc).TotalSeconds);
        long delta = size - track.LastSampleSize;
        if (delta > 0)
        {
            track.SpeedBps = delta / seconds;
            track.LastGrowthUtc = now;
        }

        track.LastSampleSize = size;
        track.LastSampleUtc = now;
        track.Size = size;

        // What the file actually is decides the activity: a partial download, a
        // new archive, a file written inside Downloads or OneDrive, a fresh copy
        // or an in-place save. This is what the island shows as the activity type.
        bool inDownloads = path.StartsWith(_downloadsRoot, StringComparison.OrdinalIgnoreCase);
        bool inOneDrive = oneDrive || path.StartsWith(_oneDriveRoot, StringComparison.OrdinalIgnoreCase);

        ActivityKind kind = temp
            ? ActivityKind.Download
            : archive
                ? ActivityKind.Compression
                : inDownloads
                    ? ActivityKind.Download
                    : inOneDrive
                        ? ActivityKind.Upload
                        : created
                            ? ActivityKind.Copy
                            : ActivityKind.Save;

        string activityId = "file:" + path;
        if (!_byId.TryGetValue(activityId, out SystemActivity? activity) || activity.IsFinished)
        {
            activity = new SystemActivity
            {
                Id = activityId,
                Kind = kind,
                Title = Path.GetFileName(path),
                Path = path,
                Bytes = size,
                Progress = -1,
                Status = "In corso"
            };
            Add(activity);
        }

        activity.Title = Path.GetFileName(path);
        activity.Bytes = size;
        activity.Path = path;
        activity.IsFinished = false;
        activity.Progress = -1;
        activity.Status = "In corso";
        string speed = track.SpeedBps > 0 ? " · " + FormatSpeed(track.SpeedBps) : "";
        activity.Detail = kind switch
        {
            ActivityKind.Save => $"Salvataggio · {FormatSize(size)}",
            ActivityKind.Download => $"Download · {FormatSize(size)}{speed}",
            ActivityKind.Compression => $"Compressione · {FormatSize(size)}",
            ActivityKind.Upload => $"Caricamento · {FormatSize(size)}{speed}",
            _ => $"{FormatSize(size)}{speed}"
        };

        track.ActivityId = activityId;
        track.LastWriteUtc = modified;
    }

    private void OnDeleted(string path)
    {
        if (IsShelfPath(path))
            return;

        DateTime now = DateTime.UtcNow;
        string name = Path.GetFileName(path);
        string folder = Path.GetDirectoryName(path) ?? "";

        if (_files.Remove(path, out FileTrack? track))
        {
            if (_byId.TryGetValue(track.ActivityId, out SystemActivity? activity))
            {
                activity.Progress = 100;
                activity.IsFinished = true;
                activity.Status = "Completato";
                activity.Detail = "File rimosso o spostato";
                _removeAfter[activity.Id] = now.AddSeconds(3);
            }
        }

        if (name.Length > 0)
        {
            _recentDeletes[name] = now;
            _recentDeleteFolders[name] = folder;

            // The target file may have arrived just before the delete event.
            if (_recentCreates.TryGetValue(name, out DateTime created) &&
                (now - created).TotalSeconds < 6 &&
                _recentCreatePaths.TryGetValue(name, out string? newPath) &&
                !string.Equals(Path.GetDirectoryName(newPath), folder, StringComparison.OrdinalIgnoreCase))
            {
                CreateMoveActivity(name, path, newPath);
                _recentCreates.Remove(name);
                _recentCreatePaths.Remove(name);
                _recentDeletes.Remove(name);
                _recentDeleteFolders.Remove(name);
            }
        }

        PruneRecent(now);
    }

    private string? ConsumeRecentDelete(string newPath)
    {
        string name = Path.GetFileName(newPath);
        if (!_recentDeletes.TryGetValue(name, out DateTime deleted) ||
            (DateTime.UtcNow - deleted).TotalSeconds > 6)
            return null;

        string oldFolder = _recentDeleteFolders.TryGetValue(name, out string? folder) ? folder : "";
        string newFolder = Path.GetDirectoryName(newPath) ?? "";
        if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
            return null;

        _recentDeletes.Remove(name);
        _recentDeleteFolders.Remove(name);
        return Path.Combine(oldFolder, name);
    }

    private void CreateMoveActivity(string name, string oldPath, string newPath)
    {
        string id = "move:" + Guid.NewGuid().ToString("N");
        var activity = new SystemActivity
        {
            Id = id,
            Kind = ActivityKind.Move,
            Title = name,
            Detail = $"Spostato in {Path.GetDirectoryName(newPath)}",
            Path = newPath,
            Progress = 100,
            IsFinished = true,
            Status = "Completato"
        };
        Add(activity);
        _removeAfter[id] = DateTime.UtcNow.AddSeconds(4);

        // The copy activity created for the target is now redundant.
        string copyId = "file:" + newPath;
        if (_byId.TryGetValue(copyId, out SystemActivity? copy) && !copy.IsFinished)
            Remove(copyId);
        else
            _files.Remove(newPath);
    }

    private void TouchExtraction(string folder, string fileName, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        string id = "extract:" + folder.ToLowerInvariant();
        if (!_byId.TryGetValue(id, out SystemActivity? activity))
        {
            activity = new SystemActivity
            {
                Id = id,
                Kind = ActivityKind.Extraction,
                Title = "Estrazione in corso",
                Detail = Path.GetFileName(folder),
                Path = folder,
                Progress = -1,
                Status = "In corso"
            };
            Add(activity);
            _extractionCounts[id] = 0;
        }

        _extractionCounts[id] = _extractionCounts.GetValueOrDefault(id) + 1;
        _extractionLast[id] = now;
        activity.Detail = $"{_extractionCounts[id]} file · {Path.GetFileName(folder)}";
        activity.IsFinished = false;
        activity.Progress = -1;
        activity.Status = "In corso";
    }

    private void OnTick()
    {
        if (_disposed)
            return;

        DateTime now = DateTime.UtcNow;

        if (_ticks++ % 3 == 0)
        {
            PollProcesses();
            PruneRecent(now);
        }

        foreach ((string path, FileTrack track) in _files.ToList())
        {
            if (string.IsNullOrWhiteSpace(track.ActivityId))
                continue;

            double idle = (now - track.LastGrowthUtc).TotalSeconds;
            if (idle < 6)
                continue;

            // A partial download that is still on disk is not finished: the
            // browser is pausing or waiting for data. Only a real disappearance
            // or rename completes it.
            if (IsTempDownload(path) && File.Exists(path))
            {
                if (_byId.TryGetValue(track.ActivityId, out SystemActivity? partial) && !partial.IsFinished)
                {
                    partial.Status = "In pausa";
                    partial.Detail = $"Download · {FormatSize(track.Size)} · in attesa";
                }

                continue;
            }

            if (_byId.TryGetValue(track.ActivityId, out SystemActivity? activity) && !activity.IsFinished)
            {
                activity.Progress = 100;
                activity.IsFinished = true;
                activity.Status = "Completato";
                activity.Detail = track.Size > 0
                    ? $"{ActivityVerb(activity.Kind)} · {FormatSize(track.Size)}"
                    : "Completato";
                _removeAfter[activity.Id] = now.AddSeconds(4.5);
            }

            _files.Remove(path);
        }

        foreach ((string id, DateTime last) in _extractionLast.ToList())
        {
            if ((now - last).TotalSeconds < 5)
                continue;

            if (_byId.TryGetValue(id, out SystemActivity? activity))
            {
                activity.Progress = 100;
                activity.IsFinished = true;
                activity.Status = "Completato";
                activity.Detail = $"{_extractionCounts.GetValueOrDefault(id)} file estratti";
                _removeAfter[id] = now.AddSeconds(4);
            }

            _extractionLast.Remove(id);
            _extractionCounts.Remove(id);
        }

        foreach ((string id, DateTime removeAt) in _removeAfter.ToList())
        {
            if (removeAt <= now)
            {
                Remove(id);
                _removeAfter.Remove(id);
            }
        }

        Changed?.Invoke();
    }

    private void PollProcesses()
    {
        var liveMedia = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool archiver = false;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Write("Process scan for activities failed: " + ex.Message);
            return;
        }

        foreach (Process process in processes)
        {
            try
            {
                string name = process.ProcessName;
                if (ArchiverProcesses.Contains(name))
                    archiver = true;

                if (!MediaProcesses.Contains(name))
                    continue;

                string title = process.MainWindowTitle?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(title) || process.MainWindowHandle == IntPtr.Zero)
                    continue;

                string id = "media:" + process.Id;
                liveMedia.Add(id);
                if (!_byId.TryGetValue(id, out SystemActivity? activity))
                {
                    activity = new SystemActivity
                    {
                        Id = id,
                        Kind = ActivityKind.Media,
                        Title = title,
                        Detail = FriendlyProcessName(name) + " · in riproduzione",
                        IsMedia = true,
                        Progress = -1,
                        Status = "In corso"
                    };
                    Add(activity);
                }
                else
                {
                    activity.Title = title;
                    activity.Detail = FriendlyProcessName(name) + " · in riproduzione";
                    activity.IsFinished = false;
                }
            }
            catch
            {
                // A process can exit between enumeration and inspection.
            }
            finally
            {
                process.Dispose();
            }
        }

        // The process list above only knows a handful of players. Windows
        // exposes the actual audio sessions, which also covers browsers,
        // meetings and any other app playing media right now.
        HashSet<uint> audioProcessIds;
        try
        {
            audioProcessIds = GetAudioSessionProcessIds();
        }
        catch (Exception ex)
        {
            _audioProbeUnavailable = true;
            audioProcessIds = new HashSet<uint>();
            Log.Write("Audio session probe failed: " + ex.Message);
        }

        foreach (uint processId in audioProcessIds)
        {
            if (processId == 0 || processId == (uint)Environment.ProcessId)
                continue;

            Process? process = null;
            try
            {
                process = Process.GetProcessById((int)processId);
                string name = process.ProcessName;
                string title = process.MainWindowTitle?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(title))
                    title = FriendlyProcessName(name);

                string id = "media:" + processId;
                liveMedia.Add(id);
                if (!_byId.TryGetValue(id, out SystemActivity? activity))
                {
                    activity = new SystemActivity
                    {
                        Id = id,
                        Kind = ActivityKind.Media,
                        Title = title,
                        Detail = FriendlyProcessName(name) + " · audio attivo",
                        IsMedia = true,
                        Progress = -1,
                        Status = "In corso"
                    };
                    Add(activity);
                }
                else
                {
                    activity.Title = title;
                    activity.Detail = FriendlyProcessName(name) + " · audio attivo";
                    activity.IsFinished = false;
                }
            }
            catch
            {
                // The session owner can exit between the COM query and this lookup.
            }
            finally
            {
                process?.Dispose();
            }
        }

        _archiverRunning = archiver;

        foreach (string id in _mediaIds.ToList())
        {
            if (liveMedia.Contains(id))
                continue;

            if (_byId.TryGetValue(id, out SystemActivity? activity))
            {
                activity.Progress = 100;
                activity.IsFinished = true;
                activity.Status = "Completato";
                activity.Detail = "Riproduzione terminata";
                _removeAfter[id] = DateTime.UtcNow.AddSeconds(3);
            }

            _mediaIds.Remove(id);
        }

        foreach (string id in liveMedia)
            _mediaIds.Add(id);
    }

    private HashSet<uint> GetAudioSessionProcessIds()
    {
        var result = new HashSet<uint>();
        if (_audioProbeUnavailable)
            return result;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) != 0 || device is null)
                return result;

            Guid iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object? activated) != 0 ||
                activated is not IAudioSessionManager2 sessionManager)
                return result;

            manager = sessionManager;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
                return result;

            if (sessions.GetCount(out int count) != 0)
                return result;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(i, out control) != 0 || control is null)
                        continue;

                    if (control.GetState(out int state) != 0 || state != 1 /* AudioSessionStateActive */)
                        continue;

                    if (control is IAudioSessionControl2 control2 &&
                        control2.GetProcessId(out uint processId) == 0 && processId != 0)
                        result.Add(processId);
                }
                catch
                {
                    // One malformed session must not hide the others.
                }
                finally
                {
                    Release(control);
                }
            }
        }
        catch (Exception ex)
        {
            _audioProbeUnavailable = true;
            Log.Write("Audio session probe unavailable: " + ex.Message);
        }
        finally
        {
            Release(sessions);
            Release(manager);
            Release(device);
            Release(enumerator);
        }

        return result;
    }

    private static void Release(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
        catch
        {
            // Best effort; the session objects die with the process.
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice(string id, out IntPtr device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessions);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification(string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl? session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath(string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName(string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath(string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    private void Add(SystemActivity activity)
    {
        if (_disposed)
            return;

        if (_dismissedUntil.TryGetValue(activity.Id, out DateTime until) && until > DateTime.UtcNow)
            return;

        if (_byId.ContainsKey(activity.Id))
        {
            _byId[activity.Id] = activity;
            return;
        }

        _byId[activity.Id] = activity;
        Activities.Insert(0, activity);
        while (Activities.Count > 24)
        {
            SystemActivity oldest = Activities[^1];
            Activities.RemoveAt(Activities.Count - 1);
            _byId.Remove(oldest.Id);
        }

        Changed?.Invoke();
    }

    private void Remove(string id)
    {
        if (_byId.Remove(id, out SystemActivity? activity))
        {
            Activities.Remove(activity);
            _mediaIds.Remove(id);
            Changed?.Invoke();
        }
    }

    private bool IsShelfPath(string path) =>
        path.StartsWith(_shelfRoot, StringComparison.OrdinalIgnoreCase);

    private static bool IsTempDownload(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return TempExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal));
    }

    private static bool IsArchive(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return ArchiveExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static string FriendlyProcessName(string processName) => processName switch
    {
        "vlc" => "VLC",
        "mpv" => "mpv",
        "spotify" => "Spotify",
        "music.ui" => "Media Player",
        "wmplayer" => "Windows Media Player",
        "potplayermini64" or "potplayermini" => "PotPlayer",
        "mpc-hc64" => "MPC-HC",
        "mpc-be64" => "MPC-BE",
        _ => char.ToUpperInvariant(processName[0]) + processName[1..]
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:0.##} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };

    private static string ActivityVerb(ActivityKind kind) => kind switch
    {
        ActivityKind.Download => "Download completato",
        ActivityKind.Compression => "Archivio creato",
        ActivityKind.Upload => "Caricamento completato",
        ActivityKind.Save => "Salvato",
        ActivityKind.Copy => "Copia completata",
        _ => "Completato"
    };

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
            return "";

        return FormatSize((long)bytesPerSecond) + "/s";
    }

    private void PruneRecent(DateTime now)
    {
        foreach ((string name, DateTime value) in _recentDeletes.ToList())
        {
            if ((now - value).TotalSeconds > 10)
            {
                _recentDeletes.Remove(name);
                _recentDeleteFolders.Remove(name);
            }
        }

        foreach ((string name, DateTime value) in _recentCreates.ToList())
        {
            if ((now - value).TotalSeconds > 10)
            {
                _recentCreates.Remove(name);
                _recentCreatePaths.Remove(name);
            }
        }

        foreach ((string path, DateTime value) in _lastTouched.ToList())
        {
            if ((now - value).TotalSeconds > 30)
                _lastTouched.Remove(path);
        }

        foreach ((string id, DateTime value) in _dismissedUntil.ToList())
        {
            if (value <= now)
                _dismissedUntil.Remove(id);
        }
    }

    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(DispatcherPriority.Background, action);
    }

    private sealed class FileTrack
    {
        public string Path { get; init; } = "";
        public long Size { get; set; }
        public long LastSampleSize { get; set; }
        public DateTime LastSampleUtc { get; set; }
        public DateTime LastGrowthUtc { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public DateTime CreatedUtc { get; init; }
        public double SpeedBps { get; set; }
        public string ActivityId { get; set; } = "";
    }
}
