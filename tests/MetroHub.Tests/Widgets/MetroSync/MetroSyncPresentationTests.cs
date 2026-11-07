using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MetroHub.Core.Common;
using MetroHub.Core.Models;
using MetroHub.Presentation.Dialogs;
using MetroHub.Tests.Widgets.MetroSync.TestKit;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.MetroSync;
using MetroHub.Widgets.Catalog.MetroSync.Models;
using MetroHub.Widgets.Catalog.MetroSync.Services;
using MetroHub.Widgets.Registry;
using Xunit;

namespace MetroHub.Tests.Widgets.MetroSync;

[Collection("MetroSyncTests")]
public sealed class MetroSyncPresentationTests
{
    private static TelegramHttpTransport Http(FakeTelegramServer server, HttpClient client) =>
        new(client, server.BaseAddress, FakeTelegramServer.Token, allowInsecureLoopbackForTests: true);

    [Fact]
    public void WidgetRegistry_MetroSync_IsConfiguredWith8x10Only()
    {
        Assert.True(WidgetRegistry.TryGet("metrosync", out var def));
        Assert.NotNull(def);
        Assert.Equal("metrosync", def.Id);
        Assert.Equal("MetroSync", def.DisplayName);
        Assert.Equal(typeof(MetroSyncWidgetViewModel), def.ViewModelType);
        Assert.Equal(typeof(MetroSyncWidgetView), def.ViewType);

        // Strict 8x10 requirement: exactly 1 allowed size = WidgetSize.Full (8x10)
        Assert.Single(def.AllowedSizes);
        Assert.Equal(WidgetSize.Full, def.AllowedSizes[0]);
        Assert.Equal(8, def.AllowedSizes[0].SpanX);
        Assert.Equal(10, def.AllowedSizes[0].SpanY);
        Assert.Equal(WidgetSize.Full, def.DefaultSize);

        var tile = new TileModel
        {
            Id = Guid.NewGuid().ToString("N"),
            TargetPath = "metrosync",
            TileType = TileType.Widget,
            SpanX = 8,
            SpanY = 10
        };

        var vm = def.CreateViewModel(tile);
        Assert.NotNull(vm);
        Assert.IsType<MetroSyncWidgetViewModel>(vm);

        var syncVm = (MetroSyncWidgetViewModel)vm;
        Assert.Single(syncVm.AllowedSizes);
        Assert.Equal(WidgetSize.Full, syncVm.AllowedSizes[0]);
    }

    [Fact]
    public void FilePayloadItem_FileSizeDisplay_FormatsBytesCorrectly()
    {
        var itemNull = new FilePayloadItem { FileSize = null };
        Assert.Equal(FormatUtils.FormatBytes(0), itemNull.FileSizeDisplay);
        Assert.False(itemNull.HasError);
        Assert.False(itemNull.IsDownloaded);

        var itemBytes = new FilePayloadItem { FileSize = 512 };
        Assert.Equal("512 B", itemBytes.FileSizeDisplay);

        var itemKb = new FilePayloadItem { FileSize = 4096 };
        Assert.Equal("4 KB", itemKb.FileSizeDisplay);

        var itemMb = new FilePayloadItem { FileSize = 2_200_000 };
        Assert.Equal("2.1 MB", itemMb.FileSizeDisplay);

        itemMb.ErrorMessage = "Too large for bot download (20 MB limit)";
        Assert.True(itemMb.HasError);
    }

    [Fact]
    public void TextPayloadItem_HasUrl_AccuratelyDetectsUrls()
    {
        var plain = new TextPayloadItem { Text = "Hello, world! Just some text." };
        Assert.False(plain.HasUrl);
        Assert.Null(plain.FirstUrl);

        var withUrl = new TextPayloadItem { Text = "Check out https://github.com/microsoft/wpf for details" };
        Assert.True(withUrl.HasUrl);
        Assert.Equal("https://github.com/microsoft/wpf", withUrl.FirstUrl);
    }

    [Fact]
    public async Task PairingService_WaitForCandidateSenderAsync_DiscoversSenderWithoutPin()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var options = new MetroSyncPairingOptions
        {
            PollTimeoutSeconds = 1,
            PollDelay = TimeSpan.FromMilliseconds(10)
        };
        var service = new MetroSyncPairingService(MetroSyncSecurity.Default, TimeProvider.System, options);

        const long ignoredUserId = 111111;
        const long targetUserId = 222222;

        var ignoredSet = new HashSet<long> { ignoredUserId };

        var discoveryTask = service.WaitForCandidateSenderAsync(transport, ignoredSet, CancellationToken.None);

        // Enqueue message from ignored user first
        server.Enqueue(UpdateBuilder.Text(fromId: ignoredUserId, text: "/start", chatType: "private"));

        // Enqueue message from target user
        server.Enqueue(UpdateBuilder.Text(fromId: targetUserId, text: "/start", chatType: "private"));

        var candidate = await discoveryTask;

        Assert.NotNull(candidate);
        Assert.Equal(targetUserId, candidate.UserId);
        Assert.Equal("T", candidate.FirstName);
        Assert.Equal("T", candidate.DisplayName);
    }

    [Fact]
    public void MetroSyncWidgetView_InitializesWithoutMissingResources()
    {
        WpfTestHost.RunSta(() =>
        {
            var view = new MetroSyncWidgetView();
            Assert.NotNull(view);
            Assert.IsAssignableFrom<FrameworkElement>(view);
            Assert.NotNull(view.FindName("FeedScrollViewer"));
            Assert.NotNull(view.FindName("HeaderMenuButton"));
            Assert.NotNull(view.FindName("UnpairedPanel"));
        });
    }

    [Fact]
    public void MetroSyncSetupDialog_InitializesWithoutMissingResources()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new MetroSyncSetupDialog();
            Assert.NotNull(dialog);
            Assert.IsAssignableFrom<Window>(dialog);
            Assert.IsAssignableFrom<MetroDialog>(dialog);
            Assert.Equal("Connect MetroSync", dialog.Title);

            // Verify ProgressRing IsIndeterminate is false by default (never hardcoded to true)
            var ring = dialog.FindName("PollingProgressRing") as Wpf.Ui.Controls.ProgressRing;
            Assert.NotNull(ring);
            Assert.False(ring.IsIndeterminate);
        });
    }

    [Fact]
    public void MetroSyncTemplates_InstantiatesDataTemplatesForPayloadTypes()
    {
        WpfTestHost.RunSta(() =>
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/MetroHub;component/Widgets/Catalog/MetroSync/MetroSyncTemplates.xaml", UriKind.Absolute)
            };

            var textKey = new DataTemplateKey(typeof(TextPayloadItem));
            var imageKey = new DataTemplateKey(typeof(ImagePayloadItem));
            var fileKey = new DataTemplateKey(typeof(FilePayloadItem));

            Assert.True(dict.Contains(textKey));
            Assert.True(dict.Contains(imageKey));
            Assert.True(dict.Contains(fileKey));

            var textTemplate = dict[textKey] as DataTemplate;
            var imageTemplate = dict[imageKey] as DataTemplate;
            var fileTemplate = dict[fileKey] as DataTemplate;

            Assert.NotNull(textTemplate);
            Assert.NotNull(imageTemplate);
            Assert.NotNull(fileTemplate);

            var textContent = textTemplate.LoadContent();
            var imageContent = imageTemplate.LoadContent();
            var fileContent = fileTemplate.LoadContent();

            Assert.NotNull(textContent);
            Assert.NotNull(imageContent);
            Assert.NotNull(fileContent);
        });
    }
}