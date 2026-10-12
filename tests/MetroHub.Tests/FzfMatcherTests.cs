using System;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public class FzfMatcherTests
{
    [Theory]
    [InlineData("vscod", "Visual Studio Code")]
    [InlineData("chrm", "Google Chrome")]
    [InlineData("subl", "Sublime Text")]
    [InlineData("gitcm", "git-commit.sh")]
    [InlineData("calc", "Calculator")]
    [InlineData("t3code", "t3-code.txt")]
    public void CanMatchSubsequence_ValidMatches_ReturnsTrue(string query, string target)
    {
        Assert.True(FzfMatcher.CanMatchSubsequence(query.AsSpan(), target.AsSpan()));
    }

    [Theory]
    [InlineData("vscodx", "Visual Studio Code")]
    [InlineData("xyz", "Google Chrome")]
    [InlineData("notpresent", "Short")]
    public void CanMatchSubsequence_InvalidMatches_ReturnsFalse(string query, string target)
    {
        Assert.False(FzfMatcher.CanMatchSubsequence(query.AsSpan(), target.AsSpan()));
    }

    [Fact]
    public void Score_SubsequenceMatch_ReturnsScoreInExpectedRange()
    {
        int score = FzfMatcher.Score("vscod".AsSpan(), "Visual Studio Code".AsSpan());
        Assert.InRange(score, FzfMatcher.MinScore, FzfMatcher.MaxScore);

        int chromeScore = FzfMatcher.Score("chrm".AsSpan(), "Google Chrome".AsSpan());
        Assert.InRange(chromeScore, FzfMatcher.MinScore, FzfMatcher.MaxScore);
    }

    [Fact]
    public void Score_ConsecutiveRuns_ScoreHigherThanScatteredMatches()
    {
        // "code" is a contiguous run inside "Visual Studio Code"
        int tightScore = FzfMatcher.Score("code".AsSpan(), "Visual Studio Code".AsSpan());

        // "vso" is scattered across words (V...S...o)
        int scatteredScore = FzfMatcher.Score("vso".AsSpan(), "Visual Studio Code".AsSpan());

        Assert.True(tightScore > 0);
        Assert.True(scatteredScore > 0);
        Assert.True(tightScore >= scatteredScore);
    }

    [Fact]
    public void SearchRanker_FzfIntegration_ScoresVisualCodeWhenSearchingVscod()
    {
        var candidate = new Candidate("1", "Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("vscod");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.True(result.Score > 0, "Expected vscod to match Visual Studio Code");
        Assert.Equal(MatchKind.Fuzzy, result.MatchKind);
    }

    [Fact]
    public void SearchRanker_FzfIntegration_ScoresChromeWhenSearchingChrm()
    {
        var candidate = new Candidate("2", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", SearchCategory.Apps, "test");
        var query = SearchRanker.ParseQuery("chrm");

        var result = SearchRanker.ScoreCandidate(candidate, query);

        Assert.True(result.Score > 0, "Expected chrm to match Google Chrome");
        Assert.Equal(MatchKind.Fuzzy, result.MatchKind);
    }

    [Fact]
    public void SearchRanker_FzfIntegration_MatchesAcrossFilesAndDocuments()
    {
        var docCandidate = new Candidate("3", "financial_quarterly_report_2026.pdf", @"D:\Documents\financial_quarterly_report_2026.pdf", SearchCategory.Documents, "test");
        var query = SearchRanker.ParseQuery("finrpt"); // fin... r... pt

        var result = SearchRanker.ScoreCandidate(docCandidate, query);

        Assert.True(result.Score > 0, "Expected finrpt to match financial_quarterly_report_2026.pdf");
        Assert.Equal(MatchKind.Fuzzy, result.MatchKind);
    }
}
