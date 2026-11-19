using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Nebula.Utilities;

namespace Nebula.Services;

/// <summary>
/// Shell actions shared by the taskbar menu and the Start panel: run elevated,
/// open the "Open with" dialog, reveal the file and start the app's own
/// uninstaller. Uninstall information comes from the per-user and machine-wide
/// Uninstall keys Windows already uses in Settings → App.
/// </summary>
internal static class AppActions
{
    public static bool RunAsAdmin(string? rawPath, out string? error)
    {
        error = null;
        string? target = ResolveTarget(rawPath);
        if (target is null)
        {
            error = "Nessun programma avviabile da questo collegamento.";
            return false;
        }

        try
        {
            var info = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(target) ?? ""
            };
            Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Richiesta di privilegi annullata.";
            return false;
        }
        catch (Exception ex)
        {
            error = "Impossibile avviare come amministratore: " + ex.Message;
            Log.Write("RunAsAdmin failed: " + ex);
            return false;
        }
    }

    /// <summary>Opens the Windows "Open with" chooser for the target.</summary>
    public static bool OpenWith(string? rawPath, out string? error)
    {
        error = null;
        string? target = ResolveTarget(rawPath) ?? rawPath;
        if (string.IsNullOrWhiteSpace(target))
        {
            error = "Percorso non valido.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe",
                "shell32.dll,OpenAs_RunDLL \"" + target + "\"")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = "Impossibile aprire la finestra Apri con: " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Starts the registered uninstaller for this app. When Windows has no
    /// uninstall entry (portable apps, shell targets) the Apps page of Settings
    /// opens instead of doing nothing.
    /// </summary>
    public static bool Uninstall(string? rawPath, string? displayName, out string? error)
    {
        error = null;
        string? target = ResolveTarget(rawPath);
        UninstallEntry? entry = FindUninstallEntry(target, displayName);

        if (entry is null)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:appsfeatures") { UseShellExecute = true });
                error = "Nessuna voce di disinstallazione trovata: ho aperto App installate di Windows.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Nessuna voce di disinstallazione trovata: " + ex.Message;
                return false;
            }
        }

        try
        {
            if (entry.IsMsi)
            {
                Process.Start(new ProcessStartInfo("msiexec.exe",
                    "/x " + entry.ProductCode + " /qb") { UseShellExecute = true });
            }
            else
            {
                ProcessStartInfo info = ParseCommand(entry.Command);
                info.UseShellExecute = true;
                Process.Start(info);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "Disinstallazione non avviata: " + ex.Message;
            Log.Write("Uninstall failed: " + ex);
            return false;
        }
    }

    public static string? ResolveTarget(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        string path = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            string? resolved = ShortcutResolver.ResolveExecutable(path);
            if (!string.IsNullOrWhiteSpace(resolved))
                path = resolved;
        }

        return File.Exists(path) ? path : null;
    }

    private static UninstallEntry? FindUninstallEntry(string? exePath, string? displayName)
    {
        string? folder = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
        string exeName = string.IsNullOrWhiteSpace(exePath) ? "" : Path.GetFileName(exePath);
        string wanted = (displayName ?? "").Trim();

        foreach ((RegistryKey root, string subKey) in UninstallKeys())
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(subKey);
                if (key is null)
                    continue;

                foreach (string name in key.GetSubKeyNames())
                {
                    using RegistryKey? app = key.OpenSubKey(name);
                    if (app is null)
                        continue;

                    string command = app.GetValue("UninstallString") as string ?? "";
                    if (string.IsNullOrWhiteSpace(command))
                        continue;

                    string appName = (app.GetValue("DisplayName") as string ?? "").Trim();
                    string icon = app.GetValue("DisplayIcon") as string ?? "";
                    string install = app.GetValue("InstallLocation") as string ?? "";

                    bool matchesIcon = exeName.Length > 2 &&
                                       icon.IndexOf(exeName, StringComparison.OrdinalIgnoreCase) >= 0;
                    bool matchesFolder = !string.IsNullOrWhiteSpace(folder) &&
                                         (install.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
                                          (!string.IsNullOrWhiteSpace(install) &&
                                           folder.StartsWith(install.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));
                    bool matchesName = wanted.Length > 2 &&
                                       appName.Equals(wanted, StringComparison.OrdinalIgnoreCase);

                    if (!matchesIcon && !matchesFolder && !matchesName)
                        continue;

                    // Prefer an exact name match over a coincidental path match.
                    if (!matchesName && !matchesIcon && !matchesFolder)
                        continue;

                    if (matchesIcon || matchesName || matchesFolder)
                    {
                        return new UninstallEntry(
                            command,
                            app.GetValue("ProductCode") as string ?? "",
                            !string.IsNullOrWhiteSpace(app.GetValue("ProductCode") as string) &&
                            command.Contains("msiexec", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch
            {
                // One unreadable key must not hide the others.
            }
        }

        return null;
    }

    private static IEnumerable<(RegistryKey Root, string SubKey)> UninstallKeys()
    {
        const string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        yield return (Registry.LocalMachine, sub);
        yield return (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        yield return (Registry.CurrentUser, sub);
    }

    private static ProcessStartInfo ParseCommand(string command)
    {
        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            if (end > 1)
            {
                string file = trimmed[1..end];
                string args = trimmed[(end + 1)..].Trim();
                return new ProcessStartInfo(file, args);
            }
        }

        int space = trimmed.IndexOf(' ');
        if (space > 0 && File.Exists(trimmed[..space]))
            return new ProcessStartInfo(trimmed[..space], trimmed[(space + 1)..].Trim());

        return new ProcessStartInfo(trimmed, "");
    }

    private sealed record UninstallEntry(string Command, string ProductCode, bool IsMsi);
}
