using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using MetroHub.Presentation.Search;
using Wpf.Ui.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed class SearchIconTests
{
    [Fact]
    public void PureMemoryData_AllEntries_HaveValidIcons()
    {
        Assert.NotEmpty(PureMemoryData.SystemCommands);
        foreach (var cmd in PureMemoryData.SystemCommands)
        {
            Assert.NotEqual(SymbolRegular.Empty, cmd.Icon);
        }

        Assert.NotEmpty(PureMemoryData.WindowsSettings);
        foreach (var setting in PureMemoryData.WindowsSettings)
        {
            Assert.NotEqual(SymbolRegular.Empty, setting.Icon);
        }

        Assert.NotEmpty(PureMemoryData.WebBangs);
        foreach (var bang in PureMemoryData.WebBangs)
        {
            Assert.NotEqual(SymbolRegular.Empty, bang.Icon);
        }

        Assert.NotEmpty(PureMemoryData.SearchTips);
        foreach (var tip in PureMemoryData.SearchTips)
        {
            Assert.NotEqual(SymbolRegular.Empty, tip.Icon);
        }
    }

    [Fact]
    public void SearchIconResolver_ExplicitIcon_TakesPrecedenceOverCategory()
    {
        var candidateWithIcon = new Candidate(
            Id: "test:lock",
            DisplayName: "Lock",
            FullPathOrKey: "lock",
            Category: SearchCategory.Actions,
            SourceId: "table")
        {
            Icon = SymbolRegular.LockClosed24,
            KindLabel = "System"
        };

        var resolvedSymbol = SearchIconResolver.ResolveSymbol(candidateWithIcon);
        var resolvedBadge = SearchIconResolver.ResolveKindBadge(candidateWithIcon);

        Assert.Equal(SymbolRegular.LockClosed24, resolvedSymbol);
        Assert.Equal("System", resolvedBadge);
    }

    [Theory]
    [InlineData(SearchCategory.Actions, SymbolRegular.Flash24)]
    [InlineData(SearchCategory.Apps, SymbolRegular.Apps24)]
    [InlineData(SearchCategory.Folders, SymbolRegular.Folder24)]
    [InlineData(SearchCategory.Images, SymbolRegular.Image24)]
    [InlineData(SearchCategory.Media, SymbolRegular.MusicNote224)]
    [InlineData(SearchCategory.Code, SymbolRegular.Code24)]
    [InlineData(SearchCategory.Documents, SymbolRegular.Document24)]
    [InlineData(SearchCategory.Other, SymbolRegular.Document24)]
    public void SearchIconResolver_UnsetIcon_FallsBackToCategoryDefault(SearchCategory category, SymbolRegular expectedSymbol)
    {
        var candidate = new Candidate(
            Id: "fallback:test",
            DisplayName: "Item",
            FullPathOrKey: "item.txt",
            Category: category,
            SourceId: "everything");

        var symbol = SearchIconResolver.ResolveSymbol(candidate);
        var badge = SearchIconResolver.ResolveKindBadge(candidate);

        Assert.Equal(expectedSymbol, symbol);
        Assert.Equal(category.ToString(), badge);
    }

    [Fact]
    public async Task TableSource_ProvidesExplicitIconsAndKindLabels()
    {
        var source = new TableSource();

        // 1. System Command
        var lockQuery = SearchRanker.ParseQuery("lock");
        var lockResults = await source.SearchAsync(lockQuery, CancellationToken.None);
        var lockCandidate = lockResults.FirstOrDefault(c => c.Id == "sys:lock");
        Assert.NotNull(lockCandidate);
        Assert.Equal(SymbolRegular.LockClosed24, lockCandidate.Icon.Symbol);
        Assert.Equal("System", lockCandidate.KindLabel);

        // 2. Windows Setting
        var settingQuery = SearchRanker.ParseQuery("bluetooth");
        var settingResults = await source.SearchAsync(settingQuery, CancellationToken.None);
        var settingCandidate = settingResults.FirstOrDefault(c => c.Id.StartsWith("setting:"));
        Assert.NotNull(settingCandidate);
        Assert.Equal(SymbolRegular.Bluetooth24, settingCandidate.Icon.Symbol);
        Assert.Equal("Windows Setting", settingCandidate.KindLabel);

        // 3. Web Bang
        var bangQuery = SearchRanker.ParseQuery("g query");
        var bangResults = await source.SearchAsync(bangQuery, CancellationToken.None);
        var bangCandidate = bangResults.FirstOrDefault(c => c.Id.StartsWith("bang:google"));
        Assert.NotNull(bangCandidate);
        Assert.Equal(SymbolRegular.Search24, bangCandidate.Icon.Symbol);
        Assert.Equal("Web Search", bangCandidate.KindLabel);
    }

    [Fact]
    public async Task CalculatorSource_ProvidesExplicitIconAndKindLabel()
    {
        var calc = new CalculatorSource();
        var query = SearchRanker.ParseQuery("12 + 34");
        var results = await calc.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        var result = results[0];
        Assert.Equal(SymbolRegular.Calculator24, result.Icon.Symbol);
        Assert.Equal("Calculator", result.KindLabel);
    }
}

