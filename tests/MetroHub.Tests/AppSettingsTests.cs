using System.IO;
using System.Text.Json;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public class AppSettingsTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void AppSettings_DefaultConstructor_HasExpectedFluentDefaults()
    {
        var settings = new AppSettings();

        Assert.Equal(64, settings.GridBaseSize);
        Assert.Equal(8, settings.TileGap);
        Assert.Equal(2, settings.TileCornerRadius);
        Assert.Equal(0.85, settings.AcrylicOpacity, 2);
        Assert.Equal("System", settings.ThemeMode);
        Assert.Equal("Mica", settings.BackdropType);
        Assert.Equal(8, settings.GroupColumnWidth);
        Assert.True(settings.SidebarAutoHide);
        Assert.False(settings.SidebarPinned);
        Assert.NotNull(settings.SidebarShortcuts);
        Assert.NotEmpty(settings.SidebarShortcuts);
    }

    [Fact]
    public void AppSettings_JsonRoundtrip_PreservesAllFields()
    {
        var original = new AppSettings
        {
            HotkeyModifiers = 0x0001, // MOD_ALT
            HotkeyKey = 0x41,         // 'A'
            HotkeyDisplayString = "Alt + A",
            ThemeMode = "Dark",
            BackdropType = "Acrylic",
            CustomWallpaperPath = @"C:\Wallpapers\custom.png",
            WallpaperDimOpacity = 0.65,
            WallpaperParallax = false,
            LaunchAtStartup = true,
            CloseOnLaunch = false,
            Orientation = "Horizontal",
            GridBaseSize = 72,
            TileGap = 12,
            AcrylicOpacity = 0.90,
            GroupColumnWidth = 8,
            TileCornerRadius = 4,
            SidebarAutoHide = false,
            SidebarPinned = true
        };

        string json = JsonSerializer.Serialize(original, s_jsonOptions);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json, s_jsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.HotkeyModifiers, deserialized.HotkeyModifiers);
        Assert.Equal(original.HotkeyKey, deserialized.HotkeyKey);
        Assert.Equal(original.HotkeyDisplayString, deserialized.HotkeyDisplayString);
        Assert.Equal(original.ThemeMode, deserialized.ThemeMode);
        Assert.Equal(original.BackdropType, deserialized.BackdropType);
        Assert.Equal(original.CustomWallpaperPath, deserialized.CustomWallpaperPath);
        Assert.Equal(original.WallpaperDimOpacity, deserialized.WallpaperDimOpacity, 2);
        Assert.Equal(original.WallpaperParallax, deserialized.WallpaperParallax);
        Assert.Equal(original.LaunchAtStartup, deserialized.LaunchAtStartup);
        Assert.Equal(original.CloseOnLaunch, deserialized.CloseOnLaunch);
        Assert.Equal(original.Orientation, deserialized.Orientation);
        Assert.Equal(original.GridBaseSize, deserialized.GridBaseSize);
        Assert.Equal(original.TileGap, deserialized.TileGap);
        Assert.Equal(original.AcrylicOpacity, deserialized.AcrylicOpacity, 2);
        Assert.Equal(original.GroupColumnWidth, deserialized.GroupColumnWidth);
        Assert.Equal(original.TileCornerRadius, deserialized.TileCornerRadius);
        Assert.Equal(original.SidebarAutoHide, deserialized.SidebarAutoHide);
        Assert.Equal(original.SidebarPinned, deserialized.SidebarPinned);
    }

    [Fact]
    public void AppSettings_PartialJsonDeserialization_RetainsDefaultsForMissingFields()
    {
        // Simulate legacy JSON that only had ThemeMode and BackdropType
        string legacyJson = """
        {
            "ThemeMode": "Light",
            "BackdropType": "DesktopWallpaper"
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, s_jsonOptions);

        Assert.NotNull(settings);
        Assert.Equal("Light", settings.ThemeMode);
        Assert.Equal("DesktopWallpaper", settings.BackdropType);
        
        // Missing fields should fall back to default property initializers
        Assert.Equal(64, settings.GridBaseSize);
        Assert.Equal(8, settings.TileGap);
        Assert.Equal(2, settings.TileCornerRadius);
        Assert.Equal(0.85, settings.AcrylicOpacity, 2);
        Assert.NotNull(settings.SidebarShortcuts);
    }

    [Fact]
    public void AppSettings_SidebarShortcuts_InitializesWithDefaultsWhenNullOrEmpty()
    {
        string emptyShortcutsJson = """
        {
            "ThemeMode": "Dark",
            "SidebarShortcuts": []
        }
        """;

        var settings = JsonSerializer.Deserialize<AppSettings>(emptyShortcutsJson, s_jsonOptions);
        Assert.NotNull(settings);

        // Mimic StorageService.LoadSettings() safety check
        if (settings.SidebarShortcuts == null || settings.SidebarShortcuts.Count == 0)
        {
            settings.SidebarShortcuts = SidebarShortcutItem.CreateDefaultList();
        }

        Assert.NotEmpty(settings.SidebarShortcuts);
        Assert.Contains(settings.SidebarShortcuts, s => s.Id == "default_explorer");
    }

    [Theory]
    [InlineData(0)] // Metro Sharp
    [InlineData(2)] // Subtle Edge
    [InlineData(4)] // Medium
    [InlineData(8)] // Fluent Rounded
    public void AppSettings_TileCornerRadius_AcceptsDesignTokens(int cornerRadius)
    {
        var settings = new AppSettings { TileCornerRadius = cornerRadius };
        Assert.Equal(cornerRadius, settings.TileCornerRadius);
    }
}
