using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Nebula.Interop;

namespace Nebula.Services;

/// <summary>
/// Captures the still preview shown when the pointer rests on a taskbar icon
/// with more than one window. PrintWindow is tried first so a covered window
/// still previews correctly; a screen grab covers the apps whose surface is
/// composed instead of painted.
/// </summary>
public static class WindowThumbnail
{
    public static BitmapSource? Capture(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd))
            return null;

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT rect))
            return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 8 || height < 8)
            return null;

        try
        {
            using var source = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            bool captured = false;
            using (Graphics graphics = Graphics.FromImage(source))
            {
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    captured = NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_RENDERFULLCONTENT);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }

            if (!captured || IsBlank(source))
            {
                using Graphics graphics = Graphics.FromImage(source);
                graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0,
                    new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            }

            double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
            int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(height * scale));

            using var target = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(source, 0, 0, targetWidth, targetHeight);
            }

            IntPtr handle = target.GetHbitmap();
            try
            {
                BitmapSource image = Imaging.CreateBitmapSourceFromHBitmap(
                    handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally
            {
                NativeMethods.DeleteObject(handle);
            }
        }
        catch (Exception ex)
        {
            // A closed, elevated or protected window only loses its picture.
            Log.Write("Window thumbnail failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>A fully black capture means PrintWindow did not paint anything.</summary>
    private static bool IsBlank(Bitmap bitmap)
    {
        int step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 24);
        for (int y = 0; y < bitmap.Height; y += step)
        {
            for (int x = 0; x < bitmap.Width; x += step)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.R > 10 || color.G > 10 || color.B > 10)
                    return false;
            }
        }

        return true;
    }
}
