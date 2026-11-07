using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            Assert.Equal("Follow these steps to connect your device", dialog.Title);

            // Verify ProgressRing IsIndeterminate is false by default (never hardcoded to true)
            var ring = dialog.FindName("PollingProgressRing") as Wpf.Ui.Controls.ProgressRing;
            Assert.NotNull(ring);
            Assert.False(ring.IsIndeterminate);

            // Verify state preservation across app deactivations / deep links
            Assert.False(dialog.DismissOnDeactivate);
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

    [Fact]
    public async Task TelegramHttpTransport_GetBotPhotoBytesAsync_FetchesBotAvatarViaChatPhoto()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        const string botUsername = "samplebot";
        byte[] expectedBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // PNG header
        server.AddFile("bot_avatar_file", expectedBytes);
        server.SetChatPhoto(botUsername, "bot_avatar_file");

        var actualBytes = await transport.GetBotPhotoBytesAsync(botUsername, 999888, CancellationToken.None);

        Assert.NotNull(actualBytes);
        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public async Task TelegramHttpTransport_GetBotPhotoBytesAsync_ReturnsNullWhenNoPhoto()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        var actualBytes = await transport.GetBotPhotoBytesAsync("botwithoutphoto", 111222, CancellationToken.None);

        Assert.Null(actualBytes);
    }

    [Fact]
    public async Task TelegramHttpTransport_GetUserProfilePhotoBytesAsync_FetchesAvatarBytesCorrectly()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        const long userId = 444555;
        byte[] expectedBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // PNG header
        server.AddFile("avatar_444555", expectedBytes);
        server.SetUserProfilePhoto(userId, "avatar_444555");

        var actualBytes = await transport.GetUserProfilePhotoBytesAsync(userId, CancellationToken.None);

        Assert.NotNull(actualBytes);
        Assert.Equal(expectedBytes, actualBytes);
    }

    [Fact]
    public async Task TelegramHttpTransport_GetUserProfilePhotoBytesAsync_ReturnsNullWhenNoPhoto()
    {
        await using var server = await FakeTelegramServer.StartAsync();
        using var http = TelegramHttpTransport.CreateDedicatedClient();
        var transport = Http(server, http);

        const long userIdWithoutPhoto = 777888;
        var actualBytes = await transport.GetUserProfilePhotoBytesAsync(userIdWithoutPhoto, CancellationToken.None);

        Assert.Null(actualBytes);
    }

    [Fact]
    public void MetroSyncSetupDialog_State3ElementsExist_AndManualToggleHidesInState3()
    {
        WpfTestHost.RunSta(() =>
        {
            var dialog = new MetroSyncSetupDialog();
            Assert.NotNull(dialog.FindName("State3Panel"));
            Assert.NotNull(dialog.FindName("ConfirmationAvatarFallback"));
            Assert.NotNull(dialog.FindName("ConfirmationAvatarImage"));
            Assert.NotNull(dialog.FindName("ConfirmationAccountNameText"));
            Assert.NotNull(dialog.FindName("ConfirmationAccountHandleText"));

            var manualBtn = dialog.FindName("ManualIdToggleButton") as FrameworkElement;
            Assert.NotNull(manualBtn);

            // In initial Intro state, manual link is collapsed
            Assert.Equal(Visibility.Collapsed, manualBtn.Visibility);

            // Transition to State 3 via private method SetVisualState(Confirmation)
            var setVisualStateMethod = typeof(MetroSyncSetupDialog).GetMethod(
                "SetVisualState",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(setVisualStateMethod);

            // Confirmation is enum value 3
            var confirmationState = Enum.ToObject(setVisualStateMethod.GetParameters()[0].ParameterType, 3);
            setVisualStateMethod.Invoke(dialog, [confirmationState]);

            // In State 3 (Confirmation), ManualIdToggleButton MUST be collapsed
            Assert.Equal(Visibility.Collapsed, manualBtn.Visibility);

            var state3Panel = dialog.FindName("State3Panel") as FrameworkElement;
            Assert.NotNull(state3Panel);
            Assert.Equal(Visibility.Visible, state3Panel.Visibility);
        });
    }

    [Fact]
    public void MetroSyncWidgetView_HeaderAvatar_UpdatesWithPairedStateAndProfileFile()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var tile = new TileModel
            {
                Id = Guid.NewGuid().ToString("N"),
                TargetPath = "metrosync",
                TileType = TileType.Widget,
                SpanX = 8,
                SpanY = 10
            };
            using var vm = new MetroSyncWidgetViewModel(tile, baseDirectory: sandbox.Root);
            var view = new MetroSyncWidgetView
            {
                DataContext = vm
            };

            var headerBorder = view.FindName("HeaderAvatarBorder") as FrameworkElement;
            var headerImage = view.FindName("HeaderAvatarImage") as Image;
            Assert.NotNull(headerBorder);
            Assert.NotNull(headerImage);

            // 1. Initially unpaired -> avatar image collapsed
            Assert.Equal(Visibility.Collapsed, headerImage.Visibility);

            // 2. Create profile.jpg in sandbox
            Directory.CreateDirectory(Path.GetDirectoryName(vm.ProfilePhotoPath)!);
            var bmpSource = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            using (var fs = File.Create(vm.ProfilePhotoPath))
            {
                encoder.Save(fs);
            }

            // 3. Mark VM as paired
            vm.IsPaired = true;

            // Now header avatar image must be visible automatically via property changed notification
            Assert.Equal(Visibility.Visible, headerImage.Visibility);
            Assert.NotNull(headerImage.Source);

            // 4. Mark VM as unpaired
            vm.IsPaired = false;

            // Header avatar image must be collapsed and cleared automatically
            Assert.Equal(Visibility.Collapsed, headerImage.Visibility);
            Assert.Null(headerImage.Source);
        });
    }

    [Fact]
    public void MetroSyncSetupDialog_SetCandidatePreview_RendersAvatarAndFallbackCorrectly()
    {
        WpfTestHost.RunSta(() =>
        {
            var bmpSource = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] avatarBytes = ms.ToArray();

            var dialog = new MetroSyncSetupDialog();

            // 1. Candidate with avatar
            var candidateWithPhoto = new CandidateSender(12345, 67890, "johndoe", "John");
            dialog.SetCandidatePreview(candidateWithPhoto, avatarBytes);

            Assert.Equal(Visibility.Visible, dialog.ConfirmationAvatarImage.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.ConfirmationAvatarFallback.Visibility);
            Assert.NotNull(dialog.ConfirmationAvatarImage.Source);
            Assert.Equal("John", dialog.ConfirmationAccountNameText.Text);
            Assert.Equal("@johndoe", dialog.ConfirmationAccountHandleText.Text);
            Assert.Equal(Visibility.Collapsed, dialog.ManualIdToggleButton.Visibility);
            Assert.Equal(Visibility.Visible, dialog.State3Panel.Visibility);

            // 2. Candidate without avatar
            var candidateWithoutPhoto = new CandidateSender(54321, 98760, null, null);
            dialog.SetCandidatePreview(candidateWithoutPhoto, null);

            Assert.Equal(Visibility.Collapsed, dialog.ConfirmationAvatarImage.Visibility);
            Assert.Equal(Visibility.Visible, dialog.ConfirmationAvatarFallback.Visibility);
            Assert.Null(dialog.ConfirmationAvatarImage.Source);
            Assert.Equal("54321", dialog.ConfirmationAccountNameText.Text);
            Assert.Equal("54321", dialog.ConfirmationAccountHandleText.Text);
            Assert.Equal(Visibility.Collapsed, dialog.ManualIdToggleButton.Visibility);
            Assert.Equal(Visibility.Visible, dialog.State3Panel.Visibility);
        });
    }

    [Fact]
    public void MetroSyncSetupDialog_SaveAndConnect_PersistsAvatarBytes_OrDeletesWhenNull()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var security = new MetroSyncSecurity(sandbox.Root);
            var profilePath = Path.Combine(sandbox.Root, "profile.jpg");

            var bmpSource = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            byte[] avatarBytes = ms.ToArray();

            // 1. Connect with avatar -> writes profile.jpg to disk
            var dialog1 = new MetroSyncSetupDialog(null, security);
            dialog1.SetCandidatePreview(new CandidateSender(12345, 67890, "alice", "Alice"), avatarBytes);
            dialog1.SaveAndConnect("111111:AAABBBCCC", 12345);

            Assert.True(File.Exists(profilePath));
            Assert.Equal(avatarBytes, File.ReadAllBytes(profilePath));

            // 2. Switching to candidate without avatar -> deletes stale profile.jpg from disk
            var dialog2 = new MetroSyncSetupDialog(null, security);
            dialog2.SetCandidatePreview(new CandidateSender(67890, 54321, "bob", "Bob"), null);
            dialog2.SaveAndConnect("222222:DDDEEEFFF", 67890);

            Assert.False(File.Exists(profilePath));
        });
    }

    [Fact]
    public void MetroSyncSetupDialog_OnNotMe_PreservesDiskProfilePhoto_AndClearsPreview()
    {
        WpfTestHost.RunSta(() =>
        {
            using var sandbox = new WidgetTestSandbox();
            var security = new MetroSyncSecurity(sandbox.Root);
            var profilePath = Path.Combine(sandbox.Root, "profile.jpg");

            byte[] originalBytes = [0x01, 0x02, 0x03, 0x04];
            Directory.CreateDirectory(sandbox.Root);
            File.WriteAllBytes(profilePath, originalBytes);

            var dialog = new MetroSyncSetupDialog(null, security);
            var strangerCandidate = new CandidateSender(99999, 11111, "stranger", "Stranger");
            byte[] strangerBytes = [0xAA, 0xBB, 0xCC];

            dialog.SetCandidatePreview(strangerCandidate, strangerBytes);
            Assert.Equal(Visibility.Visible, dialog.State3Panel.Visibility);

            // User clicks "Not me"
            dialog.NotMeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            // Candidate preview cleared & transitioned back to PollingWait
            Assert.Equal(Visibility.Collapsed, dialog.ConfirmationAvatarImage.Visibility);
            Assert.Null(dialog.ConfirmationAvatarImage.Source);
            Assert.Equal(Visibility.Visible, dialog.ConfirmationAvatarFallback.Visibility);
            Assert.Equal(Visibility.Visible, dialog.State2Panel.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.State3Panel.Visibility);

            // Existing profile.jpg on disk remains untouched
            Assert.True(File.Exists(profilePath));
            Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
        });
    }

    [Fact]
    public async Task MetroSyncWidgetViewModel_DisconnectAsync_DeletesProfilePhoto()
    {
        using var sandbox = new WidgetTestSandbox();
        var tile = new TileModel
        {
            Id = Guid.NewGuid().ToString("N"),
            TargetPath = "metrosync",
            TileType = TileType.Widget,
            SpanX = 8,
            SpanY = 10
        };
        using var vm = new MetroSyncWidgetViewModel(tile, baseDirectory: sandbox.Root);

        Directory.CreateDirectory(Path.GetDirectoryName(vm.ProfilePhotoPath)!);
        File.WriteAllBytes(vm.ProfilePhotoPath, [0xFF, 0xD8, 0xFF]);
        Assert.True(File.Exists(vm.ProfilePhotoPath));

        await vm.DisconnectAsync();

        Assert.False(File.Exists(vm.ProfilePhotoPath));
        Assert.False(vm.IsPaired);
    }
}