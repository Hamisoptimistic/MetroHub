using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public class SearchOrchestratorTests
{
    private static List<CatalogItemModel> CreateSampleApps() => new()
    {
        new CatalogItemModel { Name = "Visual Studio Code", TargetPath = @"C:\Program Files\code.exe" },
        new CatalogItemModel { Name = "Notepad", TargetPath = @"C:\Windows\notepad.exe" },
        new CatalogItemModel { Name = "Calculator", TargetPath = @"C:\Windows\calc.exe" },
        new CatalogItemModel { Name = "Windows Terminal", TargetPath = @"C:\WindowsApps\wt.exe" },
        new CatalogItemModel { Name = "Firefox", TargetPath = @"C:\Program Files\firefox.exe" }
    };

    [Fact]
    public async Task SetQuery_DebouncesKeystrokes_OnlyExecutesLatestQuery()
    {
        var appSource = new AppSearchSource(CreateSampleApps);
        using var orchestrator = new SearchOrchestrator(new[] { appSource }, debounceMs: 50);

        var snapshots = new List<SearchSnapshot>();
        orchestrator.SnapshotUpdated += s =>
        {
            if (!s.IsEmpty) snapshots.Add(s);
        };

        // Rapid keystrokes within 15ms
        orchestrator.SetQuery("v");
        orchestrator.SetQuery("vs");
        orchestrator.SetQuery("vsc");

        // Wait for debounce (50ms) + execution (<10ms)
        await Task.Delay(150);

        Assert.Single(snapshots);
        var snapshot = snapshots[0];
        Assert.Single(snapshot.Groups);
        Assert.Equal("Visual Studio Code", snapshot.Groups[0].Items[0].Candidate.DisplayName);
    }

    [Fact]
    public void SetQuery_Empty_ImmediatelyResetsSnapshot()
    {
        using var orchestrator = new SearchOrchestrator(debounceMs: 50);

        SearchSnapshot? published = null;
        orchestrator.SnapshotUpdated += s => published = s;

        orchestrator.SetQuery("");

        Assert.NotNull(published);
        Assert.True(published.IsEmpty);
    }

    [Fact]
    public async Task SearchSession_AppSearchSource_CompletesUnder50msBudget()
    {
        var appSource = new AppSearchSource(CreateSampleApps);
        using var orchestrator = new SearchOrchestrator(new[] { appSource }, debounceMs: 10);

        var tcs = new TaskCompletionSource<SearchSnapshot>();
        orchestrator.SnapshotUpdated += s =>
        {
            if (!s.IsEmpty) tcs.TrySetResult(s);
        };

        orchestrator.SetQuery("calc");

        var snapshot = await Task.WhenAny(tcs.Task, Task.Delay(500)) == tcs.Task
            ? await tcs.Task
            : null;

        Assert.NotNull(snapshot);
        Assert.True(snapshot.ElapsedMs < 50, $"Elapsed {snapshot.ElapsedMs}ms exceeded 50ms budget");
        Assert.Single(snapshot.Groups);
        Assert.Equal("Calculator", snapshot.Groups[0].Items[0].Candidate.DisplayName);
        Assert.Equal(MatchKind.Prefix, snapshot.Groups[0].Items[0].MatchKind);
    }

    [Fact]
    public async Task SearchSession_ModePrefix_SkipsAppsIfFilesOnly()
    {
        var appSource = new AppSearchSource(CreateSampleApps);
        using var orchestrator = new SearchOrchestrator(new[] { appSource }, debounceMs: 10);

        var tcs = new TaskCompletionSource<SearchSnapshot>();
        orchestrator.SnapshotUpdated += s =>
        {
            tcs.TrySetResult(s);
        };

        // "file:" mode prefix asks for files only
        orchestrator.SetQuery("file:calc");

        var snapshot = await Task.WhenAny(tcs.Task, Task.Delay(500)) == tcs.Task
            ? await tcs.Task
            : null;

        Assert.NotNull(snapshot);
        // Apps should be excluded from file mode query
        Assert.Empty(snapshot.Groups);
    }

    [Fact]
    public async Task SearchSession_CancelsInFlightWorkWhenNewQueryArrives()
    {
        var slowSource = new SlowTestSearchSource(delayMs: 200);
        using var orchestrator = new SearchOrchestrator(new[] { slowSource }, debounceMs: 10);

        var snapshots = new List<SearchSnapshot>();
        orchestrator.SnapshotUpdated += s =>
        {
            if (!s.IsEmpty) snapshots.Add(s);
        };

        // Fire first query
        orchestrator.SetQuery("slow1");
        await Task.Delay(30);

        // Fire second query before first finishes
        orchestrator.SetQuery("slow2");
        await Task.Delay(350);

        // Only the second query should have published
        Assert.Single(snapshots);
        Assert.Equal(2, snapshots[0].SessionId);
    }

    private sealed class SlowTestSearchSource : ISearchSource
    {
        public string SourceId => "slow_test";
        public SourceState State => SourceState.Ready;

        private readonly int _delayMs;

        public SlowTestSearchSource(int delayMs) => _delayMs = delayMs;

        public async Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            await Task.Delay(_delayMs, cancellationToken);
            return new[]
            {
                new Candidate(query.RawText, query.RawText, query.RawText, SearchCategory.Apps, SourceId)
            };
        }
    }

    [Fact]
    public void BuildSearchGroups_AppsCategoryAlwaysFirst_EvenWhenFoldersScoreTiesOrExceeds()
    {
        var appCandidate = new Candidate("1", "Brave Browser", @"C:\Program Files\Brave\brave.exe", SearchCategory.Apps, "test");
        var folderCandidate = new Candidate("2", "brave", @"C:\Users\HamB\AppData\Local\brave", SearchCategory.Folders, "test", IsFolder: true);
        var docCandidate = new Candidate("3", "brave-guide.pdf", @"C:\Docs\brave-guide.pdf", SearchCategory.Documents, "test");

        // Folders has a top score of 120 (e.g. recency boost), Apps has 100, Documents has 80
        var results = new List<ScoredResult>
        {
            new(folderCandidate, 120, MatchKind.Exact),
            new(appCandidate, 100, MatchKind.Exact),
            new(docCandidate, 80, MatchKind.Prefix)
        };

        var groups = SearchOrchestrator.BuildSearchGroups(results, maxPerGroup: 5);

        Assert.Equal(3, groups.Count);
        // Apps MUST be first, even if Folders has higher score!
        Assert.Equal(SearchCategory.Apps, groups[0].Category);
        Assert.Equal("Apps", groups[0].Title);

        // Remaining categories ordered by TopScore descending
        Assert.Equal(SearchCategory.Folders, groups[1].Category);
        Assert.Equal("Folders", groups[1].Title);

        Assert.Equal(SearchCategory.Documents, groups[2].Category);
        Assert.Equal("Documents", groups[2].Title);
    }
}

