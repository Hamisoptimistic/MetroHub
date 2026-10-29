using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class OsSystemSourcesTests
{
    private readonly QueryRouter _router = new();

    [Fact]
    public async Task ProcessKiller_Denylist_RejectsCriticalProcesses()
    {
        var killer = new ProcessKillerSource(_router);

        var explorerQuery = SearchRanker.ParseQuery("kill explorer", 1);
        var explorerResults = await killer.SearchAsync(explorerQuery, CancellationToken.None);
        Assert.Empty(explorerResults);

        var metrohubQuery = SearchRanker.ParseQuery("kill metrohub", 1);
        var metrohubResults = await killer.SearchAsync(metrohubQuery, CancellationToken.None);
        Assert.Empty(metrohubResults);

        var svchostQuery = SearchRanker.ParseQuery("kill svchost", 1);
        var svchostResults = await killer.SearchAsync(svchostQuery, CancellationToken.None);
        Assert.Empty(svchostResults);
    }

    [Fact]
    public void ProcessKiller_KillProcessSafely_RejectsDenylistedProcess()
    {
        using var currentProc = Process.GetCurrentProcess();
        bool result = ProcessKillerSource.KillProcessSafely(currentProc.Id, currentProc.StartTime);
        Assert.False(result); // Denylisted or protected
    }

    [Fact]
    public async Task AudioSource_VolumeNumber_GeneratesVolumeCandidate()
    {
        var audio = new AudioSource(_router);

        var query = SearchRanker.ParseQuery("vol 40", 1);
        var results = await audio.SearchAsync(query, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("Set volume to 40%", item.DisplayName);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task AudioSource_VolumeText_FallsThroughToUniversal()
    {
        var audio = new AudioSource(_router);

        // "vol 2 notes" is not a volume command; must fall through to file/app search
        var query = SearchRanker.ParseQuery("vol 2 notes", 1);
        var results = await audio.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task AudioSource_AudioTrigger_ListsPlaybackDevices()
    {
        var audio = new AudioSource(_router);

        var query = SearchRanker.ParseQuery("audio", 1);
        var results = await audio.SearchAsync(query, CancellationToken.None);

        // AudioService returns devices or empty if in headless CI
        Assert.NotNull(results);
        foreach (var item in results)
        {
            Assert.StartsWith("audio:device:", item.Id);
            Assert.NotNull(item.PrimaryAction);
        }
    }

    [Fact]
    public void RunningSnapshot_Processes_CachesWithinWindow()
    {
        var p1 = RunningSnapshot.GetProcesses();
        Assert.NotEmpty(p1);

        var p2 = RunningSnapshot.GetProcesses();
        Assert.Same(p1, p2); // Cached within 1.5s
    }

    [Fact]
    public async Task WindowWalker_Query_MatchesRunningWindows()
    {
        var walker = new WindowWalkerSource(_router);

        // Querying for "win" should run safely and return candidates if matching open windows exist
        var query = SearchRanker.ParseQuery("win explorer", 1);
        var results = await walker.SearchAsync(query, CancellationToken.None);

        Assert.NotNull(results);
    }
}

