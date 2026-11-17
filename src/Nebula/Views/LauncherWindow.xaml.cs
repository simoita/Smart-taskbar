using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nebula.Models;
using Nebula.Services;

namespace Nebula.Views;

public partial class LauncherWindow : Window
{
    private readonly AppCatalog _catalog;
    private readonly WindowTracker? _tracker;
    private readonly AppSettings? _settings;
    private readonly ThemeEngine? _theme;
    private readonly bool _pickMode;
    private readonly Action<string>? _onPicked;
    private readonly ObservableCollection<AppEntry> _results = new();
    private DateTime _shownAt;
    private bool _closing;
    private bool _settingsOpen;

    public LauncherWindow(AppCatalog catalog, bool pickMode = false, Action<string>? onPicked = null)
        : this(catalog, null, null, null, pickMode, onPicked)
    {
    }

    public LauncherWindow(AppCatalog catalog, WindowTracker? tracker, AppSettings? settings,
        ThemeEngine? theme = null, bool pickMode = false, Action<string>? onPicked = null)
    {
        _catalog = catalog;
        _tracker = tracker;
        _settings = settings;
        _theme = theme;
        _pickMode = pickMode;
        _onPicked = onPicked;

        InitializeComponent();
        ResultList.ItemsSource = _results;

        if (_settings is not null)
        {
            Card.Width = Math.Clamp(_settings.SearchCardWidth, 560, 900);
            _settings.PropertyChanged += OnSettingsChanged;
        }

        if (_theme is not null)
            _theme.Changed += OnThemeChanged;

        if (pickMode)
        {
            CaptionText.Text = "Scegli un'app da fissare alla dock";
        }

        Loaded += OnLoaded;
        _catalog.Loaded += OnCatalogLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Rect work = SystemParameters.WorkArea;
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;

        AnimateIn();

        _shownAt = DateTime.UtcNow;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        RefreshResults();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _catalog.Loaded -= OnCatalogLoaded;
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

    private void OnCatalogLoaded() => Dispatcher.InvokeAsync(RefreshResults);

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshResults();
    }

    private void RefreshResults()
    {
        string query = SearchBox.Text.Trim();
        int appLimit = _pickMode ? 12 : 8;
        IReadOnlyList<AppEntry> found = _catalog.Search(SearchBox.Text, appLimit);
        if (_pickMode)
            found = found.Where(a => !string.IsNullOrWhiteSpace(a.Path)).ToList();

        var merged = found.ToList();
        if (!_pickMode && query.Length > 0 && _settings?.SearchIncludeWindows != false)
            merged.AddRange(SearchOpenWindows(query, 5));

        _results.Clear();
        foreach (AppEntry entry in merged)
        {
            entry.EnsureIcon();
            _results.Add(entry);
        }

        EmptyText.Visibility = _results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = _results.Count == 0 ? "" : _results.Count == 1 ? "1 risultato" : $"{_results.Count} risultati";

        // On an empty query nothing is armed, so Enter cannot trigger "lock PC" by mistake.
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        ResultList.SelectedIndex = hasQuery && _results.Count > 0 ? 0 : -1;
    }

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

        try
        {
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Launcher could not open '{entry.Path}': {ex.Message}");
        }

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
