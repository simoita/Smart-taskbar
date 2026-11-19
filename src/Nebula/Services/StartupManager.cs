using Microsoft.Win32;

namespace Nebula.Services;

internal static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Nebula";

    /// <summary>True when the current user already has a Nebula autostart entry.</summary>
    public static bool IsRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch (Exception ex)
        {
            Log.Write("Startup registration check failed: " + ex.Message);
            return false;
        }
    }

    public static void Apply(bool enabled)
    {
        // Diagnostics and the self test run with an isolated profile. They must
        // never add or remove the real user's autostart entry.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEBULA_DATA_DIR")))
            return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null)
                return;

            if (enabled)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exe))
                    key.SetValue(ValueName, $"\"{exe}\" --startup");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Startup registration failed: " + ex.Message);
        }
    }
}
