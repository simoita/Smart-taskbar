using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Nebula.Interop;

namespace Nebula.Services;

public sealed record TabRecoveryWindow(IntPtr Handle, NativeMethods.WINDOWPLACEMENT Placement);

/// <summary>
/// Crash-safe list of windows that Nebula has hidden for tab groups. The watchdog
/// and the next Nebula launch both read this file if the process disappears.
/// </summary>
public static class TabRecovery
{
    private const string Header = "NebulaTabs v1";
    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Nebula", "tabs.pending");

    public static void Save(IEnumerable<TabRecoveryWindow> windows)
    {
        lock (Gate)
        {
            try
            {
                var unique = windows
                    .Where(w => w.Handle != IntPtr.Zero)
                    .GroupBy(w => w.Handle)
                    .Select(g => g.First())
                    .ToList();

                if (unique.Count == 0)
                {
                    DeleteUnlocked();
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = new List<string> { Header };
                foreach (TabRecoveryWindow window in unique)
                {
                    NativeMethods.WINDOWPLACEMENT p = window.Placement;
                    lines.Add(string.Join("|",
                        "window",
                        window.Handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                        p.flags.ToString(CultureInfo.InvariantCulture),
                        p.showCmd.ToString(CultureInfo.InvariantCulture),
                        p.ptMinPosition.X.ToString(CultureInfo.InvariantCulture),
                        p.ptMinPosition.Y.ToString(CultureInfo.InvariantCulture),
                        p.ptMaxPosition.X.ToString(CultureInfo.InvariantCulture),
                        p.ptMaxPosition.Y.ToString(CultureInfo.InvariantCulture),
                        p.rcNormalPosition.Left.ToString(CultureInfo.InvariantCulture),
                        p.rcNormalPosition.Top.ToString(CultureInfo.InvariantCulture),
                        p.rcNormalPosition.Right.ToString(CultureInfo.InvariantCulture),
                        p.rcNormalPosition.Bottom.ToString(CultureInfo.InvariantCulture)));
                }

                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines);
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                Log.Write("Tab recovery save failed: " + ex.Message);
            }
        }
    }

    public static void Delete()
    {
        lock (Gate)
            DeleteUnlocked();
    }

    private static void DeleteUnlocked()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            Log.Write("Tab recovery delete failed: " + ex.Message);
        }
    }

    /// <summary>Restores orphaned grouped windows after an unclean Nebula exit.</summary>
    public static int RestorePending()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath))
                    return 0;

                string[] lines = File.ReadAllLines(FilePath);
                if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
                {
                    Log.Write("Tab recovery file has an unknown format; removing it.");
                    DeleteUnlocked();
                    return 0;
                }

                int restored = 0;
                bool complete = true;
                foreach (string line in lines.Skip(1))
                {
                    if (!TryParse(line, out TabRecoveryWindow? window) || window is null)
                        continue;

                    if (!NativeMethods.IsWindow(window.Handle))
                        continue;

                    NativeMethods.WINDOWPLACEMENT placement = window.Placement;
                    placement.length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
                    bool placed = NativeMethods.SetWindowPlacement(window.Handle, ref placement);
                    if (!placed || !NativeMethods.IsWindowVisible(window.Handle))
                    {
                        NativeMethods.ShowWindow(window.Handle, NativeMethods.SW_SHOW);
                    }

                    if (NativeMethods.IsWindowVisible(window.Handle))
                        restored++;
                    else
                        complete = false;
                }

                if (complete)
                    DeleteUnlocked();

                Log.Write($"Recovered {restored} grouped window(s) after unclean exit.");
                return restored;
            }
            catch (Exception ex)
            {
                Log.Write("Tab recovery failed: " + ex.Message);
                return 0;
            }
        }
    }

    private static bool TryParse(string line, out TabRecoveryWindow? window)
    {
        window = null;
        string[] parts = line.Split('|');
        if (parts.Length < 12 || parts[0] != "window")
            return false;

        try
        {
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long handle) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags) ||
                !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int show))
                return false;

            int[] rest = new int[8];
            for (int i = 0; i < rest.Length; i++)
            {
                if (!int.TryParse(parts[i + 4], NumberStyles.Integer, CultureInfo.InvariantCulture, out rest[i]))
                    return false;
            }

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                flags = flags,
                showCmd = show,
                ptMinPosition = new NativeMethods.POINT { X = rest[0], Y = rest[1] },
                ptMaxPosition = new NativeMethods.POINT { X = rest[2], Y = rest[3] },
                rcNormalPosition = new NativeMethods.RECT
                {
                    Left = rest[4],
                    Top = rest[5],
                    Right = rest[6],
                    Bottom = rest[7]
                }
            };

            window = new TabRecoveryWindow((IntPtr)handle, placement);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
