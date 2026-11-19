using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Nebula.Services;

internal sealed record ShellApp(string Name, string LaunchTarget);

/// <summary>
/// Reads the shell's app namespace, which includes packaged (Store/UWP) apps
/// that never appear as .lnk files in the Start menu folders.
/// </summary>
internal static class ShellApps
{
    public static List<ShellApp> Enumerate(int max = 400)
    {
        var result = new List<ShellApp>();
        Exception? failure = null;

        // Shell.Application is an STA COM object; the catalog scan runs on a
        // worker thread, so own a short-lived STA thread for the enumeration.
        var thread = new Thread(() =>
        {
            object? shell = null;
            object? folder = null;
            object? items = null;
            try
            {
                Type? type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null)
                    return;

                shell = Activator.CreateInstance(type);
                if (shell is null)
                    return;

                dynamic shellObject = shell;
                folder = shellObject.NameSpace("shell:AppsFolder");
                if (folder is null)
                    return;

                dynamic folderObject = folder;
                items = folderObject.Items();
                if (items is null)
                    return;

                dynamic itemCollection = items;
                int count = Math.Min(itemCollection.Count, max);
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = itemCollection.Item(i);
                        if (item is null)
                            continue;

                        dynamic entry = item;
                        string name = (entry.Name as string ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        string appId = "";
                        try
                        {
                            appId = entry.ExtendedProperty("System.AppUserModel.ID") as string ?? "";
                        }
                        catch
                        {
                            // Some shell items do not expose the property.
                        }

                        string rawPath = "";
                        try
                        {
                            rawPath = entry.Path as string ?? "";
                        }
                        catch
                        {
                            // Packaged apps can fail Path resolution on some builds.
                        }

                        string target;
                        if (!string.IsNullOrWhiteSpace(appId))
                            target = "shell:AppsFolder\\" + appId;
                        else if (!string.IsNullOrWhiteSpace(rawPath) && rawPath.Contains('!'))
                            target = "shell:AppsFolder\\" + rawPath;
                        else if (!string.IsNullOrWhiteSpace(rawPath) && File.Exists(rawPath))
                            target = rawPath;
                        else
                            continue;

                        result.Add(new ShellApp(name, target));
                    }
                    catch
                    {
                        // A single broken shell item must not break the scan.
                    }
                    finally
                    {
                        Release(item);
                    }
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Release(items);
                Release(folder);
                Release(shell);
            }
        })
        {
            IsBackground = true,
            Name = "Nebula shell apps scan"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // The Start menu must not hang on a shell provider. If the scan takes
        // longer, the .lnk catalog is still complete enough for this session.
        if (!thread.Join(TimeSpan.FromSeconds(6)))
            Log.Write("Shell app scan timed out; packaged apps may be incomplete.");
        else if (failure is not null)
            Log.Write("Shell app scan failed: " + failure.Message);

        return result
            .GroupBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static void Release(object? value)
    {
        try
        {
            if (value is not null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch
        {
            // Best effort; the process releases the remaining objects at exit.
        }
    }
}
