using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Result summary of an orphan asset cleanup operation.
/// </summary>
public sealed record SweepResult(int ScannedCount, int DeletedCount, long ReclaimedBytes);

/// <summary>
/// Immutable candidate metadata used for pure, deterministic orphan evaluation.
/// </summary>
public sealed record AssetFileInfo(string FullPath, DateTime CreatedUtc, DateTime LastWriteUtc, long Length);

/// <summary>
/// Result of an orphan evaluation for a single candidate file.
/// </summary>
public sealed record OrphanFile(string FullPath, long Length, bool IsStaleTemp);

/// <summary>
/// Mark & Sweep Garbage Collector for pasted assets (images and notes).
/// Safely identifies and deletes unreferenced files older than a grace period without breaking
/// active tiles, multi-workspace layouts, or in-memory Undo/Redo stacks.
/// </summary>
public static class PastedAssetCleanupService
{
    private static int _isSweeping;
    public static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromHours(24);
    public static readonly TimeSpan ThrottleInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Pure, side-effect-free evaluation function. Determines which files are eligible for deletion.
    /// Safe against empty reference sets (circuit breaker) and handles stale temp files.
    /// </summary>
    public static List<OrphanFile> SelectOrphans(
        IEnumerable<AssetFileInfo> files,
        IReadOnlySet<string> referencedAssets,
        DateTime nowUtc,
        TimeSpan gracePeriod)
    {
        var orphans = new List<OrphanFile>();

        // Circuit breaker: if reference set is completely empty, do NOT select any orphans.
        // An empty reference set indicates Mark phase failure or uninitialized layout.
        if (referencedAssets.Count == 0)
        {
            return orphans;
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.FullPath)) continue;

            string fullPath = file.FullPath;
            string fileName = Path.GetFileName(fullPath);

            // 1. Stale temporary paste files (*.tmp or *.tmp.*)
            bool isTmp = fullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                         fullPath.Contains(".tmp.", StringComparison.OrdinalIgnoreCase);

            if (isTmp)
            {
                if (nowUtc - file.LastWriteUtc > TimeSpan.FromHours(1))
                {
                    orphans.Add(new OrphanFile(fullPath, file.Length, IsStaleTemp: true));
                }
                continue;
            }

            // 2. Grace period check: protect recently created or touched files
            if ((nowUtc - file.CreatedUtc) < gracePeriod || (nowUtc - file.LastWriteUtc) < gracePeriod)
            {
                continue;
            }

            // 3. Reference set check: protect files referenced by full path or filename
            if (referencedAssets.Contains(fullPath) || referencedAssets.Contains(fileName))
            {
                continue;
            }

            // 4. Stale, unreferenced orphan
            orphans.Add(new OrphanFile(fullPath, file.Length, IsStaleTemp: false));
        }

        return orphans;
    }

    /// <summary>
    /// Collects all normalized file paths and file names currently referenced across:
    /// 1. Active in-memory tiles snapshot
    /// 2. In-memory undo/redo history snapshots
    /// 3. In-flight pending save buffers in StorageService
    /// 4. Primary disk layout (layout.json & layout.json.bak)
    /// 5. All workspace disk layouts ({wsId}/layout.json & layout.json.bak)
    ///
    /// Fail-closed guarantee: If any layout file exists but fails to read/parse, or workspace
    /// enumeration fails, this method returns false and sets referenced to null to abort the sweep.
    /// </summary>
    public static bool TryCollectReferencedPastedAssets(
        IReadOnlyCollection<TileModel>? activeTiles,
        IReadOnlyCollection<string>? historySnapshots,
        out HashSet<string>? referenced)
    {
        referenced = null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void InspectTile(TileModel? tile)
        {
            if (tile == null) return;
            InspectPath(tile.TargetPath);
            InspectPath(tile.IconPath);
        }

        void InspectPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string full = Path.GetFullPath(path);
                set.Add(full);
                string fileName = Path.GetFileName(full);
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    set.Add(fileName);
                }
            }
            catch { }
        }

        // 1. Active in-memory tiles snapshot
        if (activeTiles != null)
        {
            foreach (var tile in activeTiles)
            {
                InspectTile(tile);
            }
        }

        // 2. In-memory history snapshots (protects Ctrl + Z / Ctrl + Y)
        if (historySnapshots != null)
        {
            foreach (var snapshot in historySnapshots)
            {
                var snapModel = LayoutHistoryService.ParseSnapshot(snapshot);
                if (snapModel?.Tiles != null)
                {
                    foreach (var t in snapModel.Tiles)
                    {
                        InspectTile(t);
                    }
                }
            }
        }

        // 3. Pending write buffers in StorageService
        foreach (var pendingJson in StorageService.GetPendingLayoutJsonStrings())
        {
            var snapModel = LayoutHistoryService.ParseSnapshot(pendingJson);
            if (snapModel?.Tiles != null)
            {
                foreach (var t in snapModel.Tiles)
                {
                    InspectTile(t);
                }
            }
        }

        // 4. Primary disk layouts (config/layout.json and backups/layout.json.bak)
        if (!InspectLayoutFile(AppPaths.LayoutPath)) return false;
        if (!InspectLayoutFile(AppPaths.LayoutBakPath)) return false;

        // 5. All workspace disk layouts (workspaces/{wsId}/layout.json and .bak)
        if (Directory.Exists(AppPaths.WorkspacesDir))
        {
            try
            {
                foreach (string wsDir in Directory.EnumerateDirectories(AppPaths.WorkspacesDir))
                {
                    string wsId = Path.GetFileName(wsDir);
                    if (!InspectLayoutFile(AppPaths.GetWorkspaceLayoutPath(wsId))) return false;
                    if (!InspectLayoutFile(AppPaths.GetWorkspaceLayoutBakPath(wsId))) return false;
                }
            }
            catch (Exception ex)
            {
                Safe.Logger(ex, "PastedAssetCleanupService.EnumerateWorkspaces: Failed to enumerate workspaces");
                return false; // Fail-closed: ABORT SWEEP
            }
        }

        referenced = set;
        return true;

        bool InspectLayoutFile(string filePath)
        {
            if (!File.Exists(filePath)) return true; // Non-existent file is safe to skip

            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length <= 2) return true; // Empty file or "[]"

                string content = File.ReadAllText(filePath);
                var snapModel = LayoutHistoryService.ParseSnapshot(content);
                if (snapModel == null)
                {
                    // Existing non-empty layout file failed to parse! Fail-closed: abort.
                    Safe.Logger(new InvalidDataException($"Layout snapshot failed to deserialize: {filePath}"),
                        $"PastedAssetCleanupService.InspectLayoutFile({filePath})");
                    return false;
                }

                if (snapModel.Tiles != null)
                {
                    foreach (var t in snapModel.Tiles)
                    {
                        InspectTile(t);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Safe.Logger(ex, $"PastedAssetCleanupService.InspectLayoutFile({filePath}): Read failure");
                return false; // Fail-closed: abort
            }
        }
    }

    /// <summary>
    /// Executes an asynchronous mark-and-sweep cleanup.
    /// Files that are unreferenced across all layouts and older than the grace period are deleted.
    /// Concurrency-safe, throttled, and fail-closed.
    /// </summary>
    public static async Task<SweepResult> SweepAsync(
        TimeSpan? gracePeriod = null,
        IReadOnlyCollection<TileModel>? activeTiles = null,
        IReadOnlyCollection<string>? historySnapshots = null,
        bool force = false)
    {
        if (Interlocked.CompareExchange(ref _isSweeping, 1, 0) != 0)
        {
            return new SweepResult(0, 0, 0);
        }

        return await Task.Run(() =>
        {
            try
            {
                string timestampPath = Path.Combine(AppPaths.ConfigDir, "last_asset_sweep.txt");

                // Throttling: Run at most once per 24 hours unless force = true
                if (!force && IsThrottled(timestampPath, ThrottleInterval))
                {
                    return new SweepResult(0, 0, 0);
                }

                TimeSpan effectiveGrace = gracePeriod ?? DefaultGracePeriod;

                // 1. Mark Phase (Fail-Closed)
                if (!TryCollectReferencedPastedAssets(activeTiles, historySnapshots, out var referenced) || referenced == null)
                {
                    System.Diagnostics.Debug.WriteLine("[PastedAssetCleanupService] Mark phase failed or was incomplete. Aborting sweep to prevent data loss.");
                    return new SweepResult(0, 0, 0);
                }

                string[] targetDirs = [AppPaths.PastedImagesDir, AppPaths.PastedNotesDir];
                var candidateFiles = new List<AssetFileInfo>();

                // 2. Discover files eagerly inside try block
                foreach (string dir in targetDirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    try
                    {
                        var files = Directory.EnumerateFiles(dir).ToList();
                        foreach (string file in files)
                        {
                            try
                            {
                                var fi = new FileInfo(file);
                                candidateFiles.Add(new AssetFileInfo(file, fi.CreationTimeUtc, fi.LastWriteTimeUtc, fi.Length));
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Safe.Logger(ex, $"PastedAssetCleanupService: Failed to enumerate files in {dir}");
                    }
                }

                // 3. Circuit Breaker
                // If the reference set is 0 but there are real candidates, abort to prevent catastrophic wipe
                if (referenced.Count == 0 && candidateFiles.Any(f => !f.FullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)))
                {
                    System.Diagnostics.Debug.WriteLine("[PastedAssetCleanupService] Circuit breaker tripped: 0 referenced assets with non-empty directory. Aborting.");
                    return new SweepResult(candidateFiles.Count, 0, 0);
                }

                // 4. Pure Orphan Selection
                DateTime nowUtc = DateTime.UtcNow;
                var orphans = SelectOrphans(candidateFiles, referenced, nowUtc, effectiveGrace);

                int deletedCount = 0;
                long reclaimedBytes = 0;

                // 5. Sweep Phase (Per-file exception safe)
                foreach (var orphan in orphans)
                {
                    try
                    {
                        if (File.Exists(orphan.FullPath))
                        {
                            File.Delete(orphan.FullPath);
                            deletedCount++;
                            reclaimedBytes += orphan.Length;
                        }
                    }
                    catch (Exception ex)
                    {
                        Safe.Logger(ex, $"PastedAssetCleanupService: Failed to delete {orphan.FullPath}");
                    }
                }

                // Record successful sweep timestamp
                try
                {
                    AppPaths.EnsureDirectory(timestampPath);
                    File.WriteAllText(timestampPath, DateTime.UtcNow.ToBinary().ToString());
                }
                catch { }

                return new SweepResult(candidateFiles.Count, deletedCount, reclaimedBytes);
            }
            catch (Exception ex)
            {
                Safe.Logger(ex, "PastedAssetCleanupService.SweepAsync");
                return new SweepResult(0, 0, 0);
            }
            finally
            {
                Interlocked.Exchange(ref _isSweeping, 0);
            }
        }).ConfigureAwait(false);
    }

    private static bool IsThrottled(string timestampPath, TimeSpan interval)
    {
        try
        {
            if (File.Exists(timestampPath))
            {
                string raw = File.ReadAllText(timestampPath).Trim();
                if (long.TryParse(raw, out long binaryVal))
                {
                    var lastRun = DateTime.FromBinary(binaryVal);
                    if (DateTime.UtcNow - lastRun < interval)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }
}
