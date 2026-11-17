using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Nebula.Interop;
using Nebula.Models;
using Nebula.Utilities;

namespace Nebula.Services;

/// <summary>
/// Applies icon art to per-user Windows shell surfaces only: shortcut files,
/// HKCU\Software\Classes default icons and the user's taskbar pins. No system
/// binary is patched and no machine-wide (HKLM) value is ever written.
/// </summary>
public sealed class SystemIconService
{
    private static readonly string[] CommonExtensions =
    {
        ".txt", ".md", ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg",
        ".mp3", ".wav", ".flac", ".mp4", ".mkv", ".avi", ".doc", ".docx", ".xls",
        ".xlsx", ".ppt", ".pptx", ".zip", ".rar", ".7z", ".iso", ".exe", ".msi",
        ".html", ".htm", ".json", ".csv", ".py", ".cs", ".js", ".psd"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly object Gate = new();
    private readonly Dictionary<string, string> _buildCache = new(StringComparer.OrdinalIgnoreCase);

    public SystemIconService()
    {
        RootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula", "system-icons");
        IconsDirectory = Path.Combine(RootDirectory, "icons");
        StagingDirectory = Path.Combine(RootDirectory, "staging");
        BackupPath = Path.Combine(RootDirectory, "backup.json");
    }

    public string RootDirectory { get; }
    public string IconsDirectory { get; }
    public string StagingDirectory { get; }
    public string BackupPath { get; }

    public bool HasBackup => File.Exists(BackupPath);

    public int AppliedCount
    {
        get
        {
            SystemIconBackup? backup = LoadBackup();
            return backup?.Operations.Count ?? 0;
        }
    }

    public SystemIconBackup? GetBackup() => LoadBackup();

    public IReadOnlyList<SystemIconTarget> DiscoverTargets()
    {
        var targets = new List<SystemIconTarget>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SystemIconBackup? backup = LoadBackup();

        void AddShortcut(string path, string group)
        {
            try
            {
                if (!File.Exists(path) || !seen.Add(path))
                    return;

                string name = Path.GetFileNameWithoutExtension(path);
                string? target = ShortcutResolver.Resolve(path);
                string? source = null;
                try
                {
                    string raw = ShortcutResolver.ReadRawIconLocation(path);
                    string? candidate = ExpandIconPath(raw);
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        source = raw;
                }
                catch
                {
                    // A broken shortcut can still be shown, using its target.
                }

                source ??= File.Exists(target) ? target : path;
                SystemIconOperation? operation = FindOperation(backup, ShortcutId(path));
                if (operation is not null && !string.IsNullOrWhiteSpace(operation.SourceIcon))
                    source = operation.SourceIcon;

                targets.Add(new SystemIconTarget
                {
                    Id = ShortcutId(path),
                    Kind = SystemIconTargetKind.Shortcut,
                    DisplayName = name,
                    Detail = group,
                    ShortcutPath = path,
                    SourceIcon = source
                });
            }
            catch (Exception ex)
            {
                Log.Write("System icon shortcut scan failed: " + ex.Message);
            }
        }

        void AddShortcutFolder(string folder, string group, bool recursive)
        {
            try
            {
                if (!Directory.Exists(folder))
                    return;

                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.System
                };
                foreach (string path in Directory.EnumerateFiles(folder, "*.lnk", options))
                {
                    string detail = group;
                    if (recursive)
                    {
                        string relative = Path.GetRelativePath(folder, Path.GetDirectoryName(path) ?? folder);
                        if (relative != ".")
                            detail += " · " + relative;
                    }
                    AddShortcut(path, detail);
                }
            }
            catch (Exception ex)
            {
                Log.Write("System icon folder scan failed: " + ex.Message);
            }
        }

        AddShortcutFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop", false);
        AddShortcutFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Start menu", true);
        AddShortcutFolder(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"), "Taskbar", false);

        foreach (string extension in CommonExtensions)
        {
            try
            {
                if (seen.Contains("file:" + extension))
                    continue;

                string? progId = ResolveProgId(extension);
                string classPath = string.IsNullOrWhiteSpace(progId) ? extension : progId;
                string keyPath = @"Software\Classes\" + classPath + @"\DefaultIcon";
                SystemIconOperation? operation = FindOperation(backup, "file:" + extension);
                string? source = operation?.SourceIcon ?? ResolveFileTypeSource(extension, progId);
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                seen.Add("file:" + extension);
                targets.Add(new SystemIconTarget
                {
                    Id = "file:" + extension,
                    Kind = SystemIconTargetKind.FileType,
                    DisplayName = extension,
                    Detail = "Tipo file" + (string.IsNullOrWhiteSpace(classPath) ? "" : " · " + classPath),
                    RegistryKey = keyPath,
                    SourceIcon = source
                });
            }
            catch (Exception ex)
            {
                Log.Write("System icon association scan failed: " + ex.Message);
            }
        }

        // Include file types the user has already personalised in HKCU, even
        // when they are outside the built-in catalogue.
        try
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(@"Software\Classes");
            if (classes is not null)
            {
                foreach (string name in classes.GetSubKeyNames())
                {
                    if (!name.StartsWith(".", StringComparison.Ordinal) || seen.Contains("file:" + name))
                        continue;

                    string? progId = ResolveProgId(name);
                    string classPath = string.IsNullOrWhiteSpace(progId) ? name : progId;
                    string key = @"Software\Classes\" + classPath + @"\DefaultIcon";
                    using RegistryKey? iconKey = Registry.CurrentUser.OpenSubKey(key);
                    string? value = iconKey?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    SystemIconOperation? operation = FindOperation(backup, "file:" + name);
                    seen.Add("file:" + name);
                    targets.Add(new SystemIconTarget
                    {
                        Id = "file:" + name,
                        Kind = SystemIconTargetKind.FileType,
                        DisplayName = name,
                        Detail = "Tipo file · personale" + (string.IsNullOrWhiteSpace(classPath) ? "" : " · " + classPath),
                        RegistryKey = key,
                        SourceIcon = operation?.SourceIcon ?? value
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("System icon HKCU scan failed: " + ex.Message);
        }

        // Windows 11 ignores the global Folder/Shell Icons registry keys for
        // the default folder artwork. The supported per-user surface is the
        // folder's own desktop.ini: only the user's known shell folders are
        // offered here, and the original file is backed up byte for byte.
        void AddUserFolder(string label, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            string path = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string id = "folder:" + path.ToLowerInvariant();
            if (!seen.Add(id))
                return;
            SystemIconOperation? operation = FindOperation(backup, id);
            targets.Add(new SystemIconTarget
            {
                Id = id,
                Kind = SystemIconTargetKind.Folder,
                DisplayName = "Cartella " + label,
                Detail = "Cartella utente · desktop.ini",
                FolderPath = path,
                SourceIcon = operation?.SourceIcon
                              ?? Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",3"
            });
        }

        AddUserFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddUserFolder("Documenti", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        AddUserFolder("Download", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        AddUserFolder("Immagini", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddUserFolder("Musica", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddUserFolder("Video", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

        return targets;
    }

    public Task<SystemIconApplyOutcome> ApplyAsync(
        IReadOnlyList<SystemIconTarget> targets,
        SystemIconChoice choice,
        Action<string>? progress = null)
    {
        return Task.Run(() => Apply(targets, choice, progress));
    }

    public SystemIconApplyOutcome Apply(
        IReadOnlyList<SystemIconTarget> targets,
        SystemIconChoice choice,
        Action<string>? progress = null)
    {
        lock (Gate)
        {
            try
            {
                if (targets.Count == 0)
                    return new SystemIconApplyOutcome(false, 0, "Nessuna voce selezionata.");

                string style = NormalizeStyle(choice.Style);
                if (style == "native")
                    return RestoreCore(targets, progress);

                if (!File.Exists(choice.CustomPath) && !string.IsNullOrWhiteSpace(choice.CustomPath))
                    return new SystemIconApplyOutcome(false, 0, "Il file icona scelto non esiste più.");
                if (!string.IsNullOrWhiteSpace(choice.CustomPath) && targets.Count != 1)
                    return new SystemIconApplyOutcome(false, 0, "Un'icona personale si applica a una voce alla volta.");

                string? custom = string.IsNullOrWhiteSpace(choice.CustomPath) ? null : choice.CustomPath;
                if (custom is not null && !IsSupportedImage(custom))
                    return new SystemIconApplyOutcome(false, 0, "Formato non supportato. Usa .ico, .png, .jpg, .jpeg o .exe.");

                Directory.CreateDirectory(IconsDirectory);
                Directory.CreateDirectory(StagingDirectory);
                SystemIconBackup backup = LoadBackup() ?? new SystemIconBackup();
                if (!backup.Completed && backup.Operations.Count > 0)
                {
                    SystemIconApplyOutcome recovery = RestoreCore(null, progress);
                    if (!recovery.Success)
                        return recovery;
                    backup = new SystemIconBackup();
                }

                var batch = new List<SystemIconOperation>();
                foreach (SystemIconTarget target in targets)
                {
                    SystemIconOperation? existing = backup.Operations.FirstOrDefault(
                        o => string.Equals(o.Id, target.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        existing = Capture(target);
                        backup.Operations.Add(existing);
                    }

                    batch.Add(existing);
                }

                // The journal is durable before the first icon, shortcut or
                // registry value is touched.
                backup.Completed = false;
                SaveBackup(backup);

                Color accent = GetAccent();
                int changed = 0;
                foreach (SystemIconOperation operation in batch)
                {
                    progress?.Invoke("Genero " + operation.DisplayName + "…");
                    string source = custom ?? operation.SourceIcon ?? "";
                    if (!SourceExists(source))
                    {
                        source = Path.Combine(Environment.SystemDirectory, "shell32.dll") + ",3";
                        operation.SourceIcon ??= source;
                    }

                    string ico = GetOrBuildIcon(source, style, choice.Shape, accent);
                    string applied = ico + ",0";
                    ApplyOperation(operation, applied);
                    VerifyApplied(operation, applied);

                    operation.Style = style;
                    operation.Shape = NormalizeShape(choice.Shape);
                    operation.CustomSource = custom;
                    operation.GeneratedIconPath = ico;
                    operation.AppliedValue = applied;
                    operation.AppliedUtc = DateTimeOffset.UtcNow;
                    NotifyShortcut(operation.ShortcutPath);

                    backup.Completed = false;
                    SaveBackup(backup);
                    changed++;
                }

                backup.Completed = true;
                SaveBackup(backup);
                CleanupUnreferencedIcons(backup);
                NotifyShell();
                progress?.Invoke("Icone applicate: " + changed);
                return new SystemIconApplyOutcome(true, changed,
                    $"Applicate {changed} icone. Backup salvato in {BackupPath}.");
            }
            catch (Exception ex)
            {
                Log.Write("System icon apply failed: " + ex);
                try
                {
                    SystemIconBackup? broken = LoadBackup();
                    if (broken is not null)
                    {
                        SystemIconApplyOutcome rollback = RestoreOperations(broken, broken.Operations, progress);
                        if (rollback.Success)
                        {
                            DeleteBackup();
                            CleanupUnreferencedIcons(null);
                        }
                    }
                }
                catch (Exception rollbackEx)
                {
                    Log.Write("System icon rollback failed: " + rollbackEx);
                }

                return new SystemIconApplyOutcome(false, 0, "Icone non applicate: " + ex.Message);
            }
        }
    }

    public Task<SystemIconApplyOutcome> RestoreAsync(
        IReadOnlyList<SystemIconTarget>? targets,
        Action<string>? progress = null)
    {
        return Task.Run(() => Restore(targets, progress));
    }

    public SystemIconApplyOutcome Restore(IReadOnlyList<SystemIconTarget>? targets, Action<string>? progress = null)
    {
        lock (Gate)
        {
            return RestoreCore(targets, progress);
        }
    }

    private SystemIconApplyOutcome RestoreCore(IReadOnlyList<SystemIconTarget>? targets, Action<string>? progress)
    {
        try
        {
            SystemIconBackup? backup = LoadBackup();
            if (backup is null)
            {
                CleanupStaging();
                return new SystemIconApplyOutcome(true, 0, "Nessuna icona di sistema da ripristinare.");
            }

            var wanted = targets is null
                ? backup.Operations.ToList()
                : backup.Operations.Where(o => targets.Any(t =>
                    string.Equals(t.Id, o.Id, StringComparison.OrdinalIgnoreCase))).ToList();

            if (wanted.Count == 0)
                return new SystemIconApplyOutcome(true, 0, "Le voci scelte non hanno un backup.");

            backup.Completed = false;
            SaveBackup(backup);

            SystemIconApplyOutcome outcome = RestoreOperations(backup, wanted, progress);
            if (outcome.Success)
            {
                foreach (SystemIconOperation operation in wanted)
                    backup.Operations.Remove(operation);

                if (backup.Operations.Count == 0)
                {
                    DeleteBackup();
                    CleanupUnreferencedIcons(null);
                }
                else
                {
                    backup.Completed = true;
                    SaveBackup(backup);
                    CleanupUnreferencedIcons(backup);
                }

                NotifyShell();
            }

            return outcome;
        }
        catch (Exception ex)
        {
            Log.Write("System icon restore failed: " + ex);
            return new SystemIconApplyOutcome(false, 0, "Ripristino non riuscito: " + ex.Message);
        }
    }

    private SystemIconApplyOutcome RestoreOperations(
        SystemIconBackup backup,
        IReadOnlyList<SystemIconOperation> operations,
        Action<string>? progress)
    {
        var failures = new List<string>();
        int restored = 0;

        foreach (SystemIconOperation operation in operations)
        {
            try
            {
                progress?.Invoke("Ripristino " + operation.DisplayName + "…");
                if (!string.IsNullOrWhiteSpace(operation.ShortcutPath))
                {
                    if (File.Exists(operation.ShortcutPath))
                    {
                        ShortcutResolver.WriteRawIconLocation(
                            operation.ShortcutPath, operation.OriginalShortcutIcon ?? "");
                        string after = ShortcutResolver.ReadRawIconLocation(operation.ShortcutPath);
                        if (!string.Equals(after, operation.OriginalShortcutIcon ?? "", StringComparison.Ordinal))
                            throw new InvalidOperationException("Il collegamento non ha ripreso il valore originale.");
                        NotifyShortcut(operation.ShortcutPath);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(operation.FolderPath))
                {
                    RestoreFolder(operation);
                }
                else if (!string.IsNullOrWhiteSpace(operation.RegistryKey))
                {
                    RestoreRegistry(operation);
                }

                restored++;
            }
            catch (Exception ex)
            {
                failures.Add(operation.DisplayName + ": " + ex.Message);
                Log.Write("System icon restore item failed: " + ex.Message);
            }
        }

        if (failures.Count > 0)
        {
            return new SystemIconApplyOutcome(false, restored,
                "Ripristino parziale: " + failures.Count + " voce/i non ripristinate.", failures);
        }

        return new SystemIconApplyOutcome(true, restored,
            restored == 1 ? "Icona originale ripristinata e verificata." : $"{restored} icone originali ripristinate e verificate.");
    }

    private SystemIconOperation Capture(SystemIconTarget target)
    {
        var operation = new SystemIconOperation
        {
            Id = target.Id,
            Kind = target.Kind.ToString(),
            DisplayName = target.DisplayName,
            ShortcutPath = target.ShortcutPath,
            FolderPath = target.FolderPath,
            RegistryKey = target.RegistryKey,
            RegistryValueName = target.RegistryValueName,
            SourceIcon = target.SourceIcon
        };

        if (!string.IsNullOrWhiteSpace(target.ShortcutPath))
        {
            if (!File.Exists(target.ShortcutPath))
                throw new FileNotFoundException("Collegamento non trovato.", target.ShortcutPath);
            operation.OriginalShortcutIcon = ShortcutResolver.ReadRawIconLocation(target.ShortcutPath);
            return operation;
        }

        if (!string.IsNullOrWhiteSpace(target.FolderPath))
        {
            if (!Directory.Exists(target.FolderPath))
                throw new DirectoryNotFoundException("Cartella non trovata.");
            string ini = Path.Combine(target.FolderPath, "desktop.ini");
            operation.OriginalFolderAttributes = (int)File.GetAttributes(target.FolderPath);
            operation.OriginalDesktopIniExists = File.Exists(ini);
            if (operation.OriginalDesktopIniExists)
            {
                operation.OriginalDesktopIniAttributes = (int)File.GetAttributes(ini);
                operation.OriginalDesktopIniBase64 = Convert.ToBase64String(File.ReadAllBytes(ini));
            }
            return operation;
        }

        if (string.IsNullOrWhiteSpace(target.RegistryKey))
            throw new InvalidOperationException("Voce senza superficie modificabile.");

        operation.CreatedRegistryKeys = FindMissingRegistryKeys(target.RegistryKey);
        operation.OriginalRegistryValue = ReadRegistrySnapshot(target.RegistryKey, operation.RegistryValueName);
        return operation;
    }

    private void ApplyOperation(SystemIconOperation operation, string value)
    {
        if (!string.IsNullOrWhiteSpace(operation.ShortcutPath))
        {
            if (!File.Exists(operation.ShortcutPath))
                throw new FileNotFoundException("Collegamento non trovato.", operation.ShortcutPath);
            ShortcutResolver.WriteRawIconLocation(operation.ShortcutPath, value);
            return;
        }

        if (!string.IsNullOrWhiteSpace(operation.FolderPath))
        {
            ApplyFolderIcon(operation.FolderPath, value);
            return;
        }

        if (string.IsNullOrWhiteSpace(operation.RegistryKey))
            throw new InvalidOperationException("Chiave di registro mancante.");

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(operation.RegistryKey, true)
                                ?? throw new InvalidOperationException("Chiave HKCU non scrivibile.");
        key.SetValue(operation.RegistryValueName, value, RegistryValueKind.String);
    }

    private static void VerifyApplied(SystemIconOperation operation, string value)
    {
        if (!string.IsNullOrWhiteSpace(operation.ShortcutPath))
        {
            string after = ShortcutResolver.ReadRawIconLocation(operation.ShortcutPath);
            if (!string.Equals(after, value, StringComparison.Ordinal))
                throw new InvalidOperationException("Verifica collegamento fallita.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(operation.FolderPath))
        {
            string ini = Path.Combine(operation.FolderPath, "desktop.ini");
            if (!File.Exists(ini) ||
                !File.ReadAllText(ini).Contains("IconResource=" + value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Verifica desktop.ini fallita.");
            return;
        }

        RegistryValueSnapshot afterValue = ReadRegistrySnapshot(operation.RegistryKey!, operation.RegistryValueName);
        if (!afterValue.ValueExists || !string.Equals(afterValue.StringValue, value, StringComparison.Ordinal))
            throw new InvalidOperationException("Verifica registro fallita.");
    }

    private static void ApplyFolderIcon(string folder, string value)
    {
        string ini = Path.Combine(folder, "desktop.ini");
        string? existing = null;
        if (File.Exists(ini))
        {
            try
            {
                existing = File.ReadAllText(ini);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("desktop.ini non leggibile: " + ex.Message, ex);
            }
        }

        string text = BuildDesktopIni(existing, value);
        FileAttributes folderAttributes = File.GetAttributes(folder);
        File.SetAttributes(folder, folderAttributes & ~(FileAttributes.ReadOnly | FileAttributes.System));

        if (File.Exists(ini))
        {
            FileAttributes iniAttributes = File.GetAttributes(ini);
            File.SetAttributes(ini, iniAttributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }

        File.WriteAllText(ini, text, new UnicodeEncoding(false, true));
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(folder, folderAttributes | FileAttributes.System);
    }

    private static string BuildDesktopIni(string? existing, string iconResource)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(existing))
            lines.AddRange(existing.Replace("\r\n", "\n").Split('\n'));

        var result = new List<string>();
        bool inShellClass = false;
        bool inserted = false;
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                if (inShellClass && !inserted)
                {
                    result.Add("IconResource=" + iconResource);
                    inserted = true;
                }

                inShellClass = trimmed.Equals("[.ShellClassInfo]", StringComparison.OrdinalIgnoreCase);
                result.Add(line);
                if (inShellClass)
                {
                    result.Add("IconResource=" + iconResource);
                    inserted = true;
                }
                continue;
            }

            if (inShellClass &&
                (trimmed.StartsWith("IconResource=", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase) ||
                 trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase)))
                continue;

            result.Add(line);
        }

        if (!inserted)
        {
            if (result.Count > 0 && result[^1].Length > 0)
                result.Add("");
            result.Add("[.ShellClassInfo]");
            result.Add("IconResource=" + iconResource);
        }

        return string.Join("\r\n", result).TrimEnd() + "\r\n";
    }

    private static void RestoreFolder(SystemIconOperation operation)
    {
        string folder = operation.FolderPath!;
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("Cartella non trovata: " + folder);

        string ini = Path.Combine(folder, "desktop.ini");
        if (File.Exists(ini))
        {
            FileAttributes iniAttributes = File.GetAttributes(ini);
            File.SetAttributes(ini, iniAttributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
        }

        FileAttributes folderAttributes = File.GetAttributes(folder);
        File.SetAttributes(folder, folderAttributes & ~(FileAttributes.ReadOnly | FileAttributes.System));

        if (operation.OriginalDesktopIniExists)
        {
            byte[] original = Convert.FromBase64String(operation.OriginalDesktopIniBase64 ?? "");
            File.WriteAllBytes(ini, original);
            File.SetAttributes(ini, (FileAttributes)operation.OriginalDesktopIniAttributes);
        }
        else if (File.Exists(ini))
        {
            File.Delete(ini);
        }

        File.SetAttributes(folder, (FileAttributes)operation.OriginalFolderAttributes);

        if (operation.OriginalDesktopIniExists)
        {
            byte[] expected = Convert.FromBase64String(operation.OriginalDesktopIniBase64 ?? "");
            byte[] after = File.ReadAllBytes(ini);
            if (!expected.SequenceEqual(after))
                throw new InvalidOperationException("desktop.ini non coincide con l'originale.");
        }
        else if (File.Exists(ini))
        {
            throw new InvalidOperationException("desktop.ini creato da Nebula non è stato rimosso.");
        }
    }

    private static void RestoreRegistry(SystemIconOperation operation)
    {
        RegistryValueSnapshot original = operation.OriginalRegistryValue
            ?? new RegistryValueSnapshot { KeyExists = false, ValueExists = false };

        if (original.KeyExists)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(operation.RegistryKey!, true)
                                     ?? Registry.CurrentUser.CreateSubKey(operation.RegistryKey!, true);
            if (key is null)
                throw new InvalidOperationException("Chiave HKCU non accessibile.");

            if (original.ValueExists)
                WriteSnapshotValue(key, operation.RegistryValueName, original);
            else
                key.DeleteValue(operation.RegistryValueName, false);
        }
        else
        {
            // The whole DefaultIcon key was created by us. Remove the value
            // before deleting the now-empty key below.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(operation.RegistryKey!, true);
            key?.DeleteValue(operation.RegistryValueName, false);
        }

        foreach (string created in operation.CreatedRegistryKeys
                     .Where(k => !string.IsNullOrWhiteSpace(k))
                     .OrderByDescending(k => k.Length))
        {
            try
            {
                bool empty;
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(created, true))
                    empty = key is not null && key.SubKeyCount == 0 && key.ValueCount == 0;

                if (empty)
                    Registry.CurrentUser.DeleteSubKeyTree(created, false);
            }
            catch (Exception ex)
            {
                Log.Write("System icon key cleanup failed: " + ex.Message);
            }
        }

        RegistryValueSnapshot after = ReadRegistrySnapshot(operation.RegistryKey!, operation.RegistryValueName);
        if (original.KeyExists != after.KeyExists ||
            original.ValueExists != after.ValueExists ||
            (original.ValueExists && !SnapshotsEqual(original, after)))
            throw new InvalidOperationException("Il valore di registro non coincide con l'originale.");
    }

    private string GetOrBuildIcon(string source, string style, string shape, Color accent)
    {
        string key = string.Join("|", source, style, shape, accent.R, accent.G, accent.B);
        if (_buildCache.TryGetValue(key, out string? cached) && File.Exists(cached))
            return cached;

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..20];
        string path = Path.Combine(IconsDirectory, "nebula-" + hash + ".ico");
        if (!File.Exists(path) || IconArtRenderer.ReadIcoSizes(path).Count != IconArtRenderer.Sizes.Length)
        {
            using Bitmap sourceBitmap = IconArtRenderer.LoadSource(source);
            IconArtRenderer.WriteIco(sourceBitmap, style, shape, accent, path);
        }

        _buildCache[key] = path;
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

    private static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".ico" or ".png" or ".jpg" or ".jpeg" or ".exe";

    private static bool SourceExists(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        string path = ExpandIconPath(source);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static string? ExpandIconPath(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;
        string value = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        int comma = value.LastIndexOf(',');
        if (comma > 0 && int.TryParse(value[(comma + 1)..].Trim(), out _))
            value = value[..comma].Trim().Trim('"');
        if (!Path.IsPathRooted(value))
        {
            string system = Path.Combine(Environment.SystemDirectory, value);
            if (File.Exists(system))
                return system;
        }
        return value;
    }

    private static string? ReadDefaultString(RegistryKey root, string path)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            return key?.GetValue("", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveProgId(string extension)
    {
        string? progId = ReadDefaultString(Registry.CurrentUser, @"Software\Classes\" + extension)
                         ?? ReadDefaultString(Registry.LocalMachine, @"Software\Classes\" + extension)
                         ?? ReadDefaultString(Registry.ClassesRoot, extension);
        if (!string.IsNullOrWhiteSpace(progId))
            return progId;

        // UserChoice is not used when it is a packaged app (AppX): the
        // classic ProgID still describes the visible shell icon and is the
        // per-user surface we can safely back up and restore.
        try
        {
            using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
            string? chosen = choice?.GetValue("ProgId") as string;
            if (!string.IsNullOrWhiteSpace(chosen) &&
                !chosen.StartsWith("AppX", StringComparison.OrdinalIgnoreCase))
                return chosen;
        }
        catch
        {
            // Optional surface.
        }

        return null;
    }

    private static string? ResolveFileTypeSource(string extension, string? progId)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(progId))
            {
                string? fromProgId = ReadIconFromClass(progId);
                if (!string.IsNullOrWhiteSpace(fromProgId))
                    return fromProgId;
            }

            string? direct = ReadIconFromClass(extension);
            if (!string.IsNullOrWhiteSpace(direct))
                return direct;

            string? systemFile = ReadIconFromClass(@"SystemFileAssociations\" + extension);
            if (!string.IsNullOrWhiteSpace(systemFile))
                return systemFile;

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadIconFromClass(string classPath)
    {
        string? fromMachine = ReadDefaultString(Registry.LocalMachine, @"Software\Classes\" + classPath + @"\DefaultIcon");
        if (!string.IsNullOrWhiteSpace(fromMachine))
            return fromMachine;

        string? fromMerged = ReadDefaultString(Registry.ClassesRoot, classPath + @"\DefaultIcon");
        if (!string.IsNullOrWhiteSpace(fromMerged))
            return fromMerged;

        return null;
    }

    private static string ShortcutId(string path) => "lnk:" + path.ToLowerInvariant();

    private static SystemIconOperation? FindOperation(SystemIconBackup? backup, string id) =>
        backup?.Operations.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<string> FindMissingRegistryKeys(string keyPath)
    {
        var missing = new List<string>();
        string[] parts = keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        string current = "";
        foreach (string part in parts)
        {
            current = current.Length == 0 ? part : current + "\\" + part;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(current);
            if (key is null)
                missing.Add(current);
        }
        return missing;
    }

    private static RegistryValueSnapshot ReadRegistrySnapshot(string keyPath, string valueName)
    {
        var snapshot = new RegistryValueSnapshot();
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        snapshot.KeyExists = key is not null;
        if (key is null)
            return snapshot;

        snapshot.ValueExists = key.GetValueNames().Any(n =>
            string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase));
        if (!snapshot.ValueExists)
            return snapshot;

        RegistryValueKind kind = key.GetValueKind(valueName);
        snapshot.Kind = (int)kind;
        object? value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        switch (kind)
        {
            case RegistryValueKind.String:
                snapshot.StringValue = value as string ?? "";
                break;
            case RegistryValueKind.ExpandString:
                snapshot.StringValue = value as string ?? "";
                break;
            case RegistryValueKind.MultiString:
                snapshot.MultiStringValue = value as string[] ?? Array.Empty<string>();
                break;
            case RegistryValueKind.DWord:
                snapshot.DwordValue = value is int d ? d : 0;
                break;
            case RegistryValueKind.QWord:
                snapshot.QwordValue = value is long q ? q : 0;
                break;
            case RegistryValueKind.Binary:
                snapshot.BinaryBase64 = value is byte[] bytes ? Convert.ToBase64String(bytes) : "";
                break;
        }
        return snapshot;
    }

    private static void WriteSnapshotValue(RegistryKey key, string valueName, RegistryValueSnapshot snapshot)
    {
        switch ((RegistryValueKind)snapshot.Kind)
        {
            case RegistryValueKind.MultiString:
                key.SetValue(valueName, snapshot.MultiStringValue ?? Array.Empty<string>(), RegistryValueKind.MultiString);
                break;
            case RegistryValueKind.DWord:
                key.SetValue(valueName, snapshot.DwordValue ?? 0, RegistryValueKind.DWord);
                break;
            case RegistryValueKind.QWord:
                key.SetValue(valueName, snapshot.QwordValue ?? 0L, RegistryValueKind.QWord);
                break;
            case RegistryValueKind.Binary:
                key.SetValue(valueName, Convert.FromBase64String(snapshot.BinaryBase64 ?? ""), RegistryValueKind.Binary);
                break;
            case RegistryValueKind.ExpandString:
                key.SetValue(valueName, snapshot.StringValue ?? "", RegistryValueKind.ExpandString);
                break;
            default:
                key.SetValue(valueName, snapshot.StringValue ?? "", RegistryValueKind.String);
                break;
        }
    }

    private static bool SnapshotsEqual(RegistryValueSnapshot a, RegistryValueSnapshot b) =>
        a.ValueExists == b.ValueExists &&
        a.Kind == b.Kind &&
        string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal) &&
        string.Equals(a.BinaryBase64, b.BinaryBase64, StringComparison.Ordinal) &&
        (a.MultiStringValue ?? Array.Empty<string>()).SequenceEqual(b.MultiStringValue ?? Array.Empty<string>()) &&
        a.DwordValue == b.DwordValue &&
        a.QwordValue == b.QwordValue;

    private SystemIconBackup? LoadBackup()
    {
        if (!File.Exists(BackupPath))
            return null;
        try
        {
            string json = File.ReadAllText(BackupPath);
            SystemIconBackup? backup = JsonSerializer.Deserialize<SystemIconBackup>(json, JsonOptions);
            if (backup is null || backup.Version != 1)
                return null;
            backup.Operations ??= new List<SystemIconOperation>();
            return backup;
        }
        catch (Exception ex)
        {
            Log.Write("System icon backup unreadable: " + ex.Message);
            try
            {
                File.Copy(BackupPath, BackupPath + ".broken", true);
            }
            catch
            {
                // Best effort.
            }
            return null;
        }
    }

    private void SaveBackup(SystemIconBackup backup)
    {
        Directory.CreateDirectory(RootDirectory);
        string json = JsonSerializer.Serialize(backup, JsonOptions);
        string temp = BackupPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, BackupPath, true);
    }

    private void DeleteBackup()
    {
        try
        {
            if (File.Exists(BackupPath))
                File.Delete(BackupPath);
            if (File.Exists(BackupPath + ".tmp"))
                File.Delete(BackupPath + ".tmp");
        }
        catch (Exception ex)
        {
            Log.Write("System icon backup cleanup failed: " + ex.Message);
        }
    }

    private void CleanupUnreferencedIcons(SystemIconBackup? backup)
    {
        try
        {
            if (!Directory.Exists(IconsDirectory))
                return;
            var keep = new HashSet<string>(
                backup?.Operations.Select(o => o.GeneratedIconPath)
                    .Where(p => !string.IsNullOrWhiteSpace(p))! ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            foreach (string file in Directory.EnumerateFiles(IconsDirectory, "*.ico"))
            {
                if (!keep.Contains(file))
                    TryDelete(file);
            }
            CleanupStaging();
        }
        catch (Exception ex)
        {
            Log.Write("System icon file cleanup failed: " + ex.Message);
        }
    }

    private void CleanupStaging()
    {
        try
        {
            if (!Directory.Exists(StagingDirectory))
                return;
            foreach (string file in Directory.EnumerateFiles(StagingDirectory, "*", SearchOption.AllDirectories))
                TryDelete(file);
        }
        catch (Exception ex)
        {
            Log.Write("System icon staging cleanup failed: " + ex.Message);
        }
    }

    /// <summary>Called on every start: finishes or rolls back an interrupted operation.</summary>
    public void Recover()
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(RootDirectory);
                Directory.CreateDirectory(IconsDirectory);
                Directory.CreateDirectory(StagingDirectory);
                CleanupStaging();

                SystemIconBackup? backup = LoadBackup();
                if (backup is null)
                {
                    CleanupUnreferencedIcons(null);
                    return;
                }

                if (!backup.Completed)
                {
                    Log.Write("System icon recovery: rolling back interrupted operation.");
                    SystemIconApplyOutcome outcome = RestoreOperations(backup, backup.Operations, null);
                    if (outcome.Success)
                    {
                        DeleteBackup();
                        CleanupUnreferencedIcons(null);
                    }
                    else
                    {
                        Log.Write("System icon recovery incomplete: " + outcome.Message);
                    }
                }
                else
                {
                    CleanupUnreferencedIcons(backup);
                }
            }
            catch (Exception ex)
            {
                Log.Write("System icon recovery failed: " + ex);
            }
        }
    }

    /// <summary>Removes temporary files without deleting a completed backup.</summary>
    public void CleanupOnExit()
    {
        lock (Gate)
        {
            CleanupStaging();
            SystemIconBackup? backup = LoadBackup();
            if (backup is not null && backup.Completed)
                CleanupUnreferencedIcons(backup);
            else if (backup is null)
                CleanupUnreferencedIcons(null);
        }
    }

    public static Color PreviewAccent => GetAccent();

    public static Color ColorFromHex(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return fallback;
        string value = hex.Trim().TrimStart('#');
        try
        {
            if (value.Length == 6)
                return Color.FromArgb(
                    Convert.ToInt32(value[..2], 16),
                    Convert.ToInt32(value.Substring(2, 2), 16),
                    Convert.ToInt32(value.Substring(4, 2), 16));
            if (value.Length == 8)
                return Color.FromArgb(
                    Convert.ToInt32(value[..2], 16),
                    Convert.ToInt32(value.Substring(2, 2), 16),
                    Convert.ToInt32(value.Substring(4, 2), 16),
                    Convert.ToInt32(value.Substring(6, 2), 16));
        }
        catch
        {
            // Fall back to the theme colour.
        }
        return fallback;
    }

    private static Color GetAccent()
    {
        try
        {
            System.Windows.Media.Color color = App.Instance.Theme.AccentA;
            return Color.FromArgb(color.R, color.G, color.B);
        }
        catch
        {
            return Color.FromArgb(139, 92, 246);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell()
    {
        try
        {
            // SHCNE_ASSOCCHANGED, SHCNF_IDLIST
            SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Explorer refreshes eventually on its own.
        }
    }

    private static void NotifyShortcut(string? shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
            return;

        IntPtr path = IntPtr.Zero;
        IntPtr directory = IntPtr.Zero;
        try
        {
            path = Marshal.StringToHGlobalUni(shortcutPath);
            // SHCNE_UPDATEITEM, SHCNF_PATHW: invalidates just this shortcut.
            SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);

            string? folder = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                directory = Marshal.StringToHGlobalUni(folder);
                // SHCNE_UPDATEDIR, SHCNF_PATHW: refreshes the desktop/start menu view.
                SHChangeNotify(0x00001000, 0x0005, directory, IntPtr.Zero);
            }
        }
        catch
        {
            // Best effort; Explorer also refreshes on its own.
        }
        finally
        {
            if (path != IntPtr.Zero)
                Marshal.FreeHGlobal(path);
            if (directory != IntPtr.Zero)
                Marshal.FreeHGlobal(directory);
        }
    }
}
