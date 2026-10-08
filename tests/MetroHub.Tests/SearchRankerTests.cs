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
        Assert.True(SearchRanker.IsExcluded("unins000", @"C:\App\unins000.exe"));
        Assert.True(SearchRanker.IsExcluded("crashreport", @"C:\App\crashreport.exe"));
        Assert.True(SearchRanker.IsExcluded("debug.pdb", @"C:\App\debug.pdb"));
        Assert.False(SearchRanker.IsExcluded("Calculator", @"C:\Windows\calc.exe"));
        // "uninstall" removed from ExcludedKeywords
        Assert.False(SearchRanker.IsExcluded("uninstall.exe", @"C:\App\uninstall.exe"));
        // Keywords check file NAME only, so a file in a diagnostics path is NOT excluded if name is normal
        Assert.False(SearchRanker.IsExcluded("Report.pdf", @"C:\App\diagnostics\Report.pdf"));
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

        // Warm up JIT and candidate properties
        for (int i = 0; i < candidates.Count; i++)
        {
            _ = candidates[i].NormalizedDisplayName;
        }
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

    [Fact]
    public void ScoreCandidate_DiacriticsAndLanguages_MatchesSeamlessly()
    {
        // Turkish: türkçe matches turkce, İstanbul matches istanbul
        var turkceCandidate = new Candidate("1", "türkçe_rehber.pdf", @"C:\Docs\türkçe_rehber.pdf", SearchCategory.Documents, "test");
        var turkceQuery = SearchRanker.ParseQuery("turkce");
        var turkceResult = SearchRanker.ScoreCandidate(turkceCandidate, turkceQuery);
        Assert.True(turkceResult.Score > 0);
        Assert.Equal(MatchKind.Prefix, turkceResult.MatchKind);

        var istanbulCandidate = new Candidate("2", "İstanbul.docx", @"C:\Docs\İstanbul.docx", SearchCategory.Documents, "test");
        var istanbulQuery = SearchRanker.ParseQuery("istanbul");
        var istanbulResult = SearchRanker.ScoreCandidate(istanbulCandidate, istanbulQuery);
        Assert.True(istanbulResult.Score > 0);
        Assert.Equal(MatchKind.Prefix, istanbulResult.MatchKind);

        // German: München matches munchen
        var munchenCandidate = new Candidate("3", "München_Trip.jpg", @"C:\Photos\München_Trip.jpg", SearchCategory.Images, "test");
        var munchenQuery = SearchRanker.ParseQuery("munchen");
        var munchenResult = SearchRanker.ScoreCandidate(munchenCandidate, munchenQuery);
        Assert.True(munchenResult.Score > 0);
        Assert.Equal(MatchKind.Prefix, munchenResult.MatchKind);

        // French: café matches cafe
        var cafeCandidate = new Candidate("4", "Le Café.txt", @"C:\Docs\Le Café.txt", SearchCategory.Documents, "test");
        var cafeQuery = SearchRanker.ParseQuery("cafe");
        var cafeResult = SearchRanker.ScoreCandidate(cafeCandidate, cafeQuery);
        Assert.True(cafeResult.Score > 0);
        Assert.Equal(MatchKind.WordPrefix, cafeResult.MatchKind);

        // Greek: Ελλάδα matches ελλαδα
        var greekCandidate = new Candidate("5", "Ελλάδα_Χάρτης.png", @"C:\Docs\Ελλάδα_Χάρτης.png", SearchCategory.Images, "test");
        var greekQuery = SearchRanker.ParseQuery("ελλαδα");
        var greekResult = SearchRanker.ScoreCandidate(greekCandidate, greekQuery);
        Assert.True(greekResult.Score > 0);
        Assert.Equal(MatchKind.Prefix, greekResult.MatchKind);
    }

    [Fact]
    public void ScoreCandidate_SpecialCharacters_MatchesCorrectly()
    {
        // Dash: my-file matches my-file and my file
        var dashCandidate = new Candidate("1", "my-file-report.pdf", @"C:\Docs\my-file-report.pdf", SearchCategory.Documents, "test");
        var dashQuery1 = SearchRanker.ParseQuery("my-file");
        var dashResult1 = SearchRanker.ScoreCandidate(dashCandidate, dashQuery1);
        Assert.True(dashResult1.Score > 0);
        Assert.Equal(MatchKind.Prefix, dashResult1.MatchKind);

        var dashQuery2 = SearchRanker.ParseQuery("my file");
        var dashResult2 = SearchRanker.ScoreCandidate(dashCandidate, dashQuery2);
        Assert.True(dashResult2.Score > 0);
        Assert.Equal(MatchKind.WordPrefix, dashResult2.MatchKind);

        // Comma & Backtick
        var commaCandidate = new Candidate("2", "report,v1.docx", @"C:\Docs\report,v1.docx", SearchCategory.Documents, "test");
        var commaQuery = SearchRanker.ParseQuery("report,v1");
        var commaResult = SearchRanker.ScoreCandidate(commaCandidate, commaQuery);
        Assert.True(commaResult.Score > 0);
        Assert.Equal(MatchKind.Prefix, commaResult.MatchKind);
    }

    [Fact]
    public void IsNoisePath_DetectsPackageManagerAndAppCaches()
    {
        Assert.True(SearchRanker.IsNoisePath(@"C:\Users\HamB\AppData\Local\UniGetUI\CachedMedia\Npm\brave"));
        Assert.True(SearchRanker.IsNoisePath(@"C:\Users\HamB\AppData\Local\Devolutions\Pinget\Microsoft\Windows Package Manager"));
        Assert.True(SearchRanker.IsNoisePath(@"C:\Users\HamB\AppData\Local\Chocolatey\brave"));
        Assert.True(SearchRanker.IsNoisePath(@"C:\Projects\MetroHub\bin\Debug\net10.0\app.dll"));
        Assert.False(SearchRanker.IsNoisePath(@"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe"));
        Assert.False(SearchRanker.IsNoisePath(@"D:\Books\Five Proofs of the Existence of God.pdf"));
    }

    [Fact]
    public void ScoreCandidate_Accents_NormalizesCandidateNameAndPath()
    {
        // 1. "cafe" finds "Café Menu.pdf"
        var candidate = new Candidate("1", "Café Menu.pdf", @"C:\Docs\Café Menu.pdf", SearchCategory.Documents, "test");
        var query = SearchRanker.ParseQuery("cafe");
        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.True(result.Score > 0);
        Assert.Equal(MatchKind.Prefix, result.MatchKind);

        // 2. Path normalization: "paris" finds candidate whose path has "Café Paris"
        var candidateInPath = new Candidate("2", "Menu.pdf", @"C:\Café Paris\Menu.pdf", SearchCategory.Documents, "test");
        var pathQuery = SearchRanker.ParseQuery("paris");
        var pathResult = SearchRanker.ScoreCandidate(candidateInPath, pathQuery);

        Assert.True(pathResult.Score > 0);
        Assert.Equal(MatchKind.PathOnly, pathResult.MatchKind);
    }

    [Fact]
    public void ScoreCandidate_MultiWordQuery_MatchesUnderscoredTokensWithScore50()
    {
        // "annual report" must find "Annual_Report_2024.pdf" and score 50
        var candidate = new Candidate("1", "Annual_Report_2024.pdf", @"C:\Docs\Annual_Report_2024.pdf", SearchCategory.Documents, "test");
        var query = SearchRanker.ParseQuery("annual report");
        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.Equal(50, result.Score);
        Assert.Equal(MatchKind.WordPrefix, result.MatchKind);
    }

    [Fact]
    public void ScoreCandidate_CapBoosts_NeverExceeds20()
    {
        // Candidate with base score from Contains (40), max openCount (+25), recency modified today (+10), location (+10)
        // Total uncapped boost would be 25 + 10 + 10 = 45 -> score 85.
        // With boost cap at 20, total boost must be exactly 20 -> score 60!
        var candidate = new Candidate(
            "1",
            "My_Notes_Contains_Target.txt",
            @"C:\Users\HamB\Desktop\My_Notes_Contains_Target.txt",
            SearchCategory.Documents,
            "test",
            Modified: DateTimeOffset.UtcNow);

        var query = SearchRanker.ParseQuery("Target");
        var result = SearchRanker.ScoreCandidate(candidate, query, openCount: 10);

        // Base match: WordPrefix (60) or Contains (40). WordPrefix base is 60.
        // Max boost is 20 -> total score is 60 + 20 = 80.
        Assert.True(result.Score <= 80);

        // Exact match candidate with NO dynamic boosts (base 100) must beat the heavily boosted weaker match
        var exactCandidate = new Candidate("2", "Target", @"C:\App\Target.exe", SearchCategory.Apps, "test");
        var exactResult = SearchRanker.ScoreCandidate(exactCandidate, query, openCount: 0);

        Assert.True(exactResult.Score >= 100);
        Assert.True(exactResult.Score > result.Score, "Exact match must always beat boosted weaker match");
    }

    [Fact]
    public void ScoreCandidate_Fuzzy_BoundedByQueryLength()
    {
        // Query <= 4 chars: max distance is 1
        var appCandidate = new Candidate("1", "Slack", @"C:\App\slack.exe", SearchCategory.Apps, "test");

        // "slak" (4 chars) -> distance 1 from "slack" (matches)
        var qShortDist1 = SearchRanker.ParseQuery("slak");
        var res1 = SearchRanker.ScoreCandidate(appCandidate, qShortDist1);
        Assert.True(res1.Score > 0);
        Assert.Equal(MatchKind.Fuzzy, res1.MatchKind);

        // "sla" (3 chars) -> distance 2 from "slack" (must NOT match because query length <= 4)
        var qShortDist2 = SearchRanker.ParseQuery("sla");
        var res2 = SearchRanker.ScoreCandidate(appCandidate, qShortDist2);
        // "sla" is a prefix of "slack" so it matches via prefix, not fuzzy.
        // Let's test a distance-2 typo with length <= 4 that is NOT a prefix:
        var qTypo2 = SearchRanker.ParseQuery("slxx"); // 4 chars, 2 changes from "slack"
        var resTypo2 = SearchRanker.ScoreCandidate(appCandidate, qTypo2);
        Assert.Equal(0, resTypo2.Score); // Rejected!

        // Query > 4 chars: distance 2 is allowed
        var browserCandidate = new Candidate("2", "Chrome", @"C:\App\chrome.exe", SearchCategory.Apps, "test");
        var qLongDist2 = SearchRanker.ParseQuery("chrXXe"); // 6 chars, distance 2 from "chrome"
        var resLong2 = SearchRanker.ScoreCandidate(browserCandidate, qLongDist2);
        Assert.True(resLong2.Score > 0);
        Assert.Equal(MatchKind.Fuzzy, resLong2.MatchKind);
    }
}

