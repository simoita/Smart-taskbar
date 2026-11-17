using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Services;

namespace Nebula.ViewModels;

public sealed class DockItemViewModel : ObservableObject
{
    private ImageSource? _icon;
    private AppGroup? _group;
    private readonly AppSettings _settings;

    private DockItemViewModel(AppSettings settings)
    {
        _settings = settings;
    }

    public string Key { get; init; } = "";
    public string Name { get; private set; } = "";
    public string LaunchPath { get; private set; } = "";
    public string? TargetPath { get; private set; }
    public bool IsPinned { get; private set; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (!SetProperty(ref _icon, value))
                return;

            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(DisplayIcon));
        }
    }

    public AppGroup? Group
    {
        get => _group;
        set
        {
            if (!SetProperty(ref _group, value))
                return;

            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsActive));
            OnPropertyChanged(nameof(IsMinimized));
            OnPropertyChanged(nameof(TooltipTitle));
            OnPropertyChanged(nameof(TooltipSubtitle));
            OnPropertyChanged(nameof(HasIcon));
            OnPropertyChanged(nameof(StateOpacity));
            OnPropertyChanged(nameof(OpenIndicatorVisibility));
            OnPropertyChanged(nameof(MinimizedIndicatorVisibility));
            OnPropertyChanged(nameof(ClosedIndicatorVisibility));
            OnPropertyChanged(nameof(StateDescription));
        }
    }

    public bool IsRunning => _group is not null;
    public bool IsActive => _group?.IsActive == true;
    public bool HasIcon => _icon is not null;

    /// <summary>Per-app override when present, otherwise the global icon style.</summary>
    public string IconStyle => FindStyleOverride() ?? _settings.IconStyle;

    private string? FindStyleOverride()
    {
        foreach (KeyValuePair<string, string> pair in _settings.CustomIconStyles)
        {
            if (string.Equals(pair.Key, Key, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, LaunchPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, TargetPath, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    public ImageSource? DisplayIcon => IconStyle == "mono" ? ToGray(_icon) : _icon;

    public Visibility IconPlateVisibility => IconStyle is "tile" or "circle" or "neon"
        ? Visibility.Visible
        : Visibility.Collapsed;

    public double IconPlateOpacity => IconStyle switch
    {
        "neon" => 0.9,
        "tile" => 0.55,
        "circle" => 0.4,
        _ => 0
    };

    public CornerRadius IconPlateCornerRadius => IconStyle switch
    {
        "circle" => new CornerRadius(999),
        "tile" => new CornerRadius(Math.Max(6, _settings.IconSize * 0.24)),
        "neon" => new CornerRadius(Math.Max(6, _settings.IconSize * 0.2)),
        _ => new CornerRadius(0)
    };

    public double IconDisplaySize => Math.Max(8, _settings.IconSize - (IconStyle == "native" ? 0 : 6));

    public Geometry? IconClip
    {
        get
        {
            double size = IconDisplaySize;
            var rect = new Rect(0, 0, size, size);
            return _settings.IconShape switch
            {
                "circle" => new EllipseGeometry(rect),
                "square" => new RectangleGeometry(rect),
                "squircle" => new RectangleGeometry(rect, size * 0.28, size * 0.28),
                _ => new RectangleGeometry(rect, size * 0.16, size * 0.16)
            };
        }
    }

    public CornerRadius IconCornerRadius => _settings.IconShape switch
    {
        "circle" => new CornerRadius(999),
        "square" => new CornerRadius(2),
        "squircle" => new CornerRadius(Math.Max(4, _settings.IconSize * 0.28)),
        _ => new CornerRadius(Math.Max(4, _settings.IconSize * 0.16))
    };

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public bool IsMinimized =>
        _group is not null && _group.Windows.Count > 0 &&
        _group.Windows.All(w => NativeMethods.IsIconic(w.Handle));

    public double StateOpacity => !IsRunning
        ? _settings.ClosedIconOpacity
        : IsMinimized ? _settings.MinimizedIconOpacity : 1.0;

    public Visibility OpenIndicatorVisibility => IsRunning && !IsMinimized && _settings.ShowOpenIndicator
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility MinimizedIndicatorVisibility => IsRunning && IsMinimized && _settings.ShowMinimizedIndicator
        ? Visibility.Visible
        : Visibility.Collapsed;

    public Visibility ClosedIndicatorVisibility => !IsRunning && _settings.ShowClosedIndicator
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string StateDescription => !IsRunning ? "chiusa" : IsMinimized ? "ridotta" : "aperta";

    public double IndicatorWidth => _settings.DockIndicatorStyle switch
    {
        "dot" => 7,
        "bar" => 22,
        "frame" => 28,
        _ => 14
    };

    public double IndicatorHeight => _settings.DockIndicatorStyle switch
    {
        "dot" => 7,
        "bar" => 2,
        "frame" => 2,
        _ => 3
    };

    public CornerRadius IndicatorRadius => new(IndicatorHeight / 2);

    public string TooltipTitle => IsRunning ? _group!.Name : Name;

    public string TooltipSubtitle => _group switch
    {
        null => "Apri",
        { Windows.Count: 1 } => _group.Windows[0].Title,
        { Windows.Count: > 1 } => $"{_group.Windows.Count} finestre",
        _ => "Apri"
    };

    public string IconPath { get; private set; } = "";

    public bool HasCustomIcon => _settings.CustomIcons.Keys.Any(key =>
        string.Equals(key, Key, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, LaunchPath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(key, TargetPath, StringComparison.OrdinalIgnoreCase));

    public static DockItemViewModel CreatePinned(AppSettings settings, string path, string name, string? target, AppGroup? group, ImageSource? icon) =>
        new(settings)
        {
            Key = "pin:" + path,
            Name = name,
            LaunchPath = path,
            TargetPath = target,
            IsPinned = true,
            Group = group,
            Icon = icon,
            IconPath = path
        };

    public static DockItemViewModel CreateRunning(AppSettings settings, AppGroup group) =>
        new(settings)
        {
            Key = "run:" + group.ExePath,
            Name = group.Name,
            LaunchPath = group.ExePath,
            TargetPath = group.ExePath,
            IsPinned = false,
            Group = group,
            Icon = group.Icon,
            IconPath = group.ExePath
        };

    public void UpdateGroup(AppGroup? group)
    {
        Group = group;
        if (group?.Icon is not null && !HasCustomIcon)
            Icon = group.Icon;
    }

    public void RefreshPresentation()
    {
        OnPropertyChanged(nameof(StateOpacity));
        OnPropertyChanged(nameof(OpenIndicatorVisibility));
        OnPropertyChanged(nameof(MinimizedIndicatorVisibility));
        OnPropertyChanged(nameof(ClosedIndicatorVisibility));
        OnPropertyChanged(nameof(StateDescription));
        OnPropertyChanged(nameof(IndicatorWidth));
        OnPropertyChanged(nameof(IndicatorHeight));
        OnPropertyChanged(nameof(IndicatorRadius));
        OnPropertyChanged(nameof(DisplayIcon));
        OnPropertyChanged(nameof(IconStyle));
        OnPropertyChanged(nameof(IconPlateVisibility));
        OnPropertyChanged(nameof(IconPlateOpacity));
        OnPropertyChanged(nameof(IconPlateCornerRadius));
        OnPropertyChanged(nameof(IconDisplaySize));
        OnPropertyChanged(nameof(IconClip));
        OnPropertyChanged(nameof(IconCornerRadius));
    }

    private static ImageSource? ToGray(ImageSource? source)
    {
        if (source is null)
            return null;
        try
        {
            if (source is not BitmapSource bitmap)
                return source;
            var gray = new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            return gray;
        }
        catch
        {
            return source;
        }
    }
}
