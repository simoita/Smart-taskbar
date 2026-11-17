using System.Windows;
using System.Windows.Media;

namespace Nebula.Controls;

public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double>? _values;

    public Brush Stroke { get; set; } = Brushes.MediumPurple;
    public Brush Fill { get; set; } = Brushes.Transparent;

    public IReadOnlyList<double>? Values
    {
        get => _values;
        set
        {
            _values = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 4 || height < 4 || _values is null || _values.Count < 2)
            return;

        int count = _values.Count;
        double step = width / (count - 1);
        var points = new List<Point>(count);
        for (int i = 0; i < count; i++)
        {
            double normalized = Math.Clamp(_values[i], 0, 100) / 100.0;
            points.Add(new Point(i * step, height - 2 - normalized * (height - 4)));
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext ctx = line.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
                ctx.LineTo(points[i], true, false);
        }
        line.Freeze();

        var fill = new StreamGeometry();
        using (StreamGeometryContext ctx = fill.Open())
        {
            ctx.BeginFigure(new Point(0, height), true, true);
            ctx.LineTo(points[0], true, false);
            for (int i = 1; i < points.Count; i++)
                ctx.LineTo(points[i], true, false);
            ctx.LineTo(new Point(width, height), true, false);
        }
        fill.Freeze();

        var fillBrush = new LinearGradientBrush(
            Color.FromArgb(90, 139, 92, 246),
            Color.FromArgb(0, 139, 92, 246),
            new Point(0, 0), new Point(0, 1));
        dc.DrawGeometry(fillBrush, null, fill);

        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 139, 92, 246)), 5)
        {
            LineJoin = PenLineJoin.Round
        };
        dc.DrawGeometry(null, glowPen, line);

        var pen = new Pen(Stroke, 1.8)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        dc.DrawGeometry(null, pen, line);
    }
}
