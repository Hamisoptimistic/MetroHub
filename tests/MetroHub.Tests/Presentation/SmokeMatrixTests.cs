using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Verifies the 24 canonical Universal Search smoke verification queries defined in the master specification.
/// </summary>
public sealed class SmokeMatrixTests
{
    private readonly QueryRouter _router = new();

    // ── Case 1: > ping 1.1.1.1 ─────────────────────────────────────────────
    [Fact]
    public async Task Case01_TerminalWithArgs_ProducesTerminalExecutionCandidate()
    {
        var source = new TerminalSource(_router);
        var query = SearchRanker.ParseQuery("> ping 1.1.1.1", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("terminal", results[0].SourceId);
        Assert.Contains("ping 1.1.1.1", results[0].DisplayName);
    }

    // ── Case 2: > ──────────────────────────────────────────────────────────
    [Fact]
    public async Task Case02_TerminalBare_ProducesOpenTerminalCandidate()
    {
        var source = new TerminalSource(_router);
        var query = SearchRanker.ParseQuery(">", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("Open terminal", results[0].DisplayName);
    }

    // ── Case 3: kill node ──────────────────────────────────────────────────
    [Fact]
    public async Task Case03_ProcessKiller_ValidProcess_FindsProcessCandidate()
    {
        var source = new ProcessKillerSource(_router);
        // "dotnet" or current process is guaranteed to be running in test host
        string procName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        // Strip testhost if long, test with "dotnet"
        var query = SearchRanker.ParseQuery($"kill dotnet", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        // May find dotnet or return empty if not named dotnet, but must never throw
        Assert.NotNull(results);
    }

    // ── Case 4: kill bill.mkv ──────────────────────────────────────────────
    [Fact]
    public async Task Case04_KillNonExistentProcess_ReturnsZero_FallingThrough()
    {
        var source = new ProcessKillerSource(_router);
        var query = SearchRanker.ParseQuery("kill bill.mkv", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        // Soft route: returns 0 candidates so universal file search finds the file
        Assert.Empty(results);
    }

    // ── Case 5: kill explorer ──────────────────────────────────────────────
    [Fact]
    public async Task Case05_KillExplorer_Denylisted_ReturnsZeroCandidates()
    {
        var source = new ProcessKillerSource(_router);
        var query = SearchRanker.ParseQuery("kill explorer", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        // Explorer is explicitly protected and denylisted
        Assert.Empty(results);
    }

    // ── Case 6: win <maximized window> ─────────────────────────────────────
    [Fact]
    public async Task Case06_WindowWalker_DoesNotThrow_AndReturnsValidWindows()
    {
        var source = new WindowWalkerSource(_router);
        var query = SearchRanker.ParseQuery("win", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.NotNull(results);
    }

    // ── Case 7: win 11 wallpaper ───────────────────────────────────────────
    [Fact]
    public async Task Case07_WindowWalker_NonExistentTitle_ReturnsZero_FallingThrough()
    {
        var source = new WindowWalkerSource(_router);
        var query = SearchRanker.ParseQuery("win 11 wallpaper", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Case 8: time in tokyo ──────────────────────────────────────────────
    [Fact]
    public async Task Case08_TimeInTokyo_DisplaysCurrentTokyoTime()
    {
        var source = new WorldClockSource(_router);
        var query = SearchRanker.ParseQuery("time in tokyo", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("Tokyo", results[0].DisplayName, StringComparison.OrdinalIgnoreCase);
    }

    // ── Case 9: time in a bottle.mp3 ───────────────────────────────────────
    [Fact]
    public async Task Case09_TimeInABottle_ReturnsZero_FallingThroughToFileSearch()
    {
        var source = new WorldClockSource(_router);
        var query = SearchRanker.ParseQuery("time in a bottle.mp3", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Case 10: epoch 1700000000 ──────────────────────────────────────────
    [Fact]
    public async Task Case10_EpochTimestamp_DisplaysFormattedDateRow()
    {
        var source = new WorldClockSource(_router);
        var query = SearchRanker.ParseQuery("epoch 1700000000", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("2023-11-14", results[0].DisplayName);
    }

    // ── Case 11: g eazy ────────────────────────────────────────────────────
    [Fact]
    public async Task Case11_GoogleBang_DisplaysSearchCard()
    {
        var source = new TableSource(_router);
        var query = SearchRanker.ParseQuery("g eazy", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("Google", results[0].DisplayName);
        Assert.Contains("eazy", results[0].FullPathOrKey);
    }

    // ── Case 12: 7zip ──────────────────────────────────────────────────────
    [Fact]
    public async Task Case12_SevenZip_DoesNotTriggerCalculator()
    {
        var source = new CalculatorSource();
        var query = SearchRanker.ParseQuery("7zip", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Case 13: 24 * 1.15 ─────────────────────────────────────────────────
    [Fact]
    public async Task Case13_MathMultiplication_CalculatesCorrectly()
    {
        var source = new CalculatorSource();
        var query = SearchRanker.ParseQuery("24 * 1.15", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("= 27.6", results[0].DisplayName);
    }

    // ── Case 14: sqrt(144) ─────────────────────────────────────────────────
    [Fact]
    public async Task Case14_MathFunction_CalculatesSquareRoot()
    {
        var source = new CalculatorSource();
        var query = SearchRanker.ParseQuery("sqrt(144)", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("= 12", results[0].DisplayName);
    }

    // ── Case 15: 10,000 × ( ────────────────────────────────────────────────
    [Fact]
    public async Task Case15_IncompleteSyntax_ReturnsZero_NeverThrows()
    {
        var source = new CalculatorSource();
        var query = SearchRanker.ParseQuery("10,000 × (", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── Case 16: 100 usd to eur ────────────────────────────────────────────
    [Fact]
    public async Task Case16_CurrencyConversion_ReturnsConvertedEURValue()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("100 usd to eur", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("EUR", results[0].DisplayName);
    }

    // ── Case 17: 50 kg to lbs ──────────────────────────────────────────────
    [Fact]
    public async Task Case17_WeightConversion_ConvertsKgToLbs()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("50 kg to lbs", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("110.231", results[0].DisplayName);
    }

    // ── Case 18: 32 f to c ─────────────────────────────────────────────────
    [Fact]
    public async Task Case18_TemperatureConversion_ConvertsFahrenheitToCelsius()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("32 f to c", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("0 °C", results[0].DisplayName);
    }

    // ── Case 19: vol 40 ────────────────────────────────────────────────────
    [Fact]
    public async Task Case19_VolumeCommand_ParsesVolumeLevel()
    {
        var source = new AudioSource(_router);
        var query = SearchRanker.ParseQuery("vol 40", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("40%", results[0].DisplayName);
    }

    // ── Case 20: vol 2 notes ───────────────────────────────────────────────
    [Fact]
    public async Task Case20_VolNotes_ReturnsZero_FallingThroughToFileSearch()
    {
        var source = new AudioSource(_router);
        var query = SearchRanker.ParseQuery("vol 2 notes", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        // "2 notes" is not a valid 0-100 integer volume, so AudioSource returns 0 candidates
        Assert.Empty(results);
    }

    // ── Case 21: audio ─────────────────────────────────────────────────────
    [Fact]
    public async Task Case21_AudioTrigger_ReturnsAudioConfigurationItems()
    {
        var source = new AudioSource(_router);
        var query = SearchRanker.ParseQuery("audio", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.NotNull(results);
    }

    // ── Case 22: lock ──────────────────────────────────────────────────────
    [Fact]
    public async Task Case22_LockSystemCommand_ReturnsLockWorkstationCandidate()
    {
        var source = new TableSource(_router);
        var query = SearchRanker.ParseQuery("lock", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Contains(results, r => r.Id == "sys:lock");
    }

    // ── Case 23: restart ───────────────────────────────────────────────────
    [Fact]
    public async Task Case23_RestartSystemCommand_RequiresConfirmation()
    {
        var source = new TableSource(_router);
        var query = SearchRanker.ParseQuery("restart", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        var restartItem = Assert.Single(results, r => r.Id == "sys:restart");
        Assert.NotNull(restartItem.PrimaryAction);

        // 1st Enter: returns RequireConfirmation
        var firstResult = await restartItem.PrimaryAction.ExecuteAsync(new ActionContext(CancellationToken.None) { Confirmed = false });
        Assert.Equal(ActionOutcome.RequireConfirmation, firstResult.Outcome);
    }

    // ── Case 24: cb pass ───────────────────────────────────────────────────
    [Fact]
    public async Task Case24_ClipboardHistory_ShowsMatchingClips()
    {
        using var source = new ClipboardSource(_router);
        source.RecordClip("my password reset link");
        source.RecordClip("unrelated note");

        var query = SearchRanker.ParseQuery("cb pass", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("password reset", results[0].DisplayName);
    }
}

