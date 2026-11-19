using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nebula.Interop;
using Nebula.Services;
using Nebula.Utilities;

namespace Nebula.Views;

public sealed class StartTile : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Glyph { get; init; } = "\uE71D";

    /// <summary>Icons load in the background: the tile refreshes when one arrives.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value))
                return;

            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>Right-click menu with open, run as admin, open with, pin and uninstall.</summary>
    public ContextMenu? Menu { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class StartPanelWindow : Window
{
    private readonly ObservableCollection<StartTile> _pinned = new();
    private readonly ObservableCollection<StartTile> _recent = new();
    private readonly ObservableCollection<AppEntry> _results = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly List<AppEntry> _quick = new();
    private string _quickQuery = "\u0000";
    private int _searchVersion;
    private DateTime _shownAt;
    private bool _suppressDeactivate;
    private bool _closing;

    public StartPanelWindow()
    {
        InitializeComponent();

        DataContext = App.Instance?.Dock;
        PinnedItems.ItemTemplate = (DataTemplate)FindResource("StartTileTemplate");
        RecentItems.ItemTemplate = (DataTemplate)FindResource("StartTileTemplate");
        PinnedItems.ItemsSource = _pinned;
        RecentItems.ItemsSource = _recent;
        ResultList.ItemsSource = _results;
        UserText.Text = Environment.UserName;

        // Typing must never wait for a search: keystrokes are coalesced and the
        // expensive work (files, icons) happens off the UI thread.
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshResults();
        };

        Loaded += OnLoaded;
        Closed += OnClosed;

        if (App.Instance is { } app)
        {
            app.Catalog.Loaded += OnCatalogLoaded;
            app.Files.IndexReady += OnFilesReady;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        if (App.Instance is { } app)
        {
            app.Catalog.Loaded -= OnCatalogLoaded;
            app.Files.IndexReady -= OnFilesReady;
        }

        DataContext = null;
    }

    private void OnCatalogLoaded() => Dispatcher.InvokeAsync(() =>
    {
        _quickQuery = "\u0000";
        RefreshContent();
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            RefreshResults();
    });

    private void OnFilesReady() => Dispatcher.InvokeAsync(() =>
    {
        _quickQuery = "\u0000";
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            RefreshResults();
    });

    public void RefreshContent()
    {
        App app = App.Instance;
        if (app is null)
            return;

        _pinned.Clear();
        _recent.Clear();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in app.Settings.PinnedPaths.Take(18))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            StartTile? tile = BuildTile(path, app);
            if (tile is null || !used.Add(tile.Path))
                continue;
            _pinned.Add(tile);
        }

        // A fresh installation has a small pin set; fill the grid with the
        // first useful applications instead of showing an empty panel.
        if (_pinned.Count < 6)
        {
            foreach (AppEntry entry in app.Catalog.Apps
                         .Where(entry => entry.Kind == AppEntryKind.Application &&
                                         !string.IsNullOrWhiteSpace(entry.Path))
                         .Take(12))
            {
                if (!used.Add(entry.Path))
                    continue;

                _pinned.Add(CreateCatalogTile(entry));

                if (_pinned.Count >= 10)
                    break;
            }
        }

        foreach (string path in app.Settings.RecentApps.Take(8))
        {
            StartTile? tile = BuildTile(path, app);
            if (tile is null || used.Contains(tile.Path))
                continue;
            if (!File.Exists(tile.Path))
                continue;

            _recent.Add(tile);
        }

        if (_recent.Count == 0)
        {
            foreach (AppEntry entry in app.Catalog.Apps
                         .Where(entry => entry.Kind == AppEntryKind.Application &&
                                         !string.IsNullOrWhiteSpace(entry.Path))
                         .Take(12))
            {
                if (used.Contains(entry.Path))
                    continue;

                _recent.Add(CreateCatalogTile(entry));

                if (_recent.Count >= 6)
                    break;
            }
        }
    }

    private static StartTile? BuildTile(string path, App app)
    {
        string target = ShortcutResolver.ResolveExecutable(path) ?? path;
        AppEntry? entry = app.Catalog.FindByPath(path) ?? app.Catalog.FindByPath(target);
        string name = entry?.Name ?? Path.GetFileNameWithoutExtension(target);

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var tile = new StartTile
        {
            Name = name,
            Path = path,
            Glyph = "\uE71D",
            Icon = entry?.Icon,
            Menu = BuildMenu(path, name)
        };

        // Opening the panel must not wait for the shell to hand out icons.
        if (tile.Icon is null)
            IconLoader.Request(target, icon => tile.Icon = icon);

        return tile;
    }

    private static StartTile CreateCatalogTile(AppEntry entry)
    {
        var tile = new StartTile
        {
            Name = entry.Name,
            Path = entry.Path,
            Glyph = entry.Glyph ?? "\uE71D",
            Icon = entry.Icon
        };

        if (tile.Icon is null)
            IconLoader.Request(entry.Path, icon => tile.Icon = icon);

        return tile;
    }

    /// <summary>
    /// Full right-click menu for an app: the same actions Windows puts on a
    /// shortcut, plus pinning and the app's own uninstaller.
    /// </summary>
    private static ContextMenu BuildMenu(string path, string name)
    {
        var menu = new ContextMenu();

        MenuItem Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Add("Apri", () => App.Instance?.LaunchPath(path));
        Add("Apri come amministratore", () =>
            AppActions.RunAsAdmin(path, out string? error));
        Add("Apri con…", () => AppActions.OpenWith(path, out _));
        Add("Apri percorso file", () =>
            AppLauncher.RevealInExplorer(ShortcutResolver.ResolveExecutable(path) ?? path, out _));

        menu.Items.Add(new Separator());

        bool pinned = App.Instance?.Settings.PinnedPaths
            .Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) == true;
        Add(pinned ? "Rimuovi dalla taskbar" : "Fissa alla taskbar", () =>
            App.Instance?.TogglePin(path));

        menu.Items.Add(new Separator());

        Add("Disinstalla", () =>
        {
            if (MessageBox.Show($"Disinstallare “{name}”?", "Nebula",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string? target = AppActions.ResolveTarget(path);
            AppActions.Uninstall(target ?? path, name, out string? error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                MessageBox.Show(error, "Nebula",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        });

        return menu;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var appSettings = App.Instance?.Settings;
        Rect work = ScreenGeometry.BoundsDip(ScreenGeometry.PanelTarget(
            appSettings?.MonitorIndex ?? 0,
            appSettings?.PanelsFollowDock ?? true));
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;

        double bottom = App.Instance?.Settings is { ReplaceWindowsTaskbar: true, DockOnTop: false } settings
            ? settings.DockHeight + 18
            : 24;

        Panel.Margin = new Thickness(0, 0, 0, bottom);
        Panel.Height = Math.Clamp(work.Height - bottom - 54, 420, 680);

        _shownAt = DateTime.UtcNow;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        AnimateIn();
        Dispatcher.InvokeAsync(AnimateTiles, DispatcherPriority.Loaded);
    }

    private void AnimateIn()
    {
        if (App.Instance?.Settings.AnimationsEnabled == false)
        {
            Dim.Opacity = 1;
            Panel.Opacity = 1;
            PanelShift.Y = 0;
            return;
        }

        double speed = Math.Clamp(App.Instance?.Settings.AnimationSpeed ?? 1, 0.5, 2);
        Dim.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170 / speed)));
        Panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210 / speed))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        PanelShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(34, 0, TimeSpan.FromMilliseconds(260 / speed))
            {
                EasingFunction = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut }
            });
    }

    private void AnimateTiles()
    {
        if (App.Instance?.Settings.AnimationsEnabled == false)
            return;

        double speed = Math.Clamp(App.Instance?.Settings.AnimationSpeed ?? 1, 0.5, 2);
        for (int index = 0; index < _pinned.Count; index++)
        {
            if (PinnedItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement tile)
                continue;

            tile.Opacity = 0;
            var shift = new TranslateTransform(0, 14);
            tile.RenderTransform = shift;
            tile.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(210 / speed))
                {
                    BeginTime = TimeSpan.FromMilliseconds(35 * index / speed),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260 / speed))
                {
                    BeginTime = TimeSpan.FromMilliseconds(35 * index / speed),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Switching views is instant; only the results wait for the pause.
        if (string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _searchTimer.Stop();
            _searchVersion++;
            _quickQuery = "\u0000";
            HomeView.Visibility = Visibility.Visible;
            ResultsView.Visibility = Visibility.Collapsed;
            _results.Clear();
            return;
        }

        HomeView.Visibility = Visibility.Collapsed;
        ResultsView.Visibility = Visibility.Visible;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RefreshResults()
    {
        App app = App.Instance;
        if (app is null)
            return;

        _searchTimer.Stop();
        int version = ++_searchVersion;
        string query = SearchBox.Text.Trim();

        ApplyResults(query, BuildQuickResults(query, app));

        // Files are ranked on a worker thread: the list is already on screen
        // before the index is touched.
        if (query.Length == 0 || !app.Settings.SearchIncludeFiles)
            return;

        _ = LoadFileResultsAsync(query, version, app);
    }

    private List<AppEntry> BuildQuickResults(string query, App app)
    {
        if (string.Equals(query, _quickQuery, StringComparison.Ordinal))
            return _quick;

        var merged = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRange(IEnumerable<AppEntry> entries)
        {
            foreach (AppEntry entry in entries)
            {
                if (seen.Add(KeyOf(entry)))
                    merged.Add(entry);
            }
        }

        AddRange(app.Catalog.Search(query, 16));

        if (app.Settings.SearchIncludeWindows)
            AddRange(SearchWindows(query, 4));

        _quickQuery = query;
        _quick.Clear();
        _quick.AddRange(merged);
        return _quick;
    }

    private async Task LoadFileResultsAsync(string query, int version, App app)
    {
        IReadOnlyList<FileSearchResult> files;
        try
        {
            files = await app.Files.SearchAsync(query, 6).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        if (_closing || version != _searchVersion)
            return;

        var merged = new List<AppEntry>(_quick);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry entry in merged)
            seen.Add(KeyOf(entry));

        foreach (FileSearchResult file in files)
        {
            AppEntry entry = ToFileEntry(file);
            if (seen.Add(KeyOf(entry)))
                merged.Add(entry);
        }

        ApplyResults(query, merged);
    }

    private void ApplyResults(string query, List<AppEntry> merged)
    {
        List<AppEntry> final = merged.Count <= 22 ? merged : merged.GetRange(0, 22);
        if (final.Count != _results.Count || !SameSequence(final))
        {
            string? selectedKey = (ResultList.SelectedItem as AppEntry) is { } selected
                ? KeyOf(selected)
                : null;

            _results.Clear();
            foreach (AppEntry entry in final)
                _results.Add(entry);

            int index = selectedKey is null ? -1 : final.FindIndex(entry => KeyOf(entry) == selectedKey);
            ResultList.SelectedIndex = index >= 0 ? index : final.Count > 0 ? 0 : -1;
        }

        foreach (AppEntry entry in final)
            IconLoader.Request(entry);

        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SameSequence(List<AppEntry> final)
    {
        for (int index = 0; index < final.Count; index++)
        {
            if (!string.Equals(KeyOf(_results[index]), KeyOf(final[index]), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static string KeyOf(AppEntry entry) =>
        entry.WindowHandle != IntPtr.Zero
            ? "w:" + entry.WindowHandle
            : "p:" + entry.Path + "|" + entry.ActionId + "|" + entry.Name;

    private static AppEntry ToFileEntry(FileSearchResult file) => new()
    {
        Name = file.Name,
        Path = file.Path,
        FolderPath = file.Folder,
        Subtitle = $"File · {file.Folder}",
        Glyph = "\uE8A5",
        Kind = AppEntryKind.File
    };

    private IEnumerable<AppEntry> SearchWindows(string query, int limit)
    {
        App app = App.Instance;
        if (app is null)
            yield break;

        string q = query.ToLowerInvariant();
        var matches = new List<(int Rank, AppEntry Entry)>();
        foreach (AppGroup group in app.Tracker.Groups)
        {
            foreach (TrackedWindow window in group.Windows)
            {
                string title = window.Title.ToLowerInvariant();
                int rank = title.StartsWith(q, StringComparison.Ordinal) ? 0
                    : title.Contains(q, StringComparison.Ordinal) ? 1
                    : group.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal) ? 2
                    : -1;

                if (rank < 0)
                    continue;

                matches.Add((rank, new AppEntry
                {
                    Name = window.Title,
                    Subtitle = group.Name + " · finestra aperta",
                    Glyph = "\uE737",
                    WindowHandle = window.Handle,
                    Kind = AppEntryKind.Window,
                    Icon = group.Icon
                }));
            }
        }

        foreach ((int _, AppEntry entry) in matches
                     .OrderBy(match => match.Rank)
                     .ThenBy(match => match.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(limit))
            yield return entry;
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path })
            return;

        LaunchPath(path);
    }

    private void LaunchPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // The panel is on the active screen: the app opens there, not on the
        // primary monitor.
        if (!AppLauncher.LaunchOnMonitor(path, ScreenGeometry.ActiveIndex(), out string? error))
        {
            MessageBox.Show(error ?? "Impossibile aprire l'app.", "Nebula",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        App.Instance?.RecordRecent(path);
        Dismiss();
    }

    private void OnSearchResultSelected()
    {
        if (ResultList.SelectedItem is not AppEntry entry)
            return;

        if (entry.WindowHandle != IntPtr.Zero)
        {
            App.Instance?.Tracker.ActivateWindow(entry.WindowHandle);
            Dismiss();
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.ActionId))
        {
            App.Instance?.RunQuickAction(entry.ActionId);
            Dismiss();
            return;
        }

        LaunchPath(entry.Path);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
                e.Handled = true;
                break;
            case Key.Enter:
                // Finish any pending search first, so Enter opens the result the
                // user is looking at instead of the previous one.
                if (_searchTimer.IsEnabled)
                {
                    _searchTimer.Stop();
                    RefreshResults();
                }

                OnSearchResultSelected();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0)
            return;

        int index = ResultList.SelectedIndex + delta;
        if (index < 0)
            index = _results.Count - 1;
        else if (index >= _results.Count)
            index = 0;

        ResultList.SelectedIndex = index;
        ResultList.ScrollIntoView(ResultList.SelectedItem);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => OnSearchResultSelected();

    /// <summary>Right-click on a search result: app actions or window actions.</summary>
    private void OnResultRightClick(object sender, MouseButtonEventArgs e)
    {
        ListBoxItem? container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not AppEntry entry)
            return;

        e.Handled = true;

        if (entry.WindowHandle != IntPtr.Zero)
        {
            var windowMenu = new ContextMenu();
            var activate = new MenuItem { Header = "Porta in primo piano" };
            activate.Click += (_, _) => App.Instance?.Tracker.ActivateWindow(entry.WindowHandle);
            windowMenu.Items.Add(activate);
            var close = new MenuItem { Header = "Chiudi finestra" };
            close.Click += (_, _) => WindowTracker.PostClose(entry.WindowHandle);
            windowMenu.Items.Add(close);
            windowMenu.PlacementTarget = container;
            windowMenu.IsOpen = true;
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Path))
            return;

        ContextMenu menu = BuildMenu(entry.Path, entry.Name);
        menu.PlacementTarget = container;
        menu.IsOpen = true;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match)
                return match;
            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnDimClick(object sender, MouseButtonEventArgs e) => Dismiss();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Dismiss();

    private void OnPowerClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        bool actionPicked = false;

        void Add(string header, string glyph, string action)
        {
            menu.Items.Add(MenuFactory.Item(header, glyph, () =>
            {
                actionPicked = true;
                Dismiss();
                App.Instance?.RunPowerAction(action);
            }));
        }

        Add("Blocca", "\uE72E", "lock");
        Add("Sospendi", "\uE708", "sleep");
        menu.Items.Add(new Separator());
        Add("Riavvia", "\uE777", "restart");
        Add("Spegni", "\uE7E8", "shutdown");
        menu.Items.Add(new Separator());
        Add("Esci da Nebula", "\uE711", "exit");

        _suppressDeactivate = true;
        menu.Closed += (_, _) =>
        {
            if (!actionPicked)
                _suppressDeactivate = false;
        };
        menu.PlacementTarget = sender as Button;
        menu.IsOpen = true;
    }

    private void OnLockPc(object sender, RoutedEventArgs e) => RunPowerFromPanel("lock");

    private void OnSleepPc(object sender, RoutedEventArgs e) => RunPowerFromPanel("sleep");

    private void OnRestartPc(object sender, RoutedEventArgs e) => RunPowerFromPanel("restart");

    private void OnShutdownPc(object sender, RoutedEventArgs e) => RunPowerFromPanel("shutdown");

    private void RunPowerFromPanel(string action)
    {
        Dismiss();
        App.Instance?.RunPowerAction(action);
    }

    private void OnOpenAllApps(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Instance?.ShowLauncher();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Instance?.ShowSettings();
    }

    private void OnOpenWidgets(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Instance?.ShowWidgets();
    }

    private void OnOpenControls(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Instance?.ShowControls();
    }

    private void OnOpenTabs(object sender, RoutedEventArgs e)
    {
        Dismiss();
        App.Instance?.ShowGroupPicker();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _suppressDeactivate ||
            DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(300))
            return;

        Dismiss();
    }

    private void Dismiss()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }
}
