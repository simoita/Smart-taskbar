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
    private readonly DispatcherTimer _smartTimer;
    private readonly List<AppEntry> _quick = new();
    private string _quickQuery = "\u0000";
    private int _searchVersion;
    private bool _closing;
    private bool _settingsOpen;
    private SmartSuggestion? _smart;
    private string _smartQuery = "\u0000";
    private CancellationTokenSource? _aiCts;
    private Rect _workArea;

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
        TitleBarTheme.Apply(this);
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

        // La parte intelligente parte dopo la ricerca locale, così la lista di
        // app e finestre non aspetta mai il parser o la rete.
        _smartTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(260)
        };
        _smartTimer.Tick += (_, _) =>
        {
            _smartTimer.Stop();
            RefreshSmart();
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
        SizeChanged += OnSizeChanged;
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
        // La barra vive in basso, subito sopra la dock: è lì che l'utente la
        // cerca, e resta un classico window spostabile dalla sua intestazione.
        Width = Card.Width + 24;
        UpdateLayout();
        _workArea = work;
        PlaceBottomBar(appSettings);

        AnimateIn();

        // Il catalogo viene ricostruito se è vecchio: un programma installato
        // durante la sessione deve comparire senza riavviare Nebula.
        _catalog.LoadAsync();

        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        RefreshResults();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        _smartTimer.Stop();
        CancelAi();
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
        Card.BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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
        _smartTimer.Stop();
        _smartTimer.Start();
    }

    /// <summary>Posiziona la barra sopra la dock, o sotto se la dock è in alto.</summary>
    private void PlaceBottomBar(AppSettings? appSettings)
    {
        if (_workArea.Width <= 0)
            return;

        double gap = appSettings is { ReplaceWindowsTaskbar: true, ReserveTaskbarSpace: true }
            ? 16
            : (appSettings?.DockHeight ?? 62) + 18;

        Left = _workArea.Left + Math.Max(0, (_workArea.Width - ActualWidth) / 2);
        double top = appSettings?.DockOnTop == true
            ? _workArea.Top + gap
            : _workArea.Bottom - ActualHeight - gap;

        Top = Math.Clamp(top, _workArea.Top + 8,
            Math.Max(_workArea.Top + 8, _workArea.Bottom - ActualHeight - 8));
    }

    /// <summary>
    /// La finestra cresce con i risultati: se il bordo inferiore uscirebbe dallo
    /// schermo, sale quel tanto che basta senza spostare una barra già mossa.
    /// </summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_workArea.Width <= 0 || !IsVisible)
            return;

        AppSettings? appSettings = _settings ?? App.Instance?.Settings;
        double gap = appSettings is { ReplaceWindowsTaskbar: true, ReserveTaskbarSpace: true }
            ? 16
            : (appSettings?.DockHeight ?? 62) + 18;
        double limit = _workArea.Bottom - gap;
        if (Top + ActualHeight > limit)
            Top = Math.Max(_workArea.Top + 8, limit - ActualHeight);
    }

    /// <summary>
    /// Calcola il suggerimento locale e, se serve, chiede la risposta all'AI.
    /// La chiamata di rete parte solo per una domanda e solo se l'utente ha
    /// configurato un modello: nessuna richiesta silenziosa.
    /// </summary>
    private void RefreshSmart()
    {
        _smartTimer.Stop();
        string query = SearchBox.Text.Trim();
        if (query.Length < 2)
        {
            CancelAi();
            ClearSmart();
            return;
        }

        if (string.Equals(query, _smartQuery, StringComparison.Ordinal) && _smart is not null)
            return;

        _smartQuery = query;
        CancelAi();

        SmartSuggestion? local = SmartSearchService.Suggest(query, _catalog);
        if (local is not null)
        {
            ShowSmart(local);
            return;
        }

        AppSettings? settings = _settings ?? App.Instance?.Settings;
        if (settings is null || !settings.SmartAiEnabled)
        {
            ClearSmart();
            return;
        }

        if (!SmartSearchService.IsAiConfigured(settings))
        {
            if (SmartSearchService.LooksLikeQuestion(query))
            {
                ShowSmart(new SmartSuggestion(SmartSuggestionKind.Answer,
                    "Assistente AI non configurato",
                    "Aggiungi endpoint, modello e chiave in Studio → Ricerca. Senza AI la barra continua a cercare app, finestre e azioni e a fare i calcoli.",
                    "\uE946"));
            }
            else
            {
                ClearSmart();
            }

            return;
        }

        // Una domanda appena iniziata ("come ") non vale una richiesta: si
        // aspetta qualche parola in più, così non si spendono token a ogni pausa.
        if (query.Length < 12 || !SmartSearchService.LooksLikeQuestion(query))
        {
            ClearSmart();
            return;
        }

        ShowSmart(new SmartSuggestion(SmartSuggestionKind.Answer, "Sto pensando…",
            "Chiedo all'assistente AI.", "\uE946"));

        var cts = new CancellationTokenSource();
        _aiCts = cts;
        _ = AskSmartAiAsync(settings, query, cts.Token);
    }

    private async Task AskSmartAiAsync(AppSettings settings, string question, CancellationToken token)
    {
        AiReply reply;
        try
        {
            reply = await SmartSearchService.AskAiAsync(settings, question, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || _closing)
            return;

        // Una query più recente possiede già la barra.
        if (!string.Equals(SearchBox.Text.Trim(), question, StringComparison.Ordinal))
            return;

        _aiCts = null;
        if (reply.Error is not null)
        {
            ShowSmart(new SmartSuggestion(SmartSuggestionKind.Answer,
                "Assistente non disponibile", reply.Error, "\uE946"));
            return;
        }

        ShowSmart(new SmartSuggestion(SmartSuggestionKind.Answer, "Risposta AI",
            reply.Text ?? "", "\uE946", CopyText: reply.Text));
    }

    private void ShowSmart(SmartSuggestion suggestion)
    {
        _smart = suggestion;
        SmartGlyph.Text = suggestion.Glyph;
        SmartTitle.Text = suggestion.Title;
        SmartDetail.Text = suggestion.Detail;
        SmartHint.Text = suggestion.Command is not null ? "Invio esegue" : "Invio copia";
        SmartCard.Visibility = Visibility.Visible;
    }

    private void ClearSmart()
    {
        _smart = null;
        _smartQuery = "\u0000";
        SmartCard.Visibility = Visibility.Collapsed;
        SmartTitle.Text = "";
        SmartDetail.Text = "";
    }

    private void CancelAi()
    {
        if (_aiCts is null)
            return;

        _aiCts.Cancel();
        _aiCts.Dispose();
        _aiCts = null;
    }

    /// <summary>Invio o clic sulla card: esegue il comando o copia la risposta.</summary>
    private bool TryRunSmart()
    {
        if (_smart is not { } smart)
            return false;

        if (smart.Command is { } command)
        {
            if (command.Confirm)
            {
                string question = command.Argument switch
                {
                    "restart" => "Riavviare il PC?",
                    "signout" => "Disconnettere l'utente?",
                    _ => "Spegnere il PC?"
                };
                if (MessageBox.Show(this, question, "Nebula", MessageBoxButton.YesNo,
                        MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return true;
            }

            string? error = App.Instance?.RunSmartCommand(command);
            if (error is not null)
            {
                ShowSmart(new SmartSuggestion(SmartSuggestionKind.Answer,
                    "Comando non riuscito", error, "\uE783"));
                return true;
            }

            Dismiss();
            return true;
        }

        if (!string.IsNullOrEmpty(smart.CopyText))
        {
            try
            {
                Clipboard.SetText(smart.CopyText);
                SmartHint.Text = "Copiato";
            }
            catch (Exception ex)
            {
                Log.Write("Smart copy failed: " + ex.Message);
            }
        }

        return true;
    }

    private void OnSmartCardClick(object sender, MouseButtonEventArgs e)
    {
        TryRunSmart();
        e.Handled = true;
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

        EmptyText.Text = "Nessun risultato. Prova un'app, un calcolo come 15% di 240, un comando come «dock in alto» o una domanda.";
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

                if (_smartTimer.IsEnabled)
                {
                    _smartTimer.Stop();
                    RefreshSmart();
                }

                if (!TryRunSmart())
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
            string? actionError = App.Instance?.RunSmartCommand(
                new SmartCommand("quick", entry.ActionId));
            if (actionError is not null)
            {
                EmptyText.Text = actionError;
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

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

    private void OnOpenStudio(object sender, RoutedEventArgs e) => App.Instance?.ShowSettings();

    /// <summary>Keeps the search open while Studio is used, so theme resources can update it live.</summary>
    public void PrepareForStudio() => _settingsOpen = true;

    public void RestoreAfterStudio()
    {
        if (!_settingsOpen)
            return;

        _settingsOpen = false;
        if (!IsVisible)
            return;

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
