using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

using MetroHub.Core.Services;

namespace MetroHub.Widgets.Catalog.Radio.Services;

/// <summary>
/// Service responsible for loading, mutating, and persisting the unified station catalog.
/// Loads from %LocalAppData%\MetroHub\radio_catalog.json (seeding it from the bundled
/// pack resource on first run) and provides atomic Win32 ReplaceFileW persistence for
/// user-added custom stations, edits, and self-healed stream URLs.
/// </summary>
public sealed class RadioCatalogService
{
    private static readonly Lazy<RadioCatalogService> _lazyInstance = new(() => new RadioCatalogService());
    public static RadioCatalogService Instance => _lazyInstance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Lock _gate = new();
    private readonly string _catalogFilePath;
    private readonly string _catalogBakPath;
    private RadioCatalog? _cachedCatalog;

    /// <summary>
    /// Event fired whenever custom stations are added, removed, or healed,
    /// notifying active widget views to re-render their stations.
    /// </summary>
    public event EventHandler? CatalogChanged;

    /// <summary>
    /// Test / internal helper to notify listeners of a catalog change.
    /// </summary>
    internal void NotifyCatalogChanged() => CatalogChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Initializes a new instance of <see cref="RadioCatalogService"/>.
    /// </summary>
    /// <param name="customCatalogPath">Optional custom path for testing isolation; defaults to %LocalAppData%\MetroHub\radio_catalog.json.</param>
    public RadioCatalogService(string? customCatalogPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customCatalogPath))
        {
            _catalogFilePath = customCatalogPath;
            _catalogBakPath = customCatalogPath + ".bak";
        }
        else
        {
            _catalogFilePath = AppPaths.ResolveCacheFile("radio_catalog.json");
            _catalogBakPath = AppPaths.ResolveBackupFile("radio_catalog.json.bak");
        }
    }

    /// <summary>
    /// Returns the loaded catalog or loads it on first access.
    /// </summary>
    public RadioCatalog GetCatalog() => LoadCatalog();

    /// <summary>
    /// Gets a category by ID (e.g., "ambient", "nature", "lofi", "coding").
    /// </summary>
    public RadioCategory? GetCategory(string categoryId)
    {
        var catalog = LoadCatalog();
        lock (_gate)
        {
            return catalog.Categories.Find(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Gets all stations across all categories.
    /// </summary>
    public IReadOnlyList<RadioStation> GetAllStations()
    {
        var catalog = LoadCatalog();
        lock (_gate)
        {
            var list = new List<RadioStation>();
            foreach (var category in catalog.Categories)
            {
                list.AddRange(category.Stations);
            }
            return list;
        }
    }

    /// <summary>
    /// Finds a specific station by its unique identifier.
    /// </summary>
    public RadioStation? GetStationById(string id)
    {
        var stations = GetAllStations();
        return stations.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Loads the radio catalog from %LocalAppData%\MetroHub\radio_catalog.json.
    /// If absent, automatically seeds the file by cloning the bundled application resource.
    /// </summary>
    public RadioCatalog LoadCatalog()
    {
        lock (_gate)
        {
            if (_cachedCatalog != null)
            {
                return _cachedCatalog;
            }

            // 1. Try loading from unified AppData file
            if (File.Exists(_catalogFilePath))
            {
                try
                {
                    string diskJson = File.ReadAllText(_catalogFilePath, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(diskJson))
                    {
                        var parsed = JsonSerializer.Deserialize<RadioCatalog>(diskJson, JsonOptions);
                        if (parsed != null && parsed.Categories.Count > 0)
                        {
                            NormalizeCategories(parsed);
                            _cachedCatalog = parsed;
                            return _cachedCatalog;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Safe.Log(ex, "RadioCatalogService.ReadCatalog");
                    // Attempt to restore from backup
                    if (File.Exists(_catalogBakPath))
                    {
                        try
                        {
                            string bakJson = File.ReadAllText(_catalogBakPath, Encoding.UTF8);
                            var bakParsed = JsonSerializer.Deserialize<RadioCatalog>(bakJson, JsonOptions);
                            if (bakParsed != null && bakParsed.Categories.Count > 0)
                            {
                                NormalizeCategories(bakParsed);
                                _cachedCatalog = bakParsed;
                                return _cachedCatalog;
                            }
                        }
                        catch (Exception exBak)
                        {
                            Safe.Log(exBak, "RadioCatalogService.ReadBackupCatalog");
                        }
                    }
                }
            }

            // 2. First-run or recovery: load the bundled master factory catalog
            string? bundledJson = LoadBundledCatalogJson();
            if (!string.IsNullOrWhiteSpace(bundledJson))
            {
                try
                {
                    _cachedCatalog = JsonSerializer.Deserialize<RadioCatalog>(bundledJson, JsonOptions);
                    if (_cachedCatalog != null && _cachedCatalog.Categories.Count > 0)
                    {
                        NormalizeCategories(_cachedCatalog);
                        // Seed to AppData so future launches have the physical file ready
                        SaveCatalogAtomicInternal(_cachedCatalog);
                        return _cachedCatalog;
                    }
                }
                catch (Exception ex)
                {
                    Safe.Log(ex, "RadioCatalogService.DeserializeBundledCatalog");
                }
            }

            // 3. Emergency fallback
            _cachedCatalog ??= CreateFallbackCatalog();
            NormalizeCategories(_cachedCatalog);
            return _cachedCatalog;
        }
    }

    /// <summary>
    /// Adds a new custom radio station directly into the specified category and persists atomically.
    /// </summary>
    public Task AddCustomStationAsync(RadioStation station, string categoryId)
    {
        ArgumentNullException.ThrowIfNull(station);
        if (string.IsNullOrWhiteSpace(categoryId)) categoryId = "ambient";

        lock (_gate)
        {
            var catalog = LoadCatalog();

            // Find matching category or fallback to first
            var category = catalog.Categories.Find(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase))
                           ?? catalog.Categories.FirstOrDefault();

            if (category == null)
            {
                category = new RadioCategory { Id = categoryId, DisplayName = categoryId };
                catalog.Categories.Add(category);
            }

            // Ensure unique ID and flags
            string uniqueId = string.IsNullOrWhiteSpace(station.Id) || station.Id == "__add_placeholder__"
                ? $"custom_{Guid.NewGuid():N}"
                : station.Id;

            // Remove existing station with same ID or same stream URL in this category to prevent duplicates
            category.Stations.RemoveAll(s => s.Id == uniqueId || string.Equals(s.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase));

            var customStation = station with
            {
                Id = uniqueId,
                Category = category.Id,
                IsCustom = true
            };

            category.Stations.Add(customStation);

            SaveCatalogAtomicInternal(catalog);
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a station from the catalog and persists atomically.
    /// Only custom stations can be deleted; factory presets are protected.
    /// </summary>
    public Task<bool> DeleteStationAsync(string stationId)
    {
        if (string.IsNullOrWhiteSpace(stationId)) return Task.FromResult(false);

        bool removed = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();
            foreach (var category in catalog.Categories)
            {
                int countBefore = category.Stations.Count;
                category.Stations.RemoveAll(s => s.Id == stationId && s.IsCustom);
                if (category.Stations.Count < countBefore)
                {
                    removed = true;
                    break;
                }
            }

            if (removed)
            {
                SaveCatalogAtomicInternal(catalog);
            }
        }

        if (removed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Updates the stream URL for an existing station (used by the silent self-healing resolver).
    /// </summary>
    public Task<bool> UpdateStationUrlAsync(string stationId, string newStreamUrl)
    {
        if (string.IsNullOrWhiteSpace(stationId) || string.IsNullOrWhiteSpace(newStreamUrl))
            return Task.FromResult(false);

        bool updated = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();
            foreach (var category in catalog.Categories)
            {
                for (int i = 0; i < category.Stations.Count; i++)
                {
                    if (category.Stations[i].Id == stationId)
                    {
                        category.Stations[i] = category.Stations[i] with { StreamUrl = newStreamUrl };
                        updated = true;
                        break;
                    }
                }
                if (updated) break;
            }

            if (updated)
            {
                SaveCatalogAtomicInternal(catalog);
            }
        }

        if (updated)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(updated);
    }

    /// <summary>
    /// Restores the catalog to the factory bundled presets, resetting all customizations.
    /// </summary>
    public Task RestoreFactoryDefaultsAsync()
    {
        lock (_gate)
        {
            string? bundledJson = LoadBundledCatalogJson();
            if (!string.IsNullOrWhiteSpace(bundledJson))
            {
                var factoryCatalog = JsonSerializer.Deserialize<RadioCatalog>(bundledJson, JsonOptions);
                if (factoryCatalog != null && factoryCatalog.Categories.Count > 0)
                {
                    NormalizeCategories(factoryCatalog);
                    _cachedCatalog = factoryCatalog;
                    SaveCatalogAtomicInternal(_cachedCatalog);
                }
            }
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a new category and persists the catalog atomically.
    /// </summary>
    public Task AddCategoryAsync(RadioCategory category)
    {
        ArgumentNullException.ThrowIfNull(category);

        lock (_gate)
        {
            var catalog = LoadCatalog();

            string catId = string.IsNullOrWhiteSpace(category.Id)
                ? (string.IsNullOrWhiteSpace(category.DisplayName)
                    ? $"cat_{Guid.NewGuid():N}"
                    : category.DisplayName.Trim().ToLowerInvariant().Replace(' ', '_'))
                : category.Id.Trim().ToLowerInvariant();

            if (catalog.Categories.Any(c => string.Equals(c.Id, catId, StringComparison.OrdinalIgnoreCase)))
            {
                catId = $"{catId}_{Guid.NewGuid():N}"[..Math.Min(32, 5 + 32)];
            }

            var normalizedCategory = category with
            {
                Id = catId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "New Category" : category.DisplayName.Trim(),
                IconSymbol = string.IsNullOrWhiteSpace(category.IconSymbol) ? "Headphones24" : category.IconSymbol.Trim(),
                SortOrder = catalog.Categories.Count,
                Stations = category.Stations ?? new List<RadioStation>()
            };

            catalog.Categories.Add(normalizedCategory);
            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                catalog.Categories[i] = catalog.Categories[i] with { SortOrder = i };
            }
            SaveCatalogAtomicInternal(catalog);
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a category, moving all its stations to the fallback category, and persists atomically.
    /// </summary>
    public Task<bool> DeleteCategoryAsync(string categoryId, string fallbackCategoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return Task.FromResult(false);

        bool removed = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();

            var source = catalog.Categories.Find(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));
            if (source == null || catalog.Categories.Count <= 1) return Task.FromResult(false);

            var fallback = catalog.Categories.Find(c =>
                !string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Id, fallbackCategoryId, StringComparison.OrdinalIgnoreCase))
                ?? catalog.Categories.First(c => !string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));

            if (source.Stations is { Count: > 0 })
            {
                foreach (var station in source.Stations)
                {
                    var movedStation = station with { Category = fallback.Id };
                    fallback.Stations.RemoveAll(s => s.Id == movedStation.Id || string.Equals(s.StreamUrl, movedStation.StreamUrl, StringComparison.OrdinalIgnoreCase));
                    fallback.Stations.Add(movedStation);
                }
            }

            catalog.Categories.Remove(source);
            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                catalog.Categories[i] = catalog.Categories[i] with { SortOrder = i };
            }
            SaveCatalogAtomicInternal(catalog);
            removed = true;
        }

        if (removed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(removed);
    }

    /// <summary>
    /// Renames an existing category's display name and persists atomically.
    /// </summary>
    public Task<bool> RenameCategoryAsync(string categoryId, string newDisplayName)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(newDisplayName))
            return Task.FromResult(false);

        bool renamed = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();
            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                if (string.Equals(catalog.Categories[i].Id, categoryId, StringComparison.OrdinalIgnoreCase))
                {
                    catalog.Categories[i] = catalog.Categories[i] with { DisplayName = newDisplayName.Trim() };
                    renamed = true;
                    break;
                }
            }

            if (renamed)
            {
                SaveCatalogAtomicInternal(catalog);
            }
        }

        if (renamed)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(renamed);
    }

    /// <summary>
    /// Updates the icon symbol for an existing category and persists atomically.
    /// </summary>
    public Task<bool> UpdateCategoryIconAsync(string categoryId, string newIconSymbol)
    {
        if (string.IsNullOrWhiteSpace(categoryId) || string.IsNullOrWhiteSpace(newIconSymbol))
            return Task.FromResult(false);

        bool updated = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();
            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                if (string.Equals(catalog.Categories[i].Id, categoryId, StringComparison.OrdinalIgnoreCase))
                {
                    catalog.Categories[i] = catalog.Categories[i] with { IconSymbol = newIconSymbol.Trim() };
                    updated = true;
                    break;
                }
            }

            if (updated)
            {
                SaveCatalogAtomicInternal(catalog);
            }
        }

        if (updated)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(updated);
    }

    /// <summary>
    /// Reorders categories by moving a category from oldIndex to newIndex and persists atomically.
    /// </summary>
    public Task<bool> ReorderCategoriesAsync(int oldIndex, int newIndex)
    {
        bool reordered = false;
        lock (_gate)
        {
            var catalog = LoadCatalog();
            if (oldIndex < 0 || oldIndex >= catalog.Categories.Count ||
                newIndex < 0 || newIndex >= catalog.Categories.Count ||
                oldIndex == newIndex)
            {
                return Task.FromResult(false);
            }

            var item = catalog.Categories[oldIndex];
            catalog.Categories.RemoveAt(oldIndex);
            catalog.Categories.Insert(newIndex, item);

            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                catalog.Categories[i] = catalog.Categories[i] with { SortOrder = i };
            }

            SaveCatalogAtomicInternal(catalog);
            reordered = true;
        }

        if (reordered)
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }

        return Task.FromResult(reordered);
    }

    private static void NormalizeCategories(RadioCatalog? catalog)
    {
        if (catalog?.Categories == null || catalog.Categories.Count == 0) return;

        var distinctOrders = catalog.Categories.Select(c => c.SortOrder).Distinct().Count();
        if (distinctOrders != catalog.Categories.Count)
        {
            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                catalog.Categories[i] = catalog.Categories[i] with { SortOrder = i };
            }
        }
        else
        {
            bool needsSorting = false;
            for (int i = 0; i < catalog.Categories.Count - 1; i++)
            {
                if (catalog.Categories[i].SortOrder > catalog.Categories[i + 1].SortOrder)
                {
                    needsSorting = true;
                    break;
                }
            }

            if (needsSorting)
            {
                catalog.Categories.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
            }

            for (int i = 0; i < catalog.Categories.Count; i++)
            {
                if (catalog.Categories[i].SortOrder != i)
                {
                    catalog.Categories[i] = catalog.Categories[i] with { SortOrder = i };
                }
            }
        }
    }

    private void SaveCatalogAtomicInternal(RadioCatalog catalog)
    {
        string? targetDir = Path.GetDirectoryName(_catalogFilePath);
        if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
        {
            try { Directory.CreateDirectory(targetDir); } catch (Exception ex) { Safe.Log(ex, "RadioCatalogService.CreateDirectory"); }
        }

        string json = JsonSerializer.Serialize(catalog, JsonOptions);
        string tmpPath = _catalogFilePath + ".tmp";

        try
        {
            AppPaths.EnsureDirectory(_catalogFilePath);
            AppPaths.EnsureDirectory(_catalogBakPath);

            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(fs, Utf8NoBom))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(_catalogFilePath))
            {
                try
                {
                    File.Replace(tmpPath, _catalogFilePath, _catalogBakPath, ignoreMetadataErrors: true);
                    return;
                }
                catch (Exception ex)
                {
                    Safe.Log(ex, "RadioCatalogService.ReplaceCatalog");
                    try { File.Copy(_catalogFilePath, _catalogBakPath, overwrite: true); } catch (Exception exBak) { Safe.Log(exBak, "RadioCatalogService.CopyBackupCatalog"); }
                    File.Move(tmpPath, _catalogFilePath, overwrite: true);
                    return;
                }
            }

            File.Move(tmpPath, _catalogFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "RadioCatalogService.AtomicSave");
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch (Exception exDel) { Safe.Ignore(exDel, "RadioCatalogService.CleanupTmp"); }
        }
    }

    private static string? LoadBundledCatalogJson()
    {
        // 1. Try loading from pack URI (WPF application context)
        try
        {
            if (Application.ResourceAssembly == null)
            {
                Application.ResourceAssembly = typeof(RadioCatalogService).Assembly;
            }

            var packUri = new Uri("pack://application:,,,/MetroHub;component/Assets/Radio/radio_catalog.json", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(packUri);
            if (streamInfo != null)
            {
                using var reader = new StreamReader(streamInfo.Stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "RadioCatalogService.LoadBundledCatalogPack");
        }

        // 2. Try loading from filesystem (BaseDirectory, relative upward paths for test runners)
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string current = baseDir;
            for (int depth = 0; depth < 8; depth++)
            {
                string candidate = Path.Combine(current, "Assets", "Radio", "radio_catalog.json");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate, Encoding.UTF8);
                }

                candidate = Path.Combine(current, "src", "MetroHub", "Assets", "Radio", "radio_catalog.json");
                if (File.Exists(candidate))
                {
                    return File.ReadAllText(candidate, Encoding.UTF8);
                }

                string? parent = Directory.GetParent(current)?.FullName;
                if (parent == null) break;
                current = parent;
            }
        }
        catch (Exception ex)
        {
            Safe.Log(ex, "RadioCatalogService.LoadBundledCatalogDisk");
        }

        return null;
    }

    private static RadioCatalog CreateFallbackCatalog()
    {
        return new RadioCatalog
        {
            Version = 1,
            Categories = new List<RadioCategory>
            {
                new()
                {
                    Id = "ambient",
                    DisplayName = "Ambient",
                    IconSymbol = "WeatherMoon24",
                    Stations = new List<RadioStation>
                    {
                        new()
                        {
                            Id = "dronezone",
                            Name = "SomaFM Drone Zone",
                            StreamUrl = "http://ice1.somafm.com/dronezone-128-mp3",
                            BitrateKbps = 128,
                            Category = "ambient",
                            Icon = "WeatherMoon24"
                        }
                    }
                }
            }
        };
    }
}
