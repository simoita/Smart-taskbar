using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Nebula.Controls;

public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(6.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MutedBrushProperty = DependencyProperty.Register(
        nameof(MutedBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush MutedBrush
    {
        get => (Brush)GetValue(MutedBrushProperty);
        set => SetValue(MutedBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
            return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = size / 2 - Thickness / 2 - 1;
        var trackPen = new Pen(Track, Thickness);
        dc.DrawEllipse(null, trackPen, center, radius, radius);

        double value = Math.Clamp(Value, 0, 100);
        if (value > 0.3)
        {
            double sweep = value / 100.0 * 359.9;
            Point start = PointOnCircle(center, radius, -90);
            Point end = PointOnCircle(center, radius, -90 + sweep);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(radius, radius), 0, sweep > 180,
                    SweepDirection.Clockwise, true, false);
            }
            geometry.Freeze();

            var pen = new Pen(Fill, Thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            dc.DrawGeometry(null, pen, geometry);
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var captionTypeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var valueTypeface = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        if (!string.IsNullOrEmpty(ValueText))
        {
            var valueText = new FormattedText(ValueText, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, valueTypeface, Math.Max(11, size * 0.17),
                TextBrush, pixelsPerDip);
            dc.DrawText(valueText, new Point(center.X - valueText.Width / 2, center.Y - valueText.Height * 0.72));
        }

        if (!string.IsNullOrEmpty(Caption))
        {
            var captionText = new FormattedText(Caption, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, captionTypeface, Math.Max(8, size * 0.085),
                MutedBrush, pixelsPerDip);
            dc.DrawText(captionText, new Point(center.X - captionText.Width / 2, center.Y + CredentialHeight(size)));
        }
    }

    private static double CredentialHeight(double size) => size * 0.10;

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180.0;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
