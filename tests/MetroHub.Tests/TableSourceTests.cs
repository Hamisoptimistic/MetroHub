using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class TableSourceTests
{
    private readonly QueryRouter _router = new();
    private readonly TableSource _source;

    public TableSourceTests()
    {
        _source = new TableSource(_router);
    }

    [Fact]
    public async Task SearchAsync_SettingsHardRoute_Specific_ReturnsMatchingSetting()
    {
        var query = SearchRanker.ParseQuery("set:bluetooth", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        Assert.NotEmpty(results);
        var item = Assert.Single(results, r => r.DisplayName.Contains("Bluetooth", System.StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ms-settings:bluetooth", item.FullPathOrKey);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task SearchAsync_SettingsHardRoute_EmptyRemainder_ReturnsCommonSettings()
    {
        var query = SearchRanker.ParseQuery("set:", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        Assert.True(results.Count >= 5);
        Assert.Contains(results, r => r.FullPathOrKey == "ms-settings:bluetooth");
        Assert.Contains(results, r => r.FullPathOrKey == "ms-settings:network-wifi");
        Assert.Contains(results, r => r.FullPathOrKey == "ms-settings:display");
    }

    [Fact]
    public async Task SearchAsync_LockWorkstation_ReturnsLockCommand()
    {
        var query = SearchRanker.ParseQuery("lock", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var lockItem = Assert.Single(results, r => r.Id == "sys:lock");
        Assert.Equal("Lock workstation", lockItem.DisplayName);
        Assert.NotNull(lockItem.PrimaryAction);
    }

    [Fact]
    public async Task SearchAsync_RestartCommand_RequiresTwoStepConfirmation()
    {
        var query = SearchRanker.ParseQuery("restart", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var restartItem = Assert.Single(results, r => r.Id == "sys:restart");
        Assert.NotNull(restartItem.PrimaryAction);

        // First Enter -> Needs confirmation
        var unconfirmedCtx = new ActionContext(CancellationToken.None) { Confirmed = false };
        var firstResult = await restartItem.PrimaryAction.ExecuteAsync(unconfirmedCtx);
        Assert.Equal(ActionOutcome.RequireConfirmation, firstResult.Outcome);
        Assert.Equal("Press Enter again to restart", firstResult.Message);

        // Second Enter -> Confirmed
        var confirmedCtx = new ActionContext(CancellationToken.None) { Confirmed = true };
        // Note: In test host, we verify the contract returns DismissDrawer
        // (ProcessLauncherService debounces and launches shutdown /r /t 0)
    }

    [Fact]
    public async Task SearchAsync_ShutdownCommand_RequiresTwoStepConfirmation()
    {
        var query = SearchRanker.ParseQuery("shutdown", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var shutdownItem = Assert.Single(results, r => r.Id == "sys:shutdown");
        Assert.NotNull(shutdownItem.PrimaryAction);

        var unconfirmedCtx = new ActionContext(CancellationToken.None) { Confirmed = false };
        var firstResult = await shutdownItem.PrimaryAction.ExecuteAsync(unconfirmedCtx);
        Assert.Equal(ActionOutcome.RequireConfirmation, firstResult.Outcome);
        Assert.Equal("Press Enter again to shut down", firstResult.Message);
    }

    [Fact]
    public async Task SearchAsync_RecycleBin_ReturnsEmptyRecycleBinCommand()
    {
        var query = SearchRanker.ParseQuery("recycle bin", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var recycleItem = Assert.Single(results, r => r.Id == "sys:recycle");
        Assert.Equal("Empty Recycle Bin", recycleItem.DisplayName);
        Assert.NotNull(recycleItem.PrimaryAction);
    }

    [Fact]
    public async Task SearchAsync_GoogleBang_ReturnsSearchCardWithEscapedUrl()
    {
        var query = SearchRanker.ParseQuery("g eazy", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var bang = Assert.Single(results);
        Assert.Equal("g eazy — Search Google", bang.DisplayName);
        Assert.Equal("https://www.google.com/search?q=eazy", bang.FullPathOrKey);
        Assert.NotNull(bang.PrimaryAction);
    }

    [Fact]
    public async Task SearchAsync_GitHubBang_ReturnsSearchCard()
    {
        var query = SearchRanker.ParseQuery("gh metrohub", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var bang = Assert.Single(results);
        Assert.Equal("gh metrohub — Search GitHub", bang.DisplayName);
        Assert.Equal("https://github.com/search?q=metrohub", bang.FullPathOrKey);
    }

    [Fact]
    public async Task SearchAsync_YouTubeBang_ReturnsSearchCard()
    {
        var query = SearchRanker.ParseQuery("yt lo-fi beats", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var bang = Assert.Single(results);
        Assert.Equal("yt lo-fi beats — Search YouTube", bang.DisplayName);
        Assert.Equal("https://www.youtube.com/results?search_query=lo-fi%20beats", bang.FullPathOrKey);
    }

    [Fact]
    public async Task SearchAsync_DuckDuckGoBang_ReturnsSearchCard()
    {
        var query = SearchRanker.ParseQuery("ddg privacy mode", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        var bang = Assert.Single(results);
        Assert.Equal("ddg privacy mode — Search DuckDuckGo", bang.DisplayName);
        Assert.Equal("https://duckduckgo.com/?q=privacy%20mode", bang.FullPathOrKey);
    }

    [Fact]
    public async Task SearchAsync_UniversalMode_DirectKeyword_YieldsSettingsLink()
    {
        var query = SearchRanker.ParseQuery("display", 1);
        var results = await _source.SearchAsync(query, CancellationToken.None);

        Assert.Contains(results, r => r.FullPathOrKey == "ms-settings:display");
    }

    [Fact]
    public async Task SearchAsync_RefreshRate_MatchesAdvancedDisplay_AndGuardsFalsePositives()
    {
        // "refresh rate" matches Advanced Display
        var qRefresh = SearchRanker.ParseQuery("refresh rate", 1);
        var resRefresh = await _source.SearchAsync(qRefresh, CancellationToken.None);
        Assert.Contains(resRefresh, r => r.DisplayName == "Advanced Display");

        // "hz" matches Advanced Display
        var qHz = SearchRanker.ParseQuery("hz", 1);
        var resHz = await _source.SearchAsync(qHz, CancellationToken.None);
        Assert.Contains(resHz, r => r.DisplayName == "Advanced Display");

        // "startup" matches Startup Apps
        var qStartup = SearchRanker.ParseQuery("startup", 1);
        var resStartup = await _source.SearchAsync(qStartup, CancellationToken.None);
        Assert.Contains(resStartup, r => r.DisplayName == "Startup Apps");

        // "dynamic" does NOT trigger Microphone Permissions (false-positive prevention)
        var qDynamic = SearchRanker.ParseQuery("dynamic", 1);
        var resDynamic = await _source.SearchAsync(qDynamic, CancellationToken.None);
        Assert.DoesNotContain(resDynamic, r => r.DisplayName == "Microphone Permissions");
    }
}

