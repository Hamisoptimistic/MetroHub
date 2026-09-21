using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public sealed class StorageService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroHub");

    private static readonly string LayoutPath = Path.Combine(AppDataDir, "layout.json");
    private static readonly string LayoutBakPath = Path.Combine(AppDataDir, "layout.json.bak");
    private static readonly string GroupsPath = Path.Combine(AppDataDir, "groups.json");
    private static readonly string GroupsBakPath = Path.Combine(AppDataDir, "groups.json.bak");
    private static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
    private static readonly string SettingsBakPath = Path.Combine(AppDataDir, "settings.json.bak");
    private static readonly string AppsCachePath = Path.Combine(AppDataDir, "apps_cache.json");
    private static readonly string AppsCacheBakPath = Path.Combine(AppDataDir, "apps_cache.json.bak");
    private static readonly string IconCacheDir = Path.Combine(AppDataDir, "icons");

    private static readonly object WriteLock = new();

    // BOM-free UTF-8 encoding to avoid the 3-byte preamble (EF BB BF)
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Cap the number of corrupt diagnostic copies to prevent unbounded disk growth
    private const int MaxCorruptCopies = 5;

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

    // ────────────────────────────────────────────────────────
    // Atomic Persistence (Win32 ReplaceFileW)
    // ────────────────────────────────────────────────────────

    private static void SaveAtomic(string targetPath, string bakPath, string content)
    {
        lock (WriteLock)
        {
            string tmpPath = targetPath + ".tmp";
            try
            {
                if (!Directory.Exists(AppDataDir))
                {
                    Directory.CreateDirectory(AppDataDir);
                }

                // 1. Write content to .tmp with WriteThrough and flush to physical disk
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(fs, Utf8NoBom))
                {
                    writer.Write(content);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                // 2. ReplaceFileW (via File.Replace) — swaps .tmp into target and rotates the
                //    previous target into .bak in a single coordinated OS call, preserving file
                //    metadata (creation timestamp, ACLs, object IDs). Note: while ReplaceFileW
                //    provides strong durability guarantees on NTFS, it is not formally ACID-
                //    transactional; a power loss during the kernel call could theoretically leave
                //    the .bak incomplete. The .tmp → target rename is the durable commit point.
                //
                //    Safety: only rotate into .bak if the current target parses and is non-empty,
                //    so a single bad save never destroys the last known-good backup.
                if (File.Exists(targetPath))
                {
                    bool currentTargetIsHealthy = false;
                    try
                    {
                        var fi = new FileInfo(targetPath);
                        currentTargetIsHealthy = fi.Length > 2; // "[]" is 2 bytes; anything > 2 has real content
                    }
                    catch { }

                    if (currentTargetIsHealthy)
                    {
                        try
                        {
                            File.Replace(tmpPath, targetPath, bakPath, ignoreMetadataErrors: true);
                            return; // Success — .tmp is consumed by File.Replace
                        }
                        catch
                        {
                            // Fallback: manual copy + move if ReplaceFile fails (non-NTFS, network drives)
                            try { File.Copy(targetPath, bakPath, overwrite: true); } catch { }
                            File.Move(tmpPath, targetPath, overwrite: true);
                            return;
                        }
                    }
                    else
                    {
                        // Current target is empty/corrupt — don't rotate it into .bak
                        File.Move(tmpPath, targetPath, overwrite: true);
                        return;
                    }
                }
                else
                {
                    // First run: target does not exist yet
                    File.Move(tmpPath, targetPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StorageService] SaveAtomic failed for {targetPath}: {ex.Message}");
                // Last-resort: only write directly if the target doesn't exist yet.
                // If the target already exists, it may be healthy — truncating it would destroy good data.
                if (!File.Exists(targetPath))
                {
                    try { File.WriteAllText(targetPath, content, Utf8NoBom); } catch { }
                }
            }
            finally
            {
                // Clean up .tmp if it still lingers
                try
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Serializes the object and saves atomically. Serialization happens inside the write lock
    /// so two racing saves cannot land in the wrong order (older snapshot overwriting newer).
    /// </summary>
    private static void SerializeAndSaveAtomic<T>(T data, string targetPath, string bakPath)
    {
        lock (WriteLock)
        {
            string json = JsonSerializer.Serialize(data, JsonOptions);
            // SaveAtomic also locks on WriteLock, but Monitor is reentrant so this is safe.
            SaveAtomic(targetPath, bakPath, json);
        }
    }

    // ────────────────────────────────────────────────────────
    // Deserialization with fallback chain
    // ────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to deserialize a JSON file. Returns null on any failure (missing, empty, corrupt).
    /// Does NOT treat an empty collection as failure — that's a valid user state.
    /// </summary>
    private static T? TryDeserializeFile<T>(string filePath) where T : class
    {
        try
        {
            if (File.Exists(filePath))
            {
                var fi = new FileInfo(filePath);
                if (fi.Length > 0)
                {
                    string json = File.ReadAllText(filePath);
                    return JsonSerializer.Deserialize<T>(json, JsonOptions);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] Error deserializing {filePath}: {ex.Message}");
        }
        return null;
    }

    // ────────────────────────────────────────────────────────
    // Layout
    // ────────────────────────────────────────────────────────

    public static ObservableCollection<TileModel> LoadLayout()
    {
        // ── Phase 1: Load with fallback chain ──
        // null  = parse failure / file missing / zero bytes (corrupt)
        // empty = user intentionally cleared all tiles (valid state, do NOT resurrect old tiles)
        ObservableCollection<TileModel>? tiles = TryDeserializeFile<ObservableCollection<TileModel>>(LayoutPath);
        bool recoveredFromBackup = false;

        if (tiles == null)
        {
            // Primary is corrupt or missing — try .bak
            tiles = TryDeserializeFile<ObservableCollection<TileModel>>(LayoutBakPath);
            if (tiles != null)
            {
                recoveredFromBackup = true;
                System.Diagnostics.Debug.WriteLine("[StorageService] Layout recovered from .bak");
            }
        }

        if (tiles == null)
        {
            // .bak also failed — try .tmp (incomplete atomic write that has valid JSON)
            tiles = TryDeserializeFile<ObservableCollection<TileModel>>(LayoutPath + ".tmp");
            if (tiles != null)
            {
                recoveredFromBackup = true;
                System.Diagnostics.Debug.WriteLine("[StorageService] Layout recovered from .tmp");
            }
        }

        // All sources failed — preserve corrupt primary for diagnostics, then generate starter
        if (tiles == null)
        {
            PreserveCorruptFile(LayoutPath);
            var defaultLayout = AppScannerService.GenerateStarterTemplate();
            SaveLayout(defaultLayout);
            return defaultLayout;
        }

        // Valid empty layout: user deleted all tiles. Accept it.
        if (tiles.Count == 0)
        {
            if (recoveredFromBackup) SaveLayout(tiles);
            return tiles;
        }

        // ── Phase 2: Normalize (single dirty flag, single save at the end) ──
        bool dirty = recoveredFromBackup;
        dirty |= NormalizeTiles(tiles);

        // ── Phase 3: Persist once if anything changed ──
        if (dirty)
        {
            SaveLayout(tiles);
        }

        return tiles;
    }

    /// <summary>
    /// Shared normalization pipeline for tiles — used by both LoadLayout and ImportLayout.
    /// Returns true if any tile was modified and the layout should be persisted.
    /// </summary>
    private static bool NormalizeTiles(ObservableCollection<TileModel> tiles)
    {
        bool dirty = false;

        // ── Step 1: Sanitize out-of-bounds Col/Row FIRST ──
        // This must run before auto-arrange so that legacy tiles with Row=0
        // get pushed to valid positions before any pixel-based arrangement.
        foreach (var tile in tiles)
        {
            if (tile.Col < 0 || tile.Row < 1 || tile.Y < GridPlacementService.PixelYFromRow(1))
            {
                tile.Col = Math.Max(0, tile.Col);
                tile.Row = Math.Max(1, tile.Row);
                tile.X = GridPlacementService.PixelXFromCol(tile.Col);
                tile.Y = GridPlacementService.PixelYFromRow(tile.Row);
                dirty = true;
            }
        }

        // ── Step 2: Auto-arrange if tiles are all stacked at the same position ──
        // Runs AFTER sanitizer so Col/Row are valid. Sets both pixel AND grid coords
        // so the sanitizer won't undo the arrangement on a future load.
        if (tiles.Count > 1 &&
            (tiles.Count(t => t.X == tiles[0].X && t.Y == tiles[0].Y) > 1 ||
             tiles.All(t => t.Col == 0 && t.Row == 1)))
        {
            double currentX = GridPlacementService.PixelXFromCol(0);
            double currentY = GridPlacementService.PixelYFromRow(1);
            foreach (var tile in tiles)
            {
                tile.X = currentX;
                tile.Y = currentY;
                tile.Col = GridPlacementService.ColFromPixel(currentX);
                tile.Row = GridPlacementService.RowFromPixel(currentY);
                currentX += tile.WidthPixels + 16;
                if (currentX > 1100)
                {
                    currentX = GridPlacementService.PixelXFromCol(0);
                    currentY += 136;
                }
            }
            dirty = true;
        }

        // ── Step 3: Refresh icons ──
        foreach (var tile in tiles)
        {
            // Skip widget tiles; they render dynamic custom views
            if (tile.TileType == TileType.Widget)
            {
                continue;
            }

            // Web URLs: never pass to Windows Shell icon extractor; preserve or recover WebFavicon
            bool isWebUrl = tile.TileType == TileType.WebUrl ||
                (!string.IsNullOrWhiteSpace(tile.TargetPath) &&
                 (tile.TargetPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  tile.TargetPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                  tile.TargetPath.StartsWith("www.", StringComparison.OrdinalIgnoreCase)));

            if (isWebUrl)
            {
                tile.TileType = TileType.WebUrl;

                bool isBogusOrMissing = string.IsNullOrWhiteSpace(tile.IconPath) ||
                                        !File.Exists(tile.IconPath) ||
                                        Path.GetFileName(tile.IconPath).StartsWith("v5_", StringComparison.OrdinalIgnoreCase);

                string domain = WebFaviconService.ExtractDomain(tile.TargetPath);
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    string safeDomain = Regex.Replace(domain, @"[^a-zA-Z0-9_\-\.]", "_");
                    string hash = IconExtractorService.ComputeDeterministicHash(domain);
                    string cachedPath = Path.Combine(IconCacheDir, $"web_{safeDomain}_{hash}.png");

                    if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 200)
                    {
                        if (tile.IconPath != cachedPath)
                        {
                            if (!string.IsNullOrWhiteSpace(tile.IconPath) &&
                                Path.GetFileName(tile.IconPath).StartsWith("v5_", StringComparison.OrdinalIgnoreCase))
                            {
                                try { if (File.Exists(tile.IconPath)) File.Delete(tile.IconPath); } catch { }
                            }

                            tile.IconPath = cachedPath;
                            dirty = true;
                        }
                    }
                    else if (isBogusOrMissing)
                    {
                        // Fire-and-forget favicon fetch: only sets the tile's IconPath property.
                        // Does NOT call SaveLayout from the callback to avoid writing a stale
                        // snapshot over a newer collection if the layout was reloaded/imported
                        // while the HTTP request was in-flight.
                        string targetUrl = tile.TargetPath;
                        var targetTile = tile;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                string? fetched = await WebFaviconService.GetFaviconPathAsync(targetUrl).ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(fetched) && File.Exists(fetched))
                                {
                                    Application.Current?.Dispatcher.InvokeAsync(() =>
                                    {
                                        targetTile.IconPath = fetched;
                                    });
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[StorageService] Favicon fetch failed for {targetUrl}: {ex.Message}");
                            }
                        });
                    }
                }
                continue;
            }

            // Non-web tiles: only extract icon if the cached path is missing or invalid.
            // Avoids calling the full COM IShellItemImageFactory pipeline for every tile on every launch.
            if (!string.IsNullOrWhiteSpace(tile.TargetPath))
            {
                bool iconAlreadyValid = !string.IsNullOrWhiteSpace(tile.IconPath) && File.Exists(tile.IconPath);
                if (!iconAlreadyValid)
                {
                    string? highRes = IconExtractorService.ExtractAndCacheIcon(tile.TargetPath);
                    if (!string.IsNullOrWhiteSpace(highRes) && tile.IconPath != highRes)
                    {
                        tile.IconPath = highRes;
                        dirty = true;
                    }
                }
            }
        }

        return dirty;
    }

    /// <summary>
    /// Preserves a corrupt file as a timestamped diagnostic copy, capped at MaxCorruptCopies.
    /// </summary>
    private static void PreserveCorruptFile(string filePath)
    {
        if (!File.Exists(filePath)) return;
        try
        {
            // Prune old corrupt copies to stay within the cap
            var dir = new DirectoryInfo(AppDataDir);
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var existing = dir.GetFiles($"{baseName}_corrupt_*.json")
                              .OrderByDescending(f => f.LastWriteTimeUtc)
                              .ToArray();
            for (int i = MaxCorruptCopies - 1; i < existing.Length; i++)
            {
                try { existing[i].Delete(); } catch { }
            }

            string corruptCopy = Path.Combine(AppDataDir, $"{baseName}_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Copy(filePath, corruptCopy, overwrite: true);
        }
        catch { }
    }

    public static void SaveLayout(ObservableCollection<TileModel> tiles)
    {
        try
        {
            SerializeAndSaveAtomic(tiles, LayoutPath, LayoutBakPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveLayout failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // Groups
    // ────────────────────────────────────────────────────────

    public static ObservableCollection<TileGroupModel> LoadGroups()
    {
        ObservableCollection<TileGroupModel>? groups = TryDeserializeFile<ObservableCollection<TileGroupModel>>(GroupsPath);

        if (groups == null)
        {
            groups = TryDeserializeFile<ObservableCollection<TileGroupModel>>(GroupsBakPath);
            if (groups != null)
            {
                System.Diagnostics.Debug.WriteLine("[StorageService] Groups recovered from .bak");
            }
        }

        if (groups == null)
        {
            return new ObservableCollection<TileGroupModel>();
        }

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

    public static void SaveGroups(ObservableCollection<TileGroupModel> groups)
    {
        try
        {
            SerializeAndSaveAtomic(groups, GroupsPath, GroupsBakPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveGroups failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // Settings
    // ────────────────────────────────────────────────────────

    public static AppSettings LoadSettings()
    {
        AppSettings? settings = TryDeserializeFile<AppSettings>(SettingsPath);

        if (settings == null)
        {
            settings = TryDeserializeFile<AppSettings>(SettingsBakPath);
            if (settings != null)
            {
                System.Diagnostics.Debug.WriteLine("[StorageService] Settings recovered from .bak");
                PreserveCorruptFile(SettingsPath);
                SaveSettings(settings);
            }
        }

        if (settings != null)
        {
            settings.SidebarShortcuts ??= MetroHub.Core.Models.SidebarShortcutItem.CreateDefaultList();
            if (settings.SidebarShortcuts.Count == 0)
            {
                settings.SidebarShortcuts = MetroHub.Core.Models.SidebarShortcutItem.CreateDefaultList();
            }
            return settings;
        }

        var def = new AppSettings();
        SaveSettings(def);
        return def;
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            SerializeAndSaveAtomic(settings, SettingsPath, SettingsBakPath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveSettings failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // Export / Import
    // ────────────────────────────────────────────────────────

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
                    // Run the same normalization pipeline that LoadLayout uses
                    // so imported layouts from other machines get sanitized coords
                    // and missing local icon paths get re-extracted.
                    NormalizeTiles(tiles);
                    SaveLayout(tiles);
                    return tiles;
                }
            }
        }
        catch { }

        return null;
    }

    // ────────────────────────────────────────────────────────
    // Apps Cache
    // ────────────────────────────────────────────────────────

    public static List<CatalogItemModel>? LoadAppsCache()
    {
        // Symmetric: try primary, then .bak fallback
        List<CatalogItemModel>? apps = TryDeserializeFile<List<CatalogItemModel>>(AppsCachePath);
        if (apps != null) return apps;

        apps = TryDeserializeFile<List<CatalogItemModel>>(AppsCacheBakPath);
        return apps;
    }

    public static void SaveAppsCache(IEnumerable<CatalogItemModel> apps)
    {
        try
        {
            SerializeAndSaveAtomic(apps, AppsCachePath, AppsCacheBakPath);
        }
        catch { }
    }
}

