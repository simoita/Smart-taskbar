using System;
using System.IO;

namespace Nebula.Utilities;

internal static class ShortcutResolver
{
    private static readonly Dictionary<string, string?> TargetCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? Resolve(string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
            return null;

        lock (TargetCache)
        {
            if (TargetCache.TryGetValue(shortcutPath, out string? cached))
                return cached;
        }

        string? target = null;
        try
        {
            dynamic? link = CreateLink(shortcutPath);
            if (link is not null)
            {
                string value = (string)link.TargetPath;
                target = string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            target = null;
        }

        lock (TargetCache)
            TargetCache[shortcutPath] = target;
        return target;
    }

    /// <summary>Resolves the executable a pin actually starts (target or icon location).</summary>
    public static string? ResolveExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return path;

        return Resolve(path) ?? ResolveIconLocation(path);
    }

    public static string? ResolveIconLocation(string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
            return null;

        lock (IconCache)
        {
            if (IconCache.TryGetValue(shortcutPath, out string? cached))
                return cached;
        }

        string? iconFile = null;
        try
        {
            dynamic? link = CreateLink(shortcutPath);
            if (link is not null)
            {
                string location = (string)link.IconLocation;
                if (!string.IsNullOrWhiteSpace(location))
                {
                    string candidate = location;
                    int comma = candidate.LastIndexOf(',');
                    if (comma > 0)
                        candidate = candidate[..comma];

                    candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                    if (File.Exists(candidate))
                        iconFile = candidate;
                }
            }
        }
        catch
        {
            iconFile = null;
        }

        lock (IconCache)
            IconCache[shortcutPath] = iconFile;
        return iconFile;
    }

    /// <summary>Returns the exact IconLocation without resolving or normalising it.</summary>
    public static string ReadRawIconLocation(string shortcutPath)
    {
        dynamic? link = CreateLink(shortcutPath);
        if (link is null)
            throw new InvalidOperationException("COM di Windows non disponibile.");
        try
        {
            return (string)link.IconLocation ?? "";
        }
        finally
        {
            ReleaseCom(link);
        }
    }

    /// <summary>Writes IconLocation and invalidates the resolver caches.</summary>
    public static void WriteRawIconLocation(string shortcutPath, string value)
    {
        dynamic? link = CreateLink(shortcutPath);
        if (link is null)
            throw new InvalidOperationException("COM di Windows non disponibile.");
        try
        {
            link.IconLocation = value;
            link.Save();
        }
        finally
        {
            ReleaseCom(link);
        }

        lock (TargetCache)
            TargetCache.Remove(shortcutPath);
        lock (IconCache)
            IconCache.Remove(shortcutPath);
    }

    private static dynamic? CreateLink(string shortcutPath)
    {
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return null;

        dynamic shell = Activator.CreateInstance(shellType)!;
        return shell.CreateShortcut(shortcutPath);
    }

    private static void ReleaseCom(object value)
    {
        try
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(value))
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Best effort: the OS closes the COM object at process exit anyway.
        }
    }
}
