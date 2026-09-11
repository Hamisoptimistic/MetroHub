using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public class StorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroHub");

    private static readonly string LayoutPath = Path.Combine(AppDataDir, "layout.json");
    private static readonly string GroupsPath = Path.Combine(AppDataDir, "groups.json");
    private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
    private static readonly string AppsCachePath = Path.Combine(AppDataDir, "apps_cache.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    static StorageService()
    {
        try
        {
            if (!Directory.Exists(AppDataDir))
            {
                Directory.CreateDirectory(AppDataDir);
            }
        }
        catch { }
    }

    public static ObservableCollection<TileModel> LoadLayout()
    {
        try
        {
            if (File.Exists(LayoutPath))
            {
                string json = File.ReadAllText(LayoutPath);
                var tiles = JsonSerializer.Deserialize<ObservableCollection<TileModel>>(json, JsonOptions);
                if (tiles != null && tiles.Count > 0)
                {
                    // Auto-arrange if legacy layout had no X/Y coordinates or tiles stacked at 0,0
                    if (tiles.Count(t => t.X == 0 && t.Y == 0) > 1 || tiles.All(t => t.X == 0 && t.Y == 0))
                    {
                        double currentX = 40;
                        double currentY = 40;
                        foreach (var tile in tiles)
                        {
                            if (tile.X == 0 && tile.Y == 0)
                            {
                                tile.X = currentX;
                                tile.Y = currentY;
                                currentX += tile.WidthPixels + 16;
                                if (currentX > 1100)
                                {
                                    currentX = 40;
                                    currentY += 136;
                                }
                            }
                            else
                            {
                                currentX = Math.Max(currentX, tile.X + tile.WidthPixels + 16);
                            }
                        }
                        SaveLayout(tiles);
                    }

                    bool iconsRefreshed = false;
                    foreach (var tile in tiles)
                    {
                        if (tile.Col < 0 || tile.Row < 1 || tile.Y < GridPlacementService.PixelYFromRow(1))
                        {
                            tile.Col = Math.Max(0, tile.Col);
                            tile.Row = Math.Max(1, tile.Row);
                            tile.X = GridPlacementService.PixelXFromCol(tile.Col);
                            tile.Y = GridPlacementService.PixelYFromRow(tile.Row);
                            iconsRefreshed = true;
                        }

                        if (!string.IsNullOrWhiteSpace(tile.TargetPath))
                        {
                            string? highRes = IconExtractorService.ExtractAndCacheIcon(tile.TargetPath);
                            if (!string.IsNullOrWhiteSpace(highRes) && tile.IconPath != highRes)
                            {
                                tile.IconPath = highRes;
                                iconsRefreshed = true;
                            }
                        }
                    }
                    if (iconsRefreshed)
                    {
                        SaveLayout(tiles);
                    }

                    return tiles;
                }
            }
        }
        catch { }

        // Fallback: create fresh default starter template
        var defaultLayout = AppScannerService.GenerateStarterTemplate();
        SaveLayout(defaultLayout);
        return defaultLayout;
    }

    public static void SaveLayout(ObservableCollection<TileModel> tiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            string tmpPath = LayoutPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, LayoutPath, overwrite: true);
        }
        catch { }
    }

    public static ObservableCollection<TileGroupModel> LoadGroups()
    {
        try
        {
            if (File.Exists(GroupsPath))
            {
                string json = File.ReadAllText(GroupsPath);
                var groups = JsonSerializer.Deserialize<ObservableCollection<TileGroupModel>>(json, JsonOptions);
                if (groups != null)
                {
                    bool sanitized = false;
                    foreach (var g in groups)
                    {
                        if (g.Col < 0 || g.Row < 0 || g.Y < GridPlacementService.OriginY + 8)
                        {
                            g.Col = Math.Max(0, g.Col);
                            g.Row = Math.Max(0, g.Row);
                            g.X = GridPlacementService.PixelXFromCol(g.Col);
                            g.Y = GridPlacementService.PixelYFromRow(g.Row) + 8;
                            sanitized = true;
                        }
                    }
                    if (sanitized) SaveGroups(groups);
                    return groups;
                }
            }
        }
        catch { }

        return new ObservableCollection<TileGroupModel>();
    }

    public static void SaveGroups(ObservableCollection<TileGroupModel> groups)
    {
        try
        {
            string json = JsonSerializer.Serialize(groups, JsonOptions);
            string tmpPath = GroupsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, GroupsPath, overwrite: true);
        }
        catch { }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (settings != null) return settings;
            }
        }
        catch { }

        var def = new AppSettings();
        SaveSettings(def);
        return def;
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string tmpPath = SettingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch { }
    }

    public static bool ExportLayout(ObservableCollection<TileModel> tiles, string targetFilePath)
    {
        try
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            File.WriteAllText(targetFilePath, json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static ObservableCollection<TileModel>? ImportLayout(string sourceFilePath)
    {
        try
        {
            if (File.Exists(sourceFilePath))
            {
                string json = File.ReadAllText(sourceFilePath);
                var tiles = JsonSerializer.Deserialize<ObservableCollection<TileModel>>(json, JsonOptions);
                if (tiles != null && tiles.Count > 0)
                {
                    SaveLayout(tiles);
                    return tiles;
                }
            }
        }
        catch { }

        return null;
    }

    public static List<CatalogItemModel>? LoadAppsCache()
    {
        try
        {
            if (File.Exists(AppsCachePath))
            {
                string json = File.ReadAllText(AppsCachePath);
                return JsonSerializer.Deserialize<List<CatalogItemModel>>(json, JsonOptions);
            }
        }
        catch { }

        return null;
    }

    public static void SaveAppsCache(IEnumerable<CatalogItemModel> apps)
    {
        try
        {
            string json = JsonSerializer.Serialize(apps, JsonOptions);
            File.WriteAllText(AppsCachePath, json);
        }
        catch { }
    }
}
