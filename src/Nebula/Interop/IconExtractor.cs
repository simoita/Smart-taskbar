using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nebula.Interop;

/// <summary>Extracts crisp shell icons (64px) from files, shortcuts and windows.</summary>
internal static class IconExtractor
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [Flags]
    private enum SIIGBF : uint
    {
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
        InCacheOnly = 0x10,
        CropToSquare = 0x20
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string file, int iconIndex, IntPtr[]? large, IntPtr[]? small, uint count);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    /// <summary>
    /// Loads the icon the shell shows for a path. The path does not have to be
    /// a real file: packaged (Store/UWP) apps are addressed as
    /// "shell:AppsFolder\AUMID" and the Settings pages as "ms-settings:…",
    /// and the shell resolves both. Requiring File.Exists here was why almost
    /// every preinstalled app had no icon at all.
    /// </summary>
    public static ImageSource? FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string location = path.Trim().Trim('@').Trim('"');
        location = Environment.ExpandEnvironmentVariables(location);
        if (location.Length == 0)
            return null;

        // Shortcuts and the uninstall registry describe their icon as
        // "file,index" (or "file,-resourceId"): the index selects the artwork.
        string file = location;
        int? index = null;
        int comma = location.LastIndexOf(',');
        if (comma > 2 && int.TryParse(location[(comma + 1)..].Trim(), out int parsed))
        {
            file = location[..comma].Trim().Trim('"');
            index = parsed;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(location, out ImageSource? cached))
                return cached;
        }

        ImageSource? icon = index is int forced
            ? LoadIndexedIcon(file, forced) ?? LoadShellImage(file)
            : null;

        icon ??= LoadShellImage(file) ?? LoadShellIcon(file) ?? LoadAssociatedIcon(file);
        if (icon is not null)
        {
            lock (Gate)
                Cache[location] = icon;
        }

        return icon;
    }

    /// <summary>Reads the index specified inside an IconLocation value.</summary>
    private static ImageSource? LoadIndexedIcon(string file, int index)
    {
        if (!File.Exists(file))
            return null;

        try
        {
            var large = new IntPtr[1];
            if (ExtractIconEx(file, index, large, null, 1) == 0 || large[0] == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                NativeMethods.DestroyIcon(large[0]);
            }
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource? FromWindow(IntPtr hwnd, string? fallbackPath)
    {
        try
        {
            IntPtr hIcon = NativeMethods.SendMessage(hwnd, NativeMethods.WM_GETICON, (IntPtr)NativeMethods.ICON_BIG, IntPtr.Zero);
            if (hIcon == IntPtr.Zero)
                hIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
            if (hIcon != IntPtr.Zero)
            {
                ImageSource source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
        }
        catch
        {
            // Fall back to the executable icon.
        }

        return FromPath(fallbackPath);
    }

    private static ImageSource? LoadShellImage(string path)
    {
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, iid, out object item);
            try
            {
                if (item is not IShellItemImageFactory factory)
                    return null;

                var size = new SIZE { cx = 64, cy = 64 };
                int hr = factory.GetImage(size, SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out IntPtr hBitmap);
                if (hr != 0 || hBitmap == IntPtr.Zero)
                    return null;

                try
                {
                    BitmapSource bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    return bitmap;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
            }
            finally
            {
                // The shell item is a COM object: without an explicit release
                // every extracted icon keeps it alive for the whole session.
                if (Marshal.IsComObject(item))
                    Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadAssociatedIcon(string path)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
                return null;

            BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadShellIcon(string path)
    {
        // SHGetFileInfo answers with a made-up icon for anything it cannot
        // find; only ask it about names the shell can actually look up.
        if (!File.Exists(path) && !Directory.Exists(path) &&
            !path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("::{", StringComparison.Ordinal))
            return null;

        try
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
