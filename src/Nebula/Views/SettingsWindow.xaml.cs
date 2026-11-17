using System.Diagnostics;
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
        ["style"] = ("Stile", "Scegli un'identità visiva, poi rifinisci vetro, forma e accento."),
        ["dock"] = ("Dock", "Posizione, auto-hide, integrazione con Windows e app fissate."),
        ["icons"] = ("Icone di sistema", "Applica uno stile a collegamenti, tipi file e cartelle del profilo."),
        ["tabs"] = ("Schede", "Raggruppa finestre, scegli lo stile e ripristina tutto in un clic."),
        ["widgets"] = ("Widget", "Orologio, data, batteria e monitor di sistema dentro la barra."),
        ["search"] = ("Ricerca", "App installate, finestre aperte e azioni, con apertura da tastiera."),
        ["controls"] = ("Controlli rapidi", "Volume, media, rete e luminosità in un solo pannello."),
        ["motion"] = ("Animazioni", "Movimento, reattività e prestazioni della dock."),
        ["behavior"] = ("Comportamento", "Avvio, scorciatoie e dati locali."),
        ["info"] = ("Info", "Nebula Taskbar Studio, comandi rapidi e ripristino.")
    };

    private readonly SettingsViewModel _vm;
    private readonly SettingsStore _store;

    public SettingsWindow(SettingsViewModel viewModel, SettingsStore store)
    {
        _vm = viewModel;
        _store = store;

        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NavStyle.IsChecked = true;
        ShowPage("style");

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

        foreach (UIElement page in new UIElement[] { PageStyle, PageDock, PageSystemIcons, PageTabs, PageWidget, PageSearch, PageControls, PageAnimations, PageBehavior, PageInfo })
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
