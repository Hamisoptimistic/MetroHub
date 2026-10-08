using System;
using System.Collections.Generic;
using System.Diagnostics;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public class SearchRankerTests
{
    [Fact]
    public void ParseQuery_EmptyOrWhitespace_ReturnsEmptyQuery()
    {
        var q1 = SearchRanker.ParseQuery(null);
        var q2 = SearchRanker.ParseQuery("   ");

        Assert.True(q1.IsEmpty);
        Assert.True(q2.IsEmpty);
        Assert.Empty(q1.Tokens);
    }

    [Fact]
    public void ParseQuery_NormalizesAccentsAndPunctuation()
    {
        var q = SearchRanker.ParseQuery("  Café-Crème.app  ");

        Assert.Equal("cafe-creme.app", q.NormalizedText);
        Assert.Equal(new[] { "cafe", "creme", "app" }, q.Tokens);
    }

    [Fact]
    public void ParseQuery_TruncatesAt256Characters()
    {
        string longInput = new string('a', 300);
        var q = SearchRanker.ParseQuery(longInput);

        Assert.Equal(256, q.RawText.Length);
        Assert.Equal(256, q.NormalizedText.Length);
    }

    [Theory]
    [InlineData("app:Terminal", SearchMode.Apps, "terminal")]
    [InlineData("file:document.pdf", SearchMode.Files, "document.pdf")]
    [InlineData("folder:Downloads", SearchMode.Folders, "downloads")]
    [InlineData(@"C:\Windows\System32", SearchMode.Path, @"c:\windows\system32")]
    public void ParseQuery_DetectsModesCorrectly(string input, SearchMode expectedMode, string expectedNormalized)
    {
        var q = SearchRanker.ParseQuery(input);

        Assert.Equal(expectedMode, q.Mode);
        Assert.Equal(expectedNormalized, q.NormalizedText);
    }

    [Fact]
    public void ParseQuery_SplitsCamelCaseAndTokens()
    {
        var q = SearchRanker.ParseQuery("visualStudioCode");

        Assert.Equal(new[] { "visual", "studio", "code" }, q.Tokens);
    }

    [Fact]
    public void ScoreCandidate_ExactMatch_ScoresHighest()
    {
        var candidate = new Candidate("1", "Calculator", @"C:\calc.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("calculator");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        // Exact (100) + App single token boost (10) = 110
        Assert.Equal(MatchKind.Exact, result.MatchKind);
        Assert.Equal(110, result.Score);
    }

    [Fact]
    public void ScoreCandidate_PrefixMatch_ScoresAboveWordPrefix()
    {
        var candidate = new Candidate("1", "Calculator", @"C:\calc.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("calc");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        // Prefix (80) + App single token boost (10) = 90
        Assert.Equal(MatchKind.Prefix, result.MatchKind);
        Assert.Equal(90, result.Score);
    }

    [Fact]
    public void ScoreCandidate_WordPrefix_ScoresSixty()
    {
        var candidate = new Candidate("1", "Visual Studio Code", @"C:\code.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("Studio");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.Equal(MatchKind.WordPrefix, result.MatchKind);
        Assert.True(result.Score >= 60);
    }

    [Fact]
    public void ScoreCandidate_AcronymMatch_MatchesInitials()
    {
        var candidate = new Candidate("1", "Visual Studio Code", @"C:\code.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("vsc");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.Equal(MatchKind.Acronym, result.MatchKind);
        Assert.True(result.Score >= 55);
    }

    [Fact]
    public void ScoreCandidate_ContainsMatch_ScoresLowerThanPrefix()
    {
        var candidate = new Candidate("1", "Barcode Scanner", @"C:\scanner.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("code");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.Equal(MatchKind.Contains, result.MatchKind);
        Assert.Equal(40, result.Score);
    }

    [Fact]
    public void ScoreCandidate_FuzzyTypoMatch_MatchesSingleEditForApps()
    {
        var candidate = new Candidate("1", "Firefox", @"C:\firefox.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("firefix"); // 1 edit away

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.Equal(MatchKind.Fuzzy, result.MatchKind);
        Assert.Equal(30, result.Score);
    }

    [Fact]
    public void ScoreCandidate_AppliesNoisePenaltyToBuildFolders()
    {
        var clean = new Candidate("1", "App", @"C:\Users\User\Projects\App\App.exe", SearchCategory.Code, "test");
        var noise = new Candidate("2", "App", @"C:\Users\User\Projects\App\bin\Debug\App.exe", SearchCategory.Code, "test");
        var query = SearchRanker.ParseQuery("App");

        var resClean = SearchRanker.ScoreCandidate(clean, query);
        var resNoise = SearchRanker.ScoreCandidate(noise, query);

        Assert.True(resClean.Score > resNoise.Score, $"Clean ({resClean.Score}) should outrank noise ({resNoise.Score})");
        Assert.Equal(resClean.Score - 30, resNoise.Score);
    }

    [Fact]
    public void SortResults_DeterministicTieBreaking()
    {
        var r1 = new ScoredResult(new Candidate("1", "Beta", @"C:\b.exe", SearchCategory.Apps, "test"), 80, MatchKind.Prefix);
        var r2 = new ScoredResult(new Candidate("2", "Alpha", @"C:\b.exe", SearchCategory.Apps, "test"), 80, MatchKind.Prefix);
        var r3 = new ScoredResult(new Candidate("3", "Alpha", @"C:\long_path\b.exe", SearchCategory.Apps, "test"), 80, MatchKind.Prefix);

        var list = new List<ScoredResult> { r1, r3, r2 };
        SearchRanker.SortResults(list);

        // Ties: Score is 80.
        // r2 (path length 8) beats r3 (path length 18).
        // r2 ("Alpha") beats r1 ("Beta") alphabetically when path lengths are equal.
        Assert.Equal("Alpha", list[0].Candidate.DisplayName);
        Assert.Equal(@"C:\b.exe", list[0].Candidate.FullPathOrKey);

        Assert.Equal("Beta", list[1].Candidate.DisplayName);
        Assert.Equal(@"C:\b.exe", list[1].Candidate.FullPathOrKey);

        Assert.Equal("Alpha", list[2].Candidate.DisplayName);
        Assert.Equal(@"C:\long_path\b.exe", list[2].Candidate.FullPathOrKey);
    }

    [Fact]
    public void Deduplicate_PrefersStartMenuLnkOverRawExe()
    {
        var candidates = new List<Candidate>
        {
            new Candidate("1", "Terminal", @"C:\Program Files\wt.exe", SearchCategory.Apps, "test"),
            new Candidate("2", "Terminal", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Terminal.lnk", SearchCategory.Apps, "test")
        };

        var deduped = SearchRanker.Deduplicate(candidates);

        Assert.Single(deduped);
        Assert.EndsWith(".lnk", deduped[0].FullPathOrKey, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsExcluded_DetectsUnwantedKeywordsAndExtensions()
    {
        Assert.True(SearchRanker.IsExcluded("uninstall", @"C:\App\uninstall.exe"));
        Assert.True(SearchRanker.IsExcluded("debug", @"C:\App\app.pdb"));
        Assert.False(SearchRanker.IsExcluded("Calculator", @"C:\Windows\calc.exe"));
    }

    [Fact]
    public void Benchmark_Score10000Candidates_CompletesUnderBudget()
    {
        // 1. Arrange 10,000 synthetic candidates
        var candidates = new List<Candidate>(10000);
        for (int i = 0; i < 10000; i++)
        {
            candidates.Add(new Candidate(
                i.ToString(),
                $"Application Suite Component #{i} Service Worker",
                $@"C:\Program Files\AppSuite{i % 50}\ServiceWorker{i}.exe",
                SearchCategory.Apps,
                "benchmark_source"));
        }

        var query = SearchRanker.ParseQuery("Worker");

        // Warm up JIT
        for (int i = 0; i < 500; i++)
        {
            SearchRanker.ScoreCandidate(candidates[i], query);
        }

        // 2. Act: Score all 10,000 candidates
        var sw = Stopwatch.StartNew();
        int matched = 0;
        for (int i = 0; i < candidates.Count; i++)
        {
            var res = SearchRanker.ScoreCandidate(candidates[i], query);
            if (res.Score > 0) matched++;
        }
        sw.Stop();

        // 3. Assert budget: Section 1 budget for key press work is <8ms UI budget.
        // 10,000 items is 100x typical installed apps count (~100-300). Under parallel test runner load, <15ms is required.
        Assert.True(matched > 0);
        Assert.True(sw.ElapsedMilliseconds <= 15, $"10,000 candidates scored in {sw.ElapsedMilliseconds}ms (exceeded 15ms ceiling)");
    }
}
