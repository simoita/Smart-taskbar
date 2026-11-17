using System.Windows;
using System.Windows.Media;
using Nebula.Models;

namespace Nebula.Services;

/// <summary>Resolves a preset + user overrides into the app-wide resource palette.</summary>
public sealed class ThemeEngine
{
    private readonly AppSettings _settings;

    public ThemeEngine(AppSettings settings)
    {
        _settings = settings;
        Reload();
    }

    public ThemePreset Preset { get; private set; } = ThemePreset.All[0];
    public bool IsLight => Preset.IsLight;
    public Color AccentA { get; private set; }
    public Color AccentB { get; private set; }
    public Color TextColor { get; private set; }
    public Color MutedColor { get; private set; }
    public Brush AccentBrush { get; private set; } = Brushes.MediumPurple;
    public Brush AccentGradient { get; private set; } = Brushes.MediumPurple;
    public Brush TintBrush { get; private set; } = Brushes.Transparent;
    public Brush DockStrokeBrush { get; private set; } = Brushes.Transparent;
    public Brush GlowBrush { get; private set; } = Brushes.Transparent;
    public Brush ItemHoverBrush { get; private set; } = Brushes.Transparent;
    public Brush UiBackground { get; private set; } = Brushes.Black;
    public Color UiBackgroundColor { get; private set; }

    public event Action? Changed;

    public static Color Hex(string value) => ColorUtil.Parse(value);

    public void Reload()
    {
        Preset = ThemePreset.Find(_settings.ThemeId);

        bool hasCustomAccent = !string.IsNullOrWhiteSpace(_settings.AccentHex) &&
                               _settings.AccentHex.TrimStart('#').Length >= 6;
        AccentA = hasCustomAccent ? ColorUtil.Parse(_settings.AccentHex) : ColorUtil.Parse(Preset.AccentA);
        AccentB = hasCustomAccent
            ? ColorUtil.ShiftHue(AccentA, 30, 0.04, 0.08)
            : ColorUtil.Parse(Preset.AccentB);
        TextColor = ColorUtil.Parse(Preset.Text);
        MutedColor = ColorUtil.Parse(Preset.Muted);

        AccentBrush = Solid(AccentA);
        AccentGradient = new LinearGradientBrush(AccentA, AccentB, new Point(0, 0), new Point(1, 1));
        TintBrush = BuildTint();
        DockStrokeBrush = BuildStroke();
        GlowBrush = BuildGlow();
        ItemHoverBrush = Solid(Preset.IsLight ? Color.FromArgb(18, 15, 23, 42) : Color.FromArgb(26, 255, 255, 255));

        ApplyUiResources();
        Changed?.Invoke();
    }

    private Brush BuildTint()
    {
        double op = Math.Clamp(_settings.GlassOpacity, 0.3, 1.0);
        Color top = ColorUtil.Parse(Preset.GlassTop);
        Color bottom = ColorUtil.Parse(Preset.GlassBottom);

        byte a1 = (byte)(Preset.IsLight ? 108 + 92 * op : 54 + 108 * op);
        byte a2 = (byte)(Preset.IsLight ? 138 + 82 * op : 94 + 98 * op);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.3, 1)
        };
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(top, a1), 0));
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(bottom, a2), 1));
        brush.Freeze();
        return brush;
    }

    private Brush BuildStroke()
    {
        Color baseColor = ColorUtil.Parse(Preset.Stroke);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(baseColor, (byte)150), 0));
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(AccentA, (byte)60), 0.55));
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(baseColor, (byte)36), 1));
        brush.Freeze();
        return brush;
    }

    private Brush BuildGlow()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(AccentA, (byte)90), 0));
        brush.GradientStops.Add(new GradientStop(ColorUtil.WithAlpha(AccentB, (byte)42), 1));
        brush.Freeze();
        return brush;
    }

    private void ApplyUiResources()
    {
        if (Application.Current is null)
            return;

        ThemePreset p = Preset;
        Color glassBottom = ColorUtil.Parse(p.GlassBottom);
        Color accentTint = ColorUtil.Mix(glassBottom, AccentA, 0.10);

        Color bgColor;
        Color panelColor;
        Color cardColor;
        Color cardHover;
        Color strokeColor;
        Color strokeSoft;
        Color track;
        Color navActive;
        Color hover;
        Color selected;

        if (p.IsLight)
        {
            bgColor = ColorUtil.Mix(ColorUtil.Parse("#F7F8FC"), glassBottom, 0.35);
            panelColor = Color.FromArgb(235, 255, 255, 255);
            cardColor = Color.FromArgb(255, 255, 255, 255);
            cardHover = ColorUtil.Mix(cardColor, AccentA, 0.05);
            strokeColor = ColorUtil.Mix(ColorUtil.Parse("#DDE1EE"), glassBottom, 0.25);
            strokeSoft = ColorUtil.Mix(ColorUtil.Parse("#ECEEF7"), glassBottom, 0.20);
            track = Color.FromArgb(28, 15, 23, 42);
            navActive = ColorUtil.Mix(Color.FromArgb(255, 255, 255, 255), AccentA, 0.12);
            hover = ColorUtil.Mix(Color.FromArgb(255, 255, 255, 255), AccentA, 0.06);
            selected = ColorUtil.WithAlpha(AccentA, 36);
        }
        else
        {
            bgColor = ColorUtil.Darken(ColorUtil.Mix(glassBottom, ColorUtil.Parse("#05060B"), 0.55), 0.15);
            panelColor = Color.FromArgb(18, 255, 255, 255);
            cardColor = Color.FromArgb(12, 255, 255, 255);
            // Keep the accent tint translucent: an opaque light hover card would
            // make the white text of dark presets disappear.
            cardHover = ColorUtil.WithAlpha(ColorUtil.Mix(Colors.White, AccentA, 0.10), 26);
            strokeColor = Color.FromArgb(34, 255, 255, 255);
            strokeSoft = Color.FromArgb(20, 255, 255, 255);
            track = Color.FromArgb(40, 255, 255, 255);
            navActive = Color.FromArgb(26, 255, 255, 255);
            hover = Color.FromArgb(18, 255, 255, 255);
            selected = ColorUtil.WithAlpha(AccentA, 52);
        }

        Color cardSurface = ColorUtil.Composite(cardColor, bgColor);
        Color hoverSurface = ColorUtil.Composite(hover, bgColor);
        Color cardHoverSurface = ColorUtil.Composite(cardHover, bgColor);
        Color panelSurface = ColorUtil.Composite(panelColor, bgColor);
        Color navActiveSurface = ColorUtil.Composite(navActive, bgColor);
        Color selectedSurface = ColorUtil.Composite(selected, cardSurface);
        byte accentSoftAlpha = (byte)(p.IsLight ? 32 : 46);
        Color accentSoftSurface = ColorUtil.Composite(ColorUtil.WithAlpha(AccentA, accentSoftAlpha), cardSurface);
        // The light glass surface is slightly tinted; it is darker than the
        // white card and is therefore the strictest text background.
        Color tintedSurface = p.IsLight ? ColorUtil.Mix(bgColor, Colors.Black, 0.10) : bgColor;
        Color tooltipBg = p.IsLight
            ? ColorUtil.Mix(bgColor, Colors.White, 0.55)
            : ColorUtil.Mix(bgColor, Colors.Black, 0.55);

        // Keep every text resource above the surface it is actually drawn on.
        // Presets remain the starting point, but a too-soft colour is corrected
        // instead of being allowed to disappear into the background.
        Color textColor = ColorUtil.EnsureContrast(TextColor, 7.0,
            bgColor, cardSurface, panelSurface, hoverSurface, cardHoverSurface,
            navActiveSurface, selectedSurface, tintedSurface, tooltipBg);
        Color muted = ColorUtil.EnsureContrast(MutedColor, 5.0,
            bgColor, cardSurface, panelSurface, hoverSurface, cardHoverSurface,
            navActiveSurface, selectedSurface, tintedSurface);
        Color faint = ColorUtil.EnsureContrast(ColorUtil.Mix(MutedColor, bgColor, 0.35), 4.5,
            bgColor, cardSurface, panelSurface, hoverSurface, cardHoverSurface,
            navActiveSurface, selectedSurface, tintedSurface);
        Color accentText = ColorUtil.EnsureContrast(AccentA, 4.5,
            bgColor, cardSurface, panelSurface, hoverSurface, cardHoverSurface,
            navActiveSurface, accentSoftSurface, tintedSurface);

        ResourceDictionary r = Application.Current.Resources;

        var bgGradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        bgGradient.GradientStops.Add(new GradientStop(ColorUtil.Mix(bgColor, AccentA, 0.035), 0));
        bgGradient.GradientStops.Add(new GradientStop(bgColor, 0.55));
        bgGradient.GradientStops.Add(new GradientStop(ColorUtil.Mix(bgColor, AccentB, 0.05), 1));
        bgGradient.Freeze();

        UiBackground = bgGradient;
        UiBackgroundColor = bgColor;

        r["C.Bg"] = bgGradient;
        r["C.BgColor"] = bgColor;
        r["C.Panel"] = Solid(panelColor);
        r["C.Card"] = Solid(cardColor);
        r["C.CardHover"] = Solid(cardHover);
        r["C.Stroke"] = Solid(strokeColor);
        r["C.StrokeSoft"] = Solid(strokeSoft);
        r["C.Text"] = Solid(textColor);
        r["C.TextColor"] = textColor;
        r["C.TextMuted"] = Solid(muted);
        r["C.TextFaint"] = Solid(faint);
        r["C.Accent"] = AccentBrush;
        r["C.AccentText"] = Solid(accentText);
        r["C.Accent2"] = Solid(AccentB);
        r["C.AccentColor"] = AccentA;
        r["C.Accent2Color"] = AccentB;
        r["C.AccentGrad"] = AccentGradient;
        r["C.AccentSoft"] = Solid(ColorUtil.WithAlpha(AccentA, accentSoftAlpha));
        r["C.AccentFaint"] = Solid(ColorUtil.WithAlpha(AccentA, (byte)22));
        // Text-bearing accent surfaces use the solid accent, so contrast is
        // computed against AccentA rather than the more decorative gradient.
        r["C.OnAccent"] = Solid(ColorUtil.BestForeground(AccentA));
        r["C.Track"] = Solid(track);
        r["C.NavActive"] = Solid(navActive);
        r["C.Hover"] = Solid(hover);
        r["C.Selected"] = Solid(selected);
        r["C.Knob"] = Solid(p.IsLight ? Colors.White : Color.FromRgb(245, 246, 255));
        r["C.Success"] = Solid(ColorUtil.EnsureContrast(ColorUtil.Parse("#34D399"), 4.5, bgColor, cardSurface));
        r["C.Warn"] = Solid(ColorUtil.EnsureContrast(ColorUtil.Parse("#FBBF24"), 4.5, bgColor, cardSurface));
        r["C.Danger"] = Solid(ColorUtil.EnsureContrast(ColorUtil.Parse("#FB7185"), 4.5, bgColor, cardSurface));
        r["C.TooltipBg"] = Solid(tooltipBg);
        r["C.DockGlass"] = TintBrush;
        r["C.DockStroke"] = DockStrokeBrush;
        r["C.DockGlow"] = GlowBrush;
        r["C.ItemHover"] = ItemHoverBrush;
        r["C.Shadow"] = Solid(Color.FromArgb(120, 0, 0, 0));
        r["C.WallpaperTint"] = Solid(ColorUtil.WithAlpha(ColorUtil.Mix(glassBottom, Colors.Black, p.IsLight ? 0.05 : 0.35), (byte)32));

        ApplySurfaceResources(r, p, bgColor, cardColor, strokeColor, strokeSoft);
    }

    /// <summary>
    /// Dedicated resources for the three new surfaces. They are deliberately scoped with S.*
    /// so switching the surface preset cannot alter the dock or the tab bar.
    /// </summary>
    private void ApplySurfaceResources(ResourceDictionary r, ThemePreset p, Color bgColor,
        Color cardColor, Color strokeColor, Color strokeSoft)
    {
        string style = _settings.SurfaceStyle;
        if (style is not ("glass" or "minimal" or "neon" or "soft" or "mono"))
            style = _settings.TabsStyle;

        Brush cardBg;
        Brush cardBorder;
        Brush innerBg;
        Brush innerBorder;
        Brush dim;
        Brush sheen;
        CornerRadius cardRadius;
        CornerRadius innerRadius;
        CornerRadius resultRadius;
        CornerRadius controlRadius;
        Thickness cardPadding;
        Thickness resultPadding;
        Thickness borderThickness;
        double trackHeight;
        double knobSize;
        string textFont;
        string displayFont;
        FontWeight titleWeight;

        switch (style)
        {
            case "minimal":
                cardBg = Solid(p.IsLight ? Colors.White : ColorUtil.Mix(bgColor, Colors.Black, 0.22));
                cardBorder = Solid(strokeColor);
                innerBg = Solid(p.IsLight ? Color.FromArgb(255, 250, 251, 255) : Color.FromArgb(18, 255, 255, 255));
                innerBorder = Solid(strokeSoft);
                dim = Solid(Color.FromArgb(132, 0, 0, 0));
                sheen = Solid(Color.FromArgb(p.IsLight ? (byte)160 : (byte)14, 255, 255, 255));
                cardRadius = new CornerRadius(10);
                innerRadius = new CornerRadius(8);
                resultRadius = new CornerRadius(6);
                controlRadius = new CornerRadius(6);
                cardPadding = new Thickness(18);
                resultPadding = new Thickness(9, 7, 9, 7);
                borderThickness = new Thickness(1);
                trackHeight = 3;
                knobSize = 13;
                textFont = "Segoe UI";
                displayFont = "Segoe UI";
                titleWeight = FontWeights.SemiBold;
                break;

            case "neon":
                // A dark neon card would hide dark text on light presets.
                cardBg = p.IsLight
                    ? Solid(ColorUtil.Mix(bgColor, Colors.White, 0.88))
                    : Solid(ColorUtil.WithAlpha(ColorUtil.Mix(bgColor, Colors.Black, 0.72), 248));
                cardBorder = AccentGradient;
                innerBg = Solid(ColorUtil.WithAlpha(AccentA, (byte)(p.IsLight ? 18 : 24)));
                innerBorder = Solid(ColorUtil.WithAlpha(AccentA, (byte)96));
                dim = Solid(Color.FromArgb(184, 0, 0, 0));
                sheen = Solid(Color.FromArgb(28, 255, 255, 255));
                cardRadius = new CornerRadius(16);
                innerRadius = new CornerRadius(10);
                resultRadius = new CornerRadius(9);
                controlRadius = new CornerRadius(8);
                cardPadding = new Thickness(18);
                resultPadding = new Thickness(11, 9, 11, 9);
                borderThickness = new Thickness(1.5);
                trackHeight = 5;
                knobSize = 18;
                textFont = "Segoe UI Variable Text, Segoe UI";
                displayFont = "Segoe UI Variable Display, Segoe UI";
                titleWeight = FontWeights.Bold;
                break;

            case "soft":
                cardBg = Solid(ColorUtil.WithAlpha(ColorUtil.Mix(bgColor, Colors.Black, p.IsLight ? 0.04 : 0.42), 246));
                cardBorder = Solid(ColorUtil.WithAlpha(AccentA, 70));
                innerBg = Solid(p.IsLight ? Color.FromArgb(255, 252, 250, 255) : Color.FromArgb(24, 255, 255, 255));
                innerBorder = Solid(ColorUtil.WithAlpha(AccentA, 38));
                dim = Solid(Color.FromArgb(170, 7, 8, 16));
                sheen = Solid(Color.FromArgb(p.IsLight ? (byte)190 : (byte)30, 255, 255, 255));
                cardRadius = new CornerRadius(26);
                innerRadius = new CornerRadius(18);
                resultRadius = new CornerRadius(14);
                controlRadius = new CornerRadius(12);
                cardPadding = new Thickness(22);
                resultPadding = new Thickness(12, 9, 12, 9);
                borderThickness = new Thickness(1);
                trackHeight = 5;
                knobSize = 19;
                textFont = "Segoe UI Variable Text, Segoe UI";
                displayFont = "Segoe UI Variable Display, Segoe UI";
                titleWeight = FontWeights.SemiBold;
                break;

            case "mono":
                cardBg = Solid(ColorUtil.Mix(bgColor, p.IsLight ? Colors.White : Colors.Black, p.IsLight ? 0.04 : 0.78));
                cardBorder = Solid(ColorUtil.Mix(strokeColor, Colors.Gray, 0.25));
                innerBg = Solid(p.IsLight ? Color.FromArgb(255, 248, 249, 251) : Color.FromArgb(16, 255, 255, 255));
                innerBorder = Solid(strokeSoft);
                dim = Solid(Color.FromArgb(172, 0, 0, 0));
                sheen = Solid(Color.FromArgb(p.IsLight ? (byte)120 : (byte)16, 255, 255, 255));
                cardRadius = new CornerRadius(6);
                innerRadius = new CornerRadius(4);
                resultRadius = new CornerRadius(3);
                controlRadius = new CornerRadius(3);
                cardPadding = new Thickness(16);
                resultPadding = new Thickness(10, 7, 10, 7);
                borderThickness = new Thickness(1);
                trackHeight = 3;
                knobSize = 14;
                textFont = "Segoe UI";
                displayFont = "Segoe UI";
                titleWeight = FontWeights.SemiBold;
                break;

            default:
                cardBg = Solid(ColorUtil.Mix(bgColor, Colors.Black, p.IsLight ? 0.10 : 0.55));
                cardBorder = DockStrokeBrush;
                innerBg = Solid(cardColor);
                innerBorder = Solid(strokeSoft);
                dim = Solid(Color.FromArgb(158, 7, 8, 16));
                sheen = Solid(Color.FromArgb(p.IsLight ? (byte)132 : (byte)24, 255, 255, 255));
                cardRadius = new CornerRadius(22);
                innerRadius = new CornerRadius(14);
                resultRadius = new CornerRadius(11);
                controlRadius = new CornerRadius(10);
                cardPadding = new Thickness(20);
                resultPadding = new Thickness(11, 8, 11, 8);
                borderThickness = new Thickness(1);
                trackHeight = 4;
                knobSize = 17;
                textFont = "Segoe UI Variable Text, Segoe UI";
                displayFont = "Segoe UI Variable Display, Segoe UI";
                titleWeight = FontWeights.SemiBold;
                break;
        }

        r["S.CardBg"] = cardBg;
        r["S.CardBorder"] = cardBorder;
        r["S.InnerBg"] = innerBg;
        r["S.InnerBorder"] = innerBorder;
        r["S.Dim"] = dim;
        r["S.Sheen"] = sheen;
        r["S.CardRadius"] = cardRadius;
        r["S.InnerRadius"] = innerRadius;
        r["S.ResultRadius"] = resultRadius;
        r["S.ControlRadius"] = controlRadius;
        r["S.CardPadding"] = cardPadding;
        r["S.ResultPadding"] = resultPadding;
        r["S.BorderThickness"] = borderThickness;
        r["S.TrackHeight"] = trackHeight;
        r["S.TrackRadius"] = new CornerRadius(trackHeight <= 3 ? 1 : 2);
        r["S.KnobSize"] = knobSize;
        r["S.KnobHoverSize"] = knobSize + 2;
        r["S.FontFamily"] = new FontFamily(textFont);
        r["S.DisplayFont"] = new FontFamily(displayFont);
        r["S.TitleWeight"] = titleWeight;
    }

    private static SolidColorBrush Solid(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public static class ColorUtil
{
    public static Color Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return Colors.Transparent;

        string s = hex.Trim().TrimStart('#');
        try
        {
            return s.Length switch
            {
                8 => Color.FromArgb(Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16)),
                6 => Color.FromRgb(Convert.ToByte(s.Substring(0, 2), 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16)),
                _ => Colors.Transparent
            };
        }
        catch
        {
            return Colors.Transparent;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    public static Color Darken(Color c, double amount) => Mix(c, Colors.Black, Math.Clamp(amount, 0, 1));

    public static Color Lighten(Color c, double amount) => Mix(c, Colors.White, Math.Clamp(amount, 0, 1));

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    public static Color Composite(Color foreground, Color background)
    {
        double a = foreground.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * a + background.R * (1 - a)),
            (byte)Math.Round(foreground.G * a + background.G * (1 - a)),
            (byte)Math.Round(foreground.B * a + background.B * (1 - a)));
    }

    public static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            double v = value / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static Color BestForeground(params Color[] backgrounds)
    {
        Color dark = Color.FromRgb(5, 6, 11);
        Color light = Colors.White;
        double darkScore = backgrounds.Length == 0 ? 0 : backgrounds.Min(b => Contrast(dark, b));
        double lightScore = backgrounds.Length == 0 ? 0 : backgrounds.Min(b => Contrast(light, b));
        return darkScore >= lightScore ? dark : light;
    }

    /// <summary>
    /// Preserves the original colour when it is readable, otherwise moves it
    /// towards black or white until every supplied surface reaches the target.
    /// </summary>
    public static Color EnsureContrast(Color foreground, double minimum, params Color[] backgrounds)
    {
        if (backgrounds.Length == 0)
            return foreground;

        double Score(Color c) => backgrounds.Min(b => Contrast(c, b));
        if (Score(foreground) >= minimum)
            return foreground;

        Color target = BestForeground(backgrounds);
        Color best = foreground;
        double bestScore = Score(foreground);
        for (int step = 1; step <= 20; step++)
        {
            Color candidate = Mix(foreground, target, step / 20.0);
            double score = Score(candidate);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }

            if (score >= minimum)
                return candidate;
        }

        return bestScore >= Score(target) ? best : target;
    }

    public static Color ShiftHue(Color c, double degrees, double lighten, double saturate)
    {
        ColorUtil.ToHsl(c, out double h, out double s, out double l);
        h = (h + degrees / 360.0) % 1.0;
        s = Math.Clamp(s + saturate, 0, 1);
        l = Math.Clamp(l + lighten, 0, 1);
        return FromHsl(h, s, l);
    }

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2.0;
        double d = max - min;

        if (d < 0.0001)
        {
            h = 0;
            s = 0;
            return;
        }

        s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        if (max == r)
            h = ((g - b) / d + (g < b ? 6 : 0)) / 6.0;
        else if (max == g)
            h = ((b - r) / d + 2) / 6.0;
        else
            h = ((r - g) / d + 4) / 6.0;
    }

    private static Color FromHsl(double h, double s, double l)
    {
        if (s <= 0)
        {
            byte v = (byte)Math.Round(l * 255);
            return Color.FromRgb(v, v, v);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromRgb(Channel(p, q, h + 1.0 / 3), Channel(p, q, h), Channel(p, q, h - 1.0 / 3));
    }

    private static byte Channel(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double v;
        if (t < 1.0 / 6) v = p + (q - p) * 6 * t;
        else if (t < 0.5) v = q;
        else if (t < 2.0 / 3) v = p + (q - p) * (2.0 / 3 - t) * 6;
        else v = p;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}
