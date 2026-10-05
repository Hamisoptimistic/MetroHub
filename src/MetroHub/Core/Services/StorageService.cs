using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

public sealed class StorageService
{
    private static string AppDataDir => AppPaths.AppDataDir;

    private static string LayoutPath => AppPaths.LayoutPath;
    private static string LayoutBakPath => AppPaths.LayoutBakPath;
    private static string GroupsPath => AppPaths.GroupsPath;
    private static string GroupsBakPath => AppPaths.GroupsBakPath;
    private static string SettingsPath => AppPaths.SettingsPath;
    private static string SettingsBakPath => AppPaths.SettingsBakPath;
    private static string AppsCachePath => AppPaths.AppsCachePath;
    private static string AppsCacheBakPath => AppPaths.AppsCacheBakPath;
    private static string IconCacheDir => AppPaths.IconCacheDir;

    private static readonly object WriteLock = new();

    private static volatile string? _pendingLayoutJson;
    private static volatile string? _pendingGroupsJson;
    private static volatile string? _pendingSettingsJson;
    private static volatile string? _pendingWorkspacesManifestJson;
    private static readonly ConcurrentDictionary<string, string> _pendingWorkspaceLayouts = new();
    private static readonly ConcurrentDictionary<string, string> _pendingWorkspaceGroups = new();
    private static readonly object _flushGate = new();
    private static Task? _backgroundFlushTask;

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
        Safe.Try(() =>
        {
            if (!Directory.Exists(AppDataDir))
            {
                Directory.CreateDirectory(AppDataDir);
            }
        }, context: "StorageService.InitDirectory");
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
                AppPaths.EnsureDirectory(targetPath);
                AppPaths.EnsureDirectory(bakPath);

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
                    bool currentTargetIsHealthy = Safe.Try(() =>
                    {
                        var fi = new FileInfo(targetPath);
                        return fi.Length > 2; // "[]" is 2 bytes; anything > 2 has real content
                    }, fallback: false, context: $"StorageService.CheckTargetHealth({targetPath})");

                    if (currentTargetIsHealthy)
                    {
                        try
                        {
                            File.Replace(tmpPath, targetPath, bakPath, ignoreMetadataErrors: true);
                            return; // Success — .tmp is consumed by File.Replace
                        }
                        catch (Exception ex)
                        {
                            Safe.Logger(ex, $"StorageService.SaveAtomic.ReplaceFallback({targetPath})");
                            // Fallback: manual copy + move if ReplaceFile fails (non-NTFS, network drives)
                            Safe.Try(() => File.Copy(targetPath, bakPath, overwrite: true), context: $"StorageService.SaveAtomic.CopyBakFallback({targetPath})");
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
                Safe.Logger(ex, $"StorageService.SaveAtomic({targetPath})");
                System.Diagnostics.Debug.WriteLine($"[StorageService] SaveAtomic failed for {targetPath}: {ex.Message}");
                // Last-resort: only write directly if the target doesn't exist yet.
                // If the target already exists, it may be healthy — truncating it would destroy good data.
                if (!File.Exists(targetPath))
                {
                    Safe.Try(() => File.WriteAllText(targetPath, content, Utf8NoBom), context: $"StorageService.SaveAtomic.LastResortWrite({targetPath})");
                }
            }
            finally
            {
                // Clean up .tmp if it still lingers
                Safe.Try(() =>
                {
                    if (File.Exists(tmpPath))
                    {
                        File.Delete(tmpPath);
                    }
                }, context: $"StorageService.SaveAtomic.DeleteTmp({tmpPath})");
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
        return Safe.Try(() =>
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
            return null;
        }, fallback: null, context: $"StorageService.TryDeserializeFile<{typeof(T).Name}>({filePath})");
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
                                        !File.Exists(tile.IconPath);

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
        Safe.Try(() =>
        {
            string dirPath = Path.GetDirectoryName(filePath) ?? AppDataDir;
            if (filePath.Contains(AppPaths.ConfigDir, StringComparison.OrdinalIgnoreCase))
            {
                dirPath = AppPaths.BackupsDir;
            }

            AppPaths.EnsureDirectory(Path.Combine(dirPath, "dummy.txt"));
            var dir = new DirectoryInfo(dirPath);
            string baseName = Path.GetFileNameWithoutExtension(filePath);
            var existing = dir.GetFiles($"{baseName}_corrupt_*.json")
                              .OrderByDescending(f => f.LastWriteTimeUtc)
                              .ToArray();
            for (int i = MaxCorruptCopies - 1; i < existing.Length; i++)
            {
                var fileToDelete = existing[i];
                Safe.Try(() => fileToDelete.Delete(), context: $"StorageService.PreserveCorruptFile.DeleteOld({fileToDelete.FullName})");
            }

            string corruptCopy = Path.Combine(dirPath, $"{baseName}_corrupt_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            File.Copy(filePath, corruptCopy, overwrite: true);
        }, context: $"StorageService.PreserveCorruptFile({filePath})");
    }

    private static void ScheduleBackgroundFlush()
    {
        lock (_flushGate)
        {
            if (_backgroundFlushTask == null || _backgroundFlushTask.IsCompleted)
            {
                _backgroundFlushTask = Task.Run(async () =>
                {
                    while (true)
                    {
                        // 50ms coalesce delay: groups rapid drag/drop/resize bursts into 1 disk write
                        await Task.Delay(50).ConfigureAwait(false);
                        FlushPending();

                        lock (_flushGate)
                        {
                            if (_pendingLayoutJson == null && _pendingGroupsJson == null && _pendingSettingsJson == null &&
                                _pendingWorkspacesManifestJson == null && _pendingWorkspaceLayouts.IsEmpty && _pendingWorkspaceGroups.IsEmpty)
                            {
                                _backgroundFlushTask = null;
                                break;
                            }
                        }
                    }
                });
            }
        }
    }

    private static void FlushPending()
    {
        lock (WriteLock)
        {
            var layout = Interlocked.Exchange(ref _pendingLayoutJson, null);
            if (layout != null)
            {
                SaveAtomic(LayoutPath, LayoutBakPath, layout);
            }

            var groups = Interlocked.Exchange(ref _pendingGroupsJson, null);
            if (groups != null)
            {
                SaveAtomic(GroupsPath, GroupsBakPath, groups);
            }

            var settings = Interlocked.Exchange(ref _pendingSettingsJson, null);
            if (settings != null)
            {
                SaveAtomic(SettingsPath, SettingsBakPath, settings);
            }

            var manifestJson = Interlocked.Exchange(ref _pendingWorkspacesManifestJson, null);
            if (manifestJson != null)
            {
                SaveAtomic(AppPaths.WorkspacesManifestPath, AppPaths.WorkspacesManifestBakPath, manifestJson);
            }

            if (!_pendingWorkspaceLayouts.IsEmpty)
            {
                foreach (var kvp in _pendingWorkspaceLayouts)
                {
                    if (_pendingWorkspaceLayouts.TryRemove(kvp.Key, out var wsLayoutJson))
                    {
                        SaveAtomic(AppPaths.GetWorkspaceLayoutPath(kvp.Key), AppPaths.GetWorkspaceLayoutBakPath(kvp.Key), wsLayoutJson);
                    }
                }
            }

            if (!_pendingWorkspaceGroups.IsEmpty)
            {
                foreach (var kvp in _pendingWorkspaceGroups)
                {
                    if (_pendingWorkspaceGroups.TryRemove(kvp.Key, out var wsGroupsJson))
                    {
                        SaveAtomic(AppPaths.GetWorkspaceGroupsPath(kvp.Key), AppPaths.GetWorkspaceGroupsBakPath(kvp.Key), wsGroupsJson);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Synchronously drains any pending asynchronous saves to disk.
    /// Call on app shutdown or session ending to guarantee zero data loss.
    /// </summary>
    public static void Flush()
    {
        FlushPending();
        Task? task;
        lock (_flushGate)
        {
            task = _backgroundFlushTask;
        }
        if (task != null && !task.IsCompleted)
        {
            Safe.Try(() => task.Wait(1000), context: "StorageService.Flush.Wait");
        }
    }

    /// <summary>
    /// Discards any pending in-memory write buffers without writing them to disk.
    /// Used during test cleanup or teardown to prevent delayed asynchronous flushes from leaking.
    /// </summary>
    public static void ResetPending()
    {
        lock (WriteLock)
        {
            _pendingLayoutJson = null;
            _pendingGroupsJson = null;
            _pendingSettingsJson = null;
            _pendingWorkspacesManifestJson = null;
            _pendingWorkspaceLayouts.Clear();
            _pendingWorkspaceGroups.Clear();
        }
    }

    public static void SaveLayout(ObservableCollection<TileModel> tiles)
    {
        try
        {
            // Snapshot in-memory on UI thread immediately (< 0.2ms)
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            _pendingLayoutJson = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveLayout failed: {ex.Message}");
        }
    }

    public static void SaveLayoutSync(ObservableCollection<TileModel> tiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            lock (WriteLock)
            {
                _pendingLayoutJson = null;
                SaveAtomic(LayoutPath, LayoutBakPath, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveLayoutSync failed: {ex.Message}");
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
            // Snapshot in-memory on UI thread immediately (< 0.1ms)
            string json = JsonSerializer.Serialize(groups, JsonOptions);
            _pendingGroupsJson = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveGroups failed: {ex.Message}");
        }
    }

    public static void SaveGroupsSync(ObservableCollection<TileGroupModel> groups)
    {
        try
        {
            string json = JsonSerializer.Serialize(groups, JsonOptions);
            lock (WriteLock)
            {
                _pendingGroupsJson = null;
                SaveAtomic(GroupsPath, GroupsBakPath, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveGroupsSync failed: {ex.Message}");
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
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            _pendingSettingsJson = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveSettings failed: {ex.Message}");
        }
    }

    public static void SaveSettingsSync(AppSettings settings)
    {
        try
        {
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            lock (WriteLock)
            {
                _pendingSettingsJson = null;
                SaveAtomic(SettingsPath, SettingsBakPath, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveSettingsSync failed: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────
    // Export / Import
    // ────────────────────────────────────────────────────────

    public static bool ExportLayout(ObservableCollection<TileModel> tiles, string targetFilePath)
    {
        return Safe.Try(() =>
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            File.WriteAllText(targetFilePath, json);
        }, context: $"StorageService.ExportLayout({targetFilePath})");
    }

    public static ObservableCollection<TileModel>? ImportLayout(string sourceFilePath)
    {
        return Safe.Try(() =>
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
            return null;
        }, fallback: null, context: $"StorageService.ImportLayout({sourceFilePath})");
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
        Safe.Try(() =>
        {
            SerializeAndSaveAtomic(apps, AppsCachePath, AppsCacheBakPath);
        }, context: "StorageService.SaveAppsCache");
    }

    // ────────────────────────────────────────────────────────
    // Workspaces
    // ────────────────────────────────────────────────────────

    public static WorkspacesManifest LoadWorkspaces()
    {
        WorkspacesManifest? manifest = TryDeserializeFile<WorkspacesManifest>(AppPaths.WorkspacesManifestPath);

        if (manifest == null)
        {
            manifest = TryDeserializeFile<WorkspacesManifest>(AppPaths.WorkspacesManifestBakPath);
            if (manifest != null)
            {
                System.Diagnostics.Debug.WriteLine("[StorageService] Workspaces manifest recovered from .bak");
            }
        }

        // If manifest doesn't exist or is empty, perform non-destructive copy migration
        if (manifest == null || manifest.Workspaces.Count == 0)
        {
            manifest = MigrateLegacyToWorkspaces();
        }

        // Defensive: ensure at least one workspace exists
        if (manifest.Workspaces.Count == 0)
        {
            var fallback = new WorkspaceModel
            {
                Id = "default",
                Name = "Main",
                Order = 0,
                IconSymbol = "Desktop24"
            };
            manifest.Workspaces.Add(fallback);
            manifest.ActiveWorkspaceId = fallback.Id;
            SaveWorkspacesSync(manifest);
        }

        // If ActiveWorkspaceId does not match any workspace, fallback to first
        if (!manifest.Workspaces.Any(w => w.Id == manifest.ActiveWorkspaceId))
        {
            manifest.ActiveWorkspaceId = manifest.Workspaces[0].Id;
        }

        // Populate tiles and groups for each workspace in-memory
        foreach (var ws in manifest.Workspaces)
        {
            ws.Tiles.Clear();
            var tiles = LoadWorkspaceLayout(ws.Id);
            foreach (var t in tiles)
            {
                ws.Tiles.Add(t);
            }

            ws.Groups.Clear();
            var groups = LoadWorkspaceGroups(ws.Id);
            foreach (var g in groups)
            {
                ws.Groups.Add(g);
            }

            ws.IsActive = (ws.Id == manifest.ActiveWorkspaceId);
            ws.IsDirty = false;
        }

        return manifest;
    }

    /// <summary>
    /// Non-destructive migration: copies legacy layout.json and groups.json into workspaces\default\
    /// without deleting or renaming the root files. Guarantees safe rollbacks.
    /// </summary>
    private static WorkspacesManifest MigrateLegacyToWorkspaces()
    {
        string defaultDir = AppPaths.GetWorkspaceDir("default");
        AppPaths.EnsureDirectory(Path.Combine(defaultDir, "dummy.txt"));

        string targetLayout = AppPaths.GetWorkspaceLayoutPath("default");
        string targetGroups = AppPaths.GetWorkspaceGroupsPath("default");

        // 1. Copy layout.json -> workspaces\default\layout.json if legacy exists and non-empty
        if (File.Exists(LayoutPath) && new FileInfo(LayoutPath).Length > 2)
        {
            if (!File.Exists(targetLayout))
            {
                Safe.Try(() => File.Copy(LayoutPath, targetLayout, overwrite: false), context: "StorageService.MigrateLegacy.CopyLayout");
            }
        }

        // 2. Copy groups.json -> workspaces\default\groups.json if legacy exists and non-empty
        if (File.Exists(GroupsPath) && new FileInfo(GroupsPath).Length > 2)
        {
            if (!File.Exists(targetGroups))
            {
                Safe.Try(() => File.Copy(GroupsPath, targetGroups, overwrite: false), context: "StorageService.MigrateLegacy.CopyGroups");
            }
        }

        // If layout copy does not exist or failed, generate starter template
        if (!File.Exists(targetLayout) || new FileInfo(targetLayout).Length <= 2)
        {
            var starter = AppScannerService.GenerateStarterTemplate();
            SaveWorkspaceLayoutSync("default", starter);
        }

        var defaultWs = new WorkspaceModel
        {
            Id = "default",
            Name = "Main",
            Order = 0,
            IconSymbol = "Desktop24",
            IsActive = true
        };

        var manifest = new WorkspacesManifest
        {
            ActiveWorkspaceId = "default",
            Workspaces = new List<WorkspaceModel> { defaultWs }
        };

        SaveWorkspacesSync(manifest);
        return manifest;
    }

    public static void SaveWorkspaces(WorkspacesManifest manifest)
    {
        try
        {
            string json = JsonSerializer.Serialize(manifest, JsonOptions);
            _pendingWorkspacesManifestJson = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspaces failed: {ex.Message}");
        }
    }

    public static void SaveWorkspacesSync(WorkspacesManifest manifest)
    {
        try
        {
            string json = JsonSerializer.Serialize(manifest, JsonOptions);
            lock (WriteLock)
            {
                _pendingWorkspacesManifestJson = null;
                SaveAtomic(AppPaths.WorkspacesManifestPath, AppPaths.WorkspacesManifestBakPath, json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspacesSync failed: {ex.Message}");
        }
    }

    public static ObservableCollection<TileModel> LoadWorkspaceLayout(string workspaceId)
    {
        string layoutPath = AppPaths.GetWorkspaceLayoutPath(workspaceId);
        string layoutBakPath = AppPaths.GetWorkspaceLayoutBakPath(workspaceId);

        ObservableCollection<TileModel>? tiles = TryDeserializeFile<ObservableCollection<TileModel>>(layoutPath);
        bool recoveredFromBackup = false;

        if (tiles == null)
        {
            tiles = TryDeserializeFile<ObservableCollection<TileModel>>(layoutBakPath);
            if (tiles != null)
            {
                recoveredFromBackup = true;
                System.Diagnostics.Debug.WriteLine($"[StorageService] Workspace '{workspaceId}' layout recovered from .bak");
            }
        }

        if (tiles == null)
        {
            tiles = TryDeserializeFile<ObservableCollection<TileModel>>(layoutPath + ".tmp");
            if (tiles != null)
            {
                recoveredFromBackup = true;
                System.Diagnostics.Debug.WriteLine($"[StorageService] Workspace '{workspaceId}' layout recovered from .tmp");
            }
        }

        if (tiles == null)
        {
            PreserveCorruptFile(layoutPath);
            var defaultLayout = AppScannerService.GenerateStarterTemplate();
            SaveWorkspaceLayoutSync(workspaceId, defaultLayout);
            return defaultLayout;
        }

        if (tiles.Count == 0)
        {
            if (recoveredFromBackup) SaveWorkspaceLayoutSync(workspaceId, tiles);
            return tiles;
        }

        bool dirty = recoveredFromBackup;
        dirty |= NormalizeTiles(tiles);

        if (dirty)
        {
            SaveWorkspaceLayout(workspaceId, tiles);
        }

        return tiles;
    }

    public static void SaveWorkspaceLayout(string workspaceId, ObservableCollection<TileModel> tiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            _pendingWorkspaceLayouts[workspaceId] = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspaceLayout failed for '{workspaceId}': {ex.Message}");
        }
    }

    public static void SaveWorkspaceLayoutSync(string workspaceId, ObservableCollection<TileModel> tiles)
    {
        try
        {
            string json = JsonSerializer.Serialize(tiles, JsonOptions);
            lock (WriteLock)
            {
                _pendingWorkspaceLayouts.TryRemove(workspaceId, out _);
                SaveAtomic(AppPaths.GetWorkspaceLayoutPath(workspaceId), AppPaths.GetWorkspaceLayoutBakPath(workspaceId), json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspaceLayoutSync failed for '{workspaceId}': {ex.Message}");
        }
    }

    public static ObservableCollection<TileGroupModel> LoadWorkspaceGroups(string workspaceId)
    {
        string groupsPath = AppPaths.GetWorkspaceGroupsPath(workspaceId);
        string groupsBakPath = AppPaths.GetWorkspaceGroupsBakPath(workspaceId);

        ObservableCollection<TileGroupModel>? groups = TryDeserializeFile<ObservableCollection<TileGroupModel>>(groupsPath);

        if (groups == null)
        {
            groups = TryDeserializeFile<ObservableCollection<TileGroupModel>>(groupsBakPath);
            if (groups != null)
            {
                System.Diagnostics.Debug.WriteLine($"[StorageService] Workspace '{workspaceId}' groups recovered from .bak");
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
        if (sanitized) SaveWorkspaceGroups(workspaceId, groups);
        return groups;
    }

    public static void SaveWorkspaceGroups(string workspaceId, ObservableCollection<TileGroupModel> groups)
    {
        try
        {
            string json = JsonSerializer.Serialize(groups, JsonOptions);
            _pendingWorkspaceGroups[workspaceId] = json;
            ScheduleBackgroundFlush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspaceGroups failed for '{workspaceId}': {ex.Message}");
        }
    }

    public static void SaveWorkspaceGroupsSync(string workspaceId, ObservableCollection<TileGroupModel> groups)
    {
        try
        {
            string json = JsonSerializer.Serialize(groups, JsonOptions);
            lock (WriteLock)
            {
                _pendingWorkspaceGroups.TryRemove(workspaceId, out _);
                SaveAtomic(AppPaths.GetWorkspaceGroupsPath(workspaceId), AppPaths.GetWorkspaceGroupsBakPath(workspaceId), json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StorageService] SaveWorkspaceGroupsSync failed for '{workspaceId}': {ex.Message}");
        }
    }

    public static void SaveWorkspaceSync(WorkspaceModel workspace)
    {
        SaveWorkspaceLayoutSync(workspace.Id, workspace.Tiles);
        SaveWorkspaceGroupsSync(workspace.Id, workspace.Groups);
    }

    /// <summary>
    /// Moves a deleted workspace directory to %LocalAppData%\MetroHub\workspaces_trash\ with a timestamp.
    /// Non-destructive: preserves all data for potential manual recovery.
    /// </summary>
    public static void DeleteWorkspaceStorage(string workspaceId)
    {
        lock (WriteLock)
        {
            _pendingWorkspaceLayouts.TryRemove(workspaceId, out _);
            _pendingWorkspaceGroups.TryRemove(workspaceId, out _);

            string srcDir = AppPaths.GetWorkspaceDir(workspaceId);
            if (Directory.Exists(srcDir))
            {
                Safe.Try(() =>
                {
                    string trashDir = AppPaths.WorkspacesTrashDir;
                    AppPaths.EnsureDirectory(Path.Combine(trashDir, "dummy.txt"));
                    string destDir = Path.Combine(trashDir, $"{workspaceId}_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
                    Directory.Move(srcDir, destDir);
                }, context: $"StorageService.DeleteWorkspaceStorage({workspaceId})");
            }
        }
    }
}

