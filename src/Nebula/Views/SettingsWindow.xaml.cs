using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Nebula.Services;
using Nebula.Utilities;
using Nebula.ViewModels;

namespace Nebula.Views;

public partial class SettingsWindow : Window
{
    private static readonly Dictionary<string, (string Title, string Subtitle)> PageMeta = new()
    {
        ["simple"] = ("Semplice", "Tre preset per dimensioni e movimento; i pixel esatti solo se ti servono."),
        ["style"] = ("Stile", "Scegli un'identità visiva, poi rifinisci vetro, forma e accento."),
        ["dock"] = ("Dock", "Posizione, auto-hide, integrazione con Windows e app fissate."),
        ["widgets"] = ("Widget", "Orologio, data, batteria e monitor di sistema dentro la barra."),
        ["search"] = ("Ricerca", "App, file, impostazioni, finestre e azioni, con apertura da tastiera."),
        ["controls"] = ("Controlli rapidi", "Volume, media, rete e luminosità in un solo pannello."),
        ["tabs"] = ("Schede", "Raggruppa finestre, scegli lo stile e ripristina tutto in un clic."),
        ["icons"] = ("Icone di sistema", "Applica uno stile a collegamenti, tipi file e cartelle del profilo."),
        ["motion"] = ("Animazioni", "Movimento, reattività e prestazioni della dock."),
        ["behavior"] = ("Comportamento", "Avvio, scorciatoie e dati locali."),
        ["info"] = ("Info", "Nebula Taskbar Studio, comandi rapidi e ripristino.")
    };

    private const string TaskbarWidgetDragFormat = "Nebula.TaskbarWidgetRow";

    private readonly SettingsViewModel _vm;
    private readonly SettingsStore _store;
    private bool _syncingPresets;
    private Point _widgetDragStart;
    private TaskbarWidgetOptionViewModel? _widgetDragSource;
    private bool _widgetDragging;

    public SettingsWindow(SettingsViewModel viewModel, SettingsStore store)
    {
        _vm = viewModel;
        _store = store;

        InitializeComponent();
        TitleBarTheme.Apply(this);
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToScreen();
        SyncSimplePresets();
        NavSimple.IsChecked = true;
        ShowPage("simple");
        NormalizeSettingRows();

        if (_vm.Settings.AnimationsEnabled)
        {
            Chrome.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            WindowScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            WindowScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(260))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            Chrome.Opacity = 1;
        }
    }

    /// <summary>Clamps the Studio to the current work area instead of overflowing small screens.</summary>
    private void FitToScreen()
    {
        Rect work = ScreenGeometry.BoundsDip(ScreenGeometry.PanelTarget(
            _vm.Settings.MonitorIndex, _vm.Settings.PanelsFollowDock));
        Width = Math.Min(1160, Math.Max(700, work.Width - 32));
        Height = Math.Min(780, Math.Max(320, work.Height - 32));
        Left = work.Left + Math.Max(0, (work.Width - Width) / 2);
        Top = work.Top + Math.Max(0, (work.Height - Height) / 2);
    }

    private void OnRefreshScreens(object sender, RoutedEventArgs e) => _vm.RefreshScreens();

    /// <summary>
    /// Older rows put the label and its switch in the same grid cell. On narrow
    /// windows the two overlapped; give every simple row a real text/control
    /// column pair without rewriting dozens of markup blocks.
    /// </summary>
    private void NormalizeSettingRows()
    {
        if (TryFindResource("Row") is not Style rowStyle)
            return;

        foreach (Border border in FindVisualChildren<Border>(Chrome))
        {
            if (!ReferenceEquals(border.Style, rowStyle) ||
                border.Child is not Grid grid ||
                grid.ColumnDefinitions.Count > 0 ||
                grid.Children.Count != 2 ||
                grid.Children[0] is not FrameworkElement label ||
                grid.Children[1] is not FrameworkElement control)
                continue;

            if (label is not (StackPanel or TextBlock) ||
                control is not (CheckBox or ComboBox or Button or TextBox or Slider))
                continue;

            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 0);
            Grid.SetColumn(control, 1);
            label.HorizontalAlignment = HorizontalAlignment.Left;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(18, 0, 0, 0);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        if (sender is RadioButton { Uid: string key })
            ShowPage(key);
    }

    private void ShowPage(string key)
    {
        UIElement? target = key switch
        {
            "simple" => PageSimple,
            "style" => PageStyle,
            "dock" => PageDock,
            "icons" => PageSystemIcons,
            "tabs" => PageTabs,
            "widgets" => PageWidget,
            "search" => PageSearch,
            "controls" => PageControls,
            "motion" => PageAnimations,
            "behavior" => PageBehavior,
            "info" => PageInfo,
            _ => null
        };

        if (target is null)
            return;

        foreach (UIElement page in new UIElement[] { PageSimple, PageStyle, PageDock, PageSystemIcons, PageTabs, PageWidget, PageSearch, PageControls, PageAnimations, PageBehavior, PageInfo })
            page.Visibility = Visibility.Collapsed;

        target.Visibility = Visibility.Visible;

        if (_vm.Settings.AnimationsEnabled)
        {
            target.Opacity = 0;
            var slide = new TranslateTransform(0, 10);
            target.RenderTransform = slide;

            target.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }
        else
        {
            target.Opacity = 1;
            target.RenderTransform = Transform.Identity;
        }

        if (PageMeta.TryGetValue(key, out (string Title, string Subtitle) meta))
        {
            PageTitle.Text = meta.Title;
            PageSubtitle.Text = meta.Subtitle;
        }

        ContentScroll.ScrollToTop();
        Dispatcher.InvokeAsync(NormalizeSettingRows, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void SyncSimplePresets()
    {
        if (SizeCompact is null)
            return;

        _syncingPresets = true;
        try
        {
            var settings = _vm.Settings;
            SizeCompact.IsChecked = MatchesSize(54, 24, 16, 0, 0);
            SizeBalanced.IsChecked = MatchesSize(62, 30, 22, 2, 0);
            SizeLarge.IsChecked = MatchesSize(74, 36, 28, 4, 0);

            MotionCalm.IsChecked = settings.AnimationsEnabled && settings.AnimationSpeed >= 1.4 && !settings.LaunchPulse;
            MotionBalanced.IsChecked = settings.AnimationsEnabled && settings.AnimationSpeed > 0.85 && settings.AnimationSpeed < 1.4;
            MotionExpressive.IsChecked = settings.AnimationsEnabled && settings.AnimationSpeed <= 0.85;

            PreciseDockHeight.Text = settings.DockHeight.ToString("0.##", CultureInfo.CurrentCulture);
            PreciseIconSize.Text = settings.IconSize.ToString("0.##", CultureInfo.CurrentCulture);
            PreciseCornerRadius.Text = settings.CornerRadius.ToString("0.##", CultureInfo.CurrentCulture);
            PreciseSpacing.Text = settings.ItemSpacing.ToString("0.##", CultureInfo.CurrentCulture);
            PreciseEdgeMargin.Text = settings.EdgeMargin.ToString("0.##", CultureInfo.CurrentCulture);
        }
        finally
        {
            _syncingPresets = false;
        }
    }

    private bool MatchesSize(double height, double icons, double radius, double spacing, double margin)
    {
        var settings = _vm.Settings;
        return Math.Abs(settings.DockHeight - height) < 0.6 &&
               Math.Abs(settings.IconSize - icons) < 0.6 &&
               Math.Abs(settings.CornerRadius - radius) < 0.6 &&
               Math.Abs(settings.ItemSpacing - spacing) < 0.6 &&
               Math.Abs(settings.EdgeMargin - margin) < 0.6;
    }

    private void OnSizePresetChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingPresets || !IsLoaded || sender is not RadioButton { Tag: string preset })
            return;

        var settings = _vm.Settings;
        switch (preset)
        {
            case "compact":
                settings.DockHeight = 54;
                settings.IconSize = 24;
                settings.CornerRadius = 16;
                settings.ItemSpacing = 0;
                settings.EdgeMargin = 0;
                break;
            case "large":
                settings.DockHeight = 74;
                settings.IconSize = 36;
                settings.CornerRadius = 28;
                settings.ItemSpacing = 4;
                settings.EdgeMargin = 0;
                break;
            default:
                settings.DockHeight = 62;
                settings.IconSize = 30;
                settings.CornerRadius = 22;
                settings.ItemSpacing = 2;
                settings.EdgeMargin = 0;
                break;
        }

        _store.MarkDirty();
        SyncSimplePresets();
    }

    private void OnMotionPresetChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingPresets || !IsLoaded || sender is not RadioButton { Tag: string preset })
            return;

        var settings = _vm.Settings;
        settings.AnimationsEnabled = true;
        switch (preset)
        {
            case "calm":
                settings.AnimationSpeed = 1.65;
                settings.HoverMagnify = false;
                settings.LaunchPulse = false;
                break;
            case "expressive":
                settings.AnimationSpeed = 0.7;
                settings.HoverMagnify = true;
                settings.LaunchPulse = true;
                break;
            default:
                settings.AnimationSpeed = 1.0;
                settings.HoverMagnify = true;
                settings.LaunchPulse = false;
                break;
        }

        _store.MarkDirty();
        SyncSimplePresets();
    }

    private void OnTogglePreciseSizes(object sender, RoutedEventArgs e)
    {
        bool show = PreciseSizesPanel.Visibility != Visibility.Visible;
        PreciseSizesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            SyncSimplePresets();
    }

    private void OnApplyPreciseSizes(object sender, RoutedEventArgs e)
    {
        var settings = _vm.Settings;
        settings.DockHeight = ClampValue(PreciseDockHeight.Text, 44, 92, settings.DockHeight);
        settings.IconSize = ClampValue(PreciseIconSize.Text, 20, 36, settings.IconSize);
        settings.CornerRadius = ClampValue(PreciseCornerRadius.Text, 4, 36, settings.CornerRadius);
        settings.ItemSpacing = ClampValue(PreciseSpacing.Text, -4, 12, settings.ItemSpacing);
        settings.EdgeMargin = ClampValue(PreciseEdgeMargin.Text, 0, 48, settings.EdgeMargin);

        _store.MarkDirty();
        SyncSimplePresets();
    }

    private static double ClampValue(string text, double min, double max, double fallback)
    {
        bool parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) ||
                      double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        return parsed ? Math.Round(Math.Clamp(value, min, max), 1) : fallback;
    }

    private void OnSearchWidthPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string preset })
            return;

        _vm.Settings.SearchCardWidth = preset switch
        {
            "narrow" => 620,
            "wide" => 840,
            _ => 720
        };
        _store.MarkDirty();
    }

    private void OnStyleCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: StyleCardViewModel card })
        {
            _vm.Settings.ThemeId = card.Preset.Id;
            _vm.SyncSelection();
            _store.MarkDirty();
        }
    }

    private void OnAccentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AccentSwatchViewModel swatch })
        {
            _vm.SetAccent(swatch.Hex);
            _store.MarkDirty();
        }
    }

    private void OnApplyAccent(object sender, RoutedEventArgs e)
    {
        _vm.ApplyAccentInput();
        _store.MarkDirty();
    }

    private void OnSurfaceStyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string style })
        {
            _vm.Settings.SurfaceStyle = style;
            _store.MarkDirty();
        }
    }

    private void OnAccentKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _vm.ApplyAccentInput();
        _store.MarkDirty();
        e.Handled = true;
    }

    private void OnAddPin(object sender, RoutedEventArgs e)
    {
        App.Instance?.ShowLauncherForPick(path =>
        {
            _vm.AddPin(path);
            _store.MarkDirty();
        });
    }

    private void OnRemovePinClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PinnedAppViewModel pin })
        {
            _vm.RemovePin(pin);
            _store.MarkDirty();
        }
    }

    private void OnResetPins(object sender, RoutedEventArgs e)
    {
        _vm.Settings.PinnedPaths.Clear();
        _vm.Settings.PinnedPaths.AddRange(PinSeeder.DiscoverDefaults());
        _vm.RebuildPins();
        _vm.Preview.RefreshItems();
        _store.MarkDirty();
    }

    private void OnAddTaskbarWidget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskbarWidgetOptionViewModel option })
            return;

        _vm.AddTaskbarWidget(option);
        _store.MarkDirty();
    }

    private void OnAddAllTaskbarWidgets(object sender, RoutedEventArgs e)
    {
        _vm.AddAllTaskbarWidgets();
    }

    private void OnToggleTaskbarWidgetMode(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TaskbarWidgetOptionViewModel option })
            _vm.ToggleTaskbarWidgetMode(option);
    }

    private void OnRemoveTaskbarWidget(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskbarWidgetOptionViewModel option })
            return;

        _vm.RemoveTaskbarWidget(option);
        _store.MarkDirty();
    }

    private void OnMoveTaskbarWidgetUp(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskbarWidgetOptionViewModel option })
            return;

        _vm.MoveTaskbarWidget(option, -1);
        _store.MarkDirty();
    }

    private void OnMoveTaskbarWidgetDown(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: TaskbarWidgetOptionViewModel option })
            return;

        _vm.MoveTaskbarWidget(option, 1);
        _store.MarkDirty();
    }

    /// <summary>
    /// Reorders the bar by dragging a row of the Studio list: the row under the
    /// pointer receives the dragged widget just before itself.
    /// </summary>
    private void OnTaskbarWidgetRowDragStart(object sender, MouseButtonEventArgs e)
    {
        _widgetDragSource = null;
        if (e.ClickCount != 1 ||
            sender is not FrameworkElement { DataContext: TaskbarWidgetOptionViewModel option } element)
            return;

        // The buttons inside the row keep their normal click.
        if (FindAncestorButton(e.OriginalSource as DependencyObject) is not null)
            return;

        _widgetDragStart = e.GetPosition(element);
        _widgetDragSource = option;
    }

    private void OnTaskbarWidgetRowDragMove(object sender, MouseEventArgs e)
    {
        if (_widgetDragging || _widgetDragSource is null ||
            e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement element)
            return;

        Point current = e.GetPosition(element);
        if (Math.Abs(current.X - _widgetDragStart.X) < 6 &&
            Math.Abs(current.Y - _widgetDragStart.Y) < 6)
            return;

        string id = _widgetDragSource.Id;
        _widgetDragSource = null;
        _widgetDragging = true;

        try
        {
            DragDrop.DoDragDrop(element, new DataObject(TaskbarWidgetDragFormat, id),
                DragDropEffects.Move);
        }
        finally
        {
            _widgetDragging = false;
        }
    }

    private void OnTaskbarWidgetRowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(TaskbarWidgetDragFormat))
            return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnTaskbarWidgetRowDragLeave(object sender, DragEventArgs e)
    {
        // The row itself has no separate hover chrome: nothing to reset.
    }

    private void OnTaskbarWidgetRowDrop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TaskbarWidgetOptionViewModel target } ||
            !e.Data.GetDataPresent(TaskbarWidgetDragFormat) ||
            e.Data.GetData(TaskbarWidgetDragFormat) is not string fromId)
            return;

        e.Handled = true;
        _vm.ReorderTaskbarWidget(fromId, target.Id);
        _store.MarkDirty();
    }

    private static Button? FindAncestorButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is Button button)
                return button;

            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }

        return null;
    }

    private void OnOpenWidgets(object sender, RoutedEventArgs e) => App.Instance?.ShowWidgets();

    private void OnOpenSearch(object sender, RoutedEventArgs e) => App.Instance?.ShowLauncher();

    private void OnOpenControls(object sender, RoutedEventArgs e) => App.Instance?.ShowControls();

    private void OnPickDockIcon(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string appPath })
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Scegli l'icona dell'app",
            Filter = "Icone e immagini|*.ico;*.png;*.jpg;*.jpeg;*.exe|Icone|*.ico|Eseguibili|*.exe|Tutti i file|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _vm.Settings.CustomIcons[appPath] = dialog.FileName;
        App.Instance?.Dock.RefreshIconOverrides();
        _store.MarkDirty();
        _vm.PinStatus = "Icona aggiornata: " + Path.GetFileName(appPath);
    }

    private void OnPickShortcutIcon(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string shortcutPath })
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Scegli l'icona del collegamento",
            Filter = "Icone di Windows|*.ico|Eseguibili|*.exe|Tutti i file|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        if (!SetShortcutIcon(shortcutPath, dialog.FileName, out string? error))
        {
            _vm.PinStatus = error ?? "Impossibile modificare il collegamento.";
            return;
        }

        _vm.PinStatus = "Icona del collegamento aggiornata: " + Path.GetFileNameWithoutExtension(shortcutPath);
    }

    private static bool SetShortcutIcon(string shortcutPath, string iconPath, out string? error)
    {
        error = null;
        object? shell = null;
        object? shortcut = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                error = "COM di Windows non disponibile.";
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                new object[] { shortcutPath });
            if (shortcut is null)
            {
                error = "Collegamento non apribile.";
                return false;
            }

            shortcut.GetType().InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut,
                new object[] { iconPath + ",0" });
            shortcut.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            return true;
        }
        catch (Exception ex)
        {
            error = "Icona non applicata: " + ex.Message;
            return false;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
                Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.FinalReleaseComObject(shell);
        }
    }

    private void OnCreateTabGroup(object sender, RoutedEventArgs e)
    {
        App.Instance?.ShowGroupPicker();
    }

    private void OnCloseTabGroups(object sender, RoutedEventArgs e)
    {
        App.Instance?.CloseAllTabGroups();
    }

    private void OnRestoreTaskbar(object sender, RoutedEventArgs e)
    {
        App.Instance?.RestoreWindowsTaskbar();
        _store.MarkDirty();
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_vm.SettingsPath);
            if (directory is not null)
                Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("Could not open settings folder: " + ex.Message);
        }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        MessageBoxResult answer = MessageBox.Show(
            "Ripristinare tutte le impostazioni di Nebula?\n\nStile, dock, widget e app fissate torneranno ai valori iniziali.",
            "Ripristina Nebula", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        _store.ResetAll();
        App.Instance?.ApplyAll();
        _vm.RebuildPins();
        _vm.SyncSelection();
        _vm.AccentInput = _vm.Settings.AccentHex;
        _vm.PinStatus = "Impostazioni ripristinate.";
    }

    private void OnExitApp(object sender, RoutedEventArgs e) => App.Instance?.ExitApp();

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        await _vm.CheckUpdatesAsync();
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        await _vm.InstallUpdateAsync();
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove throws when the button was released early.
        }
    }

    private void OnChromeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Chrome.ActualWidth <= 0 || Chrome.ActualHeight <= 0)
            return;

        Chrome.Clip = new RectangleGeometry(
            new Rect(0, 0, Chrome.ActualWidth, Chrome.ActualHeight), 20, 20);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
