using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace MetroHub.Core.Radio;

/// <summary>
/// Service responsible for loading and querying the station catalog.
/// Loads the embedded 40-station master catalog with graceful fallbacks.
/// </summary>
public sealed class RadioCatalogService
{
    public static RadioCatalogService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private RadioCatalog? _cachedCatalog;

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
        return catalog.Categories.Find(c => string.Equals(c.Id, categoryId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets all stations across all categories.
    /// </summary>
    public IReadOnlyList<RadioStation> GetAllStations()
    {
        var catalog = LoadCatalog();
        var list = new List<RadioStation>();
        foreach (var category in catalog.Categories)
        {
            list.AddRange(category.Stations);
        }
        return list;
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
    /// Loads the radio catalog from the embedded pack resource or disk fallback.
    /// </summary>
    public RadioCatalog LoadCatalog()
    {
        if (_cachedCatalog != null)
        {
            return _cachedCatalog;
        }

        string? json = null;

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
                json = reader.ReadToEnd();
            }
        }
        catch
        {
            // Fall back to direct file read
        }

        // 2. Try loading from filesystem (BaseDirectory, relative upward paths for test runners)
        if (string.IsNullOrWhiteSpace(json))
        {
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
                        json = File.ReadAllText(fullPath, Encoding.UTF8);
                        break;
                    }
                }
            }
            catch
            {
                // Fall through to hardcoded safety fallback
            }
        }

        // 3. Deserialize catalog
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                _cachedCatalog = JsonSerializer.Deserialize<RadioCatalog>(json, JsonOptions);
            }
            catch
            {
                // Fallback will supply minimal emergency catalog
            }
        }

        // 4. Emergency fallback in case of corrupt or missing asset
        _cachedCatalog ??= CreateFallbackCatalog();

        return _cachedCatalog;
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
