using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Search;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public sealed class ExternalSourcesTests
{
    // ── 1. WorldClockSource Tests ──────────────────────────────────────────

    [Fact]
    public async Task WorldClock_TimeInTokyo_ReturnsTokyoTimeCandidate()
    {
        var source = new WorldClockSource();
        var query = SearchRanker.ParseQuery("time in tokyo", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("world_clock", item.SourceId);
        Assert.Contains("Tokyo", item.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task WorldClock_TimeLondon_ReturnsLondonTimeCandidate()
    {
        var source = new WorldClockSource();
        var query = SearchRanker.ParseQuery("time london", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Contains("London", item.DisplayName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task WorldClock_JustTime_ReturnsLocalAndUtc()
    {
        var source = new WorldClockSource();
        var query = SearchRanker.ParseQuery("time", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Id == "time:local");
        Assert.Contains(results, r => r.Id == "time:utc");
    }

    [Fact]
    public async Task WorldClock_EpochSeconds_ReturnsFormattedUtcAndLocal()
    {
        var source = new WorldClockSource();
        var query = SearchRanker.ParseQuery("epoch 1700000000", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("world_clock", item.SourceId);
        Assert.Contains("2023-11-14", item.DisplayName);
        Assert.Contains("UTC", item.DisplayName);
        Assert.Contains("1700000000", item.FullPathOrKey);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task WorldClock_EpochMilliseconds_ParsesCorrectly()
    {
        var source = new WorldClockSource();
        var query = SearchRanker.ParseQuery("epoch 1700000000000", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Contains("2023-11-14", item.DisplayName);
    }

    [Fact]
    public async Task WorldClock_TimeInABottle_ReturnsZero_FallingThroughToUniversal()
    {
        var source = new WorldClockSource();
        // Critical soft-route invariant: "time in a bottle.mp3" must return 0 candidates
        var query = SearchRanker.ParseQuery("time in a bottle.mp3", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── 2. UnitConverterSource Tests ───────────────────────────────────────

    [Fact]
    public async Task UnitConverter_KgToLbs_ConvertsAccurately()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("50 kg to lbs", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Equal("unit_converter", item.SourceId);
        Assert.Contains("110.231", item.DisplayName);
        Assert.Contains("Mass", item.FullPathOrKey);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task UnitConverter_FToC_FreezingPoint_ReturnsZeroCelsius()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("32 f to c", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var item = results[0];
        Assert.Contains("0 °C", item.DisplayName);
        Assert.Contains("Temperature", item.FullPathOrKey);
    }

    [Fact]
    public async Task UnitConverter_FToC_NegativeForty_ReturnsNegativeForty()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("-40 f to c", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("-40 °C", results[0].DisplayName);
    }

    [Fact]
    public async Task UnitConverter_MetersToFeet_ConvertsAccurately()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("10 m to ft", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("32.808", results[0].DisplayName);
    }

    [Fact]
    public async Task UnitConverter_GbToMb_ConvertsStorage()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("16 gb to mb", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("16000 mb", results[0].DisplayName);
    }

    [Fact]
    public async Task UnitConverter_UsdToEur_ConvertsCurrency()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("100 usd to eur", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Contains("EUR", results[0].DisplayName);
        Assert.Contains("Currency", results[0].FullPathOrKey);
    }

    [Fact]
    public async Task UnitConverter_InvalidQuery_ReturnsEmpty()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("not a unit query", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task UnitConverter_MismatchedUnits_ReturnsEmpty()
    {
        var source = new UnitConverterSource();
        var query = SearchRanker.ParseQuery("50 kg to meters", 1);

        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    // ── 3. ClipboardSource Tests ───────────────────────────────────────────

    [Fact]
    public async Task ClipboardSource_RecordClipAndSearch_ReturnsCandidates()
    {
        using var source = new ClipboardSource();
        source.RecordClip("First copied snippet");
        source.RecordClip("Second copied snippet");

        var query = SearchRanker.ParseQuery("cb", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("clipboard", results[0].SourceId);
        Assert.Contains("Second copied snippet", results[0].DisplayName);
        Assert.Contains("First copied snippet", results[1].DisplayName);
        Assert.NotNull(results[0].PrimaryAction);
    }

    [Fact]
    public async Task ClipboardSource_FilterByQuery_FiltersSubstrings()
    {
        using var source = new ClipboardSource();
        source.RecordClip("apple pie recipe");
        source.RecordClip("banana smoothie");
        source.RecordClip("apple cider drink");

        var query = SearchRanker.ParseQuery("cb apple", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Contains("apple", r.DisplayName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClipboardSource_RecordClip_CapsAt16Kb()
    {
        using var source = new ClipboardSource();
        string huge = new string('x', 20000);
        source.RecordClip(huge);

        var query = SearchRanker.ParseQuery("cb", 1);
        var results = source.SearchAsync(query, CancellationToken.None).Result;

        Assert.Single(results);
        Assert.Equal(16384, results[0].Size);
    }

    [Fact]
    public void ClipboardSource_RecordClip_DeduplicatesConsecutiveClips()
    {
        using var source = new ClipboardSource();
        source.RecordClip("duplicate item");
        source.RecordClip("duplicate item");

        var query = SearchRanker.ParseQuery("cb", 1);
        var results = source.SearchAsync(query, CancellationToken.None).Result;

        Assert.Single(results);
    }

    [Fact]
    public void ClipboardSource_RecordClip_CapsAt50Items()
    {
        using var source = new ClipboardSource();
        for (int i = 0; i < 60; i++)
        {
            source.RecordClip($"Item {i}");
        }

        var query = SearchRanker.ParseQuery("cb", 1);
        var results = source.SearchAsync(query, CancellationToken.None).Result;

        // Search returns at most 15 for UI presentation
        Assert.Equal(15, results.Count);
        // The newest item must be "Item 59"
        Assert.Contains("Item 59", results[0].DisplayName);
    }

    [Fact]
    public void ClipboardSource_IsSensitiveData_RejectsPasswordManagerFormats()
    {
        var dataObject = new DataObject();
        dataObject.SetData(SafeClipboard.FormatExcludeFromMonitor, "1");

        bool isSensitive = SafeClipboard.IsSensitiveData(dataObject);

        Assert.True(isSensitive);
    }
}

