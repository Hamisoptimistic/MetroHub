using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class UniversalSearchHardeningTests : IDisposable
{
    private readonly SearchOrchestrator _orchestrator;

    public UniversalSearchHardeningTests()
    {
        _orchestrator = new SearchOrchestrator(debounceMs: 20);
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
    }

    [Fact]
    public void EdgeCase_EmptyOrWhitespace_ImmediateEmptySnapshot()
    {
        _orchestrator.SetQuery("");
        Assert.True(_orchestrator.CurrentSnapshot.IsEmpty);

        _orchestrator.SetQuery("   \t\n  ");
        Assert.True(_orchestrator.CurrentSnapshot.IsEmpty);
    }

    [Fact]
    public void EdgeCase_ExtremelyLongQuery_HandledSafely()
    {
        string giantQuery = new string('x', 10000);
        var parsed = SearchRanker.ParseQuery(giantQuery);

        Assert.NotNull(parsed);
        Assert.True(parsed.NormalizedText.Length <= 256);
        Assert.False(parsed.IsEmpty);

        // Does not throw when scoring
        var candidate = new Candidate("id1", "sample.txt", @"C:\sample.txt", SearchCategory.Documents, "test");
        var scored = SearchRanker.ScoreCandidate(candidate, parsed);
        Assert.NotNull(scored);
    }

    [Fact]
    public void EdgeCase_SpecialCharactersAndRegexDelimiters_NoExceptions()
    {
        string[] messyQueries =
        {
            "***", "???", "\\\\//::", "[\"]*(^$)+?", "foo:bar ext:?!", "''''\"\"\"", "<script>alert(1)</script>"
        };

        foreach (var q in messyQueries)
        {
            var parsed = SearchRanker.ParseQuery(q);
            Assert.NotNull(parsed);

            var candidate = new Candidate("id1", "NormalFile.txt", @"C:\NormalFile.txt", SearchCategory.Documents, "test");
            var scored = SearchRanker.ScoreCandidate(candidate, parsed);
            Assert.NotNull(scored);
        }
    }

    [Fact]
    public void EdgeCase_DeepPathAndNoiseFolder_PenaltiesApplied()
    {
        var normalCandidate = new Candidate(
            "norm",
            "test.dll",
            @"C:\Projects\MetroHub\test.dll",
            SearchCategory.Code,
            "test");

        var deepCandidate = new Candidate(
            "deep",
            "test.dll",
            @"C:\nested_directory_level_one\nested_directory_level_two\level3\level4\level5\level6\level7\level8\level9\test.dll",
            SearchCategory.Code,
            "test");

        var noiseCandidate = new Candidate(
            "noise",
            "test.dll",
            @"C:\Projects\MetroHub\bin\Debug\test.dll",
            SearchCategory.Code,
            "test");

        var query = SearchRanker.ParseQuery("test");
        var scoreNormal = SearchRanker.ScoreCandidate(normalCandidate, query);
        var scoreDeep = SearchRanker.ScoreCandidate(deepCandidate, query);
        var scoreNoise = SearchRanker.ScoreCandidate(noiseCandidate, query);

        // Deep path receives deep path penalty
        Assert.True(scoreNormal.Score > scoreDeep.Score);
        // Noise path receives noise penalty
        Assert.True(scoreNormal.Score > scoreNoise.Score);
    }

    [Fact]
    public async Task EdgeCase_RapidFireTyping_LatestSessionWins()
    {
        var snapshots = new List<SearchSnapshot>();
        var signal = new TaskCompletionSource<bool>();

        _orchestrator.SnapshotUpdated += snapshot =>
        {
            lock (snapshots)
            {
                snapshots.Add(snapshot);
                if (snapshot.IsFinal)
                {
                    signal.TrySetResult(true);
                }
            }
        };

        // Fire 20 keystrokes in rapid succession (1ms apart)
        for (int i = 1; i <= 20; i++)
        {
            _orchestrator.SetQuery($"app_{i}");
            await Task.Delay(1);
        }

        // Wait up to 2 seconds for pipeline to settle
        var completed = await Task.WhenAny(signal.Task, Task.Delay(2000));
        Assert.Equal(signal.Task, completed);

        long settledSession = _orchestrator.CurrentSessionId;
        Assert.True(settledSession > 0);

        lock (snapshots)
        {
            var final = snapshots.FindLast(s => s.IsFinal);
            Assert.NotNull(final);
            Assert.Equal(settledSession, final.SessionId);
        }
    }
}
