using System.Threading;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Media;
using Xunit;

namespace MetroHub.Tests;

public class MediaWidgetSanityTests
{
    [Fact]
    public void ResolveSourceName_StandardAppIds_ReturnsCleanNames()
    {
        Assert.Equal("Spotify", MediaWidgetViewModel.ResolveSourceName("spotify.exe", null, null));
        Assert.Equal("Apple Music", MediaWidgetViewModel.ResolveSourceName("AppleMusic", null, null));
        Assert.Equal("YouTube", MediaWidgetViewModel.ResolveSourceName("chrome.exe", "Song - YouTube", null));
        Assert.Equal("YouTube Music", MediaWidgetViewModel.ResolveSourceName("chrome.exe", "Song", "Artist - Topic"));
        Assert.Equal("VLC Media Player", MediaWidgetViewModel.ResolveSourceName("vlc.exe", null, null));
    }

    [Fact]
    public void LayoutModes_ResolveCorrectlyBasedOnDimensions()
    {
        var slimModel = new TileModel { SpanX = 4, SpanY = 1, TargetPath = "media" };
        var zuneModel = new TileModel { SpanX = 4, SpanY = 6, TargetPath = "media" };
        var standardModel = new TileModel { SpanX = 6, SpanY = 2, TargetPath = "media" };

        var slimVm = new MediaWidgetViewModel(slimModel);
        Assert.True(slimVm.IsSlimMode);
        Assert.False(slimVm.IsZuneMode);
        Assert.False(slimVm.IsStandardMode);

        var zuneVm = new MediaWidgetViewModel(zuneModel);
        Assert.False(zuneVm.IsSlimMode);
        Assert.True(zuneVm.IsZuneMode);
        Assert.False(zuneVm.IsStandardMode);

        var standardVm = new MediaWidgetViewModel(standardModel);
        Assert.False(standardVm.IsSlimMode);
        Assert.False(standardVm.IsZuneMode);
        Assert.True(standardVm.IsStandardMode);
    }

    [Fact]
    public void MediaWidget_InitialState_IsClean()
    {
        var model = new TileModel { SpanX = 8, SpanY = 4, TargetPath = "media" };
        var vm = new MediaWidgetViewModel(model);

        Assert.Equal("No media playing", vm.Title);
        Assert.False(vm.HasMedia);
        Assert.True(vm.HasNoMedia);
        Assert.True(vm.CanPlayPause);
        Assert.True(vm.CanSkipNext);
        Assert.True(vm.CanSkipPrevious);
    }
}
