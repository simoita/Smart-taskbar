using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nebula.Models;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;

namespace Nebula.Views;

public enum WidgetsPanel
{
    Widget,
    Controls
}

public partial class WidgetsWindow : Window
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly string[] Weekdays = { "lun", "mar", "mer", "gio", "ven", "sab", "dom" };

    private readonly DockViewModel _dock;
    private readonly DispatcherTimer _timer;
    private readonly QuickControls _controls = new();
    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private WidgetsPanel _panel = WidgetsPanel.Widget;
    private bool _suppressControlEvents;
    private readonly Dictionary<string, TextBlock> _widgetValues = new(StringComparer.OrdinalIgnoreCase);

    public WidgetsWindow(DockViewModel dock)
    {
        _dock = dock;
        InitializeComponent();
        DataContext = dock;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (_panel != WidgetsPanel.Widget)
                return;

            CpuSpark.Values = dock.Monitor.CpuHistory;
            UpdateWidgetStripValues();
        };
        _timer.Start();

        _dock.Settings.PropertyChanged += OnSettingsChanged;
        _dock.Theme.Changed += OnThemeChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public WidgetsPanel CurrentPanel => _panel;

    /// <summary>Switches the shared panel without creating another WPF surface.</summary>
    public void ShowPanel(WidgetsPanel panel)
    {
        if (panel == WidgetsPanel.Widget && !_dock.Settings.ShowWidgets)
            return;
        if (panel == WidgetsPanel.Controls && !_dock.Settings.ShowQuickControls)
            return;

        _panel = panel;
        ApplyPanel();

        if (IsLoaded)
            AnimateIn();
    }

    /// <summary>Keeps the shared panel in sync with the Studio switches.</summary>
    public void ApplyAvailability()
    {
        bool widgets = _dock.Settings.ShowWidgets;
        bool controls = _dock.Settings.ShowQuickControls;

        WidgetTab.Visibility = widgets ? Visibility.Visible : Visibility.Collapsed;
        ControlsTab.Visibility = controls ? Visibility.Visible : Visibility.Collapsed;

        if (!widgets && !controls)
        {
            if (IsVisible)
                Close();
            return;
        }

        if (_panel == WidgetsPanel.Widget && !widgets)
            _panel = WidgetsPanel.Controls;
        if (_panel == WidgetsPanel.Controls && !controls)
            _panel = WidgetsPanel.Widget;

        ApplyPanel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CpuSpark.Values = _dock.Monitor.CpuHistory;
        CpuSpark.Stroke = (Brush)FindResource("C.Accent");
        RefreshWidgetStrip();

        _dock.Monitor.Update();
        ApplyAvailability();
        RefreshControls();
        Dispatcher.InvokeAsync(PositionPanel, DispatcherPriority.Loaded);
        AnimateIn();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        StopMotion();
        _dock.Settings.PropertyChanged -= OnSettingsChanged;
        _dock.Theme.Changed -= OnThemeChanged;
        CalendarGrid.Children.Clear();
        WidgetStrip.Children.Clear();
        _widgetValues.Clear();
        _controls.Dispose();
        DataContext = null;
        Content = null;
    }

    private void OnThemeChanged()
    {
        if (!IsLoaded)
            return;

        Dispatcher.InvokeAsync(() =>
        {
            CpuSpark.Stroke = (Brush)FindResource("C.Accent");
            NetworkDot.Fill = (Brush)FindResource(_controls.NetworkAvailable ? "C.Success" : "C.TextFaint");
            StyleTab(WidgetTab, _panel == WidgetsPanel.Widget);
            StyleTab(ControlsTab, _panel == WidgetsPanel.Controls);

            if (_panel == WidgetsPanel.Widget)
                BuildCalendar();
        }, DispatcherPriority.Background);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.PanelWidgets))
        {
            RefreshWidgetStrip();
            Dispatcher.InvokeAsync(PositionPanel, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(AppSettings.ShowWidgets)
            or nameof(AppSettings.ShowQuickControls)
            or nameof(AppSettings.QuickControlsCompact))
        {
            ApplyAvailability();
            Dispatcher.InvokeAsync(PositionPanel, DispatcherPriority.Background);
        }

        if (e.PropertyName is nameof(AppSettings.AnimationsEnabled)
            or nameof(AppSettings.AnimationSpeed))
        {
            if (_dock.Settings.AnimationsEnabled && IsLoaded)
                AnimateIn();
            else
                StopMotion();
        }
    }

    private void ApplyPanel()
    {
        if (WidgetPage is null || ControlsPage is null)
            return;

        WidgetPage.Visibility = _panel == WidgetsPanel.Widget ? Visibility.Visible : Visibility.Collapsed;
        ControlsPage.Visibility = _panel == WidgetsPanel.Controls ? Visibility.Visible : Visibility.Collapsed;
        PanelTitle.Text = _panel == WidgetsPanel.Widget ? "Widget" : "Controlli rapidi";
        Width = _panel == WidgetsPanel.Controls && _dock.Settings.QuickControlsCompact ? 370 : 410;

        StyleTab(WidgetTab, _panel == WidgetsPanel.Widget);
        StyleTab(ControlsTab, _panel == WidgetsPanel.Controls);

        if (_panel == WidgetsPanel.Controls)
        {
            _timer.Stop();
            if (CalendarGrid.Children.Count > 0)
                CalendarGrid.Children.Clear();
            RefreshControls();
        }
        else
        {
            RefreshWidgetStrip();
            if (CalendarGrid.Children.Count == 0)
                BuildCalendar();
            _timer.Start();
        }

        if (IsLoaded)
            Dispatcher.InvokeAsync(PositionPanel, DispatcherPriority.Background);
    }

    private void AnimateIn()
    {
        StopMotion();

        if (!_dock.Settings.AnimationsEnabled)
            return;

        double seconds = 0.2 / Math.Clamp(_dock.Settings.AnimationSpeed, 0.5, 2);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        PanelShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromSeconds(seconds + 0.06))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void StopMotion()
    {
        Card.BeginAnimation(OpacityProperty, null);
        PanelShift.BeginAnimation(TranslateTransform.YProperty, null);
        Card.Opacity = 1;
        PanelShift.Y = 0;
    }

    private void StyleTab(Button button, bool selected)
    {
        button.Background = selected
            ? (Brush)FindResource("C.Hover")
            : Brushes.Transparent;
        button.Foreground = selected
            ? (Brush)FindResource("C.Text")
            : (Brush)FindResource("C.TextMuted");
    }

    private void PositionPanel()
    {
        Rect work = ScreenGeometry.WorkAreaDip(ScreenGeometry.Target(_dock.Settings.MonitorIndex));
        MaxWidth = Math.Max(320, work.Width - 24);
        MaxHeight = Math.Max(360, work.Height - 40);
        Width = Math.Min(Width, MaxWidth);
        UpdateLayout();
        Left = Math.Max(work.Left + 12, work.Right - ActualWidth - 34);
        Top = Math.Max(work.Top + 24, work.Bottom - ActualHeight - 92);
    }

    private void RefreshControls()
    {
        _controls.Refresh();
        _suppressControlEvents = true;

        VolumeSlider.IsEnabled = _controls.VolumeSupported;
        VolumeSlider.Value = _controls.VolumeSupported ? _controls.VolumePercent : 0;
        VolumeValue.Text = _controls.VolumeSupported ? $"{_controls.VolumePercent}%" : "—";
        VolumeHint.Text = _controls.VolumeSupported
            ? "Audio di Windows"
            : "Tasti volume di sistema";
        MuteButton.Content = _controls.Muted ? "Riattiva" : "Muto";

        BrightnessSlider.IsEnabled = _controls.BrightnessSupported;
        BrightnessSlider.Value = _controls.BrightnessSupported ? _controls.BrightnessPercent : 0;
        BrightnessValue.Text = _controls.BrightnessSupported ? $"{_controls.BrightnessPercent}%" : "—";
        BrightnessHint.Text = _controls.BrightnessSupported
            ? "Controllo del monitor primario."
            : "Non controllabile su questo monitor: Windows espone la luminosità solo per alcuni display integrati.";

        NetworkText.Text = _controls.NetworkText;
        NetworkDot.Fill = (Brush)FindResource(_controls.NetworkAvailable ? "C.Success" : "C.TextFaint");
        MediaHint.Text = "Tasti multimediali di sistema";
        ControlStatus.Text = "";

        _suppressControlEvents = false;
    }

    private void OnWidgetTab(object sender, RoutedEventArgs e) => ShowPanel(WidgetsPanel.Widget);

    private void OnControlsTab(object sender, RoutedEventArgs e) => ShowPanel(WidgetsPanel.Controls);

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressControlEvents || !IsLoaded)
            return;

        if (!_controls.SetVolume((int)Math.Round(e.NewValue), out string? error))
        {
            ControlStatus.Text = error ?? "Volume non disponibile.";
            return;
        }

        VolumeValue.Text = $"{_controls.VolumePercent}%";
        VolumeHint.Text = "Audio di Windows";
        ControlStatus.Text = "";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (!_controls.ToggleMute(out bool muted, out string? error))
        {
            ControlStatus.Text = error ?? "Mute non disponibile.";
            return;
        }

        MuteButton.Content = muted ? "Riattiva" : "Muto";
        ControlStatus.Text = muted ? "Audio disattivato." : "Audio riattivato.";
    }

    private void OnPreviousMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.Previous);

    private void OnPlayPauseMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.PlayPause);

    private void OnNextMedia(object sender, RoutedEventArgs e) => SendMedia(MediaCommand.Next);

    private void SendMedia(MediaCommand command)
    {
        if (!_controls.SendMedia(command))
        {
            ControlStatus.Text = "Il comando multimediale non è stato accettato da Windows.";
            return;
        }

        MediaHint.Text = command switch
        {
            MediaCommand.Previous => "Precedente inviato",
            MediaCommand.Next => "Successiva inviata",
            _ => "Play/Pausa inviato"
        };
        ControlStatus.Text = "";
    }

    private void OnOpenNetwork(object sender, RoutedEventArgs e)
    {
        if (!_controls.OpenNetworkSettings(out string? error))
        {
            ControlStatus.Text = error ?? "Impossibile aprire le impostazioni di rete.";
            return;
        }

        ControlStatus.Text = "Impostazioni di rete aperte.";
    }

    private void OnBrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressControlEvents || !IsLoaded)
            return;

        if (!_controls.SetBrightness((int)Math.Round(e.NewValue), out string? error))
        {
            BrightnessHint.Text = error ?? "Luminosità non disponibile.";
            BrightnessValue.Text = "—";
            ControlStatus.Text = "Luminosità non controllabile su questo monitor.";
            return;
        }

        BrightnessValue.Text = $"{_controls.BrightnessPercent}%";
        BrightnessHint.Text = "Controllo del monitor primario.";
        ControlStatus.Text = "";
    }

    private void BuildCalendar()
    {
        CalendarGrid.Children.Clear();
        MonthLabel.Text = char.ToUpper(_month.ToString("MMMM yyyy", Culture)[0], Culture) +
                          _month.ToString("MMMM yyyy", Culture)[1..];

        foreach (string day in Weekdays)
        {
            CalendarGrid.Children.Add(new TextBlock
            {
                Text = day,
                FontSize = 9.5,
                Foreground = (Brush)FindResource("C.TextFaint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 6)
            });
        }

        int offset = ((int)_month.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++)
            CalendarGrid.Children.Add(new Border { Height = 30 });

        int days = DateTime.DaysInMonth(_month.Year, _month.Month);
        CornerRadius cellRadius = TryFindResource("S.ControlRadius") is CornerRadius radius
            ? radius
            : new CornerRadius(8);
        for (int day = 1; day <= days; day++)
        {
            bool today = _month.Year == DateTime.Today.Year &&
                         _month.Month == DateTime.Today.Month &&
                         day == DateTime.Today.Day;

            var label = new TextBlock
            {
                Text = day.ToString(Culture),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = today
                    ? (Brush)FindResource("C.OnAccent")
                    : (Brush)FindResource("C.Text")
            };

            var cell = new Border
            {
                Height = 30,
                Margin = new Thickness(1),
                CornerRadius = cellRadius,
                Background = today ? (Brush)FindResource("C.Accent") : Brushes.Transparent,
                Child = label
            };

            CalendarGrid.Children.Add(cell);
        }
    }

    private void RefreshWidgetStrip()
    {
        if (WidgetStrip is null)
            return;

        WidgetStrip.Children.Clear();
        _widgetValues.Clear();

        var selected = new HashSet<string>(
            _dock.Settings.PanelWidgets ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (WidgetDefinition definition in WidgetCatalog.All)
        {
            if (!selected.Contains(definition.Id))
                continue;

            var tile = new Border
            {
                Width = 178,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = definition.Description
            };
            tile.SetResourceReference(Border.BackgroundProperty, "S.InnerBg");
            tile.SetResourceReference(Border.BorderBrushProperty, "S.InnerBorder");
            tile.SetResourceReference(Border.BorderThicknessProperty, "S.BorderThickness");
            tile.SetResourceReference(Border.CornerRadiusProperty, "S.ControlRadius");

            var content = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var glyph = new TextBlock
            {
                Text = definition.Glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 12,
                Margin = new Thickness(0, 0, 7, 0)
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "C.Accent");
            header.Children.Add(glyph);

            var title = new TextBlock
            {
                Text = definition.Name,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "C.Text");
            Grid.SetColumn(title, 1);
            header.Children.Add(title);
            content.Children.Add(header);

            var value = new TextBlock
            {
                FontSize = 11.5,
                Margin = new Thickness(0, 5, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "C.TextMuted");
            content.Children.Add(value);

            tile.Child = content;
            tile.MouseLeftButtonUp += (_, _) => OnWidgetTileClick(definition.Id);
            WidgetStrip.Children.Add(tile);
            _widgetValues[definition.Id] = value;
        }

        WidgetStrip.Visibility = _widgetValues.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateWidgetStripValues();
    }

    private void UpdateWidgetStripValues()
    {
        if (_widgetValues.Count == 0)
            return;

        foreach ((string id, TextBlock value) in _widgetValues)
        {
            value.Text = id switch
            {
                "clock" => $"{_dock.TimeText}  ·  {_dock.DateText}",
                "date" => _dock.DateText,
                "calendar" => FirstUpper(DateTime.Today.ToString("dddd d MMMM", Culture)),
                "stats" => $"{_dock.Monitor.CpuText} CPU  ·  {_dock.Monitor.RamText} RAM",
                "battery" => _dock.Monitor.BatteryText,
                "network" => _controls.NetworkText,
                "uptime" => _dock.Monitor.Uptime,
                "disk" => _dock.Monitor.DiskText,
                "media" => "Play / Pausa",
                "search" => "Apri ricerca",
                "controls" => "Apri controlli rapidi",
                _ => ""
            };
        }
    }

    private void OnWidgetTileClick(string id)
    {
        switch (id)
        {
            case "search":
                App.Instance?.ShowLauncher();
                break;
            case "controls":
                App.Instance?.ShowControls();
                break;
            case "media":
                _controls.SendMedia(MediaCommand.PlayPause);
                break;
        }
    }

    private static string FirstUpper(string value) =>
        value.Length > 1 ? char.ToUpper(value[0], Culture) + value[1..] : value;

    private void OnPreviousMonth(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(-1);
        BuildCalendar();
    }

    private void OnNextMonth(object sender, RoutedEventArgs e)
    {
        _month = _month.AddMonths(1);
        BuildCalendar();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
