using System.Collections.ObjectModel;
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
    Controls,
    Network
}

public partial class WidgetsWindow : Window
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("it-IT");
    private static readonly string[] Weekdays = { "lun", "mar", "mer", "gio", "ven", "sab", "dom" };

    private readonly DockViewModel _dock;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _networkTimer;
    private readonly QuickControls _controls = new();
    private readonly WirelessPanelService _wireless = new();
    private readonly ObservableCollection<WifiNetworkItem> _wifiNetworks = new();
    private readonly ObservableCollection<BluetoothDeviceItem> _bluetoothDevices = new();
    private bool _networkBusy;
    private bool _suppressWirelessEvents;
    private DateTime _lastWifiScan = DateTime.MinValue;
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

        _networkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _networkTimer.Tick += (_, _) => _ = RefreshWirelessAsync();

        WifiList.ItemsSource = _wifiNetworks;
        BluetoothList.ItemsSource = _bluetoothDevices;

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
        NetworkTab.Visibility = Visibility.Visible;

        if (!widgets && !controls && _panel != WidgetsPanel.Network)
        {
            if (IsVisible)
                Close();
            return;
        }

        if (_panel == WidgetsPanel.Widget && !widgets)
            _panel = controls ? WidgetsPanel.Controls : WidgetsPanel.Network;
        if (_panel == WidgetsPanel.Controls && !controls)
            _panel = widgets ? WidgetsPanel.Widget : WidgetsPanel.Network;

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
        _networkTimer.Stop();
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
        if (e.PropertyName == nameof(AppSettings.MonitorIndex) && IsLoaded)
            Dispatcher.InvokeAsync(PositionPanel, DispatcherPriority.Background);

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
        NetworkPage.Visibility = _panel == WidgetsPanel.Network ? Visibility.Visible : Visibility.Collapsed;

        PanelTitle.Text = _panel switch
        {
            WidgetsPanel.Widget => "Widget",
            WidgetsPanel.Controls => "Controlli rapidi",
            _ => "Wi-Fi e Bluetooth"
        };
        PanelSubtitle.Text = _panel switch
        {
            WidgetsPanel.Widget => "Calendario, sistema e widget",
            WidgetsPanel.Controls => "Volume, media, rete e luminosità",
            _ => "Collega reti e dispositivi in un clic"
        };
        Width = _panel switch
        {
            WidgetsPanel.Controls when _dock.Settings.QuickControlsCompact => 400,
            WidgetsPanel.Network => 470,
            _ => 445
        };

        StyleTab(WidgetTab, _panel == WidgetsPanel.Widget);
        StyleTab(ControlsTab, _panel == WidgetsPanel.Controls);
        StyleTab(NetworkTab, _panel == WidgetsPanel.Network);

        if (_panel == WidgetsPanel.Network)
        {
            _timer.Stop();
            _networkTimer.Start();
            _ = RefreshWirelessAsync(forceScan: true);
        }
        else if (_panel == WidgetsPanel.Controls)
        {
            _timer.Stop();
            _networkTimer.Stop();
            if (CalendarGrid.Children.Count > 0)
                CalendarGrid.Children.Clear();
            RefreshControls();
        }
        else
        {
            _networkTimer.Stop();
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
        // Widgets and quick controls open on the screen the user is working on,
        // like the Start panel.
        Rect work = ScreenGeometry.WorkAreaDip(ScreenGeometry.PanelTarget(
            _dock.Settings.MonitorIndex, _dock.Settings.PanelsFollowDock));
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

    private void OnNetworkTab(object sender, RoutedEventArgs e) => ShowPanel(WidgetsPanel.Network);

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

    private async Task RefreshWirelessAsync(bool forceScan = false)
    {
        if (_networkBusy || _panel != WidgetsPanel.Network)
            return;

        _networkBusy = true;
        WirelessStatus.Text = "Aggiornamento…";
        try
        {
            RadioStateInfo wifi = await _wireless.GetWifiStateAsync();
            SetSwitchSilently(WifiSwitch, wifi.On);
            WifiSwitch.IsEnabled = wifi.Supported;
            WifiStateText.Text = !wifi.Supported
                ? "Nessuna scheda Wi-Fi rilevata."
                : wifi.On ? "Wi-Fi attivo" : "Wi-Fi disattivato";

            if (wifi.On &&
                (forceScan || DateTime.UtcNow - _lastWifiScan > TimeSpan.FromSeconds(15)))
            {
                _lastWifiScan = DateTime.UtcNow;
                _wifiNetworks.Clear();
                IReadOnlyList<WifiNetworkItem> networks = await _wireless.ScanWifiAsync();
                foreach (WifiNetworkItem network in networks)
                    _wifiNetworks.Add(network);
            }
            else if (!wifi.On)
            {
                _wifiNetworks.Clear();
            }

            WifiEmptyText.Text = !wifi.Supported
                ? "Collega una scheda Wi-Fi per vedere le reti."
                : wifi.On
                    ? "Nessuna rete trovata. Premi Aggiorna reti."
                    : "Attiva il Wi-Fi per vedere le reti.";
            WifiEmptyText.Visibility = _wifiNetworks.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            RadioStateInfo bluetooth = await _wireless.GetBluetoothStateAsync();
            SetSwitchSilently(BluetoothSwitch, bluetooth.On);
            BluetoothSwitch.IsEnabled = bluetooth.Supported;
            BluetoothStateText.Text = !bluetooth.Supported
                ? "Nessuna radio Bluetooth rilevata."
                : bluetooth.On ? "Bluetooth attivo" : "Bluetooth disattivato";

            _bluetoothDevices.Clear();
            if (bluetooth.On)
            {
                IReadOnlyList<BluetoothDeviceItem> devices = await _wireless.GetBluetoothDevicesAsync();
                foreach (BluetoothDeviceItem device in devices)
                    _bluetoothDevices.Add(device);
            }

            BluetoothEmptyText.Text = !bluetooth.Supported
                ? "Nessuna radio Bluetooth rilevata."
                : bluetooth.On
                    ? "Nessun dispositivo associato. Usa Gestisci dispositivi per associarne uno."
                    : "Attiva il Bluetooth per vedere i dispositivi.";
            BluetoothEmptyText.Visibility = _bluetoothDevices.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (_wifiNetworks.Count == 0 && !string.IsNullOrWhiteSpace(_wireless.LastMessage))
                WirelessStatus.Text = _wireless.LastMessage;
            else
                WirelessStatus.Text = "";
        }
        catch (Exception ex)
        {
            WirelessStatus.Text = "Aggiornamento non riuscito: " + ex.Message;
            Log.Write("Wireless panel refresh failed: " + ex.Message);
        }
        finally
        {
            _networkBusy = false;
        }
    }

    private void SetSwitchSilently(CheckBox box, bool value)
    {
        _suppressWirelessEvents = true;
        box.IsChecked = value;
        _suppressWirelessEvents = false;
    }

    private async void OnWifiToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressWirelessEvents || _networkBusy)
            return;

        bool on = WifiSwitch.IsChecked == true;
        WifiSwitch.IsEnabled = false;
        await _wireless.SetWifiAsync(on);
        await Task.Delay(1200);
        await RefreshWirelessAsync(forceScan: true);
        WirelessStatus.Text = _wireless.LastMessage;
    }

    private async void OnBluetoothToggle(object sender, RoutedEventArgs e)
    {
        if (_suppressWirelessEvents || _networkBusy)
            return;

        bool on = BluetoothSwitch.IsChecked == true;
        BluetoothSwitch.IsEnabled = false;
        await _wireless.SetBluetoothAsync(on);
        await Task.Delay(900);
        await RefreshWirelessAsync();
        WirelessStatus.Text = _wireless.LastMessage;
    }

    private async void OnRefreshWireless(object sender, RoutedEventArgs e)
    {
        await RefreshWirelessAsync(forceScan: true);
    }

    private async void OnWifiNetworkClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: WifiNetworkItem network })
            return;

        string message = await _wireless.ConnectWifiAsync(network);
        WirelessStatus.Text = message;
        await Task.Delay(1800);
        await RefreshWirelessAsync(forceScan: true);
        WirelessStatus.Text = message;
    }

    private void OnBluetoothDeviceClick(object sender, RoutedEventArgs e)
    {
        WirelessPanelService.OpenBluetoothSettings();
        WirelessStatus.Text = "Gestisci il dispositivo dalle impostazioni Bluetooth di Windows.";
    }

    private void OnOpenWifiSettings(object sender, RoutedEventArgs e)
    {
        WirelessPanelService.OpenWifiSettings();
        WirelessStatus.Text = "Impostazioni Wi-Fi di Windows aperte.";
    }

    private void OnOpenBluetoothSettings(object sender, RoutedEventArgs e)
    {
        WirelessPanelService.OpenBluetoothSettings();
        WirelessStatus.Text = "Impostazioni Bluetooth di Windows aperte.";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
