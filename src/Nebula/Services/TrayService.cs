using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Nebula.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService(Action openSettings, Action search, Action widgets, Action showDock, Action restoreTaskbar, Action exit)
    {
        _icon = new NotifyIcon
        {
            Text = "Nebula Taskbar Studio",
            Icon = BuildIcon(),
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Apri Nebula", null, (_, _) => openSettings());
        menu.Items.Add("Cerca app e finestre   Ctrl+Alt+Spazio", null, (_, _) => search());
        menu.Items.Add("Pannello widget   Ctrl+Alt+W", null, (_, _) => widgets());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Mostra / nascondi la dock", null, (_, _) => showDock());
        menu.Items.Add("Ripristina la taskbar di Windows", null, (_, _) => restoreTaskbar());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci da Nebula", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.ShowBalloonTip(3000);
        }
        catch
        {
            // Notifications are optional.
        }
    }

    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(64, 64);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);

            using var path = new GraphicsPath();
            const float inset = 5f;
            const float radius = 16f;
            float right = 64 - inset;
            float bottom = 64 - inset;
            path.AddArc(inset, inset, radius * 2, radius * 2, 180, 90);
            path.AddArc(right - radius * 2, inset, radius * 2, radius * 2, 270, 90);
            path.AddArc(right - radius * 2, bottom - radius * 2, radius * 2, radius * 2, 0, 90);
            path.AddArc(inset, bottom - radius * 2, radius * 2, radius * 2, 90, 90);
            path.CloseFigure();

            using var gradient = new LinearGradientBrush(
                new PointF(0, 0), new PointF(64, 64),
                System.Drawing.Color.FromArgb(139, 92, 246),
                System.Drawing.Color.FromArgb(34, 211, 238));
            graphics.FillPath(gradient, path);

            using var dot = new SolidBrush(System.Drawing.Color.White);
            graphics.FillEllipse(dot, 23, 23, 7, 7);
            graphics.FillEllipse(dot, 34, 23, 7, 7);
            graphics.FillEllipse(dot, 23, 34, 7, 7);
            graphics.FillEllipse(dot, 34, 34, 7, 7);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            Interop.NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
