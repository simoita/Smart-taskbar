using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nebula.Services;

internal sealed record InstalledProgram(string Name, string Executable);

/// <summary>
/// Programs installed on the PC that never show up as a Start menu shortcut:
/// Chrome, Edge, Git, Python, drivers' utilities and per-user installs. The
/// uninstall keys give the display name and, in most cases, the executable to
/// start, which is what the search needs to find them.
/// </summary>
internal static class InstalledPrograms
{
    private static readonly string[] NoiseWords =
    {
        "redistributable", "runtime", "sdk", "add-in", "add-on", "language pack",
        "driver", "servizio", "service", "update", "hotfix", "frameview", "physx",
        "compatibility", "documentation", "developer pack", "targeting pack",
        "build tools", "web platform", "activation", "support tool",
        "web site", "pdf help", "manual", "module docs", "additional tools"
    };

    private static readonly string[] HelperExeWords =
    {
        "unins", "uninstall", "uninst", "setup", "installer", "updater", "update",
        "crash", "report", "helper", "cleanup", "remove", "service", "register",
        "activate", "elevate", "repair", "maintenance"
    };

    public static List<InstalledProgram> Enumerate(int max = 240)
    {
        var result = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? rawName, string? displayIcon, string? installLocation)
        {
            if (result.Count >= max)
                return;

            string name = CleanName(rawName);
            if (name.Length < 2 || LooksLikeNoise(name))
                return;

            string? executable = ResolveExecutable(displayIcon, installLocation, name);
            if (executable is null || !seen.Add(executable))
                return;

            result.Add(new InstalledProgram(name, executable));
        }

        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, Consider);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, Consider);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, Consider);

        // Portable and Electron installs that never register an uninstaller.
        foreach (string folder in AppFolders())
            ScanFolder(folder, Consider, seen);

        return result;
    }

    private static void ReadHive(RegistryHive hive, RegistryView view,
        Action<string?, string?, string?> consider)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? root = baseKey.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null)
                return;

            foreach (string keyName in root.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(keyName);
                    if (key is null)
                        continue;

                    if (Convert.ToInt32(key.GetValue("SystemComponent", 0)) == 1)
                        continue;
                    if (key.GetValue("ParentKeyName") is string { Length: > 0 })
                        continue;

                    consider(
                        key.GetValue("DisplayName") as string,
                        key.GetValue("DisplayIcon") as string,
                        key.GetValue("InstallLocation") as string);
                }
                catch
                {
                    // A single unreadable key must not stop the enumeration.
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Installed program scan failed: " + ex.Message);
        }
    }

    private static IEnumerable<string> AppFolders()
    {
        // Only the per-user install root: Program Files is already covered by the
        // uninstall keys, and walking it only added SDK tools and sample apps.
        string local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        if (Directory.Exists(local))
            yield return local;
    }

    /// <summary>
    /// Only folders whose own name matches the executable inside are accepted:
    /// that is the portable-app layout, and it keeps SDK or vendor folders that
    /// hold dozens of unrelated tools out of the search results.
    /// </summary>
    private static void ScanFolder(string root, Action<string?, string?, string?> consider,
        HashSet<string> seen)
    {
        try
        {
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                string folderName = Path.GetFileName(folder);
                if (folderName.StartsWith('@') || LooksLikeNoise(folderName))
                    continue;

                string? executable = FindMainExecutable(folder, folderName, exactOnly: true);
                if (executable is null || seen.Contains(executable))
                    continue;

                consider(folderName, executable, null);
            }
        }
        catch (Exception ex)
        {
            Log.Write("App folder scan failed for " + root + ": " + ex.Message);
        }
    }

    private static string? ResolveExecutable(string? displayIcon, string? installLocation, string name)
    {
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            string candidate = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
            int comma = candidate.LastIndexOf(',');
            if (comma > 2)
                candidate = candidate[..comma];
            candidate = candidate.Trim().Trim('"');

            if (IsLaunchable(candidate))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            string folder = Environment.ExpandEnvironmentVariables(installLocation.Trim()).Trim().Trim('"');
            if (Directory.Exists(folder))
            {
                string? match = FindMainExecutable(folder, name);
                if (match is not null)
                    return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks for the program's own executable: the one named after the program
    /// or after the folder it lives in. Uninstallers, updaters and helper
    /// binaries are never a launch target.
    /// </summary>
    private static string? FindMainExecutable(string folder, string name, bool exactOnly = false)
    {
        try
        {
            string wanted = Compact(name);
            string? best = null;
            int bestScore = -1;
            int inspected = 0;

            foreach (string file in EnumerateExecutables(folder))
            {
                if (++inspected > 400)
                    break;

                string stem = Path.GetFileNameWithoutExtension(file);
                if (IsHelperName(stem))
                    continue;

                string key = Compact(stem);
                int score = key.Length == 0 ? 0
                    : key == wanted ? 3
                    : wanted.Contains(key, StringComparison.Ordinal) ? 2
                    : !exactOnly && key.Contains(wanted, StringComparison.Ordinal) ? 1
                    : -1;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = file;
                }
            }

            return bestScore >= 0 ? best : null;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string folder)
    {
        const int maxDepth = 2;
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((folder, 0));

        while (queue.Count > 0)
        {
            (string current, int depth) = queue.Dequeue();
            string[] files;
            try
            {
                files = Directory.GetFiles(current, "*.exe");
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
                yield return file;

            if (depth >= maxDepth)
                continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string child in children)
            {
                // Keep the walk away from the big runtime trees of browsers and SDKs.
                string leaf = Path.GetFileName(child);
                if (leaf.Equals("resources", StringComparison.OrdinalIgnoreCase) ||
                    leaf.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;

                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool IsLaunchable(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path) &&
        !IsHelperName(Path.GetFileNameWithoutExtension(path)) &&
        !path.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase) &&
        !path.Contains(@"\Windows\WinSxS\", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelperName(string stem)
    {
        string lower = stem.ToLowerInvariant();
        return HelperExeWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
    }

    private static bool LooksLikeNoise(string name)
    {
        string lower = name.ToLowerInvariant();
        return NoiseWords.Any(word => lower.Contains(word, StringComparison.Ordinal));
    }

    /// <summary>Drops "(64-bit)", "versione 6.7.3" and trailing version numbers.</summary>
    private static string CleanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        string name = Regex.Replace(raw, @"\s*\([^)]*\)", " ");
        name = Regex.Replace(name, @"\s*[-–]?\s*versione\s+[\d.]+.*$", "",
            RegexOptions.IgnoreCase);
        name = Regex.Replace(name, @"[\s-]+v?\d+(\.\d+)*([+-][\w.]+)?\s*$", "");
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '-', '–');
        return name;
    }

    private static string Compact(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
