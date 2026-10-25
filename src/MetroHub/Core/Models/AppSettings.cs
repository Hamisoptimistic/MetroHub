namespace MetroHub.Core.Models;

public sealed class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 0x0002; // MOD_CONTROL
    public uint HotkeyKey { get; set; } = 0xC0;         // VK_OEM_3 (~)
    public string HotkeyDisplayString { get; set; } = "Ctrl + `";

    public string ThemeMode { get; set; } = "System"; // System, Dark, Light
    public AccentStyleMode AccentStyle { get; set; } = AccentStyleMode.Flat; // Flat, StudioGlass
    public string BackdropType { get; set; } = "Mica"; // Mica, MicaAlt, Acrylic, DesktopWallpaper, Wallpaper, BingDaily, SpotlightDaily
    public string? CustomWallpaperPath { get; set; }
    public double WallpaperDimOpacity { get; set; } = 0.70; // 0.50 (Light), 0.70 (Balanced), 0.85 (Heavy)
    public bool WallpaperParallax { get; set; } = true;
    public string WallpaperEffect { get; set; } = "None"; // None, Blur, WaterRipple, WaterCaustics, NeonClock
    
    public bool LaunchAtStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool CloseOnLaunch { get; set; } = true;
    public bool DismissOnDeactivate { get; set; } = true;
    public string Orientation { get; set; } = "Vertical"; // Vertical, Horizontal

    public int GridBaseSize { get; set; } = 64;
    public int TileGap { get; set; } = 8;
    public double AcrylicOpacity { get; set; } = 0.85;
    public int GroupColumnWidth { get; set; } = 8; // Fixed 8 units
    public int TileCornerRadius { get; set; } = 2; // 0 (Metro Sharp), 2 (Subtle Edge), 4 (Medium), 8 (Fluent Rounded)

    public bool SidebarAutoHide { get; set; }
    public bool SidebarPinned { get; set; } = true;
    public List<SidebarShortcutItem> SidebarShortcuts { get; set; } = SidebarShortcutItem.CreateDefaultList();

    public bool ShowWorkspacesButton { get; set; } = true;
    public bool ShowAddShortcutButton { get; set; } = true;
    public bool ShowPowerButton { get; set; } = true;
    public bool ShowRecentApps { get; set; } = true;

    public bool BrowserExtensionCompanionEnabled { get; set; } = true;
    public bool EnableGroupOpenAllButton { get; set; } = true;
    public RecentItemsViewMode RecentViewMode { get; set; } = RecentItemsViewMode.Grid;
    public SearchSettings Search { get; set; } = new();
}

public enum RecentItemsViewMode
{
    List,
    Grid
}
