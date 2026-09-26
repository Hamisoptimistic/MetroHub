using System.Linq;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

public class RadioCatalogTests
{
    [Fact]
    public void LoadCatalog_LoadsAllFourCategories_WithFortyStations()
    {
        var service = new RadioCatalogService();
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
}
