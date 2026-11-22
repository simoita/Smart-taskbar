using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;

namespace Nebula.Views;

public partial class LauncherWindow : Window
{
    private readonly AppCatalog _catalog;
    private readonly WindowTracker? _tracker;
    private readonly AppSettings? _settings;
    private readonly FileSearchService? _files;
    private readonly ThemeEngine? _theme;
    private readonly bool _pickMode;
    private readonly Action<string>? _onPicked;
    private readonly ObservableCollection<AppEntry> _results = new();
    private readonly DispatcherTimer _searchTimer;
    private readonly List<AppEntry> _quick = new();
    private string _quickQuery = "\u0000";
    private int _searchVersion;
    private DateTime _shownAt;
    private bool _closing;
    private bool _settingsOpen;

    public LauncherWindow(AppCatalog catalog, bool pickMode = false, Action<string>? onPicked = null)
        : this(catalog, null, null, null, pickMode, onPicked)
    {
    }

    public LauncherWindow(AppCatalog catalog, WindowTracker? tracker, AppSettings? settings,
        ThemeEngine? theme = null, bool pickMode = false, Action<string>? onPicked = null,
        FileSearchService? files = null)
    {
        _catalog = catalog;
        _tracker = tracker;
        _settings = settings;
        _files = files;
        _theme = theme;
        _pickMode = pickMode;
        _onPicked = onPicked;

        InitializeComponent();
        ResultList.ItemsSource = _results;

        // Typing stays native: keystrokes are coalesced into one search per
        // pause instead of one synchronous search per character.
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(110)
        };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshResults();
        };

        if (_settings is not null)
        {
            Card.Width = Math.Clamp(_settings.SearchCardWidth, 560, 900);
            _settings.PropertyChanged += OnSettingsChanged;
        }

        _files?.StartLoadIfNeeded();
        if (_files is not null)
            _files.IndexReady += OnFilesReady;

        if (_theme is not null)
            _theme.Changed += OnThemeChanged;

        if (pickMode)
        {
            CaptionText.Text = "Scegli un'app da fissare alla dock";
        }

        Loaded += OnLoaded;
        _catalog.Loaded += OnCatalogLoaded;
        Closed += OnClosed;

        // The search card can be moved with the mouse while the dim keeps the
        // rest of the desktop in place.
        PanelDrag.AttachCard(HeaderDragArea, Card, CardDragShift);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var appSettings = _settings ?? App.Instance?.Settings;
        Rect work = ScreenGeometry.BoundsDip(ScreenGeometry.PanelTarget(
            appSettings?.MonitorIndex ?? 0,
            appSettings?.PanelsFollowDock ?? true));
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;

        AnimateIn();

        // Il catalogo viene ricostruito se è vecchio: un programma installato
        // durante la sessione deve comparire senza riavviare Nebula.
        _catalog.LoadAsync();

        _shownAt = DateTime.UtcNow;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        RefreshResults();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _catalog.Loaded -= OnCatalogLoaded;
        if (_files is not null)
            _files.IndexReady -= OnFilesReady;
        StopMotion();
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsChanged;
        if (_theme is not null)
            _theme.Changed -= OnThemeChanged;
        _results.Clear();
        DataContext = null;
        Content = null;
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.SearchCardWidth) && _settings is not null)
            Card.Width = Math.Clamp(_settings.SearchCardWidth, 560, 900);

        if (e.PropertyName is nameof(AppSettings.SearchIncludeFiles)
            or nameof(AppSettings.SearchIncludeWindows))
            RefreshResults();

        if (e.PropertyName is nameof(AppSettings.AnimationsEnabled) or nameof(AppSettings.AnimationSpeed))
        {
            if (_settings?.AnimationsEnabled == true)
                AnimateIn();
            else
                StopMotion();
        }
    }

    private void OnThemeChanged()
    {
        // Every surface brush is a DynamicResource; this repaint also refreshes the
        // cached text/selection styles in the result list after a preset change.
        if (IsLoaded)
            ResultList.InvalidateVisual();
    }

    private void AnimateIn()
    {
        StopMotion();

        if (_settings?.AnimationsEnabled == false)
            return;

        double speed = Math.Clamp(_settings?.AnimationSpeed ?? 1, 0.5, 2);
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170 / speed));
        Dim.BeginAnimation(OpacityProperty, fade);

        var cardFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200 / speed))
        {
            BeginTime = TimeSpan.FromMilliseconds(40 / speed),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Card.BeginAnimation(OpacityProperty, cardFade);

        var scale = new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(240 / speed))
        {
            BeginTime = TimeSpan.FromMilliseconds(40 / speed),
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
        };
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }

    private void StopMotion()
    {
        Dim.BeginAnimation(OpacityProperty, null);
        Card.BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Dim.Opacity = 1;
        Card.Opacity = 1;
        CardScale.ScaleX = 1;
        CardScale.ScaleY = 1;
    }

    private void OnCatalogLoaded() => Dispatcher.InvokeAsync(() =>
    {
        _quickQuery = "\u0000";
        RefreshResults();
    });

    private void OnFilesReady() => Dispatcher.InvokeAsync(() =>
    {
        _quickQuery = "\u0000";
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            RefreshResults();
    });

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;

        // Coalesce keystrokes: the text box never waits for a result set.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RefreshResults()
    {
        _searchTimer.Stop();
        int version = ++_searchVersion;
        string query = SearchBox.Text.Trim();

        ApplyResults(query, BuildQuickResults(query));

        // Files are ranked on a worker thread and merged when they are ready;
        // the list already shows apps, recent items and open windows.
        if (query.Length == 0 || _pickMode || _files is null || _settings?.SearchIncludeFiles == false)
            return;

        _ = LoadFileResultsAsync(query, version);
    }

    /// <summary>Apps, recent items and open windows: cheap enough to run inline.</summary>
    private List<AppEntry> BuildQuickResults(string query)
    {
        if (string.Equals(query, _quickQuery, StringComparison.Ordinal))
            return _quick;

        var merged = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(AppEntry entry)
        {
            if (seen.Add(KeyOf(entry)))
                merged.Add(entry);
        }

        if (_pickMode)
        {
            foreach (AppEntry entry in _catalog.Search(query, 16)
                         .Where(entry => !string.IsNullOrWhiteSpace(entry.Path)))
                Add(entry);
        }
        else if (query.Length == 0)
        {
            // Empty query: recent apps first, then the catalog. Actions stay
            // out of the way until the user actually types something.
            if (_settings is not null)
            {
                foreach (string path in _settings.RecentApps.Take(6))
                {
                    AppEntry? entry = _catalog.FindByPath(path);
                    if (entry is not null)
                    {
                        Add(entry);
                        continue;
                    }

                    if (!File.Exists(path))
                        continue;

                    Add(new AppEntry
                    {
                        Name = Path.GetFileNameWithoutExtension(path),
                        Path = path,
                        Subtitle = "Usato di recente",
                        Glyph = "\uE81C",
                        Kind = AppEntryKind.Application
                    });
                }
            }

            foreach (AppEntry entry in _catalog.Search("", 10))
            {
                if (entry.Kind != AppEntryKind.Action || merged.Count == 0)
                    Add(entry);
            }
        }
        else
        {
            foreach (AppEntry entry in _catalog.Search(query, 14))
                Add(entry);

            if (_settings?.SearchIncludeWindows != false)
            {
                foreach (AppEntry entry in SearchOpenWindows(query, 5))
                    Add(entry);
            }
        }

        _quickQuery = query;
        _quick.Clear();
        _quick.AddRange(merged);
        return _quick;
    }

    private async Task LoadFileResultsAsync(string query, int version)
    {
        IReadOnlyList<FileSearchResult> files;
        try
        {
            files = await _files!.SearchAsync(query, 7).ConfigureAwait(true);
        }
        catch
        {
            return;
        }

        // A newer keystroke already owns the list.
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

    /// <summary>
    /// Shows the results, rebuilding the collection only when the list really
    /// changed: an identical list would otherwise re-create every row on each
    /// keystroke and lose the arrow-key selection.
    /// </summary>
    private void ApplyResults(string query, List<AppEntry> merged)
    {
        List<AppEntry> final = merged.Count <= 24 ? merged : merged.GetRange(0, 24);
        if (final.Count != _results.Count || !SameSequence(final))
        {
            string? selectedKey = (ResultList.SelectedItem as AppEntry) is { } selected
                ? KeyOf(selected)
                : null;

            _results.Clear();
            foreach (AppEntry entry in final)
                _results.Add(entry);

            int index = selectedKey is null ? -1 : final.FindIndex(entry => KeyOf(entry) == selectedKey);
            ResultList.SelectedIndex = index >= 0
                ? index
                : query.Length > 0 && final.Count > 0 ? 0 : -1;
        }

        foreach (AppEntry entry in final)
            IconLoader.Request(entry);

        EmptyText.Text = "Nessun risultato. Prova con il nome di un'app, di un file o di un'impostazione.";
        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _results.Count == 0 ? "" : _results.Count == 1 ? "1 risultato" : $"{_results.Count} risultati";
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
        Subtitle = (file.IsFolder ? "Cartella · " : "File · ") + file.Folder,
        Glyph = file.IsFolder ? "\uE8B7" : "\uE8A5",
        Kind = AppEntryKind.File
    };

    private IEnumerable<AppEntry> SearchOpenWindows(string query, int limit)
    {
        if (_tracker is null)
            yield break;

        string q = query.ToLowerInvariant();
        var matches = new List<(int Rank, AppGroup Group, TrackedWindow Window)>();

        foreach (AppGroup group in _tracker.Groups)
        {
            foreach (TrackedWindow window in group.Windows)
            {
                string title = window.Title.ToLowerInvariant();
                int rank = title.StartsWith(q, StringComparison.Ordinal) ? 0
                    : title.Contains(q, StringComparison.Ordinal) ? 1
                    : group.Name.ToLowerInvariant().Contains(q, StringComparison.Ordinal) ? 2
                    : -1;

                if (rank >= 0)
                    matches.Add((rank, group, window));
            }
        }

        foreach ((int _, AppGroup group, TrackedWindow window) in matches
                     .OrderBy(m => m.Rank)
                     .ThenBy(m => m.Window.Title, StringComparer.CurrentCultureIgnoreCase)
                     .Take(limit))
        {
            yield return new AppEntry
            {
                Name = window.Title,
                Subtitle = group.Name + " · finestra aperta",
                Glyph = "\uE737",
                WindowHandle = window.Handle,
                Icon = group.Icon
            };
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Dismiss();
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
            case Key.Enter:
                // Enter must act on what the user sees: finish any pending
                // search now instead of opening the previous first result.
                if (_searchTimer.IsEnabled)
                {
                    _searchTimer.Stop();
                    RefreshResults();
                }

                LaunchSelected();
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

    private void LaunchSelected()
    {
        if (ResultList.SelectedItem is not AppEntry entry)
            return;

        if (_pickMode)
        {
            if (string.IsNullOrWhiteSpace(entry.Path))
                return;
            _onPicked?.Invoke(entry.Path);
            Dismiss();
            return;
        }

        if (entry.WindowHandle != IntPtr.Zero)
        {
            App.Instance?.ActivateWindowForSearch(entry.WindowHandle);
            Dismiss();
            return;
        }

        if (!string.IsNullOrWhiteSpace(entry.ActionId))
        {
            App.Instance?.RunQuickAction(entry.ActionId);
            Dismiss();
            return;
        }

        // Shift+Enter reveals a file result instead of opening it.
        if (entry.IsFile && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            AppLauncher.RevealInExplorer(entry.Path, out string? revealError);
            if (revealError is not null)
                Log.Write("Launcher reveal failed: " + revealError);
            Dismiss();
            return;
        }

        if (!AppLauncher.Launch(entry.Path, out string? error))
        {
            Log.Write($"Launcher could not open '{entry.Path}': {error}");
            EmptyText.Text = error ?? "Impossibile aprire il risultato.";
            EmptyText.Visibility = Visibility.Visible;
            return;
        }

        App.Instance?.RecordRecent(entry.Path);
        Dismiss();
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelected();

    private void OnDimClick(object sender, MouseButtonEventArgs e) => Dismiss();

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closing || _settingsOpen || DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(350))
            return;
        Dismiss();
    }

    private void OnOpenStudio(object sender, RoutedEventArgs e) => App.Instance?.ShowSettings();

    /// <summary>Keeps the search open while Studio is used, so theme resources can update it live.</summary>
    public void PrepareForStudio()
    {
        _settingsOpen = true;
        Topmost = false;
    }

    public void RestoreAfterStudio()
    {
        _settingsOpen = false;
        if (!IsVisible)
            return;

        Topmost = true;
        Activate();
        SearchBox.Focus();
    }

    private void Dismiss()
    {
        if (_closing)
            return;

        _closing = true;
        Close();
    }
}
