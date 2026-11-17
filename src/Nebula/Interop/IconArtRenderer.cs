using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

namespace Nebula.Interop;

/// <summary>
/// Renders shell icons into multi-resolution ICO files. Explorer receives every
/// size it asks for, so the result is never a single bitmap scaled at runtime.
/// </summary>
internal static class IconArtRenderer
{
    public static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };

    private const uint SHGFI_ICON = 0x000000100;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags,
        out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIconSize);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Bitmap LoadSource(string? location)
    {
        string path = "";
        int index = 0;
        try
        {
            (path, index) = ParseLocation(location);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                (path, index) = (Path.Combine(Environment.SystemDirectory, "shell32.dll"), 3);

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
            {
                using var loaded = Image.FromFile(path);
                return FitCanvas(loaded, 256);
            }

            Bitmap? shell = ExtractShellIcon(path, index);
            if (shell is not null)
                return shell;

            if (ext == ".ico")
            {
                using var icon = new Icon(path, new DrawingSize(256, 256));
                using Bitmap bitmap = icon.ToBitmap();
                return FitCanvas(bitmap, 256);
            }

            return GenericFallback();
        }
        catch
        {
            return GenericFallback();
        }
    }

    public static Bitmap GenericFallback()
    {
        var bitmap = new Bitmap(256, 256, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        using var brush = new LinearGradientBrush(
            new Rectangle(0, 0, 256, 256),
            Color.FromArgb(139, 92, 246),
            Color.FromArgb(34, 211, 238),
            45f);
        using GraphicsPath path = RoundedRect(new RectangleF(10, 10, 236, 236), 54);
        g.FillPath(brush, path);
        using var pen = new Pen(Color.FromArgb(120, 255, 255, 255), 4);
        g.DrawPath(pen, path);
        using var font = new Font("Segoe UI", 110, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("N", font, Brushes.White, new RectangleF(10, 14, 236, 236), format);
        return bitmap;
    }

    public static Bitmap Render(Bitmap source, string style, string shape, Color accent, int size)
    {
        size = Math.Clamp(size, 8, 512);
        var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(canvas);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingMode = CompositingMode.SourceOver;

        string normalized = NormalizeStyle(style);
        string normalizedShape = NormalizeShape(shape);

        if (normalized == "circle")
            normalizedShape = "circle";

        if (normalized is "tile" or "filled" or "circle" or "glass" or "neon")
        {
            DrawPlate(g, size, normalized, normalizedShape, accent);
            RectangleF iconRect = new(size * 0.205f, size * 0.205f, size * 0.59f, size * 0.59f);
            using GraphicsPath clip = PlatePath(
                new RectangleF(size * 0.145f, size * 0.145f, size * 0.71f, size * 0.71f),
                normalizedShape);
            GraphicsState state = g.Save();
            g.SetClip(clip);
            DrawFitted(g, source, iconRect);
            g.Restore(state);

            if (normalized == "neon")
            {
                using GraphicsPath ring = PlatePath(
                    new RectangleF(size * 0.025f, size * 0.025f, size * 0.95f, size * 0.95f),
                    normalizedShape);
                using var outerPen = new Pen(Color.FromArgb(48, accent.R, accent.G, accent.B), Math.Max(1f, size * 0.075f));
                using var innerPen = new Pen(Color.FromArgb(210, accent.R, accent.G, accent.B), Math.Max(1f, size * 0.018f));
                g.DrawPath(outerPen, ring);
                g.DrawPath(innerPen, ring);
            }

            return canvas;
        }

        RectangleF freeRect = new(size * 0.075f, size * 0.075f, size * 0.85f, size * 0.85f);
        using Bitmap scaled = ScaleFitted(source, (int)Math.Round(freeRect.Width));

        switch (normalized)
        {
            case "mono":
                using (Bitmap mono = MakeMono(scaled, accent))
                    g.DrawImage(mono, freeRect);
                break;

            case "duotone":
                using (Bitmap duo = MakeDuotone(scaled, accent))
                    g.DrawImage(duo, freeRect);
                break;

            case "outline":
            {
                using Bitmap outline = MakeOutline(scaled, Color.FromArgb(245, accent.R, accent.G, accent.B), 1.3f);
                g.DrawImage(outline, freeRect);
                g.DrawImage(scaled, freeRect);
                break;
            }

            case "neon":
            {
                using Bitmap glow = MakeOutline(scaled, Color.FromArgb(80, accent.R, accent.G, accent.B), 3.4f);
                using Bitmap edge = MakeOutline(scaled, Color.FromArgb(240, accent.R, accent.G, accent.B), 1.1f);
                g.DrawImage(glow, freeRect);
                g.DrawImage(edge, freeRect);
                g.DrawImage(scaled, freeRect);
                break;
            }

            default:
                g.DrawImage(scaled, freeRect);
                break;
        }

        return canvas;
    }

    public static void WriteIco(Bitmap source, string style, string shape, Color accent, string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            var frames = new List<(int Size, byte[] Bytes)>();
            foreach (int size in Sizes)
            {
                using Bitmap bitmap = Render(source, style, shape, accent, size);
                frames.Add((size, EncodeFrame(bitmap, size)));
            }

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)frames.Count);

                int offset = 6 + (16 * frames.Count);
                foreach ((int size, byte[] bytes) in frames)
                {
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)(size >= 256 ? 0 : size));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)bytes.Length);
                    writer.Write((uint)offset);
                    offset += bytes.Length;
                }

                foreach ((_, byte[] bytes) in frames)
                    writer.Write(bytes);
            }

            File.Move(temp, path, true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static IReadOnlyList<int> ReadIcoSizes(string path)
    {
        var sizes = new List<int>();
        using FileStream stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 6)
            return sizes;

        reader.ReadUInt16();
        ushort type = reader.ReadUInt16();
        ushort count = reader.ReadUInt16();
        if (type != 1)
            return sizes;

        for (int i = 0; i < count && stream.Position + 16 <= stream.Length; i++)
        {
            int width = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            reader.ReadUInt16();
            reader.ReadUInt32();
            reader.ReadUInt32();
            sizes.Add(width == 0 ? 256 : width);
        }

        return sizes;
    }

    public static BitmapSource ToImageSource(Bitmap bitmap)
    {
        // BitmapSource is created on the UI thread from a detached copy; the
        // caller can dispose its GDI+ bitmap immediately.
        using var copy = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(copy))
            g.DrawImageUnscaled(bitmap, DrawingPoint.Empty);

        IntPtr hBitmap = copy.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void DrawPlate(Graphics g, int size, string style, string shape, Color accent)
    {
        RectangleF rect = new(0.01f * size, 0.01f * size, 0.98f * size, 0.98f * size);
        using GraphicsPath path = PlatePath(rect, shape);

        Color top = style == "glass"
            ? Color.FromArgb(238, 22, 19, 37)
            : Color.FromArgb(255, Math.Min(255, accent.R + 34), Math.Min(255, accent.G + 26), Math.Min(255, accent.B + 40));
        Color bottom = style == "glass"
            ? Color.FromArgb(245, 10, 8, 20)
            : Color.FromArgb(255, Math.Max(0, accent.R - 44), Math.Max(0, accent.G - 50), Math.Max(0, accent.B - 30));

        using (var fill = new LinearGradientBrush(rect, top, bottom, 48f))
            g.FillPath(fill, path);

        if (style == "glass" || style == "neon" || style == "filled" || style == "tile" || style == "circle")
        {
            float inset = size * 0.055f;
            using GraphicsPath inner = PlatePath(
                new RectangleF(rect.X + inset, rect.Y + inset, rect.Width - inset * 2, rect.Height - inset * 2),
                shape);
            using var shade = new SolidBrush(style is "filled" or "tile" or "circle"
                ? Color.FromArgb(46, 0, 0, 0)
                : Color.FromArgb(16, 255, 255, 255));
            g.FillPath(shade, inner);
            using var border = new Pen(Color.FromArgb(style == "glass" ? 110 : 80, 255, 255, 255), Math.Max(0.8f, size * 0.006f));
            g.DrawPath(border, inner);
        }

        using var rim = new Pen(Color.FromArgb(style == "glass" ? 150 : 105, 255, 255, 255), Math.Max(0.8f, size * 0.009f));
        g.DrawPath(rim, path);
    }

    private static void DrawFitted(Graphics g, Bitmap source, RectangleF rect)
    {
        float scale = Math.Min(rect.Width / source.Width, rect.Height / source.Height);
        float width = source.Width * scale;
        float height = source.Height * scale;
        float x = rect.X + (rect.Width - width) / 2;
        float y = rect.Y + (rect.Height - height) / 2;
        g.DrawImage(source, x, y, width, height);
    }

    private static Bitmap ScaleFitted(Bitmap source, int side)
    {
        var result = new Bitmap(Math.Max(1, side), Math.Max(1, side), PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawFitted(g, source, new RectangleF(0, 0, result.Width, result.Height));
        return result;
    }

    private static Bitmap MakeMono(Bitmap source, Color accent)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        g.DrawImageUnscaled(source, DrawingPoint.Empty);

        BitmapData data = result.LockBits(
            new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < result.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < result.Width; x++)
                    {
                        byte b = row[x * 4];
                        byte gch = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        byte a = row[x * 4 + 3];
                        if (a == 0)
                            continue;

                        int luminance = (r * 30 + gch * 59 + b * 11) / 100;
                        int value = Math.Clamp(120 + luminance / 2, 0, 255);
                        row[x * 4] = (byte)value;
                        row[x * 4 + 1] = (byte)value;
                        row[x * 4 + 2] = (byte)value;
                    }
                }
            }
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    private static Bitmap MakeDuotone(Bitmap source, Color accent)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        g.DrawImageUnscaled(source, DrawingPoint.Empty);

        BitmapData data = result.LockBits(
            new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < result.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < result.Width; x++)
                    {
                        byte b = row[x * 4];
                        byte gch = row[x * 4 + 1];
                        byte r = row[x * 4 + 2];
                        byte a = row[x * 4 + 3];
                        if (a == 0)
                            continue;

                        int lum = (r * 30 + gch * 59 + b * 11) / 100;
                        double t = lum / 255.0;
                        row[x * 4] = (byte)(accent.B * (0.30 + t * 0.70));
                        row[x * 4 + 1] = (byte)(accent.G * (0.30 + t * 0.70));
                        row[x * 4 + 2] = (byte)(accent.R * (0.30 + t * 0.70));
                    }
                }
            }
        }
        finally
        {
            result.UnlockBits(data);
        }

        return result;
    }

    private static Bitmap MakeOutline(Bitmap source, Color color, float radius)
    {
        int width = source.Width;
        int height = source.Height;
        var outline = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        int reach = Math.Max(1, (int)Math.Ceiling(radius));

        BitmapData srcData = source.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData dstData = outline.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* src = (byte*)srcData.Scan0;
                byte* dst = (byte*)dstData.Scan0;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int alpha = src[y * srcData.Stride + x * 4 + 3];
                        if (alpha > 22)
                        {
                            dst[y * dstData.Stride + x * 4 + 3] = 0;
                            continue;
                        }

                        int best = 0;
                        for (int oy = -reach; oy <= reach; oy++)
                        {
                            int ny = y + oy;
                            if (ny < 0 || ny >= height)
                                continue;
                            for (int ox = -reach; ox <= reach; ox++)
                            {
                                int nx = x + ox;
                                if (nx < 0 || nx >= width)
                                    continue;
                                if (ox * ox + oy * oy > reach * reach + 1)
                                    continue;

                                int near = src[ny * srcData.Stride + nx * 4 + 3];
                                if (near > best)
                                    best = near;
                            }
                        }

                        if (best > 35)
                        {
                            dst[y * dstData.Stride + x * 4] = color.B;
                            dst[y * dstData.Stride + x * 4 + 1] = color.G;
                            dst[y * dstData.Stride + x * 4 + 2] = color.R;
                            dst[y * dstData.Stride + x * 4 + 3] = (byte)Math.Min(255, color.A * best / 255);
                        }
                    }
                }
            }
        }
        finally
        {
            source.UnlockBits(srcData);
            outline.UnlockBits(dstData);
        }

        return outline;
    }

    private static byte[] EncodeFrame(Bitmap bitmap, int size)
    {
        if (size >= 256)
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }

        using var converted = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(converted))
        {
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(bitmap, new Rectangle(0, 0, size, size));
        }

        int maskStride = ((size + 31) / 32) * 4;
        int xorSize = size * size * 4;
        int maskSize = maskStride * size;
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(xorSize + maskSize);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        BitmapData data = converted.LockBits(
            new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            byte[] row = new byte[size * 4];
            for (int y = size - 1; y >= 0; y--)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), row, 0, row.Length);
                writer.Write(row);
            }
        }
        finally
        {
            converted.UnlockBits(data);
        }

        var mask = new byte[maskSize];
        data = converted.LockBits(
            new Rectangle(0, 0, size, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < size; y++)
            {
                int outputRow = size - 1 - y;
                for (int x = 0; x < size; x++)
                {
                    byte alpha = Marshal.ReadByte(data.Scan0, y * data.Stride + x * 4 + 3);
                    if (alpha < 16)
                        mask[outputRow * maskStride + x / 8] |= (byte)(1 << (7 - (x % 8)));
                }
            }
        }
        finally
        {
            converted.UnlockBits(data);
        }

        writer.Write(mask);
        writer.Flush();
        return output.ToArray();
    }

    private static Bitmap FitCanvas(Image image, int side)
    {
        var result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min((float)side / image.Width, (float)side / image.Height);
        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
        g.DrawImage(image, (side - width) / 2, (side - height) / 2, width, height);
        return result;
    }

    private static Bitmap? ExtractShellIcon(string path, int index)
    {
        IntPtr large = IntPtr.Zero;
        IntPtr small = IntPtr.Zero;
        try
        {
            uint packed = (uint)(256 | (16 << 16));
            int result = SHDefExtractIcon(path, index, 0, out large, out small, packed);
            if (result == 0 && large != IntPtr.Zero)
                return FromHIcon(large, 256);
        }
        catch
        {
            // Fall through to the legacy extractor.
        }
        finally
        {
            if (small != IntPtr.Zero)
                DestroyIcon(small);
            if (large != IntPtr.Zero)
                DestroyIcon(large);
        }

        IntPtr[] icons = new IntPtr[1];
        try
        {
            uint count = ExtractIconEx(path, index, icons, null, 1);
            if (count > 0 && icons[0] != IntPtr.Zero)
                return FromHIconCopy(icons[0]);
        }
        catch
        {
            // The fallback below is a generic icon.
        }
        finally
        {
            if (icons[0] != IntPtr.Zero)
                DestroyIcon(icons[0]);
        }

        return null;
    }

    private static Bitmap FromHIcon(IntPtr handle, int side)
    {
        using var icon = Icon.FromHandle(handle);
        using Bitmap bitmap = icon.ToBitmap();
        return FitCanvas(bitmap, side);
    }

    private static Bitmap FromHIconCopy(IntPtr handle)
    {
        using var icon = Icon.FromHandle(handle);
        using Bitmap bitmap = icon.ToBitmap();
        return FitCanvas(bitmap, 256);
    }

    private static (string Path, int Index) ParseLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return ("", 0);

        string value = Environment.ExpandEnvironmentVariables(location.Trim());
        int comma = value.LastIndexOf(',');
        int index = 0;
        string path = value;
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out int parsed))
        {
            index = parsed;
            path = value[..comma].Trim();
        }

        path = path.Trim().Trim('"');
        if (path.Length > 0 && !Path.IsPathRooted(path))
        {
            string system = Path.Combine(Environment.SystemDirectory, path);
            if (File.Exists(system))
                path = system;
        }

        return (path, index);
    }

    private static GraphicsPath PlatePath(RectangleF rect, string shape)
    {
        return shape switch
        {
            "circle" => Ellipse(rect),
            "square" => Rect(rect),
            "squircle" => RoundedRect(rect, Math.Min(rect.Width, rect.Height) * 0.34f),
            _ => RoundedRect(rect, Math.Min(rect.Width, rect.Height) * 0.22f)
        };
    }

    private static GraphicsPath Rect(RectangleF rect)
    {
        var path = new GraphicsPath();
        path.AddRectangle(rect);
        return path;
    }

    private static GraphicsPath Ellipse(RectangleF rect)
    {
        var path = new GraphicsPath();
        path.AddEllipse(rect);
        return path;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static string NormalizeStyle(string? style) => style?.Trim().ToLowerInvariant() switch
    {
        "tile" => "tile",
        "filled" => "filled",
        "circle" => "circle",
        "glass" => "glass",
        "mono" or "monochrome" => "mono",
        "outline" => "outline",
        "neon" => "neon",
        "duotone" => "duotone",
        _ => "native"
    };

    private static string NormalizeShape(string? shape) => shape?.Trim().ToLowerInvariant() switch
    {
        "circle" => "circle",
        "square" => "square",
        "squircle" => "squircle",
        _ => "rounded"
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }
}
