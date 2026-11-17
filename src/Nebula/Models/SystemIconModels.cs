namespace Nebula.Models;

public enum SystemIconTargetKind
{
    Shortcut,
    FileType,
    Folder
}

/// <summary>A shell surface Nebula can safely re-icon for the current user.</summary>
public sealed class SystemIconTarget
{
    public required string Id { get; init; }
    public required SystemIconTargetKind Kind { get; init; }
    public required string DisplayName { get; init; }
    public required string Detail { get; init; }
    public string? ShortcutPath { get; init; }
    public string? FolderPath { get; init; }
    public string? RegistryKey { get; init; }
    public string RegistryValueName { get; init; } = "";
    public string? SourceIcon { get; init; }

    public string KindLabel => Kind switch
    {
        SystemIconTargetKind.Shortcut => "Collegamento",
        SystemIconTargetKind.FileType => "Tipo file",
        _ => "Cartella"
    };
}

public sealed class SystemIconBackup
{
    public int Version { get; set; } = 1;
    public bool Completed { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SystemIconOperation> Operations { get; set; } = new();
}

public sealed class SystemIconOperation
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ShortcutPath { get; set; }
    public string? FolderPath { get; set; }
    public string? RegistryKey { get; set; }
    public string RegistryValueName { get; set; } = "";
    public string? OriginalShortcutIcon { get; set; }
    public bool OriginalDesktopIniExists { get; set; }
    public string? OriginalDesktopIniBase64 { get; set; }
    public int OriginalDesktopIniAttributes { get; set; }
    public int OriginalFolderAttributes { get; set; }
    public RegistryValueSnapshot? OriginalRegistryValue { get; set; }
    public List<string> CreatedRegistryKeys { get; set; } = new();
    public string? SourceIcon { get; set; }
    public string? GeneratedIconPath { get; set; }
    public string? AppliedValue { get; set; }
    public string Style { get; set; } = "";
    public string Shape { get; set; } = "";
    public string? CustomSource { get; set; }
    public DateTimeOffset AppliedUtc { get; set; }
}

public sealed class RegistryValueSnapshot
{
    public bool ValueExists { get; set; }
    public bool KeyExists { get; set; }
    public int Kind { get; set; }
    public string? StringValue { get; set; }
    public string? BinaryBase64 { get; set; }
    public string[]? MultiStringValue { get; set; }
    public int? DwordValue { get; set; }
    public long? QwordValue { get; set; }
}

public sealed record SystemIconApplyOutcome(
    bool Success,
    int Changed,
    string Message,
    IReadOnlyList<string>? Failures = null);

public sealed record SystemIconChoice(
    string Style,
    string Shape,
    string? CustomPath);
