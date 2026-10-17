using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class QueryRouterTests
{
    [Fact]
    public void Router_DefaultHardPrefixes_RouteCorrectly()
    {
        var router = new QueryRouter();

        var terminalRoute = router.Route("> ping 1.1.1.1");
        Assert.Equal(RouteKind.Terminal, terminalRoute.Kind);
        Assert.Equal("ping 1.1.1.1", terminalRoute.Remainder);
        Assert.True(terminalRoute.IsHardRoute);
        Assert.False(terminalRoute.IsAdditive);

        var settingsRoute = router.Route("set:bluetooth");
        Assert.Equal(RouteKind.Settings, settingsRoute.Kind);
        Assert.Equal("bluetooth", settingsRoute.Remainder);
        Assert.True(settingsRoute.IsHardRoute);
        Assert.False(settingsRoute.IsAdditive);
    }

    [Fact]
    public void Router_SoftExclusiveTriggers_RouteCorrectly()
    {
        var router = new QueryRouter();

        var killRoute = router.Route("kill node");
        Assert.Equal(RouteKind.ProcessKiller, killRoute.Kind);
        Assert.Equal("node", killRoute.Remainder);
        Assert.False(killRoute.IsHardRoute);
        Assert.False(killRoute.IsAdditive);

        var winRoute = router.Route("win chrome");
        Assert.Equal(RouteKind.WindowWalker, winRoute.Kind);
        Assert.Equal("chrome", winRoute.Remainder);

        var volRoute = router.Route("vol 50");
        Assert.Equal(RouteKind.Volume, volRoute.Kind);
        Assert.Equal("50", volRoute.Remainder);

        var timeRoute = router.Route("time tokyo");
        Assert.Equal(RouteKind.WorldClock, timeRoute.Kind);
        Assert.Equal("tokyo", timeRoute.Remainder);

        var caseRoute = router.Route("case hello world");
        Assert.Equal(RouteKind.TextTransform, caseRoute.Kind);
        Assert.Equal("hello world", caseRoute.Remainder);

        var cbRoute = router.Route("cb snippet");
        Assert.Equal(RouteKind.Clipboard, cbRoute.Kind);
        Assert.Equal("snippet", cbRoute.Remainder);
    }

    [Fact]
    public void Router_AdditiveBangsAndAudio_RouteCorrectly()
    {
        var router = new QueryRouter();

        var gRoute = router.Route("g eazy");
        Assert.Equal(RouteKind.WebBangGoogle, gRoute.Kind);
        Assert.Equal("eazy", gRoute.Remainder);
        Assert.False(gRoute.IsHardRoute);
        Assert.True(gRoute.IsAdditive);

        var ghRoute = router.Route("gh dotnet/wpf");
        Assert.Equal(RouteKind.WebBangGitHub, ghRoute.Kind);
        Assert.Equal("dotnet/wpf", ghRoute.Remainder);
        Assert.True(ghRoute.IsAdditive);

        var ytRoute = router.Route("yt lo-fi");
        Assert.Equal(RouteKind.WebBangYouTube, ytRoute.Kind);
        Assert.Equal("lo-fi", ytRoute.Remainder);
        Assert.True(ytRoute.IsAdditive);

        var ddgRoute = router.Route("ddg privacy");
        Assert.Equal(RouteKind.WebBangDuckDuckGo, ddgRoute.Kind);
        Assert.Equal("privacy", ddgRoute.Remainder);
        Assert.True(ddgRoute.IsAdditive);

        var audioExact = router.Route("audio");
        Assert.Equal(RouteKind.Audio, audioExact.Kind);
        Assert.Equal(string.Empty, audioExact.Remainder);
        Assert.True(audioExact.IsAdditive);

        var audioArgs = router.Route("audio headphones");
        Assert.Equal(RouteKind.Audio, audioArgs.Kind);
        Assert.Equal("headphones", audioArgs.Remainder);
        Assert.True(audioArgs.IsAdditive);
    }

    [Fact]
    public void Router_GeneralQuery_FallsThroughToUniversal()
    {
        var router = new QueryRouter();

        var route = router.Route("metrohub report.pdf");
        Assert.Equal(RouteKind.Universal, route.Kind);
        Assert.Equal("metrohub report.pdf", route.Remainder);
        Assert.False(route.IsHardRoute);
        Assert.False(route.IsAdditive);
    }

    [Fact]
    public void Router_CustomSettingsAndUpdate_AppliesCustomPrefixes()
    {
        var custom = new SearchSettings
        {
            TerminalPrefix = "$",
            SettingsPrefix = "s:"
        };

        var router = new QueryRouter(custom);

        var termRoute = router.Route("$ dotnet build");
        Assert.Equal(RouteKind.Terminal, termRoute.Kind);
        Assert.Equal("dotnet build", termRoute.Remainder);

        // Update with reset
        router.Update(new SearchSettings());
        var defaultTermRoute = router.Route("> dotnet build");
        Assert.Equal(RouteKind.Terminal, defaultTermRoute.Kind);
        Assert.Equal("dotnet build", defaultTermRoute.Remainder);
    }

    [Fact]
    public void Router_EmptyOrInvalidPrefixInSettings_FallsBackToDefaultSafely()
    {
        var broken = new SearchSettings
        {
            TerminalPrefix = "   ",
            SettingsPrefix = null!
        };

        var router = new QueryRouter(broken);

        var termRoute = router.Route("> code .");
        Assert.Equal(RouteKind.Terminal, termRoute.Kind);
        Assert.Equal("code .", termRoute.Remainder);

        var setRoute = router.Route("set:display");
        Assert.Equal(RouteKind.Settings, setRoute.Kind);
        Assert.Equal("display", setRoute.Remainder);
    }

    [Fact]
    public async Task TerminalSource_ProducesExpectedCandidate()
    {
        var router = new QueryRouter();
        var source = new TerminalSource(router);

        var query = SearchRanker.ParseQuery("> code .", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var candidate = results[0];
        Assert.Equal("Run '> code .'", candidate.DisplayName);
        Assert.Equal("code .", candidate.FullPathOrKey);
        Assert.NotNull(candidate.PrimaryAction);
        Assert.Equal("run_terminal", candidate.PrimaryAction.Id);
    }

    [Fact]
    public async Task TerminalSource_BarePrefix_ProducesOpenTerminalCandidate()
    {
        var router = new QueryRouter();
        var source = new TerminalSource(router);

        var query = SearchRanker.ParseQuery(">", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var candidate = results[0];
        Assert.Equal("Open terminal", candidate.DisplayName);
        Assert.NotNull(candidate.PrimaryAction);
    }

    [Fact]
    public async Task EverythingSearchSource_SkipsTerminalQueries()
    {
        var everything = new EverythingSearchSource();
        var query = SearchRanker.ParseQuery("> ping 1.1.1.1", 1);

        var results = await everything.SearchAsync(query, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public void EverythingSearchSource_RawPrefix_Preserved()
    {
        var query = SearchRanker.ParseQuery("raw:*.xaml", 1);
        string text = EverythingSearchSource.BuildSearchText(query);
        Assert.Equal("*.xaml", text);
    }
}

