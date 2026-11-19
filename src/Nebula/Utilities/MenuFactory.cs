using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nebula.Utilities;

/// <summary>Builds the aligned, glyph-led entries used by Nebula context menus.</summary>
public static class MenuFactory
{
    public static MenuItem Item(string header, string glyph, Action action)
    {
        var item = new MenuItem { Header = header, Icon = Icon(glyph) };
        item.Click += (_, _) => action();
        return item;
    }

    public static TextBlock Icon(string glyph)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 13,
            Width = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "C.AccentText");
        return icon;
    }
}
