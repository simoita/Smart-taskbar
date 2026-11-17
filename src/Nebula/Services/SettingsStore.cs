using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Nebula.Models;

namespace Nebula.Services;

public sealed class SettingsStore : IDisposable
{
    private readonly DispatcherTimer _saveTimer;
    private bool _disposed;

    /// <summary>
    /// Settings live next to the real profile by default. The override exists so
    /// the selftest can exercise persistence without touching the user's file.
    /// </summary>
    public static string DataDirectory
    {
        get
        {
            string? overrideDir = Environment.GetEnvironmentVariable("NEBULA_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula")
                : Path.GetFullPath(overrideDir);
        }
    }

    public SettingsStore()
    {
        string dir = DataDirectory;
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "settings.json");
        IsFirstRun = !File.Exists(FilePath);
        Settings = Load();
        Settings.PropertyChanged += (_, _) => MarkDirty();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };
    }

    public AppSettings Settings { get; private set; }
    public string FilePath { get; }
    public bool IsFirstRun { get; }

    public event Action? Changed;

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return AppSettings.CreateDefault();

            string json = File.ReadAllText(FilePath);
            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
            loaded ??= AppSettings.CreateDefault();
            Normalize(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
            try
            {
                File.Copy(FilePath, FilePath + ".broken", true);
            }
            catch
            {
                // Best effort backup only.
            }
            return AppSettings.CreateDefault();
        }
    }

    private static void Normalize(AppSettings s)
    {
        s.GlassOpacity = Math.Clamp(s.GlassOpacity, 0.3, 1.0);
        s.CornerRadius = Math.Clamp(s.CornerRadius, 4, 36);
        s.IconSize = Math.Clamp(s.IconSize, 16, 44);
        s.DockHeight = Math.Clamp(s.DockHeight, 44, 92);
        s.ItemSpacing = Math.Clamp(s.ItemSpacing, -4, 12);
        s.EdgeMargin = Math.Clamp(s.EdgeMargin, 0, 48);
        s.AnimationSpeed = Math.Clamp(s.AnimationSpeed, 0.5, 2.0);
        s.SearchCardWidth = Math.Clamp(s.SearchCardWidth, 560, 900);
        s.TabsBarHeight = Math.Clamp(s.TabsBarHeight, 34, 64);
        if (s.TabsStyle is not ("glass" or "minimal" or "neon" or "soft" or "mono"))
            s.TabsStyle = "glass";
        if (string.IsNullOrWhiteSpace(s.SurfaceStyle))
            s.SurfaceStyle = s.TabsStyle;
        if (s.SurfaceStyle is not ("glass" or "minimal" or "neon" or "soft" or "mono"))
            s.SurfaceStyle = "glass";
        if (s.TabsBarPosition is not ("top" or "bottom"))
            s.TabsBarPosition = "top";
        s.DockZoneSpacing = Math.Clamp(s.DockZoneSpacing, 0, 24);
        s.DockAppsScale = Math.Clamp(s.DockAppsScale, 0.75, 1.3);
        s.DockSystemScale = Math.Clamp(s.DockSystemScale, 0.75, 1.3);
        s.DockActionsScale = Math.Clamp(s.DockActionsScale, 0.75, 1.3);
        s.MinimizedIconOpacity = Math.Clamp(s.MinimizedIconOpacity, 0.2, 1.0);
        s.ClosedIconOpacity = Math.Clamp(s.ClosedIconOpacity, 0.2, 1.0);
        string[] zones = s.DockZoneOrder?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();
        if (zones.Length != 3 || zones.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 ||
            zones.Any(z => z is not ("apps" or "system" or "actions")))
            s.DockZoneOrder = "apps,system,actions";
        else
            s.DockZoneOrder = string.Join(',', zones);
        if (s.DockIndicatorStyle is not ("dot" or "pill" or "bar" or "frame"))
            s.DockIndicatorStyle = "pill";
        if (s.IconStyle is not ("native" or "tile" or "circle" or "mono" or "neon"))
            s.IconStyle = "native";
        if (s.IconShape is not ("rounded" or "circle" or "square" or "squircle"))
            s.IconShape = "rounded";
        if (s.SystemIconStyle is not ("native" or "tile" or "filled" or "circle" or "glass" or "mono"
            or "outline" or "neon" or "duotone"))
            s.SystemIconStyle = "filled";
        if (s.SystemIconShape is not ("rounded" or "circle" or "square" or "squircle"))
            s.SystemIconShape = "rounded";
        s.PinnedPaths ??= new List<string>();
        s.RecentApps ??= new List<string>();
        s.PanelWidgets ??= new List<string> { "clock", "calendar", "stats", "battery", "network" };

        if (s.SettingsVersion < 2)
        {
            // Older builds rendered the clock, battery and system monitor at
            // fixed positions. Preserve that exact look as taskbar widgets.
            var migrated = new List<string>();
            if (s.ShowStats)
                migrated.Add("stats");
            if (s.ShowBattery)
                migrated.Add("battery");
            if (s.ShowClock)
                migrated.Add("clock");
            s.TaskbarWidgets = migrated;
            s.SettingsVersion = 2;
        }

        s.TaskbarWidgets ??= new List<string> { "stats", "battery", "clock" };
        var taskbarWidgets = new List<string>();
        foreach (string id in s.TaskbarWidgets)
        {
            string? known = TaskbarWidgetCatalog.Find(id)?.Id;
            if (known is null || taskbarWidgets.Contains(known, StringComparer.OrdinalIgnoreCase))
                continue;
            taskbarWidgets.Add(known);
            if (taskbarWidgets.Count >= 16)
                break;
        }
        s.TaskbarWidgets = taskbarWidgets;

        s.CustomIcons ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        s.CustomIconStyles ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in s.CustomIconStyles)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) &&
                pair.Value is "native" or "tile" or "circle" or "mono" or "neon")
                styles[pair.Key] = pair.Value;
        }
        s.CustomIconStyles = styles;

        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in s.CustomIcons)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                icons[pair.Key] = pair.Value;
        }
        s.CustomIcons = icons;
        s.UpdateManifestUrl = (s.UpdateManifestUrl ?? "").Trim();
        if (s.UpdateManifestUrl.Length == 0)
            s.UpdateManifestUrl = UpdateDefaults.ManifestUrl;
        if (ThemePreset.All.All(p => p.Id != s.ThemeId))
            s.ThemeId = "nebula";
    }

    public void MarkDirty()
    {
        if (_disposed)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
        Changed?.Invoke();
    }

    public void SaveNow()
    {
        try
        {
            string json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            Log.Write("Settings save failed: " + ex.Message);
        }
    }

    public void ResetAll()
    {
        // Copy into the existing instance so every window keeps its bindings.
        AppSettings defaults = AppSettings.CreateDefault();
        foreach (System.Reflection.PropertyInfo property in typeof(AppSettings).GetProperties())
        {
            if (property.CanWrite)
                property.SetValue(Settings, property.GetValue(defaults));
        }

        Settings.PinnedPaths = new List<string>();
        Settings.RecentApps = new List<string>();
        SaveNow();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _saveTimer.Stop();
        SaveNow();
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nebula", "app.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            lock (Gate)
                File.AppendAllText(FilePath, line);
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
