using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private SettingsWindow? _settingsWindow;
    private LauncherWindow? _launcher;
    private WidgetsWindow? _widgets;
    private TabGroupService? _tabs;
    private readonly Dictionary<TabGroup, TabBarWindow> _tabBars = new();
    private UpdateWindow? _updatePrompt;
    private readonly HashSet<string> _promptedUpdateVersions = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _trackerTimer;
    private DispatcherTimer? _taskbarGuard;
    private CancellationTokenSource? _pipeCts;

    public static new App Instance => (App)Current;

    public AppSettings Settings => _store.Settings;
    public ThemeEngine Theme { get; private set; } = null!;
    public DockViewModel Dock { get; private set; } = null!;
    public WindowTracker Tracker { get; private set; } = null!;
    public SystemMonitor Monitor { get; private set; } = null!;
    public AppCatalog Catalog { get; private set; } = null!;
    public NativeTaskbarController Taskbar { get; private set; } = null!;
    public TabGroupService Tabs => _tabs!;
    public SystemIconService SystemIcons { get; private set; } = null!;
    public UpdateCoordinator Updates { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Taskbar?.Restore();

        _mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            TrySendShow();
            Shutdown();
            return;
        }

        try
        {
            Boot(e.Args);
        }
        catch (Exception ex)
        {
            Log.Write("Startup failed: " + ex);
            MessageBox.Show(
                "Nebula non è riuscita ad avviarsi.\n\n" + ex.Message,
                "Nebula", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void Boot(string[] args)
    {
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
        Taskbar = new NativeTaskbarController();
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
        StartupManager.Apply(Settings.LaunchAtStartup);

        _taskbarGuard = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _taskbarGuard.Tick += (_, _) => Taskbar.EnsureProtection();

        Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
        UpdateTaskbarGuard();

        _dockWindow = new DockWindow(Dock, _store);
        if (Settings.ReplaceWindowsTaskbar)
            _dockWindow.Show();
        _dockWindow.ApplySettings();

        Tracker.Refresh();

        _trackerTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1400)
        };
        _trackerTimer.Tick += (_, _) => Tracker.Refresh();
        _trackerTimer.Start();

        _hotkeys = new HotkeyManager();
        _hotkeys.LauncherRequested += () => Dispatcher.InvokeAsync(ShowLauncher);
        _hotkeys.WidgetsRequested += () => Dispatcher.InvokeAsync(ShowWidgets);

        _tray = new TrayService(ShowSettings, ShowLauncher, ShowWidgets,
            ToggleDockVisibility, RestoreWindowsTaskbar, ExitApp);

        Updates.UpdateReady += OnUpdateReady;
        Updates.UpdateAvailable += OnUpdateAvailable;
        Updates.Start();

        StartPipeServer();

        bool startSilent = args.Any(a => string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
        bool wantSettings = args.Any(a => string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase));

        if (!startSilent && (_store.IsFirstRun || wantSettings))
            ShowSettings();

        if (args.Any(a => string.Equals(a, "--search", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(ShowLauncher);

        if (args.Any(a => string.Equals(a, "--widgets", StringComparison.OrdinalIgnoreCase)))
            Dispatcher.InvokeAsync(ShowWidgets);

        if (_store.IsFirstRun)
            _tray.ShowBalloon("Nebula è attiva",
                "La tua dock è pronta. Apri le impostazioni per scegliere stile, vetro e widget.");

        if (!_hotkeys.LauncherRegistered)
            Log.Write("Global launcher hotkey unavailable (already in use).");
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
                Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
                UpdateTaskbarGuard();
                if (Settings.ReplaceWindowsTaskbar)
                    _dockWindow?.ShowForTaskbarReplace();
                else
                    HideDockAndPanels();
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
                if (Settings.SurfaceStyle != Settings.TabsStyle)
                    Settings.SurfaceStyle = Settings.TabsStyle;
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
            or nameof(AppSettings.MonitorIndex))
        {
            _dockWindow?.ApplySettings();
        }

        _store.MarkDirty();
    }

    public void ApplyAll()
    {
        Theme.Reload();
        Taskbar.Apply(Settings.ReplaceWindowsTaskbar);
        UpdateTaskbarGuard();
        StartupManager.Apply(Settings.LaunchAtStartup);
        Dock.RefreshItems();
        Dock.UpdateClock();
        _tabs?.ApplyAppearance();
        _tabs?.ApplyEnabled();

        if (Settings.ReplaceWindowsTaskbar)
            _dockWindow?.ShowForTaskbarReplace();
        else
            HideDockAndPanels();

        _dockWindow?.ApplySettings();
    }

    public void ShowSettings()
    {
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

        if (_launcher is { IsVisible: true })
        {
            _launcher.Close();
            _launcher = null;
            return;
        }

        _launcher = new LauncherWindow(Catalog, Tracker, Settings, Theme);
        _launcher.Closed += (_, _) =>
        {
            _launcher = null;
            TrimAfterPanelClosed();
        };
        _launcher.Show();
        _launcher.Activate();
    }

    public void ShowLauncherForPick(Action<string> onPicked)
    {
        _launcher?.Close();
        _launcher = new LauncherWindow(Catalog, null, Settings, Theme, true, onPicked);
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
        _widgets?.Close();
        _dockWindow?.HideForTaskbarRestore();
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
        _settingsWindow?.Close();
        _updatePrompt?.Close();
        _dockWindow?.Close();

        _hotkeys?.Dispose();
        _hotkeys = null;
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
        _tabs?.Dispose();
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
                        Dispatcher.InvokeAsync(ShowSettings);
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

    private static void TrySendShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("show");
        }
        catch
        {
            // Another instance may still be starting; nothing else to do.
        }
    }
}
