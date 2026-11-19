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

        var menu = new ContextMenuStrip
        {
            Renderer = new NebulaMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new Padding(5, 7, 5, 7),
            // Reuse the shell font family so the menu still looks native on
            // Windows 10, where Segoe UI Variable is not installed.
            Font = new Font(SystemFonts.MenuFont?.FontFamily ?? new FontFamily("Segoe UI"), 9.75f),
            BackColor = System.Drawing.Color.FromArgb(26, 22, 38),
            ForeColor = System.Drawing.Color.FromArgb(242, 242, 255)
        };

        ToolStripMenuItem Add(string text, Action action, string? shortcut = null)
        {
            var item = new ToolStripMenuItem(text, null, (_, _) => action())
            {
                Padding = new Padding(10, 7, 12, 7),
                ShortcutKeyDisplayString = shortcut
            };
            menu.Items.Add(item);
            return item;
        }

        Add("Apri Nebula", openSettings);
        Add("Cerca app e finestre", search, "Ctrl+Alt+Spazio");
        Add("Pannello widget", widgets, "Ctrl+Alt+W");
        menu.Items.Add(new ToolStripSeparator());
        Add("Mostra / nascondi la dock", showDock);
        Add("Ripristina la taskbar di Windows", restoreTaskbar);
        menu.Items.Add(new ToolStripSeparator());
        Add("Esci da Nebula", exit);

        menu.Opened += (_, _) => RoundMenu(menu);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => openSettings();
    }

    private static void RoundMenu(ContextMenuStrip menu)
    {
        var bounds = new Rectangle(0, 0, menu.Width, menu.Height);
        const int radius = 12;
        using var path = new GraphicsPath();
        path.AddArc(bounds.X, bounds.Y, radius, radius, 180, 90);
        path.AddArc(bounds.Right - radius, bounds.Y, radius, radius, 270, 90);
        path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - radius, radius, radius, 90, 90);
        path.CloseFigure();
        menu.Region = new Region(path);
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
