using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Nebula.Views;

/// <summary>
/// Riduci, ingrandisci/ripristina e chiudi per le finestre Nebula che non usano
/// più la barra del titolo nativa. Il controllo lavora sul primo Window che lo
/// ospita, quindi le singole viste non devono duplicare la stessa logica.
/// </summary>
public partial class CaptionButtons : UserControl
{
    public static readonly DependencyProperty ShowMaximizeProperty = DependencyProperty.Register(
        nameof(ShowMaximize),
        typeof(bool),
        typeof(CaptionButtons),
        new PropertyMetadata(true, OnShowMaximizeChanged));

    private Window? _window;

    public CaptionButtons()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>False per i pannelli a dimensione fissa, che non si ingrandiscono.</summary>
    public bool ShowMaximize
    {
        get => (bool)GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    private static void OnShowMaximizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CaptionButtons)d).UpdateVisuals();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null)
            _window.StateChanged += OnWindowStateChanged;

        UpdateVisuals();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
            _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateVisuals();

    private void UpdateVisuals()
    {
        if (MinButton is null || MaxButton is null || MaxGlyph is null)
            return;

        MaxButton.Visibility = ShowMaximize ? Visibility.Visible : Visibility.Collapsed;

        bool maximized = _window?.WindowState == WindowState.Maximized;
        MaxGlyph.Text = maximized ? "\uE923" : "\uE922";
        MaxButton.ToolTip = maximized ? "Ripristina" : "Ingrandisci";
        AutomationProperties.SetName(MaxButton, maximized ? "Ripristina" : "Ingrandisci");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
            _window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
    {
        if (_window is null || !ShowMaximize)
            return;

        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _window?.Close();
}
