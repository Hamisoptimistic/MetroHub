using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

public class RadioCatalogTests
{
    [Fact]
    public void LoadCatalog_LoadsAllFourCategories_WithFortyStations()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new RadioCatalogService(tempCatalogPath);
            var catalog = service.LoadCatalog();

            Assert.NotNull(catalog);
            Assert.Equal(4, catalog.Categories.Count);

            var ambient = catalog.Categories.FirstOrDefault(c => c.Id == "ambient");
            var nature = catalog.Categories.FirstOrDefault(c => c.Id == "nature");
            var lofi = catalog.Categories.FirstOrDefault(c => c.Id == "lofi");
            var coding = catalog.Categories.FirstOrDefault(c => c.Id == "coding");

            Assert.NotNull(ambient);
            Assert.NotNull(nature);
            Assert.NotNull(lofi);
            Assert.NotNull(coding);

            Assert.Equal(12, ambient.Stations.Count);
            Assert.Equal(10, nature.Stations.Count);
            Assert.Equal(13, lofi.Stations.Count);
            Assert.Equal(5, coding.Stations.Count);

            int totalStations = catalog.Categories.Sum(c => c.Stations.Count);
            Assert.Equal(40, totalStations);

            // Verify each station has valid fields
            foreach (var category in catalog.Categories)
            {
                foreach (var station in category.Stations)
                {
                    Assert.False(string.IsNullOrWhiteSpace(station.Id), "Station Id cannot be empty");
                    Assert.False(string.IsNullOrWhiteSpace(station.Name), $"Station {station.Id} Name cannot be empty");
                    Assert.False(string.IsNullOrWhiteSpace(station.StreamUrl), $"Station {station.Id} StreamUrl cannot be empty");
                    Assert.True(station.BitrateKbps > 0, $"Station {station.Id} BitrateKbps must be > 0");
                }
            }
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    [Fact]
    public void LoadCatalog_SeedsIsolatedPathOnFirstRun_IfMissing()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            Assert.False(File.Exists(tempCatalogPath));

            var service = new RadioCatalogService(tempCatalogPath);
            var catalog = service.LoadCatalog();

            Assert.NotNull(catalog);
            Assert.True(File.Exists(tempCatalogPath), "File should be seeded on first run");

            string content = File.ReadAllText(tempCatalogPath);
            Assert.Contains("dronezone", content);
            Assert.Contains("ambient", content);
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    [Fact]
    public async Task AddCustomStation_AppendsToSpecifiedCategory_AndMarksIsCustom()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new RadioCatalogService(tempCatalogPath);
            var initialCatalog = service.LoadCatalog();
            var codingCat = initialCatalog.Categories.First(c => c.Id == "coding");
            int initialCodingCount = codingCat.Stations.Count;

            bool eventFired = false;
            service.CatalogChanged += (_, _) => eventFired = true;

            var newCustom = new RadioStation
            {
                Id = "custom_synth_radio",
                Name = "Synthwave Underground",
                StreamUrl = "https://stream.example.com/synth.mp3",
                BitrateKbps = 320,
                Description = "Custom high-energy synth stream",
                ApiStationUuid = "uuid-12345"
            };

            await service.AddCustomStationAsync(newCustom, "coding");

            Assert.True(eventFired, "CatalogChanged event should fire");

            var updatedCategory = service.GetCategory("coding");
            Assert.NotNull(updatedCategory);
            Assert.Equal(initialCodingCount + 1, updatedCategory.Stations.Count);

            var added = updatedCategory.Stations.FirstOrDefault(s => s.Id == "custom_synth_radio");
            Assert.NotNull(added);
            Assert.True(added.IsCustom);
            Assert.Equal("coding", added.Category);
            Assert.Equal("uuid-12345", added.ApiStationUuid);

            // Verify persistence by loading in a brand new service instance from disk
            var secondaryService = new RadioCatalogService(tempCatalogPath);
            var reloadedStation = secondaryService.GetStationById("custom_synth_radio");
            Assert.NotNull(reloadedStation);
            Assert.True(reloadedStation.IsCustom);
            Assert.Equal("Synthwave Underground", reloadedStation.Name);
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    [Fact]
    public async Task DeleteStation_OnlyDeletesCustomStation_ProtectsFactoryPreset()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new RadioCatalogService(tempCatalogPath);
            service.LoadCatalog();

            // 1. Attempt to delete a factory preset (e.g. dronezone)
            bool factoryDeleteResult = await service.DeleteStationAsync("dronezone");
            Assert.False(factoryDeleteResult, "Factory presets must be protected from deletion");
            Assert.NotNull(service.GetStationById("dronezone"));

            // 2. Add a custom station and delete it
            var custom = new RadioStation
            {
                Id = "my_custom_station",
                Name = "My Temp Stream",
                StreamUrl = "https://stream.example.com/live.mp3"
            };
            await service.AddCustomStationAsync(custom, "nature");
            Assert.NotNull(service.GetStationById("my_custom_station"));

            bool customDeleteResult = await service.DeleteStationAsync("my_custom_station");
            Assert.True(customDeleteResult, "Custom stations must be deletable");
            Assert.Null(service.GetStationById("my_custom_station"));

            // 3. Confirm deletion persisted to disk
            var reloadService = new RadioCatalogService(tempCatalogPath);
            Assert.Null(reloadService.GetStationById("my_custom_station"));
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    [Fact]
    public async Task UpdateStationUrl_UpdatesStreamUrl_ForSelfHealing()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new RadioCatalogService(tempCatalogPath);
            service.LoadCatalog();

            string newUrl = "https://new-mirror.somafm.com/dronezone-128-mp3";
            bool updated = await service.UpdateStationUrlAsync("dronezone", newUrl);

            Assert.True(updated);
            var updatedStation = service.GetStationById("dronezone");
            Assert.NotNull(updatedStation);
            Assert.Equal(newUrl, updatedStation.StreamUrl);

            // Confirm persisted
            var reloadService = new RadioCatalogService(tempCatalogPath);
            var reloadedStation = reloadService.GetStationById("dronezone");
            Assert.NotNull(reloadedStation);
            Assert.Equal(newUrl, reloadedStation.StreamUrl);
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    [Fact]
    public async Task RestoreFactoryDefaults_ResetsCustomizations()
    {
        string tempCatalogPath = Path.Combine(Path.GetTempPath(), $"radio_test_{Guid.NewGuid():N}.json");
        try
        {
            var service = new RadioCatalogService(tempCatalogPath);
            service.LoadCatalog();

            await service.AddCustomStationAsync(new RadioStation
            {
                Id = "custom_test_restore",
                Name = "Temp",
                StreamUrl = "https://test.com/stream"
            }, "ambient");

            Assert.NotNull(service.GetStationById("custom_test_restore"));

            await service.RestoreFactoryDefaultsAsync();

            Assert.Null(service.GetStationById("custom_test_restore"));
            var catalog = service.LoadCatalog();
            Assert.Equal(40, catalog.Categories.Sum(c => c.Stations.Count));
        }
        finally
        {
            CleanupTestFiles(tempCatalogPath);
        }
    }

    private static void CleanupTestFiles(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        try { if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp"); } catch { }
        try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
    }
}
