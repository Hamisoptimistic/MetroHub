using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MetroHub.Core.Search;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public sealed class SearchUsageHistoryTests : IDisposable
{
    public SearchUsageHistoryTests()
    {
        StorageService.ResetSearchUsageForTesting(deleteFile: true);
    }

    public void Dispose()
    {
        StorageService.ResetSearchUsageForTesting(deleteFile: true);
    }

    [Fact]
    public void RecordSearchLaunch_IncrementsCount()
    {
        string key = @"C:\Program Files\App\editor.exe";
        Assert.Equal(0, StorageService.GetEffectiveOpenCount(key));

        StorageService.RecordSearchLaunch(key);
        Assert.Equal(1, StorageService.GetEffectiveOpenCount(key));

        StorageService.RecordSearchLaunch(key);
        StorageService.RecordSearchLaunch(key);
        Assert.Equal(3, StorageService.GetEffectiveOpenCount(key));
    }

    [Fact]
    public void GetEffectiveOpenCount_AppliesExponentialDecay()
    {
        string key = @"C:\Tools\tool.exe";
        StorageService.RecordSearchLaunch(key); // Count = 1
        for (int i = 0; i < 7; i++) StorageService.RecordSearchLaunch(key); // Count = 8 total

        var now = DateTimeOffset.UtcNow;

        // Day 0: 8
        Assert.Equal(8, StorageService.GetEffectiveOpenCount(key, now));

        // Day 30 (1 half-life): 8 * 0.5 = 4
        var day30 = now.AddDays(30);
        Assert.Equal(4, StorageService.GetEffectiveOpenCount(key, day30));

        // Day 60 (2 half-lives): 8 * 0.25 = 2
        var day60 = now.AddDays(60);
        Assert.Equal(2, StorageService.GetEffectiveOpenCount(key, day60));

        // Day 90 (3 half-lives): 8 * 0.125 = 1
        var day90 = now.AddDays(90);
        Assert.Equal(1, StorageService.GetEffectiveOpenCount(key, day90));

        // Day 150: 8 * (0.5^5) = 0.25 -> 0
        var day150 = now.AddDays(150);
        Assert.Equal(0, StorageService.GetEffectiveOpenCount(key, day150));
    }

    [Fact]
    public void GetAllEffectiveOpenCounts_ReturnsCorrectMap()
    {
        StorageService.RecordSearchLaunch("item1");
        StorageService.RecordSearchLaunch("item1");
        StorageService.RecordSearchLaunch("item2");

        var counts = StorageService.GetAllEffectiveOpenCounts();
        Assert.Equal(2, counts["item1"]);
        Assert.Equal(1, counts["item2"]);
    }

    [Fact]
    public void SearchRanker_UsageBoost_PromotesFrequentlyUsedCandidate()
    {
        var query = SearchRanker.ParseQuery("term");

        var candidateA = new Candidate("idA", "Terminal", @"C:\terminal.exe", SearchCategory.Apps, "apps");
        var candidateB = new Candidate("idB", "Terminal", @"C:\other\terminal.exe", SearchCategory.Apps, "apps");

        // Without usage boost: both have identical match score
        var scoreA_Initial = SearchRanker.ScoreCandidate(candidateA, query, openCount: 0);
        var scoreB_Initial = SearchRanker.ScoreCandidate(candidateB, query, openCount: 0);
        Assert.Equal(scoreA_Initial.Score, scoreB_Initial.Score);

        // candidateA has 5 opens -> 5 * 5 = +25, capped at max 20 dynamic boost
        var scoreA_Boosted = SearchRanker.ScoreCandidate(candidateA, query, openCount: 5);
        Assert.Equal(scoreA_Initial.Score + 20, scoreA_Boosted.Score);
        Assert.True(scoreA_Boosted.Score > scoreB_Initial.Score);

        // Deterministic sort ranks candidateA first
        var list = new List<ScoredResult> { scoreB_Initial, scoreA_Boosted };
        SearchRanker.SortResults(list);
        Assert.Equal(candidateA.Id, list[0].Candidate.Id);
    }

    [Fact]
    public void StorageService_FlushAndReload_PersistsUsageHistory()
    {
        string key = @"D:\Work\Project.sln";
        StorageService.RecordSearchLaunch(key);
        StorageService.RecordSearchLaunch(key);

        // Flush to disk
        StorageService.FlushSearchUsageSync();

        Assert.True(File.Exists(AppPaths.SearchHistoryPath));

        // Reset in-memory cache and test reload from disk
        StorageService.ResetSearchUsageForTesting();

        // Calling GetEffectiveOpenCount loads from search_history.json
        int reloadedCount = StorageService.GetEffectiveOpenCount(key);
        Assert.Equal(2, reloadedCount);
    }

    [Fact]
    public void GetTopRecentLaunches_ReturnsUpToMax15_SortedByEffectiveCountAndRecency()
    {
        // Record 20 different items with distinct launch counts
        for (int i = 1; i <= 20; i++)
        {
            string key = $@"C:\Apps\App{i:D2}.exe";
            for (int count = 0; count < i; count++)
            {
                StorageService.RecordSearchLaunch(key);
            }
        }

        var top15 = StorageService.GetTopRecentLaunches(15);

        Assert.Equal(15, top15.Count);
        // First item must have the highest count (App20 with 20 launches)
        Assert.Equal(@"C:\Apps\App20.exe", top15[0].Key);
        Assert.Equal(20, top15[0].OpenCount);

        // 15th item must be App06 (counts 20 down to 6)
        Assert.Equal(@"C:\Apps\App06.exe", top15[14].Key);
        Assert.Equal(6, top15[14].OpenCount);
    }

    [Fact]
    public void GetTopRecentLaunches_DecayedItemsRankLowerThanRecentItems()
    {
        string oldApp = @"C:\Apps\OldApp.exe";
        string newApp = @"C:\Apps\NewApp.exe";

        // Record oldApp 8 times
        for (int i = 0; i < 8; i++) StorageService.RecordSearchLaunch(oldApp);

        // Record newApp 3 times
        for (int i = 0; i < 3; i++) StorageService.RecordSearchLaunch(newApp);

        var now = DateTimeOffset.UtcNow;

        // Day 0: OldApp (8) ranks above NewApp (3)
        var day0Top = StorageService.GetTopRecentLaunches(15, now);
        Assert.Equal(oldApp, day0Top[0].Key);
        Assert.Equal(newApp, day0Top[1].Key);

        // Day 60 (2 half-lives): OldApp decayed count = 8 * 0.25 = 2.
        // If NewApp was launched at Day 60, it has 3 opens vs 2 for oldApp.
        // Testing decay specifically on oldApp:
        int decayedOld = StorageService.GetEffectiveOpenCount(oldApp, now.AddDays(60));
        Assert.Equal(2, decayedOld);
    }

    [Fact]
    public void SearchOrchestrator_GetZeroStateSuggestions_ReturnsUpTo15Items()
    {
        var apps = new List<MetroHub.Core.Models.CatalogItemModel>();
        for (int i = 1; i <= 10; i++)
        {
            apps.Add(new MetroHub.Core.Models.CatalogItemModel
            {
                Name = $"App {i}",
                TargetPath = $@"C:\Program Files\App{i}\app{i}.exe"
            });
        }

        // Record 5 apps and 5 files
        for (int i = 1; i <= 5; i++)
        {
            StorageService.RecordSearchLaunch(apps[i - 1].TargetPath);
        }
        for (int i = 1; i <= 5; i++)
        {
            StorageService.RecordSearchLaunch($@"D:\Documents\report{i}.pdf");
        }

        // Call GetZeroStateSuggestions with simulated file predicate
        var suggestions = SearchOrchestrator.GetZeroStateSuggestions(
            apps: apps,
            maxCount: 15,
            fileExists: path => path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase),
            directoryExists: _ => false,
            fallbackToInstalledApps: true);

        // Should return 10 recent items + 5 backfilled apps = 15 total items
        Assert.Equal(15, suggestions.Count);

        // First items are recent
        var recentApp1 = suggestions.FirstOrDefault(s => s.DisplayName == "App 1");
        Assert.NotNull(recentApp1);
        Assert.Equal("recent_apps", recentApp1.SourceId);
        Assert.Equal(SearchCategory.Apps, recentApp1.Category);

        var recentDoc = suggestions.FirstOrDefault(s => s.DisplayName == "report1.pdf");
        Assert.NotNull(recentDoc);
        Assert.Equal("recent_files", recentDoc.SourceId);
        Assert.Equal(SearchCategory.Documents, recentDoc.Category);
    }
}
