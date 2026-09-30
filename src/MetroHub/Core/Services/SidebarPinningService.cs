using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MetroHub.Core.Models;

namespace MetroHub.Core.Services;

/// <summary>
/// Dedicated service handling all tile-to-sidebar pinning workflows,
/// deduplication, shortcut creation, and drag-and-drop ingestion.
/// Works against <see cref="ISidebarShortcutStore"/> so Core never imports Presentation.
/// </summary>
public static class SidebarPinningService
{
    /// <summary>
    /// Determines whether a tile is eligible for pinning to the sidebar rail.
    /// Widgets are excluded; only launchable Apps, Web URLs, and Folders are allowed.
    /// </summary>
    public static bool CanPinTile(TileModel? tile)
    {
        if (tile == null) return false;
        if (tile.TileType == TileType.Widget) return false;
        return !string.IsNullOrWhiteSpace(tile.TargetPath);
    }

    /// <summary>
    /// Normalizes a target path or URL for robust, case-insensitive, slash-agnostic equality checks.
    /// </summary>
    public static string NormalizeTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return string.Empty;

        string trimmed = target.Trim();

        // Web URLs
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return WebFaviconService.NormalizeUrl(trimmed).TrimEnd('/');
            }
            catch (Exception ex)
            {
                Safe.Log("SidebarPinningService.NormalizeTarget.WebUrl", ex);
                return trimmed.TrimEnd('/');
            }
        }

        // Local file or folder paths
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                return Path.GetFullPath(trimmed).TrimEnd('\\', '/');
            }
        }
        catch (Exception ex)
        {
            Safe.Log("SidebarPinningService.NormalizeTarget.Path", ex);
        }

        return trimmed.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Finds any shortcut in the store matching the target of the specified tile.
    /// Performs normalized target comparison and .lnk shortcut resolution.
    /// </summary>
    public static SidebarShortcutItem? FindMatchingShortcut(ISidebarShortcutStore? store, TileModel? tile)
    {
        if (store?.Shortcuts == null || tile == null || string.IsNullOrWhiteSpace(tile.TargetPath))
            return null;

        string normalizedTileTarget = NormalizeTarget(tile.TargetPath);

        foreach (var shortcut in store.Shortcuts)
        {
            if (shortcut.IsSeparator) continue;

            string normalizedShortcutTarget = NormalizeTarget(shortcut.Target);
            if (string.Equals(normalizedTileTarget, normalizedShortcutTarget, StringComparison.OrdinalIgnoreCase))
            {
                return shortcut;
            }

            // Secondary check: If both are application shortcuts, resolve shortcut targets (.lnk -> .exe)
            if (tile.TileType == TileType.App && shortcut.TargetType == SidebarShortcutType.Application)
            {
                try
                {
                    string resolvedTile = IconExtractorService.ResolveShortcutTarget(tile.TargetPath);
                    string resolvedShortcut = IconExtractorService.ResolveShortcutTarget(shortcut.Target);
                    if (!string.IsNullOrWhiteSpace(resolvedTile) && !string.IsNullOrWhiteSpace(resolvedShortcut) &&
                        string.Equals(NormalizeTarget(resolvedTile), NormalizeTarget(resolvedShortcut), StringComparison.OrdinalIgnoreCase))
                    {
                        return shortcut;
                    }
                }
                catch (Exception ex)
                {
                    Safe.Log("SidebarPinningService.FindMatchingShortcut.ResolveLink", ex);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether the specified tile is already pinned to the sidebar.
    /// </summary>
    public static bool IsTilePinned(ISidebarShortcutStore? store, TileModel? tile)
    {
        if (store == null || tile == null || !CanPinTile(tile)) return false;
        return FindMatchingShortcut(store, tile) != null;
    }

    /// <summary>
    /// Creates a fully populated <see cref="SidebarShortcutItem"/> companion from a <see cref="TileModel"/>.
    /// </summary>
    public static SidebarShortcutItem CreateShortcutFromTile(TileModel tile, int sortOrder = 0)
    {
        string title = !string.IsNullOrWhiteSpace(tile.Title) ? tile.Title : "Shortcut";
        string target = tile.TargetPath ?? string.Empty;

        // 1. Web URL Tile
        if (tile.TileType == TileType.WebUrl ||
            target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            string normalizedUrl = WebFaviconService.NormalizeUrl(target);
            string resolvedTitle = !string.IsNullOrWhiteSpace(tile.Title)
                ? tile.Title
                : WebFaviconService.InferTitleFromUrl(normalizedUrl);

            return new SidebarShortcutItem
            {
                Title = resolvedTitle,
                Target = normalizedUrl,
                TargetType = SidebarShortcutType.WebUrl,
                IconSymbol = "Globe24",
                CustomIconPath = tile.IconPath,
                SortOrder = sortOrder,
                IsRemovable = true
            };
        }

        // 2. Folder Tile
        if (tile.TileType == TileType.Folder || Directory.Exists(target))
        {
            string folderName = Path.GetFileName(target.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(folderName)) folderName = title;

            string smartIcon = SidebarShortcutItem.DetectSmartIcon(target);
            string? folderIcon = tile.IconPath ?? GetCustomFolderIcon(target);

            return new SidebarShortcutItem
            {
                Title = folderName,
                Target = target,
                TargetType = SidebarShortcutType.CustomFolder,
                IconSymbol = smartIcon,
                CustomIconPath = folderIcon,
                SortOrder = sortOrder,
                IsRemovable = true
            };
        }

        // 3. Application or Launchable File Tile
        string appName = title;
        if (string.IsNullOrWhiteSpace(appName) || appName == "Shortcut")
        {
            appName = Path.GetFileNameWithoutExtension(target);
        }

        string ext = Path.GetExtension(target).ToLowerInvariant();
        string defaultSymbol = GetDefaultFileSymbol(ext);

        string? iconPath = tile.IconPath;
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            try
            {
                iconPath = IconExtractorService.ExtractAndCacheIcon(target);
            }
            catch (Exception ex)
            {
                Safe.Log("SidebarPinningService.CreateShortcut.IconExtract", ex);
            }
        }

        return new SidebarShortcutItem
        {
            Title = appName,
            Target = target,
            TargetType = SidebarShortcutType.Application,
            IconSymbol = defaultSymbol,
            CustomIconPath = iconPath,
            SortOrder = sortOrder,
            IsRemovable = true
        };
    }

    /// <summary>
    /// Pins an eligible tile to the sidebar (deduplicated).
    /// </summary>
    public static bool PinTile(ISidebarShortcutStore? store, TileModel? tile, int? insertIndex = null)
    {
        if (store == null || tile == null || !CanPinTile(tile)) return false;
        if (IsTilePinned(store, tile)) return false;

        int index = insertIndex ?? store.Shortcuts.Count;
        var shortcutItem = CreateShortcutFromTile(tile, index);

        store.AddShortcutItem(shortcutItem, insertIndex);

        // If WebUrl with missing custom icon, trigger asynchronous background favicon fetch
        if (shortcutItem.TargetType == SidebarShortcutType.WebUrl && string.IsNullOrWhiteSpace(shortcutItem.CustomIconPath))
        {
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(shortcutItem.Target);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    shortcutItem.CustomIconPath = fetched;
                    store.SaveShortcutsState();
                }
            });
        }

        return true;
    }

    /// <summary>
    /// Unpins a tile from the sidebar if it exists.
    /// </summary>
    public static bool UnpinTile(ISidebarShortcutStore? store, TileModel? tile)
    {
        if (store == null || tile == null) return false;
        var matching = FindMatchingShortcut(store, tile);
        if (matching != null)
        {
            return store.RemoveShortcutItem(matching);
        }
        return false;
    }

    /// <summary>
    /// Toggles the pinned status of a tile on the sidebar.
    /// </summary>
    public static bool TogglePinTile(ISidebarShortcutStore? store, TileModel? tile)
    {
        if (store == null || tile == null || !CanPinTile(tile)) return false;

        if (IsTilePinned(store, tile))
        {
            return UnpinTile(store, tile);
        }
        else
        {
            return PinTile(store, tile);
        }
    }

    /// <summary>
    /// Batch pins multiple tiles to the sidebar, deduplicating each.
    /// </summary>
    public static int BatchPinTiles(ISidebarShortcutStore? store, IEnumerable<TileModel>? tiles)
    {
        if (store == null || tiles == null) return 0;

        int added = 0;
        foreach (var tile in tiles)
        {
            if (CanPinTile(tile) && !IsTilePinned(store, tile))
            {
                if (PinTile(store, tile))
                {
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>
    /// Batch unpins multiple tiles from the sidebar.
    /// </summary>
    public static int BatchUnpinTiles(ISidebarShortcutStore? store, IEnumerable<TileModel>? tiles)
    {
        if (store == null || tiles == null) return 0;

        int removed = 0;
        foreach (var tile in tiles)
        {
            if (UnpinTile(store, tile))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Handles drag-and-drop of one or more tiles from the canvas onto the sidebar.
    /// </summary>
    public static bool TryHandleTileDropOnSidebar(ISidebarShortcutStore? store, IEnumerable<TileModel>? tiles)
    {
        if (store == null || tiles == null) return false;

        var eligible = tiles.Where(CanPinTile).ToList();
        if (eligible.Count == 0) return false;

        bool anyPinned = false;
        foreach (var tile in eligible)
        {
            if (!IsTilePinned(store, tile))
            {
                if (PinTile(store, tile))
                {
                    anyPinned = true;
                }
            }
        }

        return anyPinned;
    }

    /// <summary>
    /// Handles a sidebar pin toggle for a single tile or multi-selection.
    /// Pure logic; callers provide the store and selection list.
    /// </summary>
    public static void HandleTogglePin(ISidebarShortcutStore? store, TileModel? tile, IReadOnlyList<TileModel>? selectedTiles)
    {
        if (store == null || tile == null) return;

        if (tile.IsSelected && selectedTiles != null && selectedTiles.Count > 1)
        {
            var eligible = selectedTiles.Where(CanPinTile).ToList();
            if (eligible.Count == 0) return;

            bool allPinned = eligible.All(t => IsTilePinned(store, t));
            if (allPinned)
            {
                BatchUnpinTiles(store, eligible);
            }
            else
            {
                BatchPinTiles(store, eligible);
            }
        }
        else
        {
            TogglePinTile(store, tile);
        }
    }

    /// <summary>
    /// Returns the pin state description for a tile or multi-selection.
    /// The caller uses this to configure their context menu in the Presentation layer.
    /// </summary>
    public static (bool IsVisible, bool IsPinned, int EligibleCount) GetPinState(
        ISidebarShortcutStore? store, TileModel? tile, IReadOnlyList<TileModel>? selectedTiles)
    {
        if (tile == null || !CanPinTile(tile))
            return (false, false, 0);

        if (tile.IsSelected && selectedTiles != null && selectedTiles.Count > 1)
        {
            var eligible = selectedTiles.Where(CanPinTile).ToList();
            if (eligible.Count == 0)
                return (false, false, 0);

            bool allPinned = eligible.All(t => IsTilePinned(store, t));
            return (true, allPinned, eligible.Count);
        }

        bool pinned = IsTilePinned(store, tile);
        return (true, pinned, 1);
    }

    internal static string GetDefaultFileSymbol(string ext) => ext switch
    {
        ".exe" or ".lnk" => "AppGeneric24",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".md" => "Document24",
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "Image24",
        ".mp3" or ".wav" or ".flac" or ".m4a" => "MusicNote224",
        ".mp4" or ".mkv" or ".avi" or ".mov" => "Video24",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "FolderZip24",
        _ => "AppGeneric24"
    };

    internal static string? GetCustomFolderIcon(string folderPath)
    {
        try
        {
            string iniPath = Path.Combine(folderPath, "desktop.ini");
            if (File.Exists(iniPath))
            {
                return IconExtractorService.ExtractAndCacheIcon(folderPath);
            }

            string folderIco = Path.Combine(folderPath, "folder.ico");
            if (File.Exists(folderIco))
            {
                return IconExtractorService.ExtractAndCacheIcon(folderIco);
            }

            string iconIco = Path.Combine(folderPath, "icon.ico");
            if (File.Exists(iconIco))
            {
                return IconExtractorService.ExtractAndCacheIcon(iconIco);
            }
        }
        catch (Exception ex)
        {
            Safe.Log("SidebarPinningService.GetCustomFolderIcon", ex);
        }
        return null;
    }
}
