namespace MetroHub.Core.Models;

public class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 0x0002; // MOD_CONTROL
    public uint HotkeyKey { get; set; } = 0xC0;         // VK_OEM_3 (~)
    public string HotkeyDisplayString { get; set; } = "Ctrl + `";

    public string ThemeMode { get; set; } = "System"; // System, Dark, Light
    public string BackdropType { get; set; } = "Mica"; // Mica, MicaAlt, Acrylic, DesktopWallpaper, Wallpaper
    public string? CustomWallpaperPath { get; set; }
    public double WallpaperDimOpacity { get; set; } = 0.50; // 0.35, 0.50, 0.65
    
    public bool LaunchAtStartup { get; set; } = false;
    public bool CloseOnLaunch { get; set; } = true;
    public string Orientation { get; set; } = "Vertical"; // Vertical, Horizontal

    public int GridBaseSize { get; set; } = 64;
    public int TileGap { get; set; } = 8;
    public double AcrylicOpacity { get; set; } = 0.85;
    public int GroupColumnWidth { get; set; } = 8; // Fixed 8 units
}
