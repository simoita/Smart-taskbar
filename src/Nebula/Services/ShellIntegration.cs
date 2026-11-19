using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Nebula.Services;

/// <summary>
/// Gives the portable executable the identity Windows expects from a program.
/// Without an AppUserModelID and a Start-menu entry the shell only sees an
/// anonymous tool window: Windows Search cannot find the app, Win+R does not
/// resolve its name and Alt+Tab never lists it.
/// </summary>
internal static class ShellIntegration
{
    private const string AppUserModelId = "Nebula.TaskbarStudio";

    /// <summary>Must run before the first window is created.</summary>
    public static void RegisterProcessId()
    {
        try
        {
            SetProcessAppUserModelId(AppUserModelId);
        }
        catch (Exception ex)
        {
            Log.Write("AppUserModelID registration failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Ensures the Start-menu entry and the App Paths key point at this copy of
    /// the app. Idempotent: an already correct registration is left untouched.
    /// </summary>
    public static void Register()
    {
        // The self test runs on a temporary profile and must not write to the
        // real Start menu or App Paths.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NEBULA_DATA_DIR")))
            return;

        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            return;

        try
        {
            RegisterAppPath(exe);
        }
        catch (Exception ex)
        {
            Log.Write("App Paths registration failed: " + ex.Message);
        }

        try
        {
            RegisterStartMenuShortcut(exe);
        }
        catch (Exception ex)
        {
            Log.Write("Start menu registration failed: " + ex.Message);
        }
    }

    private static void RegisterAppPath(string exe)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\Nebula.exe";
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, true)
                                ?? throw new InvalidOperationException("Chiave App Paths non scrivibile.");

        string directory = Path.GetDirectoryName(exe) ?? "";
        if (!string.Equals(key.GetValue(null, null) as string, exe, StringComparison.OrdinalIgnoreCase))
            key.SetValue("", exe, RegistryValueKind.String);
        if (!string.Equals(key.GetValue("Path", null) as string, directory, StringComparison.OrdinalIgnoreCase))
            key.SetValue("Path", directory, RegistryValueKind.String);
    }

    private static void RegisterStartMenuShortcut(string exe)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs))
            return;

        string path = Path.Combine(programs, "Nebula.lnk");
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return;

        object? shell = null;
        object? link = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return;

            dynamic shellLink = shell;
            link = shellLink.CreateShortcut(path);
            dynamic shortcut = link;

            if (File.Exists(path) &&
                string.Equals((string)shortcut.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
                return;

            shortcut.TargetPath = exe;
            shortcut.Arguments = "";
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            shortcut.Description = "Nebula Taskbar Studio";
            shortcut.IconLocation = exe + ",0";
            shortcut.Save();
            Log.Write("Start menu entry registered: " + path);
        }
        finally
        {
            Release(link);
            Release(shell);
        }
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
            // The OS releases the object at process exit anyway.
        }
    }

    [DllImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID",
        CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetProcessAppUserModelId(string appId);
}
