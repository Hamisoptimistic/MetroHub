using System;
using System.Linq;
using MetroHub.Core.Models;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.Template;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// OFFICIAL BOILERPLATE UNIT TEST: Copy this file for your new widget's test suite!
/// Demonstrates how to test:
/// 1. Initial State & Allowed Sizes
/// 2. Commands & Click Handlers
/// 3. Settings JSON Persistence Round-Trip
/// 4. Lifecycle (Heartbeat, Pause/Resume) & Disposal
/// </summary>
public class TemplateWidgetTests
{
    private static TileModel CreateTestTile(string? settingsJson = null)
    {
        return new TileModel
        {
            Id = "test_template_1",
            TileType = TileType.Widget,
            TargetPath = "template",
            SpanX = 2,
            SpanY = 2,
            SettingsJson = settingsJson
        };
    }

    [Fact]
    public void Initializes_WithDefaultSettings_WhenJsonIsNull()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            Assert.Equal("Template Widget", vm.Label);
            Assert.Equal("#2563EB", vm.BoxColor);
            Assert.Equal(0, vm.Counter);
            Assert.True(vm.IsFeatureEnabled);
            Assert.Contains(WidgetSize.Small, vm.AllowedSizes);
            Assert.Contains(WidgetSize.Medium, vm.AllowedSizes);
        });
    }

    [Fact]
    public void CycleColorCommand_IncrementsCounter_AndSavesSettings()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            vm.CycleColor();

            Assert.Equal(1, vm.Counter);
            Assert.NotNull(tile.SettingsJson);

            // Verify settings deserialize cleanly from the persisted JSON
            var settings = WidgetSerializer.Deserialize<TemplateWidgetSettings>(tile.SettingsJson);
            Assert.NotNull(settings);
            Assert.Equal(1, settings!.Counter);
            Assert.Equal(vm.BoxColor, settings.BoxColor);
        });
    }

    [Fact]
    public void ResetCounterCommand_ResetsCounterToZero()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            vm.CycleColor();
            vm.CycleColor();
            Assert.Equal(2, vm.Counter);

            vm.ResetCounter();
            Assert.Equal(0, vm.Counter);
            Assert.Equal("Template Widget", vm.Label);
        });
    }

    [Fact]
    public void OnSecondTick_UpdatesLiveStatus_WhenFeatureEnabled()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            vm.IsFeatureEnabled = true;
            var testTime = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
            vm.OnSecondTick(testTime);

            Assert.Contains("Active", vm.LiveStatus);
        });
    }

    [Fact]
    public void PauseAndResume_UpdateLiveStatusCleanly()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            vm.Pause();
            Assert.Contains("Paused", vm.LiveStatus);

            vm.Resume();
            Assert.Equal("Resumed", vm.LiveStatus);
        });
    }

    [Fact]
    public void RefreshDataAsync_ExecutesWithoutExceptions()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            vm.RefreshDataAsync().GetAwaiter().GetResult();
            Assert.Contains("Updated", vm.LiveStatus);
        });
    }

    [Fact]
    public void ContextMenu_ReturnsConfigAndResetItems()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(null);
            using var vm = new TemplateWidgetViewModel(tile);

            var items = vm.GetContextMenuItems().ToList();
            Assert.True(items.Count >= 2);
        });
    }
}
