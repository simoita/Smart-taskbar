using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;
using Nebula.Views;

namespace Nebula;

public partial class App : Application
{
    private const string MutexName = "NebulaTaskbarStudio.SingleInstance";
    private const string PipeName = "NebulaTaskbarStudio.Ipc";

    private Mutex? _mutex;
    private SettingsStore _store = null!;
    private TrayService? _tray;
    private HotkeyManager? _hotkeys;
    private DockWindow? _dockWindow;
    private readonly Dictionary<int, DockWindow> _extraDocks = new();
    private IslandWindow? _island;
    private SettingsWindow? _settingsWindow;
    private LauncherWindow? _launcher;
    private StartPanelWindow? _startPanel;
    private WinKeyHook? _winKeyHook;
    private WidgetsWindow? _widgets;
    private TabGroupService? _tabs;
    private readonly Dictionary<TabGroup, TabBarWindow> _tabBars = new();
    private UpdateWindow? _updatePrompt;
    private UpdateFailureWindow? _updateFailure;
    private QuickControls? _mediaControls;
    private readonly HashSet<string> _promptedUpdateVersions = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _trackerTimer;
    private DispatcherTimer? _taskbarGuard;
    private CancellationTokenSource? _pipeCts;

    public static App Instance => (App)Current;

    public AppSettings Settings => _store.Settings;
    public ThemeEngine Theme { get; private set; } = null!;
    public DockViewModel Dock { get; private set; } = null!;
    public WindowTracker Tracker { get; private set; } = null!;
    public SystemMonitor Monitor { get; private set; } = null!;
    public AppCatalog Catalog { get; private set; } = null!;
    public FileSearchService Files { get; private set; } = null!;
    public ActivityCenter Activities { get; private set; } = null!;
    public FileShelf Shelf { get; private set; } = null!;
    public NativeTaskbarController Taskbar { get; private set; } = null!;
    public TabGroupService Tabs => _tabs!;

    /// <summary>Every bar currently on screen: the selected monitor plus the others.</summary>
    private IEnumerable<DockWindow> AllDocks
    {
        get
        {
            if (_dockWindow is not null)
                yield return _dockWindow;

            foreach (DockWindow dock in _extraDocks.Values)
                yield return dock;
        }
    }
    public SystemIconService SystemIcons { get; private set; } = null!;
    public UpdateCoordinator Updates { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Give the process a shell identity before any window exists, so
        // Windows groups it as a program instead of an anonymous tool window.
        ShellIntegration.RegisterProcessId();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Taskbar?.Restore();

        // These modes must work even while another Nebula instance is running.
        // They are deliberately handled before the single-instance mutex.
        if (e.Args.Any(a => string.Equals(a, "--restore-taskbar", StringComparison.OrdinalIgnoreCase)))
        {
            NativeTaskbarController.RestoreNow();
            Shutdown(0);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--restore-icons", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(RestoreSystemIconsFromBackup() ? 0 : 1);
            return;
        }

        if (e.Args.Any(a => string.Equals(a, "--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // A self-test owns the native taskbar and AppBar while it runs. It
            // must use the same mutex as a normal launch; otherwise a second
            // process can hide the shell under the running instance and then
            // report a false taskbar restoration failure.
            _mutex = new Mutex(true, MutexName, out bool isSelfTestOwner);
            if (!isSelfTestOwner)
            {
                Log.Write("Self-test yielded: another Nebula instance owns the taskbar.");
                Shutdown(0);
                return;
            }

            _ = RunSelfTestAsync();
            return;
        }

        // Used by the installer and uninstaller: ask the running instance to
        // perform its normal cleanup instead of terminating it with a file lock.
        if (e.Args.Any(a => string.Equals(a, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownRunningInstance();
            Shutdown(0);
            return;
        }

        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            TrySendShow();
            Shutdown(0);
            return;
        }

        try
        {
            Boot(e.Args);
        }
        catch (Exception ex)
        {
            Log.Write("Startup failed: " + ex);

            // Never leave a half-started bar over the Windows taskbar: restore
            // the shell bar first, then get out of the way.
            try
            {
                Taskbar?.Restore();
                _dockWindow?.HideForTaskbarRestore();
            }
            catch (Exception cleanupError)
            {
                Log.Write("Startup failure cleanup error: " + cleanupError.Message);
            }

            // During an update the apply script is waiting for a receipt. Exit
            // at once so it rolls back, instead of a modal dialog sitting there
            // until the 60 second deadline.
            if (UpdateService.ReadPendingUpdate() is not null)
            {
                Shutdown();
                return;
            }

            MessageBox.Show(
                "Nebula non è riuscita ad avviarsi.\n\n" + ex.Message,
                "Nebula", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void Boot(string[] args)
    {
        UpdateService.CleanupConfirmedUpdateAtStartup();
        NativeTaskbarController.RecoverFromUncleanExit();
        TabRecovery.RestorePending();

        _store = new SettingsStore();
        Updates = new UpdateCoordinator(_store.Settings);
        SystemIcons = new SystemIconService();
        SystemIcons.Recover();
        Theme = new ThemeEngine(_store.Settings);
        Tracker = new WindowTracker();
        Monitor = new SystemMonitor();
        Catalog = new AppCatalog();
        Files = new FileSearchService();
        Files.StartLoadIfNeeded();
        Activities = new ActivityCenter(_store.Settings);
        Shelf = new FileShelf(Activities);
        Taskbar = new NativeTaskbarController();
        Taskbar.ProtectionLost += OnTaskbarProtectionLost;
        Dock = new DockViewModel(_store.Settings, Theme, Tracker, Monitor);
        _tabs = new TabGroupService(_store.Settings);
        _tabs.GroupsChanged += SyncTabBars;
        _tabs.GroupUpdated += group =>
        {
            if (_tabBars.TryGetValue(group, out TabBarWindow? bar))
                bar.Refresh();
        };

        if (Settings.PinnedPaths.Count == 0)
            Settings.PinnedPaths.AddRange(PinSeeder.DiscoverDefaults());

        NormalizePins();
        _store.SaveNow();

        Settings.PropertyChanged += OnSettingChanged;

        Catalog.LoadAsync();

        // The installer can enable autostart before the app has ever run. On the
        // first launch that choice must become the app setting instead of being
        // overwritten by the default (false).
        if (_store.IsFirstRun && StartupManager.IsRegistered())
            Settings.LaunchAtStartup = true;

        StartupManager.Apply(Settings.LaunchAtStartup);
        ShellIntegration.Register();

        _taskbarGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _taskbarGuard.Tick += (_, _) => Taskbar.EnsureProtection();

        bool replacementApplied = Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
        UpdateTaskbarGuard();

        // A layout fault in the bar must not abort the whole app: the tray,
        // settings and (most important during an update) the startup receipt
        // still have to work.
        try
        {
            _dockWindow = new DockWindow(Dock, _store);
            if (Settings.ReplaceWindowsTaskbar && replacementApplied)
                _dockWindow.Show();
            _dockWindow.ApplySettings();
            SyncDockWindows();

            if (Settings.ShowIsland)
                ShowIsland();
        }
        catch (Exception ex)
        {
            Log.Write("Dock startup failed: " + ex);
            try
            {
                _dockWindow?.HideForTaskbarRestore();
                Taskbar.Restore();
            }
            catch (Exception cleanupError)
            {
                Log.Write("Dock startup cleanup error: " + cleanupError.Message);
            }
            replacementApplied = false;
        }

        // Hiding the native bar is only half the guarantee. If the AppBar did
        // not reserve the work area, put the native bar back instead of leaving
        // a bar that maximized windows can cover.
        if (Settings.ReplaceWindowsTaskbar && Settings.ReserveTaskbarSpace &&
            replacementApplied && !AllDocks.All(dock => dock.AppBarApplySucceeded))
        {
            Log.Write("AppBar verification failed at startup; restoring the native taskbar.");
            Taskbar.Restore();
            _dockWindow?.HideForTaskbarRestore();
            replacementApplied = false;
        }

        Tracker.Refresh();

        // Window and foreground changes arrive through WinEvent hooks. The
        // timer is only a reconciliation net for events the shell did not
        // deliver; keep it slow when the hook is available.
        _trackerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(Tracker.EventDriven ? 5000 : 1400)
        };
        _trackerTimer.Tick += (_, _) => Tracker.Refresh();
        _trackerTimer.Start();

        _hotkeys = new HotkeyManager();
        _hotkeys.LauncherRequested += () => Dispatcher.InvokeAsync(ShowLauncher);
        _hotkeys.WidgetsRequested += () => Dispatcher.InvokeAsync(ShowWidgets);

        _winKeyHook = new WinKeyHook();
        _winKeyHook.WinPressed += () => Dispatcher.InvokeAsync(ToggleStartPanel);
        ApplyWinKeyHook();

        _tray = new TrayService(ShowSettings, ShowLauncher, ShowWidgets,
            ToggleDockVisibility, RestoreWindowsTaskbar, ExitApp);

        // A swap that could not complete restarts this (old) build. Tell the user
        // why the version did not change instead of leaving the failure only in
        // a log file.
        string? updateFailure = UpdateService.ConsumeFailureReport();
        if (updateFailure is not null)
        {
            Log.Write("Previous update failed: " + updateFailure);
            Dispatcher.InvokeAsync(() =>
            {
                _updateFailure?.Close();
                var failure = new UpdateFailureWindow(updateFailure, Updates.CurrentVersion);
                failure.Closed += (_, _) => _updateFailure = null;
                _updateFailure = failure;
                failure.Show();
                failure.Activate();
            }, DispatcherPriority.ApplicationIdle);
        }

        if (Settings.ReplaceWindowsTaskbar && !replacementApplied)
        {
            Log.Write("Replacement requested at startup, but native taskbar was left visible.");
            _tray.ShowBalloon("Taskbar non sostituita",
                "Nebula ha lasciato la taskbar di Windows visibile. Controlla app.log e riprova dalle impostazioni.");
        }

        Updates.UpdateReady += OnUpdateReady;
        Updates.UpdateAvailable += OnUpdateAvailable;
        Updates.DownloadProgress += progress => _updatePrompt?.SetProgress(progress);
        Updates.Start();

        StartPipeServer();

        if (UpdateService.ReadPendingUpdate() is { } pendingUpdate)
            _ = ConfirmUpdateAfterStartupAsync(pendingUpdate);

        bool captureDemo = args.Any(a => string.Equals(a, "--capture-demo", StringComparison.OrdinalIgnoreCase));
        bool startSilent = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase)) || captureDemo;
        bool wantSettings = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        if (!startSilent && (_store.IsFirstRun || wantSettings))
            ShowSettings();

        if (args.Any(a => string.Equals(a, "--search", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(ShowLauncher);

        if (args.Any(a => string.Equals(a, "--widgets", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(ShowWidgets);

        if (_store.IsFirstRun && !captureDemo)
            _tray.ShowBalloon("Nebula è attiva",
                "La tua dock è pronta. Apri le impostazioni per scegliere stile, vetro e widget.");

        if (!_hotkeys.LauncherRegistered)
            Log.Write("Global launcher hotkey unavailable (already in use).");

        if (captureDemo)
            _ = RunCaptureDemoAsync();
    }

    /// <summary>
    /// Reproducible real-session capture: opens two real Notepad windows, groups
    /// them into a real tab bar, expands the Dynamic Island and writes the screen
    /// rectangles the external capture script needs. It is a diagnostic mode,
    /// just like --selftest, and always restores the native taskbar on exit.
    /// </summary>
    private async Task RunCaptureDemoAsync()
    {
        string outputDir = Environment.GetEnvironmentVariable("NEBULA_CAPTURE_DIR") ??
                           Path.Combine(Environment.CurrentDirectory, "artifacts", "real-session",
                               DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(outputDir);
        var notepads = new List<Process>();

        try
        {
            Settings.ShowIsland = true;
            Settings.IslandStyle = "capsule";
            Settings.IslandScale = 1.0;
            Settings.IslandTopMargin = 8;
            Settings.SurfaceStyle = "liquid";
            Settings.TabsStyle = "liquid";
            Settings.AnimationsEnabled = true;
            Settings.EnableTabs = true;
            Settings.ReplaceWindowsTaskbar = true;
            Settings.ReserveTaskbarSpace = true;
            Settings.TaskbarFullWidth = true;
            Settings.DockOnTop = false;
            Settings.AutoHideDock = false;
            Settings.HideInFullscreen = false;
            _store.SaveNow();
            ShowIsland();
            await Task.Delay(900);

            for (int i = 0; i < 2; i++)
            {
                Process? process = Process.Start(new ProcessStartInfo("notepad.exe")
                {
                    UseShellExecute = true
                });
                if (process is null)
                    throw new InvalidOperationException("Impossibile avviare Blocco note per la demo.");

                notepads.Add(process);
                await WaitForDemoWindowAsync(process, 8000);
                NativeMethods.SetWindowText(process.MainWindowHandle, "Nebula Demo " + (char)('A' + i));
                await Task.Delay(300);
            }

            IntPtr[] handles = notepads
                .Select(p => p.MainWindowHandle)
                .Where(h => h != IntPtr.Zero)
                .ToArray();

            if (handles.Length < 2 || _tabs is null)
                throw new InvalidOperationException("Le finestre demo non sono disponibili.");

            if (!_tabs.CreateGroup(handles, out string? error))
                throw new InvalidOperationException("Gruppo demo non creato: " + error);

            WindowAccess.ShowAndActivate(handles[0]);
            await Task.Delay(800);
            _island?.ExpandAnimated();
            await Task.Delay(700);

            WriteCaptureManifest(outputDir);

            string donePath = Path.Combine(outputDir, "capture-done.txt");
            for (int i = 0; i < 180 && !File.Exists(donePath); i++)
                await Task.Delay(500);

            File.AppendAllText(Path.Combine(outputDir, "demo.txt"),
                "STYLE=" + Settings.SurfaceStyle + Environment.NewLine +
                "TABS_STYLE=" + Settings.TabsStyle + Environment.NewLine +
                "ISLAND_STYLE=" + Settings.IslandStyle + Environment.NewLine +
                "TAB_GROUPS=" + _tabs.Groups.Count + Environment.NewLine +
                "ISLAND_EXPANDED=" + (_island?.IsExpanded == true) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Write("Capture demo failed: " + ex);
            try
            {
                File.WriteAllText(Path.Combine(outputDir, "capture-error.txt"), ex.ToString());
            }
            catch
            {
                // Diagnostics only.
            }
        }
        finally
        {
            try { _tabs?.CloseAll(); } catch { }

            foreach (Process process in notepads)
            {
                try
                {
                    if (!process.HasExited)
                        process.CloseMainWindow();
                }
                catch { }
            }

            await Task.Delay(300);
            foreach (Process process in notepads)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
                process.Dispose();
            }

            ExitApp();
        }
    }

    /// <summary>
    /// Emergency recovery for the per-user icon surfaces: puts every shortcut,
    /// file type and user folder back to the value stored before Nebula touched
    /// it. Runs before the single-instance mutex, like --restore-taskbar.
    /// </summary>
    private static bool RestoreSystemIconsFromBackup()
    {
        try
        {
            var icons = new SystemIconService();
            icons.Recover();
            SystemIconApplyOutcome outcome = icons.Restore(null);
            // Also sweep references left behind when the backup is missing.
            SystemIconApplyOutcome sweep = icons.RemoveGeneratedReferences();

            var lines = new List<string>();
            if (outcome.Changed > 0 || outcome.Failures is { Count: > 0 })
                lines.Add(outcome.Message);
            if (sweep.Changed > 0 || sweep.Failures is { Count: > 0 })
                lines.Add(sweep.Message);
            if (lines.Count == 0)
                lines.Add("Nessuna icona personalizzata da ripristinare: le icone di Windows sono già originali.");

            // This command is also run by the uninstaller: it must never open a
            // modal dialog that would block an unattended uninstall.
            Log.Write("Icon restore command: " + string.Join(" | ", lines));
            return outcome.Success && sweep.Success;
        }
        catch (Exception ex)
        {
            Log.Write("Icon restore command failed: " + ex);
            return false;
        }
    }

    private static async Task WaitForDemoWindowAsync(Process process, int timeoutMs)
    {
        int waited = 0;
        while (waited < timeoutMs)
        {
            process.Refresh();
            if (process.MainWindowHandle != IntPtr.Zero)
                return;
            await Task.Delay(150);
            waited += 150;
        }

        throw new InvalidOperationException("La finestra demo non è comparsa in tempo.");
    }

    private void WriteCaptureManifest(string outputDir)
    {
        var lines = new List<string>();

        if (_island is { IsVisible: true })
            AddRect(lines, "ISLAND", _island.CapsuleScreenRect);

        if (_dockWindow is { IsVisible: true })
            AddRect(lines, "DOCK", ScreenRect(_dockWindow, _dockWindow.ShellBoundsDip));

        int tabIndex = 0;
        foreach (TabBarWindow bar in Application.Current.Windows.OfType<TabBarWindow>().Where(b => b.IsVisible))
            AddRect(lines, "TAB" + tabIndex++, ScreenRect(bar, new Rect(0, 0, bar.ActualWidth, bar.ActualHeight)));

        lines.Add("STYLE=" + Settings.SurfaceStyle);
        lines.Add("TABS_STYLE=" + Settings.TabsStyle);
        lines.Add("ISLAND_STYLE=" + Settings.IslandStyle);
        lines.Add("TAB_GROUPS=" + (_tabs?.Groups.Count ?? 0));
        lines.Add("ISLAND_EXPANDED=" + (_island?.IsExpanded == true));

        File.WriteAllLines(Path.Combine(outputDir, "capture-manifest.txt"), lines);
    }

    private static void AddRect(List<string> lines, string key, Rect rect)
    {
        lines.Add($"{key}={rect.Left:0},{rect.Top:0},{rect.Right:0},{rect.Bottom:0}");
    }

    private static Rect ScreenRect(Window window, Rect dipRect)
    {
        Point topLeft = window.PointToScreen(new Point(dipRect.Left, dipRect.Top));
        Point bottomRight = window.PointToScreen(new Point(dipRect.Right, dipRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void NormalizePins()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<string>();

        foreach (string path in Settings.PinnedPaths)
        {
            string key = ShortcutResolver.ResolveExecutable(path) ?? path;
            if (seen.Add(key))
                cleaned.Add(path);
        }

        if (cleaned.Count == Settings.PinnedPaths.Count)
            return;

        Settings.PinnedPaths.Clear();
        Settings.PinnedPaths.AddRange(cleaned);
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ThemeId):
            case nameof(AppSettings.AccentHex):
            case nameof(AppSettings.GlassOpacity):
            case nameof(AppSettings.CornerRadius):
            case nameof(AppSettings.ShowGlow):
            case nameof(AppSettings.ShowGrain):
            case nameof(AppSettings.ShowShimmer):
                Theme.Reload();
                break;

            case nameof(AppSettings.ReplaceWindowsTaskbar):
                bool replacementApplied = Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
                UpdateTaskbarGuard();
                if (Settings.ReplaceWindowsTaskbar && replacementApplied)
                {
                    foreach (DockWindow dock in AllDocks)
                        dock.ShowForTaskbarReplace();
                    SyncDockWindows();
                }
                else
                {
                    HideDockAndPanels();
                    SyncDockWindows();
                    if (Settings.ReplaceWindowsTaskbar)
                        _tray?.ShowBalloon("Taskbar non sostituita",
                            "La taskbar di Windows è rimasta visibile. Controlla app.log per il motivo.");
                }
                break;

            case nameof(AppSettings.TaskbarAllMonitors):
                SyncDockWindows();
                break;

            case nameof(AppSettings.DockFinish):
                foreach (DockWindow dock in AllDocks)
                    dock.ApplySettings();
                break;

            case nameof(AppSettings.LaunchAtStartup):
                StartupManager.Apply(Settings.LaunchAtStartup);
                break;

            case nameof(AppSettings.AutoUpdate):
                if (Settings.AutoUpdate)
                    _ = Updates.CheckNowAsync(true);
                break;

            case nameof(AppSettings.ShowPinned):
            case nameof(AppSettings.ShowRunning):
                Dock.RefreshItems();
                break;

            case nameof(AppSettings.EnableTabs):
                _tabs?.ApplyEnabled();
                break;

            case nameof(AppSettings.TabsStyle):
                Theme.Reload();
                _tabs?.ApplyAppearance();
                break;

            case nameof(AppSettings.SurfaceStyle):
                Theme.Reload();
                break;

            case nameof(AppSettings.TabsBarHeight):
            case nameof(AppSettings.TabsBarPosition):
                _tabs?.ApplyAppearance();
                break;

            case nameof(AppSettings.ShowSeconds):
            case nameof(AppSettings.Clock24Hour):
                Dock.UpdateClock();
                break;

            case nameof(AppSettings.ShowSearch):
                if (!Settings.ShowSearch)
                {
                    _launcher?.Close();
                    _launcher = null;
                }
                break;

            case nameof(AppSettings.IslandPlacement):
                if (Settings.IslandPlacement == "taskbar")
                {
                    // The capsule can only live inside the bar when that bar
                    // exists: turn the replacement and the full-width layout on
                    // instead of leaving the island floating at the top.
                    Settings.ReplaceWindowsTaskbar = true;
                    Settings.TaskbarFullWidth = true;
                    Settings.ReserveTaskbarSpace = true;
                }
                break;

            case nameof(AppSettings.ShowIsland):
                if (Settings.ShowIsland)
                    ShowIsland();
                else
                    CloseIsland();
                break;

            case nameof(AppSettings.WinKeyOpensStart):
                ApplyWinKeyHook();
                break;

            case nameof(AppSettings.ShowWidgets):
            case nameof(AppSettings.ShowQuickControls):
                _widgets?.ApplyAvailability();
                if (!Settings.ShowWidgets && !Settings.ShowQuickControls)
                {
                    _widgets?.Close();
                    _widgets = null;
                }
                break;
        }

        if (e.PropertyName is nameof(AppSettings.DockHeight)
            or nameof(AppSettings.CornerRadius)
            or nameof(AppSettings.IconSize)
            or nameof(AppSettings.ItemSpacing)
            or nameof(AppSettings.EdgeMargin)
            or nameof(AppSettings.DockOnTop)
            or nameof(AppSettings.AutoHideDock)
            or nameof(AppSettings.MonitorIndex)
            or nameof(AppSettings.MonitorFollowCursor)
            or nameof(AppSettings.PanelsFollowDock))
        {
            foreach (DockWindow dock in AllDocks)
                dock.ApplySettings();

            if (e.PropertyName == nameof(AppSettings.MonitorIndex) && Settings.TaskbarAllMonitors)
                SyncDockWindows();
        }

        _store.MarkDirty();
    }

    public void ApplyAll()
    {
        Theme.Reload();
        bool replacementApplied = Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
        UpdateTaskbarGuard();
        if (Settings.ReplaceWindowsTaskbar && !replacementApplied)
            HideDockAndPanels();
        StartupManager.Apply(Settings.LaunchAtStartup);
        Dock.RefreshItems();
        Dock.UpdateClock();
        _tabs?.ApplyAppearance();
        _tabs?.ApplyEnabled();

        if (Settings.ReplaceWindowsTaskbar)
        {
            foreach (DockWindow dock in AllDocks)
                dock.ShowForTaskbarReplace();
        }
        else
        {
            HideDockAndPanels();
        }

        SyncDockWindows();
        foreach (DockWindow dock in AllDocks)
            dock.ApplySettings();

        if (Settings.ShowIsland)
            ShowIsland();
        else
            CloseIsland();
    }

    public void ShowIsland()
    {
        if (!Settings.ShowIsland)
            return;

        if (_island is null)
        {
            _island = new IslandWindow(Dock, Activities, Shelf);
            _island.Closed += (_, _) => _island = null;
        }

        if (!_island.IsVisible)
            _island.Show();
        _island.ApplySettings();
    }

    public void CloseIsland()
    {
        _island?.Close();
        _island = null;
    }

    /// <summary>Raises the island above the bar when it is docked inside it.</summary>
    internal void RaiseIslandAboveDock() => _island?.BringAboveDock();

    /// <summary>Re-anchors the island after the bar moved or resized.</summary>
    internal void RepositionIsland()
    {
        if (_island is { IsVisible: true })
            _island.ApplySettings();
    }

    /// <summary>The bar that draws on a monitor, so the island can anchor to it.</summary>
    internal DockWindow? DockForMonitor(int monitor)
    {
        if (_extraDocks.TryGetValue(monitor, out DockWindow? extra))
            return extra;

        return _dockWindow;
    }

    public void ShowSettings()
    {
        CloseStartPanel();
        _launcher?.PrepareForStudio();

        if (_settingsWindow is null)
        {
            var viewModel = new SettingsViewModel(Settings, Dock, Theme, Monitor, _tabs!, SystemIcons, Updates);
            _settingsWindow = new SettingsWindow(viewModel, _store);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                _launcher?.RestoreAfterStudio();
            };
        }

        if (!_settingsWindow.IsVisible)
            _settingsWindow.Show();

        if (_settingsWindow.WindowState == WindowState.Minimized)
            _settingsWindow.WindowState = WindowState.Normal;

        _settingsWindow.Activate();
        _settingsWindow.Topmost = true;
        _settingsWindow.Topmost = false;
        _settingsWindow.Focus();
    }

    public void ShowLauncher()
    {
        if (!Settings.ShowSearch)
            return;

        CloseStartPanel();

        if (_launcher is { IsVisible: true })
        {
            _launcher.Close();
            _launcher = null;
            return;
        }

        _launcher = new LauncherWindow(Catalog, Tracker, Settings, Theme, files: Files);
        _launcher.Closed += (_, _) =>
        {
            _launcher = null;
            TrimAfterPanelClosed();
        };
        _launcher.Show();
        _launcher.Activate();
    }

    public void ShowStartPanel()
    {
        if (_startPanel is { IsVisible: true })
        {
            _startPanel.Activate();
            return;
        }

        _startPanel = new StartPanelWindow();
        _startPanel.Closed += (_, _) => _startPanel = null;
        _startPanel.RefreshContent();
        _startPanel.Show();
        _startPanel.Activate();
    }

    public void ToggleStartPanel()
    {
        if (_startPanel is { IsVisible: true })
        {
            _startPanel.Close();
            _startPanel = null;
            return;
        }

        ShowStartPanel();
    }

    public void CloseStartPanel()
    {
        _startPanel?.Close();
        _startPanel = null;
    }

    public void RecordRecent(string? path)
    {
        AppLauncher.RecordRecent(Settings, path);
        _store.MarkDirty();
    }

    /// <summary>
    /// Opens an app on the screen the user is working on. Used by the Start
    /// panel tiles and the search results.
    /// </summary>
    public void LaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (AppLauncher.LaunchOnMonitor(path, ScreenGeometry.ActiveIndex(), out string? error))
        {
            RecordRecent(path);
            return;
        }

        Log.Write($"Launch failed for '{path}': {error}");
        MessageBox.Show(error ?? "Impossibile aprire l'app.", "Nebula",
            MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Pins or unpins a path from the taskbar and refreshes every bar.</summary>
    public void TogglePin(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (Settings.PinnedPaths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            Settings.PinnedPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        else
            Settings.PinnedPaths.Add(path);

        _store.MarkDirty();
        Dock.RefreshItems();
    }

    public void RunPowerAction(string action)
    {
        if (string.Equals(action, "exit", StringComparison.OrdinalIgnoreCase))
        {
            ExitApp();
            return;
        }

        // Power commands run immediately: the panel already asked for the
        // action, so a system confirmation dialog would only interrupt it.
        try
        {
            switch (action)
            {
                case "lock":
                    NativeMethods.LockWorkStation();
                    break;
                case "sleep":
                    Process.Start(new ProcessStartInfo("rundll32.exe",
                        "powrprof.dll,SetSuspendState 0,1,0") { UseShellExecute = false });
                    break;
                case "restart":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 0") { UseShellExecute = false });
                    break;
                case "shutdown":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { UseShellExecute = false });
                    break;
                case "signout":
                    Process.Start(new ProcessStartInfo("shutdown.exe", "/l") { UseShellExecute = false });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Power action failed: " + ex.Message);
            _tray?.ShowBalloon("Nebula", "Comando di sistema non riuscito: " + ex.Message);
        }
    }

    private void ApplyWinKeyHook()
    {
        if (_winKeyHook is null)
            return;

        if (Settings.WinKeyOpensStart)
            _winKeyHook.Install();
        else
            _winKeyHook.Uninstall();
    }

    public void ShowLauncherForPick(Action<string> onPicked)
    {
        _launcher?.Close();
        _launcher = new LauncherWindow(Catalog, null, Settings, Theme, true, onPicked, Files);
        _launcher.Closed += (_, _) => _launcher = null;
        _launcher.Show();
        _launcher.Activate();
    }

    public void ShowWidgets() => ShowWidgetsPanel(WidgetsPanel.Widget);

    public void ShowControls() => ShowWidgetsPanel(WidgetsPanel.Controls);

    private void ShowWidgetsPanel(WidgetsPanel panel)
    {
        bool enabled = panel == WidgetsPanel.Widget
            ? Settings.ShowWidgets
            : Settings.ShowQuickControls;

        if (!enabled)
        {
            string target = panel == WidgetsPanel.Widget ? "Widget" : "Controlli rapidi";
            _tray?.ShowBalloon(target + " disattivati",
                "Attivali da Studio → " + (panel == WidgetsPanel.Widget ? "Widget" : "Controlli") + ".");
            return;
        }

        if (_widgets is { IsVisible: true })
        {
            if (_widgets.CurrentPanel == panel)
            {
                _widgets.Close();
                _widgets = null;
                return;
            }

            _widgets.ShowPanel(panel);
            _widgets.Activate();
            return;
        }

        _widgets = new WidgetsWindow(Dock);
        _widgets.Closed += (_, _) =>
        {
            _widgets = null;
            TrimAfterPanelClosed();
        };
        _widgets.Show();
        _widgets.ShowPanel(panel);
        _widgets.Activate();
    }

    /// <summary>Activates a window selected from the launcher without stealing its native placement.</summary>
    public void ActivateWindowForSearch(IntPtr handle)
    {
        Tracker.ActivateWindow(handle);
    }

    private void TrimAfterPanelClosed()
    {
        // ApplicationIdle can starve while the dock animates. A one-shot timer at
        // background priority runs after the closed window has left the visual tree.
        var trim = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        trim.Tick += (_, _) =>
        {
            trim.Stop();
            long before = GC.GetTotalMemory(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long after = GC.GetTotalMemory(false);
            Log.Write($"Surface memory trim: {(before / 1048576.0):N1} -> {(after / 1048576.0):N1} MB managed, " +
                      $"windows={Application.Current.Windows.Count}.");
        };
        trim.Start();
    }

    public void RunQuickAction(string actionId)
    {
        switch (actionId)
        {
            case "settings":
            case "about":
                ShowSettings();
                break;
            case "widgets":
                ShowWidgets();
                break;
            case "controls":
                ShowControls();
                break;
            case "lock":
                Interop.NativeMethods.LockWorkStation();
                break;
            case "start":
                ToggleStartPanel();
                break;
            case "tabs":
                ShowGroupPicker();
                break;
            case "sleep":
            case "restart":
            case "shutdown":
            case "signout":
            case "exit":
                RunPowerAction(actionId);
                break;
        }
    }

    /// <summary>Play/pause from the taskbar media widget.</summary>
    public void ToggleMediaPlayback()
    {
        try
        {
            _mediaControls ??= new QuickControls();
            if (!_mediaControls.SendMedia(MediaCommand.PlayPause))
                _tray?.ShowBalloon("Media", "Nessun player attivo ha accettato il comando.");
        }
        catch (Exception ex)
        {
            Log.Write("Taskbar media command failed: " + ex.Message);
        }
    }

    private void UpdateTaskbarGuard()
    {
        if (_taskbarGuard is null)
            return;

        if (Settings.ReplaceWindowsTaskbar)
            _taskbarGuard.Start();
        else
            _taskbarGuard.Stop();
    }

    public void RestoreWindowsTaskbar()
    {
        Settings.ReplaceWindowsTaskbar = false;
        Taskbar.Restore();
        UpdateTaskbarGuard();
        HideDockAndPanels();
        _store.SaveNow();
        _tray?.ShowBalloon("Taskbar ripristinata", "La taskbar di Windows è di nuovo attiva.");
    }

    public void ShowGroupPicker(IEnumerable<IntPtr>? preselected = null)
    {
        if (!Settings.EnableTabs)
        {
            MessageBox.Show(
                "Le schede sono disattivate. Attivale in Studio → Schede.",
                "Nebula Tab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var wanted = preselected?.Where(h => h != IntPtr.Zero).Distinct().ToList() ?? new List<IntPtr>();
        foreach (IntPtr handle in wanted)
        {
            if (_tabs!.FindGroup(handle) is not null)
            {
                MessageBox.Show(
                    "Una delle finestre è già in un gruppo di schede.",
                    "Nebula Tab", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!WindowAccess.TryCapture(handle, out _, out string reason))
            {
                string title = WindowAccess.ReadText(handle);
                MessageBox.Show(
                    $"La finestra “{title}” non può essere raggruppata: {reason}.",
                    "Nebula Tab", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        IReadOnlyList<WindowCandidate> candidates = _tabs!.GetCandidates();
        if (candidates.Count < 2)
        {
            MessageBox.Show(
                "Servono almeno due finestre gestibili. Le finestre protette, elevate o UWP non possono essere raggruppate.",
                "Nebula Tab", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new GroupPickerWindow(candidates, wanted);
        if (picker.ShowDialog() != true)
            return;

        if (!_tabs.CreateGroup(picker.SelectedHandles, out string? error))
        {
            MessageBox.Show(error ?? "Impossibile creare il gruppo.",
                "Nebula Tab", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void CloseAllTabGroups() => _tabs?.CloseAll();

    private void SyncTabBars()
    {
        if (_tabs is null)
            return;

        foreach (TabGroup group in _tabBars.Keys.ToList())
        {
            if (_tabs.Groups.Contains(group))
                continue;

            TabBarWindow bar = _tabBars[group];
            _tabBars.Remove(group);
            bar.AnimateClose();
        }

        foreach (TabGroup group in _tabs.Groups)
        {
            if (_tabBars.ContainsKey(group))
                continue;

            var bar = new TabBarWindow(group, _tabs, Settings, Theme);
            _tabBars[group] = bar;
            bar.Closed += (_, _) =>
            {
                if (_tabBars.TryGetValue(group, out TabBarWindow? current) && current == bar)
                    _tabBars.Remove(group);
            };
            bar.Show();
        }
    }

    private void HideDockAndPanels()
    {
        _launcher?.Close();
        _startPanel?.Close();
        _startPanel = null;
        _widgets?.Close();
        foreach (DockWindow dock in AllDocks)
            dock.HideForTaskbarRestore();
    }

    /// <summary>
    /// Creates or removes the bars on the other monitors. The bar on the
    /// selected screen always exists; the others follow the "all monitors"
    /// setting and are closed as soon as a screen disappears.
    /// </summary>
    private void SyncDockWindows()
    {
        if (_dockWindow is null)
            return;

        var wanted = new HashSet<int>();
        if (Settings.TaskbarAllMonitors && Settings.ReplaceWindowsTaskbar)
        {
            for (int index = 0; index < ScreenGeometry.Count; index++)
                wanted.Add(index);
        }

        // The selected monitor is owned by the primary window, which also
        // follows cursor/settings changes.
        wanted.Remove(ScreenGeometry.Clamp(Settings.MonitorIndex));

        foreach (KeyValuePair<int, DockWindow> pair in _extraDocks.ToList())
        {
            if (wanted.Contains(pair.Key))
                continue;

            _extraDocks.Remove(pair.Key);
            pair.Value.Close();
        }

        foreach (int index in wanted)
        {
            if (_extraDocks.ContainsKey(index) || index >= ScreenGeometry.Count)
                continue;

            var dock = new DockWindow(Dock, _store, index);
            _extraDocks[index] = dock;
            dock.Show();
            dock.ApplySettings();
        }
    }

    private void ToggleDockVisibility()
    {
        if (_dockWindow is null)
            return;

        if (!_dockWindow.IsVisible)
        {
            _dockWindow.ShowForTaskbarReplace();
            return;
        }

        _dockWindow.ToggleUserHidden();
    }

    /// <summary>
    /// The new build only marks an update successful after Boot has completed
    /// and the taskbar/AppBar contract is healthy. The updater waits for this
    /// receipt; if it never arrives, the old directory is restored.
    /// </summary>
    private async Task ConfirmUpdateAfterStartupAsync(PendingUpdate pending)
    {
        if (!UpdateService.VersionMatches(pending.TargetVersion, Updates.CurrentVersion))
            return;

        // Boot reached this point, so the new build runs and its bar is up. The
        // shell state below is diagnostic only: a refused AppBar reservation or
        // a busy Explorer used to veto every attempt and loop the same package
        // forever. A build that really cannot start never gets here.
        for (int attempt = 0; attempt < 10; attempt++)
        {
            if (IsHealthyForUpdateConfirmation())
                break;
            await Task.Delay(500);
        }

        Log.Write("Update health before confirmation: " + HealthSnapshot());
        if (UpdateService.ConfirmHealthyUpdate(pending.Token))
            Log.Write("Update health confirmation written for " + pending.TargetVersion);
        else
            Log.Write("Update confirmation was not written; the pending state or version did not match.");
    }

    private string HealthSnapshot() =>
        $"replace={Settings.ReplaceWindowsTaskbar}; reserve={Settings.ReserveTaskbarSpace}; " +
        $"nativeHidden={Taskbar.IsApplied}; docks={AllDocks.Count()}; " +
        $"visible={AllDocks.Count(dock => dock.IsVisible)}; " +
        $"appbar={string.Join(",", AllDocks.Select(dock => dock.AppBarApplySucceeded ? "ok" : "no"))}";

    private bool IsHealthyForUpdateConfirmation()
    {
        if (!Settings.ReplaceWindowsTaskbar)
            return true;

        return Taskbar.IsApplied &&
               AllDocks.Any(dock => dock.IsVisible) &&
               (!Settings.ReserveTaskbarSpace || AllDocks.All(dock => dock.AppBarApplySucceeded));
    }

    private void OnUpdateReady()
    {
        _updatePrompt?.SetBusy("Installazione completata: Nebula si sta riavviando…");
        _tray?.ShowBalloon("Aggiornamento Nebula",
            "Nuova versione pronta: Nebula si riavvia per completare l'aggiornamento.");
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1200);
            ExitApp();
        });
    }

    private void OnUpdateAvailable(UpdateManifest manifest)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_promptedUpdateVersions.Contains(manifest.Version))
                return;
            if (_updatePrompt?.IsVisible == true)
                return;

            _promptedUpdateVersions.Add(manifest.Version);
            var prompt = new UpdateWindow(manifest, Updates.CurrentVersion);
            prompt.InstallRequested += async () =>
            {
                if (_updatePrompt is null)
                    return;

                (bool ok, string message) = await Updates.InstallPendingAsync();
                if (!ok)
                    _updatePrompt?.ShowError(message);
            };
            prompt.Closed += (_, _) => _updatePrompt = null;
            _updatePrompt = prompt;
            prompt.Show();
            prompt.Activate();
        });
    }

    public void ExitApp()
    {
        try
        {
            _store?.SaveNow();
        }
        catch (Exception ex)
        {
            Log.Write("Save on exit failed: " + ex.Message);
        }

        _tabs?.CloseAll();
        Taskbar?.Restore();
        SystemIcons?.CleanupOnExit();
        _trackerTimer?.Stop();
        _pipeCts?.Cancel();

        foreach (TabBarWindow bar in _tabBars.Values.ToList())
            bar.Close();
        _tabBars.Clear();

        _widgets?.Close();
        _launcher?.Close();
        _startPanel?.Close();
        _startPanel = null;
        _winKeyHook?.Dispose();
        _winKeyHook = null;
        Activities?.Dispose();
        Shelf?.Dispose();
        _settingsWindow?.Close();
        _updatePrompt?.Close();
        _updateFailure?.Close();
        _updateFailure = null;
        _island?.Close();
        _island = null;
        foreach (DockWindow dock in AllDocks.ToList())
            dock.Close();
        _extraDocks.Clear();

        _hotkeys?.Dispose();
        _hotkeys = null;
        _mediaControls?.Dispose();
        _mediaControls = null;
        _tray?.Dispose();
        _tray = null;
        Updates?.Dispose();

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _taskbarGuard?.Stop();
        _tabs?.CloseAll();
        Taskbar?.Restore();
        SystemIcons?.CleanupOnExit();
        Updates?.Dispose();
        _mediaControls?.Dispose();
        _updateFailure?.Close();
        _updateFailure = null;
        _island?.Close();
        _island = null;
        _tabs?.Dispose();
        Tracker?.Dispose();
        _winKeyHook?.Dispose();
        _winKeyHook = null;
        Activities?.Dispose();
        _store?.Dispose();
        _trackerTimer?.Stop();

        _pipeCts?.Cancel();
        _pipeCts?.Dispose();
        _pipeCts = null;

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // Mutex may not be owned when startup failed early.
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("UI exception: " + e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// The watchdog is the only guarantee that the native bar comes back after a
    /// forced termination. If it dies and cannot be restarted, the replacement is
    /// disabled instead of leaving the user with a hidden shell bar.
    /// </summary>
    private void OnTaskbarProtectionLost()
    {
        Log.Write("Taskbar protection lost; hiding the dock and disabling replacement.");
        _dockWindow?.HideForTaskbarRestore();
        Settings.ReplaceWindowsTaskbar = false;
        _store.SaveNow();
        _tray?.ShowBalloon("Taskbar ripristinata",
            "Nebula non è riuscita a mantenere attivo il watchdog. La taskbar di Windows è di nuovo visibile.");
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        CancellationToken token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader = new StreamReader(server);
                    string? message = await reader.ReadLineAsync().ConfigureAwait(false);

                    if (string.Equals(message, "show", StringComparison.OrdinalIgnoreCase))
                        _ = Dispatcher.InvokeAsync(ShowSettings);
                    else if (string.Equals(message, "exit", StringComparison.OrdinalIgnoreCase))
                        _ = Dispatcher.InvokeAsync(ExitApp);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(700, token).ConfigureAwait(false);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    private static void TrySendShow() => TrySendPipe("show");

    private static bool TrySendPipe(string message, int connectTimeoutMs = 800, int retryForMs = 0)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(retryForMs);
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(connectTimeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(message);
                return true;
            }
            catch
            {
                // The primary may still be bringing its pipe server up. Keep
                // trying for the caller-provided window instead of reporting
                // success when the command was never delivered.
                if (DateTime.UtcNow >= deadline)
                    return false;
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Asks the primary instance to shut down and waits until it releases the
    /// single-instance mutex, so an installer can replace the files safely.
    /// </summary>
    private static void ShutdownRunningInstance()
    {
        try
        {
            // Check the process first: the installer calls --exit even when the
            // app is not running, and that must stay instant. If the app is
            // starting, the pipe server can appear a moment after the process.
            bool anotherInstance = Process.GetProcessesByName("Nebula")
                .Any(process => process.Id != Environment.ProcessId);
            if (!anotherInstance)
                return;

            if (!TrySendPipe("exit", 500, 6_000))
            {
                // A modal dialog (for example the update-failure report) keeps
                // the WPF dispatcher from handling the pipe message. Close the
                // main window once, then retry; this keeps uninstall silent.
                foreach (Process other in Process.GetProcessesByName("Nebula"))
                {
                    if (other.Id == Environment.ProcessId)
                        continue;
                    try
                    {
                        other.CloseMainWindow();
                    }
                    catch
                    {
                        // The process may have exited between enumeration and close.
                    }
                }

                if (!TrySendPipe("exit", 500, 3_000))
                {
                    Log.Write("Could not deliver the exit request to the running instance.");
                    return;
                }
            }

            using var probe = new Mutex(false, MutexName);
            bool released;
            try
            {
                released = probe.WaitOne(TimeSpan.FromSeconds(12));
            }
            catch (AbandonedMutexException)
            {
                released = true;
            }

            if (released)
                probe.ReleaseMutex();
            else
                Log.Write("The running instance did not exit within 12 seconds.");
        }
        catch (Exception ex)
        {
            Log.Write("Waiting for the running instance failed: " + ex.Message);
        }
    }
}
