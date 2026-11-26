using Nebula.Services;

namespace Nebula.Models;

public sealed class AppSettings : ObservableObject
{
    private string _themeId = "nebula";
    private string _accentHex = "";
    private double _glassOpacity = 0.78;
    private double _cornerRadius = 22;
    private double _iconSize = 30;
    private double _dockHeight = 62;
    private double _itemSpacing = 2;
    private double _edgeMargin = 0;
    private bool _dockOnTop;
    private bool _autoHideDock;
    private bool _hideInFullscreen = true;
    private bool _replaceWindowsTaskbar = true;
    private bool _reserveTaskbarSpace = true;
    private bool _taskbarFullWidth = true;
    private bool _showPinned = true;
    private bool _showRunning = true;
    private bool _showClock = true;
    private bool _showDate = true;
    private bool _showSeconds;
    private bool _showStats = true;
    private bool _showBattery = true;
    private bool _showSearch = true;
    private bool _showIsland;
    private string _islandStyle = "capsule";
    private double _islandScale = 1.0;
    private double _islandTopMargin = 8;
    private bool _islandShowControls = true;
    private bool _islandShowActivities = true;
    private bool _islandShowShelf = true;
    private string _islandPlacement = "floating";
    private bool _winKeyOpensStart = true;
    private bool _searchIncludeWindows = true;
    private bool _searchIncludeFiles = true;
    private double _searchCardWidth = 720;
    private bool _showWidgets = true;
    private bool _showQuickControls = true;
    private bool _quickControlsCompact;
    private bool _showSettingsButton = true;
    private bool _showGlow = true;
    private bool _showGrain = true;
    private bool _showShimmer = true;
    private bool _liquidSheen = true;
    private bool _animationsEnabled = true;
    private double _animationSpeed = 1.0;
    private bool _hoverMagnify = true;
    private bool _launchPulse = true;
    private bool _launchAtStartup;
    private bool _clock24Hour = true;
    private int _monitorIndex;
    private bool _monitorFollowCursor;
    private bool _taskbarAllMonitors;
    private string _dockFinish = "glass";
    private bool _panelsFollowDock = true;
    private bool _compactMode;
    private bool _enableTabs = true;
    private string _tabsStyle = "glass";
    private string _surfaceStyle = "glass";
    private double _tabsBarHeight = 46;
    private string _tabsBarPosition = "top";
    private bool _dockAppsZone = true;
    private bool _dockSystemZone = true;
    private bool _dockActionsZone = true;
    private string _dockZoneOrder = "apps,system,actions";
    private double _dockZoneSpacing = 6;
    private double _dockAppsScale = 1;
    private double _dockSystemScale = 1;
    private double _dockActionsScale = 1;
    private bool _dockZoneDividers = true;
    private bool _dockZonesSeparate;
    private bool _showOpenIndicator = true;
    private bool _showMinimizedIndicator = true;
    private bool _showClosedIndicator;
    private string _dockIndicatorStyle = "pill";
    private double _minimizedIconOpacity = 0.48;
    private double _closedIconOpacity = 0.62;
    private string _iconStyle = "native";
    private string _iconShape = "rounded";
    private string _systemIconStyle = "filled";
    private string _systemIconShape = "rounded";
    private string _updateManifestUrl = UpdateDefaults.ManifestUrl;
    private bool _autoUpdate = true;
    private List<string> _panelWidgets = new() { "clock", "calendar", "stats", "battery", "network" };
    private List<string> _taskbarWidgets = new() { "stats", "battery", "clock" };
    private Dictionary<string, string> _taskbarWidgetModes = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _customIcons = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _customIconStyles = new(StringComparer.OrdinalIgnoreCase);

    public string ThemeId
    {
        get => _themeId;
        set => SetProperty(ref _themeId, value);
    }

    /// <summary>Optional custom accent (#RRGGBB). Empty means "use the preset accent".</summary>
    public string AccentHex
    {
        get => _accentHex;
        set => SetProperty(ref _accentHex, value);
    }

    public double GlassOpacity
    {
        get => _glassOpacity;
        set => SetProperty(ref _glassOpacity, value);
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set => SetProperty(ref _cornerRadius, value);
    }

    public double IconSize
    {
        get => _iconSize;
        set => SetProperty(ref _iconSize, value);
    }

    public double DockHeight
    {
        get => _dockHeight;
        set => SetProperty(ref _dockHeight, value);
    }

    public double ItemSpacing
    {
        get => _itemSpacing;
        set => SetProperty(ref _itemSpacing, value);
    }

    public double EdgeMargin
    {
        get => _edgeMargin;
        set => SetProperty(ref _edgeMargin, value);
    }

    public bool DockOnTop
    {
        get => _dockOnTop;
        set => SetProperty(ref _dockOnTop, value);
    }

    public bool AutoHideDock
    {
        get => _autoHideDock;
        set => SetProperty(ref _autoHideDock, value);
    }

    public bool HideInFullscreen
    {
        get => _hideInFullscreen;
        set => SetProperty(ref _hideInFullscreen, value);
    }

    public bool ReplaceWindowsTaskbar
    {
        get => _replaceWindowsTaskbar;
        set => SetProperty(ref _replaceWindowsTaskbar, value);
    }

    /// <summary>
    /// Reserves the strip used by the bar through the Windows AppBar API, so
    /// maximized windows stop above/below it exactly like the native taskbar.
    /// </summary>
    public bool ReserveTaskbarSpace
    {
        get => _reserveTaskbarSpace;
        set => SetProperty(ref _reserveTaskbarSpace, value);
    }

    /// <summary>Stretches the replacement bar across the monitor like a real taskbar.</summary>
    public bool TaskbarFullWidth
    {
        get => _taskbarFullWidth;
        set => SetProperty(ref _taskbarFullWidth, value);
    }

    public bool ShowPinned
    {
        get => _showPinned;
        set => SetProperty(ref _showPinned, value);
    }

    public bool ShowRunning
    {
        get => _showRunning;
        set => SetProperty(ref _showRunning, value);
    }

    public bool ShowClock
    {
        get => _showClock;
        set => SetProperty(ref _showClock, value);
    }

    public bool ShowDate
    {
        get => _showDate;
        set => SetProperty(ref _showDate, value);
    }

    public bool ShowSeconds
    {
        get => _showSeconds;
        set => SetProperty(ref _showSeconds, value);
    }

    public bool ShowStats
    {
        get => _showStats;
        set => SetProperty(ref _showStats, value);
    }

    public bool ShowBattery
    {
        get => _showBattery;
        set => SetProperty(ref _showBattery, value);
    }

    public bool ShowSearch
    {
        get => _showSearch;
        set => SetProperty(ref _showSearch, value);
    }

    /// <summary>Floating top-center capsule with clock, system state and quick controls.</summary>
    public bool ShowIsland
    {
        get => _showIsland;
        set => SetProperty(ref _showIsland, value);
    }

    /// <summary>capsule, pill or minimal.</summary>
    public string IslandStyle
    {
        get => _islandStyle;
        set => SetProperty(ref _islandStyle, value);
    }

    public double IslandScale
    {
        get => _islandScale;
        set => SetProperty(ref _islandScale, value);
    }

    public double IslandTopMargin
    {
        get => _islandTopMargin;
        set => SetProperty(ref _islandTopMargin, value);
    }

    /// <summary>Shows media and volume controls in the expanded island.</summary>
    public bool IslandShowControls
    {
        get => _islandShowControls;
        set => SetProperty(ref _islandShowControls, value);
    }

    /// <summary>Shows live downloads, copies, archives and media in the island.</summary>
    public bool IslandShowActivities
    {
        get => _islandShowActivities;
        set => SetProperty(ref _islandShowActivities, value);
    }

    /// <summary>Shows the temporary file drawer in the expanded island.</summary>
    public bool IslandShowShelf
    {
        get => _islandShowShelf;
        set => SetProperty(ref _islandShowShelf, value);
    }

    /// <summary>floating (top centre of the screen) or taskbar (inside the Nebula bar).</summary>
    public string IslandPlacement
    {
        get => _islandPlacement;
        set => SetProperty(ref _islandPlacement, value);
    }

    /// <summary>The Windows key opens the Nebula Start panel instead of the native menu.</summary>
    public bool WinKeyOpensStart
    {
        get => _winKeyOpensStart;
        set => SetProperty(ref _winKeyOpensStart, value);
    }

    public bool ShowWidgets
    {
        get => _showWidgets;
        set => SetProperty(ref _showWidgets, value);
    }

    public bool ShowQuickControls
    {
        get => _showQuickControls;
        set => SetProperty(ref _showQuickControls, value);
    }

    public bool QuickControlsCompact
    {
        get => _quickControlsCompact;
        set => SetProperty(ref _quickControlsCompact, value);
    }

    public bool SearchIncludeWindows
    {
        get => _searchIncludeWindows;
        set => SetProperty(ref _searchIncludeWindows, value);
    }

    /// <summary>Includes the indexed user files in launcher and Start search.</summary>
    public bool SearchIncludeFiles
    {
        get => _searchIncludeFiles;
        set => SetProperty(ref _searchIncludeFiles, value);
    }

    /// <summary>Width of the launcher card in DIPs; the Studio exposes it as a live graphic rule.</summary>
    public double SearchCardWidth
    {
        get => _searchCardWidth;
        set => SetProperty(ref _searchCardWidth, value);
    }

    public bool ShowSettingsButton
    {
        get => _showSettingsButton;
        set => SetProperty(ref _showSettingsButton, value);
    }

    public bool ShowGlow
    {
        get => _showGlow;
        set => SetProperty(ref _showGlow, value);
    }

    public bool ShowGrain
    {
        get => _showGrain;
        set => SetProperty(ref _showGrain, value);
    }

    public bool ShowShimmer
    {
        get => _showShimmer;
        set => SetProperty(ref _showShimmer, value);
    }

    /// <summary>A soft specular highlight follows the pointer along the bar.</summary>
    public bool LiquidSheen
    {
        get => _liquidSheen;
        set => SetProperty(ref _liquidSheen, value);
    }

    public bool AnimationsEnabled
    {
        get => _animationsEnabled;
        set => SetProperty(ref _animationsEnabled, value);
    }

    public double AnimationSpeed
    {
        get => _animationSpeed;
        set => SetProperty(ref _animationSpeed, value);
    }

    public bool HoverMagnify
    {
        get => _hoverMagnify;
        set => SetProperty(ref _hoverMagnify, value);
    }

    public bool LaunchPulse
    {
        get => _launchPulse;
        set => SetProperty(ref _launchPulse, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public bool Clock24Hour
    {
        get => _clock24Hour;
        set => SetProperty(ref _clock24Hour, value);
    }

    public int MonitorIndex
    {
        get => _monitorIndex;
        set => SetProperty(ref _monitorIndex, value);
    }

    /// <summary>Moves the bar to the monitor that currently holds the pointer.</summary>
    public bool MonitorFollowCursor
    {
        get => _monitorFollowCursor;
        set => SetProperty(ref _monitorFollowCursor, value);
    }

    /// <summary>
    /// Draws a Nebula bar on every connected monitor at the same time. The bar
    /// on the selected screen keeps the full widget set; the others show apps
    /// and actions and launch new windows on their own monitor.
    /// </summary>
    public bool TaskbarAllMonitors
    {
        get => _taskbarAllMonitors;
        set => SetProperty(ref _taskbarAllMonitors, value);
    }

    /// <summary>
    /// Bar finish: glass (vetro con bagliori), flat (monocolore piatto, nessun
    /// bagliore o ombra) or outline (monocolore con bordo sottile).
    /// </summary>
    public string DockFinish
    {
        get => _dockFinish;
        set => SetProperty(ref _dockFinish, value);
    }

    /// <summary>Start, search, widgets and Studio open on the dock's monitor.</summary>
    public bool PanelsFollowDock
    {
        get => _panelsFollowDock;
        set => SetProperty(ref _panelsFollowDock, value);
    }

    public bool CompactMode
    {
        get => _compactMode;
        set => SetProperty(ref _compactMode, value);
    }

    public bool EnableTabs
    {
        get => _enableTabs;
        set => SetProperty(ref _enableTabs, value);
    }

    /// <summary>glass, minimal or neon.</summary>
    public string TabsStyle
    {
        get => _tabsStyle;
        set => SetProperty(ref _tabsStyle, value);
    }

    /// <summary>Surface preset for widget, quick controls and search: glass, minimal or neon.</summary>
    public string SurfaceStyle
    {
        get => _surfaceStyle;
        set => SetProperty(ref _surfaceStyle, value);
    }

    public double TabsBarHeight
    {
        get => _tabsBarHeight;
        set => SetProperty(ref _tabsBarHeight, value);
    }

    /// <summary>top or bottom.</summary>
    public string TabsBarPosition
    {
        get => _tabsBarPosition;
        set => SetProperty(ref _tabsBarPosition, value);
    }

    public bool DockAppsZone
    {
        get => _dockAppsZone;
        set => SetProperty(ref _dockAppsZone, value);
    }

    public bool DockSystemZone
    {
        get => _dockSystemZone;
        set => SetProperty(ref _dockSystemZone, value);
    }

    public bool DockActionsZone
    {
        get => _dockActionsZone;
        set => SetProperty(ref _dockActionsZone, value);
    }

    /// <summary>Comma separated order: apps, system, actions.</summary>
    public string DockZoneOrder
    {
        get => _dockZoneOrder;
        set => SetProperty(ref _dockZoneOrder, value);
    }

    public double DockZoneSpacing
    {
        get => _dockZoneSpacing;
        set => SetProperty(ref _dockZoneSpacing, value);
    }

    public double DockAppsScale
    {
        get => _dockAppsScale;
        set => SetProperty(ref _dockAppsScale, value);
    }

    public double DockSystemScale
    {
        get => _dockSystemScale;
        set => SetProperty(ref _dockSystemScale, value);
    }

    public double DockActionsScale
    {
        get => _dockActionsScale;
        set => SetProperty(ref _dockActionsScale, value);
    }

    public bool DockZoneDividers
    {
        get => _dockZoneDividers;
        set => SetProperty(ref _dockZoneDividers, value);
    }

    /// <summary>Each zone gets its own rounded shell instead of sharing the bar.</summary>
    public bool DockZonesSeparate
    {
        get => _dockZonesSeparate;
        set => SetProperty(ref _dockZonesSeparate, value);
    }

    public bool ShowOpenIndicator
    {
        get => _showOpenIndicator;
        set => SetProperty(ref _showOpenIndicator, value);
    }

    public bool ShowMinimizedIndicator
    {
        get => _showMinimizedIndicator;
        set => SetProperty(ref _showMinimizedIndicator, value);
    }

    public bool ShowClosedIndicator
    {
        get => _showClosedIndicator;
        set => SetProperty(ref _showClosedIndicator, value);
    }

    /// <summary>dot, pill, bar or frame.</summary>
    public string DockIndicatorStyle
    {
        get => _dockIndicatorStyle;
        set => SetProperty(ref _dockIndicatorStyle, value);
    }

    public double MinimizedIconOpacity
    {
        get => _minimizedIconOpacity;
        set => SetProperty(ref _minimizedIconOpacity, value);
    }

    public double ClosedIconOpacity
    {
        get => _closedIconOpacity;
        set => SetProperty(ref _closedIconOpacity, value);
    }

    /// <summary>native, tile, circle, mono or neon.</summary>
    public string IconStyle
    {
        get => _iconStyle;
        set => SetProperty(ref _iconStyle, value);
    }

    /// <summary>rounded, circle, square or squircle.</summary>
    public string IconShape
    {
        get => _iconShape;
        set => SetProperty(ref _iconShape, value);
    }

    /// <summary>Style used by the per-user system icon surface.</summary>
    public string SystemIconStyle
    {
        get => _systemIconStyle;
        set => SetProperty(ref _systemIconStyle, value);
    }

    /// <summary>Plate shape used by the per-user system icon surface.</summary>
    public string SystemIconShape
    {
        get => _systemIconShape;
        set => SetProperty(ref _systemIconShape, value);
    }

    public string UpdateManifestUrl
    {
        get => _updateManifestUrl;
        set => SetProperty(ref _updateManifestUrl, value);
    }

    /// <summary>When true, Nebula checks the manifest in background and installs new versions.</summary>
    public bool AutoUpdate
    {
        get => _autoUpdate;
        set => SetProperty(ref _autoUpdate, value);
    }

    /// <summary>Compact widgets shown in the widget panel, in display order.</summary>
    public List<string> PanelWidgets
    {
        get => _panelWidgets;
        set => SetProperty(ref _panelWidgets, value);
    }

    /// <summary>Widgets rendered inside the taskbar strip, in display order.</summary>
    public List<string> TaskbarWidgets
    {
        get => _taskbarWidgets;
        set => SetProperty(ref _taskbarWidgets, value);
    }

    /// <summary>Per-widget presentation: "expanded" (default) or "icon".</summary>
    public Dictionary<string, string> TaskbarWidgetModes
    {
        get => _taskbarWidgetModes;
        set => SetProperty(ref _taskbarWidgetModes, value);
    }

    /// <summary>Schema version used to migrate older settings files in place.</summary>
    public int SettingsVersion { get; set; }

    /// <summary>Per-app icon overrides: app path/key to an .ico, .png or .exe path.</summary>
    public Dictionary<string, string> CustomIcons
    {
        get => _customIcons;
        set => SetProperty(ref _customIcons, value);
    }

    /// <summary>Per-app icon style overrides: app path/key to native/tile/circle/mono/neon.</summary>
    public Dictionary<string, string> CustomIconStyles
    {
        get => _customIconStyles;
        set => SetProperty(ref _customIconStyles, value);
    }

    /// <summary>Simple monochrome finishes: glass, flat or outline.</summary>
    public bool IsSimpleFinish =>
        string.Equals(DockFinish, "flat", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(DockFinish, "outline", StringComparison.OrdinalIgnoreCase);

    public List<string> PinnedPaths { get; set; } = new();

    /// <summary>Drag order of the dock icons, by item key (pin:/run:).</summary>
    public List<string> DockItemOrder { get; set; } = new();

    public List<string> RecentApps { get; set; } = new();

    public static AppSettings CreateDefault() => new() { SettingsVersion = 2 };
}
