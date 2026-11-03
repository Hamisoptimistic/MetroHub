using MetroHub.Core.Models;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.AudioControls;
using MetroHub.Widgets.Registry;
using Xunit;

namespace MetroHub.Tests;

public class AudioControlsWidgetTests
{
    private static TileModel CreateTestTile(int spanX, int spanY)
    {
        return new TileModel
        {
            Id = System.Guid.NewGuid().ToString("N"),
            TileType = TileType.Widget,
            TargetPath = "audio_controls",
            SpanX = spanX,
            SpanY = spanY
        };
    }

    [Fact]
    public void AllowedSizes_IncludesLargeWideAndSquareLarge()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(6, 4);
            using var vm = new AudioControlsWidgetViewModel(tile);

            Assert.Contains(WidgetSize.LargeWide, vm.AllowedSizes);
            Assert.Contains(WidgetSize.SquareLarge, vm.AllowedSizes);
            Assert.Contains(WidgetSize.SlimWide, vm.AllowedSizes);
            Assert.Contains(WidgetSize.SlimBanner, vm.AllowedSizes);
            Assert.Contains(WidgetSize.Mega, vm.AllowedSizes);
            Assert.Contains(WidgetSize.Huge, vm.AllowedSizes);
            Assert.Equal(6, vm.AllowedSizes.Count);
        });
    }

    [Fact]
    public void WidgetRegistry_AudioControls_MatchesAllowedSizes()
    {
        var def = WidgetRegistry.Get("audio_controls");
        Assert.NotNull(def);
        Assert.Contains(WidgetSize.LargeWide, def.AllowedSizes);
        Assert.Contains(WidgetSize.SquareLarge, def.AllowedSizes);
        Assert.Equal(6, def.AllowedSizes.Count);
    }

    [Theory]
    [InlineData(4, 1, true)]
    [InlineData(8, 1, true)]
    [InlineData(6, 4, false)]
    [InlineData(6, 6, false)]
    [InlineData(8, 4, false)]
    [InlineData(8, 6, false)]
    public void IsCompactMode_ResolvesCorrectlyBySpanY(int spanX, int spanY, bool expectedCompact)
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile(spanX, spanY);
            using var vm = new AudioControlsWidgetViewModel(tile);

            Assert.Equal(expectedCompact, vm.IsCompactMode);
        });
    }
}
