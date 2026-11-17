using System.IO;

namespace Nebula.Utilities;

internal static class PinSeeder
{
    public static List<string> DiscoverDefaults()
    {
        var pins = new List<string>();

        try
        {
            string pinnedFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

            if (Directory.Exists(pinnedFolder))
            {
                pins.AddRange(Directory
                    .EnumerateFiles(pinnedFolder, "*.lnk")
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                    .Take(10));
            }
        }
        catch
        {
            // Fall through to the sensible defaults below.
        }

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // Drop duplicates that point at the same executable (e.g. File Explorer.lnk + explorer.exe).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string pin in pins)
        {
            string key = ShortcutResolver.ResolveExecutable(pin) ?? pin;
            if (seen.Add(key))
                result.Add(pin);
        }

        void Ensure(string path)
        {
            if (!File.Exists(path))
                return;

            string key = ShortcutResolver.ResolveExecutable(path) ?? path;
            if (seen.Add(key))
                result.Add(path);
        }

        Ensure(Path.Combine(windows, "explorer.exe"));
        Ensure(Path.Combine(windows, "SystemSettings.exe"));

        return result;
    }
}
