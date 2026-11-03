using System.Linq;
using System.Windows.Controls;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Weather;
using Xunit;

namespace MetroHub.Tests;

public class WeatherWidgetTests
{
    private static TileModel CreateTestTile()
    {
        return new TileModel
        {
            Id = System.Guid.NewGuid().ToString("N"),
            TileType = TileType.Widget,
            TargetPath = "weather",
            SpanX = 4,
            SpanY = 2
        };
    }

    [Fact]
    public void WeatherWidget_DefaultBackgroundStyle_IsAmbientGlow()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new WeatherWidgetViewModel(tile);

            Assert.Equal(WeatherBackgroundStyle.AmbientGlow, vm.BackgroundStyle);
            Assert.True(vm.IsAmbientGlowEnabled);
        });
    }

    [Fact]
    public void WeatherWidget_ContextMenu_DoesNotContainAutomaticLocationOrHorizonAura()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new WeatherWidgetViewModel(tile);

            var items = vm.GetContextMenuItems().ToList();
            var headers = items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();

            // Must NOT contain Automatic Location
            Assert.DoesNotContain(headers, h => h?.Contains("Automatic Location", System.StringComparison.OrdinalIgnoreCase) == true);

            // Style submenu should exist
            var styleMenu = items.OfType<MenuItem>().FirstOrDefault(i => i.Header?.ToString() == "Style");
            Assert.NotNull(styleMenu);

            var styleSubItems = styleMenu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).ToList();

            // Must contain Ambient Glow and None, but NOT Horizon Aura
            Assert.Contains("Ambient Glow", styleSubItems);
            Assert.Contains("None", styleSubItems);
            Assert.DoesNotContain(styleSubItems, s => s?.Contains("Horizon Aura", System.StringComparison.OrdinalIgnoreCase) == true);
            Assert.Equal(2, styleSubItems.Count);
        });
    }

    [Fact]
    public void WeatherWidget_ToggleUnits_TogglesFahrenheit()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new WeatherWidgetViewModel(tile);

            Assert.False(vm.IsFahrenheit);

            vm.ToggleUnits();
            Assert.True(vm.IsFahrenheit);

            vm.ToggleUnits();
            Assert.False(vm.IsFahrenheit);
        });
    }

    [Fact]
    public void WeatherWidget_SetStyle_SwitchesBetweenAmbientGlowAndNone()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new WeatherWidgetViewModel(tile);

            vm.SetStyle(WeatherBackgroundStyle.None);
            Assert.Equal(WeatherBackgroundStyle.None, vm.BackgroundStyle);
            Assert.False(vm.IsAmbientGlowEnabled);

            vm.SetStyle(WeatherBackgroundStyle.AmbientGlow);
            Assert.Equal(WeatherBackgroundStyle.AmbientGlow, vm.BackgroundStyle);
            Assert.True(vm.IsAmbientGlowEnabled);
        });
    }

    [Fact]
    public void WeatherWidget_PrecipitationProbability_PrefersDailyMaxOverHourly()
    {
        WpfTestHost.RunSta(() =>
        {
            var cachePath = MetroHub.Core.Services.AppPaths.ResolveCacheFile("v1_weather_cache.json");
            MetroHub.Core.Services.AppPaths.EnsureDirectory(cachePath);

            var entry = new MetroHub.Widgets.Catalog.Weather.Services.WeatherCacheEntry
            {
                City = "TestCity",
                Latitude = 0.0,
                Longitude = 0.0,
                TimestampUtc = System.DateTime.UtcNow,
                Forecast = new MetroHub.Widgets.Catalog.Weather.Services.OpenMeteoForecastResponse
                {
                    Current = new MetroHub.Widgets.Catalog.Weather.Services.CurrentWeatherDto { Temperature = 20.0, RelativeHumidity = 50 },
                    Daily = new MetroHub.Widgets.Catalog.Weather.Services.DailyWeatherDto
                    {
                        PrecipitationProbabilityMax = [85],
                        TemperatureMax = [25.0],
                        TemperatureMin = [15.0]
                    },
                    Hourly = new MetroHub.Widgets.Catalog.Weather.Services.HourlyWeatherDto
                    {
                        PrecipitationProbability = [10] // Midnight index
                    }
                }
            };
            System.IO.File.WriteAllText(cachePath, System.Text.Json.JsonSerializer.Serialize(entry));

            var tile = CreateTestTile();
            using var vm = new WeatherWidgetViewModel(tile);

            Assert.Equal(85, vm.PrecipitationProbability);
            Assert.Equal("85%", vm.PrecipitationProbabilityText);
        });
    }
}
