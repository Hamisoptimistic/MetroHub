using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MetroHub.Core.Models;
using MetroHub.Presentation.Controls;
using Wpf.Ui.Controls;
using MenuItem = System.Windows.Controls.MenuItem;

namespace MetroHub.Core.Services;

/// <summary>
/// Dedicated service handling all tile-to-sidebar pinning workflows,
/// deduplication, dynamic context menu configuration, and drag-and-drop ingestion.
/// Keeps MainWindow, TileControl, and SidebarRailControl lean and unbloated.
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
            catch
            {
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
        catch
        {
            // Fall back to trimmed string if path contains special characters or uri schemes
        }

        return trimmed.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Finds any shortcut in the sidebar rail matching the target of the specified tile.
    /// Performs normalized target comparison and .lnk shortcut resolution.
    /// </summary>
    public static SidebarShortcutItem? FindMatchingShortcut(SidebarRailControl? rail, TileModel? tile)
    {
        if (rail == null || rail.Shortcuts == null || tile == null || string.IsNullOrWhiteSpace(tile.TargetPath))
            return null;

        string normalizedTileTarget = NormalizeTarget(tile.TargetPath);

        foreach (var shortcut in rail.Shortcuts)
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
                catch
                {
                    // Ignore resolution errors
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks whether the specified tile is already pinned to the sidebar rail.
    /// </summary>
    public static bool IsTilePinned(SidebarRailControl? rail, TileModel? tile)
    {
        if (rail == null || tile == null || !CanPinTile(tile)) return false;
        return FindMatchingShortcut(rail, tile) != null;
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
                Debug.WriteLine($"[SidebarPinningService] Icon extraction failed for '{target}': {ex.Message}");
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
    /// Pins an eligible tile to the sidebar rail (deduplicated).
    /// </summary>
    public static bool PinTile(SidebarRailControl? rail, TileModel? tile, int? insertIndex = null)
    {
        if (rail == null || tile == null || !CanPinTile(tile)) return false;
        if (IsTilePinned(rail, tile)) return false;

        int index = insertIndex ?? rail.Shortcuts.Count;
        var shortcutItem = CreateShortcutFromTile(tile, index);

        rail.AddShortcutItem(shortcutItem, insertIndex);

        // If WebUrl with missing custom icon, trigger asynchronous background favicon fetch
        if (shortcutItem.TargetType == SidebarShortcutType.WebUrl && string.IsNullOrWhiteSpace(shortcutItem.CustomIconPath))
        {
            _ = Task.Run(async () =>
            {
                string? fetched = await WebFaviconService.GetFaviconPathAsync(shortcutItem.Target);
                if (!string.IsNullOrWhiteSpace(fetched))
                {
                    await rail.Dispatcher.InvokeAsync(() =>
                    {
                        shortcutItem.CustomIconPath = fetched;
                        rail.SaveShortcutsState();
                    });
                }
            });
        }

        return true;
    }

    /// <summary>
    /// Unpins a tile from the sidebar rail if it exists.
    /// </summary>
    public static bool UnpinTile(SidebarRailControl? rail, TileModel? tile)
    {
        if (rail == null || tile == null) return false;
        var matching = FindMatchingShortcut(rail, tile);
        if (matching != null)
        {
            return rail.RemoveShortcutItem(matching);
        }
        return false;
    }

    /// <summary>
    /// Toggles the pinned status of a tile on the sidebar rail.
    /// </summary>
    public static bool TogglePinTile(SidebarRailControl? rail, TileModel? tile)
    {
        if (rail == null || tile == null || !CanPinTile(tile)) return false;

        if (IsTilePinned(rail, tile))
        {
            return UnpinTile(rail, tile);
        }
        else
        {
            return PinTile(rail, tile);
        }
    }

    /// <summary>
    /// Batch pins multiple tiles to the sidebar rail, deduplicating each.
    /// </summary>
    public static int BatchPinTiles(SidebarRailControl? rail, IEnumerable<TileModel>? tiles)
    {
        if (rail == null || tiles == null) return 0;

        int added = 0;
        foreach (var tile in tiles)
        {
            if (CanPinTile(tile) && !IsTilePinned(rail, tile))
            {
                if (PinTile(rail, tile))
                {
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>
    /// Batch unpins multiple tiles from the sidebar rail.
    /// </summary>
    public static int BatchUnpinTiles(SidebarRailControl? rail, IEnumerable<TileModel>? tiles)
    {
        if (rail == null || tiles == null) return 0;

        int removed = 0;
        foreach (var tile in tiles)
        {
            if (UnpinTile(rail, tile))
            {
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// Configures the Pin/Unpin context menu item and icon based on current tile eligibility,
    /// pinned state, and multi-selection count.
    /// </summary>
    public static void ConfigureTileContextMenu(
        MenuItem? menuItem,
        SymbolIcon? icon,
        TileModel? tile,
        MainWindow? mainWindow)
    {
        if (menuItem == null) return;

        if (tile == null || !CanPinTile(tile))
        {
            menuItem.Visibility = Visibility.Collapsed;
            return;
        }

        menuItem.Visibility = Visibility.Visible;
        var rail = mainWindow?.SidebarRail;

        var selectedTiles = mainWindow?.SelectedTiles;
        if (tile.IsSelected && selectedTiles != null && selectedTiles.Count > 1)
        {
            var eligible = selectedTiles.Where(CanPinTile).ToList();
            if (eligible.Count == 0)
            {
                menuItem.Visibility = Visibility.Collapsed;
                return;
            }

            bool allPinned = eligible.All(t => IsTilePinned(rail, t));
            if (allPinned)
            {
                menuItem.Header = "Unpin Selected Tiles from Sidebar";
                if (icon != null)
                {
                    icon.Symbol = SymbolRegular.PinOff24;
                }
            }
            else
            {
                menuItem.Header = eligible.Count > 1 
                    ? $"Pin {eligible.Count} Tiles to Sidebar" 
                    : "Pin to Sidebar";

                if (icon != null)
                {
                    icon.Symbol = SymbolRegular.Pin24;
                }
            }
        }
        else
        {
            bool pinned = IsTilePinned(rail, tile);
            menuItem.Header = pinned ? "Unpin from Sidebar" : "Pin to Sidebar";
            if (icon != null)
            {
                icon.Symbol = pinned ? SymbolRegular.PinOff24 : SymbolRegular.Pin24;
            }
        }
    }

    /// <summary>
    /// Handles clicking the Pin/Unpin context menu item for single or multi-selected tiles.
    /// </summary>
    public static void HandleContextMenuClick(TileModel? tile, MainWindow? mainWindow)
    {
        if (tile == null || mainWindow == null) return;
        var rail = mainWindow.SidebarRail;
        if (rail == null) return;

        var selectedTiles = mainWindow.SelectedTiles;
        if (tile.IsSelected && selectedTiles != null && selectedTiles.Count > 1)
        {
            var eligible = selectedTiles.Where(CanPinTile).ToList();
            if (eligible.Count == 0) return;

            bool allPinned = eligible.All(t => IsTilePinned(rail, t));
            if (allPinned)
            {
                BatchUnpinTiles(rail, eligible);
            }
            else
            {
                BatchPinTiles(rail, eligible);
            }
        }
        else
        {
            TogglePinTile(rail, tile);
        }
    }

    /// <summary>
    /// Handles drag-and-drop of one or more tiles from the canvas onto the sidebar rail.
    /// </summary>
    public static bool TryHandleTileDropOnSidebar(SidebarRailControl? rail, IEnumerable<TileModel>? tiles)
    {
        if (rail == null || tiles == null) return false;

        var eligible = tiles.Where(CanPinTile).ToList();
        if (eligible.Count == 0) return false;

        bool anyPinned = false;
        foreach (var tile in eligible)
        {
            if (!IsTilePinned(rail, tile))
            {
                if (PinTile(rail, tile))
                {
                    anyPinned = true;
                }
            }
        }

        return anyPinned;
    }

    private static string GetDefaultFileSymbol(string ext) => ext switch
    {
        ".exe" or ".lnk" => "AppGeneric24",
        ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".md" => "Document24",
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" => "Image24",
        ".mp3" or ".wav" or ".flac" or ".m4a" => "MusicNote224",
        ".mp4" or ".mkv" or ".avi" or ".mov" => "Video24",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "FolderZip24",
        _ => "AppGeneric24"
    };

    private static string? GetCustomFolderIcon(string folderPath)
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
        catch
        {
            // Ignore icon extraction failure
        }
        return null;
    }
}
