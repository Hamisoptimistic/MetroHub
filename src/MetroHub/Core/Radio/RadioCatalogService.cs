using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace MetroHub.Core.Radio;

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

    private readonly object _gate = new();
    private readonly string _catalogFilePath;
    private readonly string _catalogBakPath;
    private RadioCatalog? _cachedCatalog;

    /// <summary>
    /// Event fired whenever custom stations are added, removed, or healed,
    /// notifying active widget views to re-render their stations.
    /// </summary>
    public event EventHandler? CatalogChanged;

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
            string appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MetroHub");
            _catalogFilePath = Path.Combine(appDataDir, "radio_catalog.json");
            _catalogBakPath = Path.Combine(appDataDir, "radio_catalog.json.bak");
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
                            _cachedCatalog = parsed;
                            return _cachedCatalog;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RadioCatalogService] Failed reading {_catalogFilePath}: {ex.Message}");
                    // Attempt to restore from backup
                    if (File.Exists(_catalogBakPath))
                    {
                        try
                        {
                            string bakJson = File.ReadAllText(_catalogBakPath, Encoding.UTF8);
                            var bakParsed = JsonSerializer.Deserialize<RadioCatalog>(bakJson, JsonOptions);
                            if (bakParsed != null && bakParsed.Categories.Count > 0)
                            {
                                _cachedCatalog = bakParsed;
                                return _cachedCatalog;
                            }
                        }
                        catch { }
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
                        // Seed to AppData so future launches have the physical file ready
                        SaveCatalogAtomicInternal(_cachedCatalog);
                        return _cachedCatalog;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RadioCatalogService] Failed deserializing bundled catalog: {ex.Message}");
                }
            }

            // 3. Emergency fallback
            _cachedCatalog ??= CreateFallbackCatalog();
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
                    _cachedCatalog = factoryCatalog;
                    SaveCatalogAtomicInternal(_cachedCatalog);
                }
            }
        }

        CatalogChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private void SaveCatalogAtomicInternal(RadioCatalog catalog)
    {
        string? targetDir = Path.GetDirectoryName(_catalogFilePath);
        if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
        {
            try { Directory.CreateDirectory(targetDir); } catch { }
        }

        string json = JsonSerializer.Serialize(catalog, JsonOptions);
        string tmpPath = _catalogFilePath + ".tmp";

        try
        {
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
                catch
                {
                    try { File.Copy(_catalogFilePath, _catalogBakPath, overwrite: true); } catch { }
                    File.Move(tmpPath, _catalogFilePath, overwrite: true);
                    return;
                }
            }

            File.Move(tmpPath, _catalogFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RadioCatalogService] Atomic save failed: {ex.Message}");
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
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
        catch { }

        // 2. Try loading from filesystem (BaseDirectory, relative upward paths for test runners)
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates = new[]
            {
                Path.Combine(baseDir, "Assets", "Radio", "radio_catalog.json"),
                Path.Combine(baseDir, "..", "..", "..", "..", "src", "MetroHub", "Assets", "Radio", "radio_catalog.json"),
                Path.Combine(baseDir, "..", "..", "src", "MetroHub", "Assets", "Radio", "radio_catalog.json")
            };

            foreach (var path in candidates)
            {
                string fullPath = Path.GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    return File.ReadAllText(fullPath, Encoding.UTF8);
                }
            }
        }
        catch { }

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
