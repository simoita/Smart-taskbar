using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Nebula.Utilities;

/// <summary>
/// Produces a heavily downscaled crop of the desktop wallpaper. Stretching it back
/// up gives a soft frosted-glass backdrop with zero runtime cost.
/// </summary>
internal static class BackdropFactory
{
    private static readonly Dictionary<string, ImageBrush?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageBrush? Get(double aspect)
    {
        if (aspect <= 0.1 || double.IsNaN(aspect) || double.IsInfinity(aspect))
            return null;

        string? wallpaper = GetWallpaperPath();
        if (wallpaper is null)
            return null;

        string key = $"{wallpaper}|{aspect:0.000}";
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out ImageBrush? cached))
                return cached;
        }

        ImageBrush? brush = Create(wallpaper, aspect);
        lock (Cache)
        {
            if (Cache.Count > 8)
                Cache.Clear();
            Cache[key] = brush;
        }

        return brush;
    }

    public static string? GetWallpaperPath()
    {
        try
        {
            string? fromRegistry = Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;

            if (!string.IsNullOrWhiteSpace(fromRegistry) && File.Exists(fromRegistry))
                return fromRegistry;

            string fallback = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper", "Windows", "img0.jpg");
            return File.Exists(fallback) ? fallback : null;
        }
        catch
        {
            return null;
        }
    }

    private static ImageBrush? Create(string path, double aspect)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1024;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            int pixelWidth = image.PixelWidth;
            int pixelHeight = image.PixelHeight;
            if (pixelWidth < 8 || pixelHeight < 8)
                return null;

            int cropWidth, cropHeight;
            double sourceAspect = pixelWidth / (double)pixelHeight;
            if (sourceAspect > aspect)
            {
                cropHeight = pixelHeight;
                cropWidth = Math.Max(1, (int)(cropHeight * aspect));
            }
            else
            {
                cropWidth = pixelWidth;
                cropHeight = Math.Max(1, (int)(cropWidth / aspect));
            }

            int x = Math.Max(0, (pixelWidth - cropWidth) / 2);
            int y = Math.Max(0, (pixelHeight - cropHeight) / 2 - cropHeight / 18);

            var cropped = new CroppedBitmap(image, new Int32Rect(x, y, cropWidth, cropHeight));
            cropped.Freeze();

            const int targetWidth = 110;
            int targetHeight = Math.Max(10, (int)(targetWidth / aspect));

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
                dc.DrawImage(cropped, new Rect(0, 0, targetWidth, targetHeight));

            var bitmap = new RenderTargetBitmap(targetWidth, targetHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();

            var brush = new ImageBrush(bitmap)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center
            };
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }
}
