using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfImage = System.Windows.Controls.Image;
using Microsoft.Win32;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;
using Nebula.ViewModels;

namespace Nebula.Views;

public sealed class IconStyleChoiceViewModel : ObservableObject
{
    private ImageSource? _preview;

    public IconStyleChoiceViewModel(string id, string name, string subtitle)
    {
        Id = id;
        Name = name;
        Subtitle = subtitle;
    }

    public string Id { get; }
    public string Name { get; }
    public string Subtitle { get; }

    public ImageSource? Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value);
    }
}

public partial class IconPickerWindow : Window
{
    private static readonly (string Id, string Name, string Subtitle)[] StyleDefs =
    {
        ("native", "Originale", "Nessuna modifica"),
        ("glass", "Vetro", "Scuro e luminoso"),
        ("filled", "Pieno", "Accento deciso"),
        ("tile", "Riquadro", "Compatta"),
        ("circle", "Cerchio", "Piastra tonda"),
        ("duotone", "Duotono", "Due toni"),
        ("mono", "Mono", "Grigio"),
        ("outline", "Contorno", "Sagoma"),
        ("neon", "Neon", "Glow")
    };

    private readonly SystemIconTarget _target;
    private readonly SystemIconService _icons;
    private readonly Bitmap? _originalSource;
    private Bitmap? _customSource;
    private readonly List<IconStyleChoiceViewModel> _choices = new();
    private string _style;
    private string _shape;
    private bool _ready;

    public IconPickerWindow(SystemIconTarget target, SystemIconService icons, string style, string shape)
    {
        InitializeComponent();

        _target = target;
        _icons = icons;
        _style = string.IsNullOrWhiteSpace(style) ? "filled" : style;
        _shape = string.IsNullOrWhiteSpace(shape) ? "rounded" : shape;
        _originalSource = IconArtRenderer.LoadSource(target.SourceIcon);

        TargetText.Text = target.KindLabel + " · " + target.DisplayName + " · " + target.Detail;
        SourceText.Text = string.IsNullOrWhiteSpace(target.SourceIcon)
            ? "Icona predefinita di Windows"
            : Path.GetFileName(target.SourceIcon);

        ShapeBox.ItemsSource = new[]
        {
            new SystemIconShapeOption("rounded", "Arrotondata"),
            new SystemIconShapeOption("squircle", "Squircle"),
            new SystemIconShapeOption("circle", "Cerchio"),
            new SystemIconShapeOption("square", "Quadrata")
        };
        ShapeBox.SelectedValuePath = "Id";
        ShapeBox.SelectedValue = _shape;

        foreach ((string id, string name, string subtitle) in StyleDefs)
            _choices.Add(new IconStyleChoiceViewModel(id, name, subtitle));
        StyleList.ItemsSource = _choices;
        StyleList.SelectedItem = _choices.FirstOrDefault(c => c.Id == _style) ?? _choices[2];

        _ready = true;
        RefreshPreviews();

        Closed += (_, _) =>
        {
            _originalSource?.Dispose();
            _customSource?.Dispose();
        };
    }

    public SystemIconChoice? Choice { get; private set; }

    private Bitmap CurrentSource => _customSource ?? _originalSource ?? IconArtRenderer.GenericFallback();

    private void RefreshPreviews()
    {
        if (!_ready)
            return;

        _style = (StyleList.SelectedItem as IconStyleChoiceViewModel)?.Id ?? _style;
        _shape = ShapeBox.SelectedValue as string ?? _shape;
        System.Drawing.Color accent = SystemIconService.PreviewAccent;

        foreach (IconStyleChoiceViewModel choice in _choices)
        {
            using Bitmap preview = IconArtRenderer.Render(CurrentSource, choice.Id, _shape, accent, 44);
            choice.Preview = IconArtRenderer.ToImageSource(preview);
        }

        using (Bitmap large = IconArtRenderer.Render(CurrentSource, _style, _shape, accent, 112))
            LargePreview.Source = IconArtRenderer.ToImageSource(large);

        SizePreviewPanel.Children.Clear();
        foreach (int size in new[] { 16, 20, 24, 32, 40, 48, 64, 96 })
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 11, 0) };
            var image = new WpfImage
            {
                Source = null,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            using (Bitmap bitmap = IconArtRenderer.Render(CurrentSource, _style, _shape, accent, Math.Min(size, 96)))
                image.Source = IconArtRenderer.ToImageSource(bitmap);
            stack.Children.Add(image);
            stack.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = size + " px",
                FontSize = 9,
                Foreground = (System.Windows.Media.Brush)FindResource("C.TextFaint"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0)
            });
            SizePreviewPanel.Children.Add(stack);
        }

        StatusText.Text = _customPath is null
            ? "Stile pronto. L'anteprima usa l'icona originale della voce."
            : "Icona personale: " + Path.GetFileName(_customPath);
    }

    private string? _customPath;

    private void OnStyleChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ready)
            RefreshPreviews();
    }

    private void OnShapeChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_ready)
            RefreshPreviews();
    }

    private void OnLoadCustomClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Scegli un'icona per " + _target.DisplayName,
            Filter = "Icone e immagini|*.ico;*.png;*.jpg;*.jpeg;*.exe|Icone|*.ico|Immagini|*.png;*.jpg;*.jpeg|Eseguibili|*.exe|Tutti i file|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _customSource?.Dispose();
            _customSource = IconArtRenderer.LoadSource(dialog.FileName);
            _customPath = dialog.FileName;
            CustomText.Text = Path.GetFileName(dialog.FileName);
            RefreshPreviews();
        }
        catch (Exception ex)
        {
            StatusText.Text = "File non leggibile: " + ex.Message;
        }
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        if (_style == "native")
        {
            Choice = new SystemIconChoice("native", _shape, null);
            DialogResult = true;
            return;
        }

        if (_customPath is not null && !File.Exists(_customPath))
        {
            StatusText.Text = "Il file personale non esiste più.";
            return;
        }

        Choice = new SystemIconChoice(_style, _shape, _customPath);
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
