using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nebula.Services;

/// <summary>
/// Paints the tray menu with the same dark glass language as the in-app
/// context menus, reading the live theme brushes so a theme change is
/// reflected the next time the menu opens.
/// </summary>
public sealed class NebulaMenuRenderer : ToolStripProfessionalRenderer
{
    private static Color Res(string key, Color fallback)
    {
        if (System.Windows.Application.Current?.FindResource(key) is System.Windows.Media.SolidColorBrush brush)
        {
            System.Windows.Media.Color color = brush.Color;
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        return fallback;
    }

    private static Color SurfaceColor => Res("C.TooltipBg", Color.FromArgb(26, 22, 38));
    private static Color StrokeColor => Res("C.Stroke", Color.FromArgb(34, 255, 255, 255));
    private static Color StrokeSoftColor => Res("C.StrokeSoft", Color.FromArgb(20, 255, 255, 255));
    private static Color HoverColor => Res("C.Hover", Color.FromArgb(30, 255, 255, 255));
    private static Color TextColor => Res("C.Text", Color.FromArgb(242, 242, 255));
    private static Color MutedColor => Res("C.TextMuted", Color.FromArgb(165, 160, 185));

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(SurfaceColor);
        e.Graphics.FillRectangle(brush, e.ToolStrip.ClientRectangle);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using GraphicsPath path = Rounded(bounds, 11);
        using var pen = new Pen(StrokeColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
            return;

        var bounds = new Rectangle(4, 2, Math.Max(0, e.Item.Width - 8), Math.Max(0, e.Item.Height - 4));
        using GraphicsPath path = Rounded(bounds, 8);
        using var brush = new SolidBrush(HoverColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(StrokeSoftColor);
        e.Graphics.DrawLine(pen, 12, y, Math.Max(12, e.Item.Width - 12), y);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        bool isShortcut = e.Item is ToolStripMenuItem item
                          && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString)
                          && e.Text == item.ShortcutKeyDisplayString;

        e.TextColor = !e.Item.Enabled ? MutedColor : isShortcut ? MutedColor : TextColor;
        base.OnRenderItemText(e);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = radius * 2;
        if (bounds.Width <= diameter || bounds.Height <= diameter)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
