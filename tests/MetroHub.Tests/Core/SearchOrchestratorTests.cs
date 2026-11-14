using System;
using System.Collections.Generic;
using System.Linq;
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

    [Fact]
    public async Task SearchOrchestrator_RapidQueriesWhileTier2Slow_ExecutesTier2ForLatestAndPublishesFinalSnapshot()
    {
        var appSource = new AppSearchSource(CreateSampleApps);

        var firstQueryGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tier2ExecutedQueries = new List<string>();

        var slowTier2Source = new BlockingTier2SearchSource(
            onSearch: q =>
            {
                lock (tier2ExecutedQueries)
                {
                    tier2ExecutedQueries.Add(q.NormalizedText);
                }
                if (q.NormalizedText == "a")
                {
                    // Block "a" until explicitly released
                    firstQueryGate.Task.Wait(3000);
                }
            });

        var publishedSnapshots = new List<SearchSnapshot>();
        var abcFinalTcs = new TaskCompletionSource<SearchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var orchestrator = new SearchOrchestrator(
            tier1Sources: new[] { appSource },
            tier2Sources: new[] { slowTier2Source },
            debounceMs: 10);

        orchestrator.SnapshotUpdated += snap =>
        {
            lock (publishedSnapshots)
            {
                publishedSnapshots.Add(snap);
            }
            if (snap.IsFinal && snap.Groups.Any(g => g.Items.Any(i => i.Candidate.DisplayName == "file_abc.txt")))
            {
                abcFinalTcs.TrySetResult(snap);
            }
        };

        // 1. First query: "a" enters Tier 2 and blocks
        orchestrator.SetQuery("a");
        await Task.Delay(40);

        // 2. Rapid keystrokes while Tier 2 is blocked on "a"
        orchestrator.SetQuery("ab");
        await Task.Delay(20);
        orchestrator.SetQuery("abc");

        // Allow "abc" Tier 1 to complete and register in pending slot
        await Task.Delay(40);

        // 3. Unblock "a"
        firstQueryGate.TrySetResult(true);

        // 4. "abc" Tier 2 must execute and publish a final snapshot containing the file candidate
        var completed = await Task.WhenAny(abcFinalTcs.Task, Task.Delay(3000));
        Assert.Equal(abcFinalTcs.Task, completed);

        var finalSnap = await abcFinalTcs.Task;
        Assert.True(finalSnap.IsFinal);
        Assert.Contains(finalSnap.Groups, g => g.Category == SearchCategory.Documents);
        Assert.Contains(finalSnap.Groups, g => g.Items.Any(i => i.Candidate.DisplayName == "file_abc.txt"));

        // 5. Verify intermediate snapshot for "abc" had IsFinal = false and final snapshot had IsFinal = true
        lock (publishedSnapshots)
        {
            var abcSnapshots = publishedSnapshots.Where(s => s.SessionId == orchestrator.CurrentSessionId).ToList();
            Assert.True(abcSnapshots.Count >= 2, $"Expected at least 2 snapshots for abc session, got {abcSnapshots.Count}");
            Assert.False(abcSnapshots[0].IsFinal, "Intermediate snapshot for abc must have IsFinal = false");
            Assert.True(abcSnapshots[^1].IsFinal, "Final snapshot for abc must have IsFinal = true");
        }
    }

    [Fact]
    public async Task SearchOrchestrator_SingleFlightConcurrencyGuard_StrictlyEnforcesSingleWorker()
    {
        var appSource = new AppSearchSource(CreateSampleApps);

        int concurrentCount = 0;
        int maxConcurrentSeen = 0;
        var lockObj = new object();
        var finalPublishedTcs = new TaskCompletionSource<SearchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        var trackedTier2Source = new BlockingTier2SearchSource(
            onSearch: q =>
            {
                int current = Interlocked.Increment(ref concurrentCount);
                lock (lockObj)
                {
                    if (current > maxConcurrentSeen)
                    {
                        maxConcurrentSeen = current;
                    }
                }

                // Simulate realistic I/O latency
                Thread.Sleep(25);

                Interlocked.Decrement(ref concurrentCount);
            });

        using var orchestrator = new SearchOrchestrator(
            tier1Sources: new[] { appSource },
            tier2Sources: new[] { trackedTier2Source },
            debounceMs: 5);

        orchestrator.SnapshotUpdated += snap =>
        {
            if (snap.IsFinal && snap.Groups.Any(g => g.Items.Any(i => i.Candidate.DisplayName == "file_query_final.txt")))
            {
                finalPublishedTcs.TrySetResult(snap);
            }
        };

        // Fire multiple rapid queries in burst, stressing the single-flight gate and double-check loop
        for (int i = 0; i < 15; i++)
        {
            orchestrator.UpdateQuery($"query_{i}");
            await Task.Delay(5);
        }

        // Final query to observe
        orchestrator.UpdateQuery("query_final");

        var completed = await Task.WhenAny(finalPublishedTcs.Task, Task.Delay(4000));
        Assert.Equal(finalPublishedTcs.Task, completed);

        var snap = await finalPublishedTcs.Task;
        Assert.True(snap.IsFinal);

        // Crucial invariant: At no point could two threads execute Tier 2 concurrently
        Assert.Equal(1, maxConcurrentSeen);
    }

    private sealed class BlockingTier2SearchSource : ISearchSource
    {
        public string SourceId => "blocking_tier2";
        public SourceState State => SourceState.Ready;

        private readonly Action<SearchQuery> _onSearch;

        public BlockingTier2SearchSource(Action<SearchQuery> onSearch) => _onSearch = onSearch;

        public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
        {
            _onSearch(query);

            var candidate = new Candidate(
                Id: $"id_{query.NormalizedText}",
                DisplayName: $"file_{query.NormalizedText}.txt",
                FullPathOrKey: $@"C:\Files\file_{query.NormalizedText}.txt",
                Category: SearchCategory.Documents,
                SourceId: SourceId);

            return Task.FromResult<IReadOnlyList<Candidate>>(new[] { candidate });
        }
    }
}

