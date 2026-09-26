using System;
using System.IO;
using System.Threading;
using System.Windows;
using MetroHub.Core.Models;
using MetroHub.Widgets.Catalog.Rover;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

public class RoverWidgetTests
{
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            try { new Application(); } catch { }
        }
    }

    [Fact]
    public void RoverManifest_All29AnimationsExist_AndAllFramesAreWithinBounds()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            var animations = RoverManifest.Animations;
            Assert.NotNull(animations);
            Assert.True(animations.Count >= 29, $"Expected at least 29 animations, got {animations.Count}");

            string[] expectedAnimations =
            {
                "Congratulate", "Hide", "Acknowledge", "Thinking", "Travel", "Cooking", "Writing",
                "GetAttention", "GestureLeft", "Surprised", "Shopping", "ImageSearching", "Celebrity",
                "LookUpLeft", "Greet", "Idle", "HideQuick", "CharacterSucceeds", "Sports", "Show",
                "Money", "Searching", "Embarrassed", "Books", "LookUp", "ClickedOn", "GetAttentionMinor",
                "RestPose", "Pleased"
            };

            foreach (var name in expectedAnimations)
            {
                Assert.True(animations.ContainsKey(name), $"Expected animation '{name}' not found in manifest.");
                var anim = animations[name];
                Assert.NotEmpty(anim.Frames);

                foreach (var frame in anim.Frames)
                {
                    Assert.InRange(frame.X, 0, 2160 - 80);
                    Assert.InRange(frame.Y, 0, 2160 - 80);
                    Assert.True(frame.DurationMs >= 50, $"Frame duration should be at least 50ms, was {frame.DurationMs}");
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void RoverAnimationEngine_QuiescenceAtRest_TimerIsStopped()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            var engine = new RoverAnimationEngine();
            engine.SetStaticPose("RestPose");

            // Crucial architectural verification: Timer MUST NOT be running when at rest
            Assert.False(engine.IsRunning, "Engine timer should be stopped at RestPose to guarantee 0.000% CPU.");
            Assert.Equal("RestPose", engine.CurrentAnimationName);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void RoverWidgetSettings_SerializesAndDeserializesCorrectly()
    {
        var original = new RoverWidgetSettings
        {
            IsMuted = true,
            Volume = 0.85,
            SleepTimeoutMinutes = 5,
            ShowSpeechBubbles = false,
            BackgroundStyle = "XPBliss"
        };

        string json = WidgetSerializer.Serialize(original);
        Assert.False(string.IsNullOrWhiteSpace(json));

        var deserialized = WidgetSerializer.Deserialize<RoverWidgetSettings>(json);
        Assert.NotNull(deserialized);
        Assert.True(deserialized.IsMuted);
        Assert.Equal(0.85, deserialized.Volume, 2);
        Assert.Equal(5, deserialized.SleepTimeoutMinutes);
        Assert.False(deserialized.ShowSpeechBubbles);
        Assert.Equal("XPBliss", deserialized.BackgroundStyle);
    }

    [Fact]
    public void RoverAudioService_MuteAndPlay_DoesNotThrow()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            using var audio = new RoverAudioService();
            audio.IsMuted = true;
            audio.PlaySound("1");
            audio.PlaySound("invalid");
            audio.PlaySound(null);

            audio.IsMuted = false;
            audio.Volume = 0.5;
            audio.PlaySound("3"); // Short bark
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void RoverWidgetView_InstantiatesAndBindsAcrossAllSizes()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            var sizes = new[]
            {
                (SpanX: 2, SpanY: 2), // Medium
                (SpanX: 4, SpanY: 2), // Wide
                (SpanX: 4, SpanY: 4), // Large
                (SpanX: 8, SpanY: 3)  // Banner
            };

            foreach (var size in sizes)
            {
                var tile = new TileModel { SpanX = size.SpanX, SpanY = size.SpanY };
                var vm = new RoverWidgetViewModel(tile);
                var view = new RoverWidgetView
                {
                    DataContext = vm
                };

                // Trigger layout measure to verify full visual tree without exception
                view.Measure(new Size(size.SpanX * 60, size.SpanY * 60));
                view.Arrange(new Rect(0, 0, size.SpanX * 60, size.SpanY * 60));

                Assert.NotNull(view);
                Assert.Equal(vm, view.DataContext);

                // Verify size properties
                if (size.SpanX == 2) Assert.True(vm.IsMedium);
                if (size.SpanX == 4 && size.SpanY == 2) Assert.True(vm.IsWide);
                if (size.SpanX == 4 && size.SpanY == 4) Assert.True(vm.IsLarge);
                if (size.SpanX == 8) Assert.True(vm.IsBanner);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void WidgetRegistry_RegistersRover_AndCreatesViewModel()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            bool found = MetroHub.Widgets.Registry.WidgetRegistry.TryGet("rover", out var def);
            Assert.True(found, "Rover definition must be registered in WidgetRegistry.");
            Assert.NotNull(def);
            Assert.Equal("rover", def.Id);
            Assert.Equal("Rover (Windows XP)", def.DisplayName);
            Assert.Equal("Lifestyle", def.Category);
            Assert.Equal(Wpf.Ui.Controls.SymbolRegular.AnimalDog24, def.Icon);

            var tile = new TileModel { TargetPath = "rover", SpanX = 4, SpanY = 2 };
            var vm = MetroHub.Widgets.Registry.WidgetRegistry.CreateViewModelForTile(tile);
            Assert.NotNull(vm);
            Assert.IsType<RoverWidgetViewModel>(vm);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    [Fact]
    public void RenderRoverWidgetsToArtifactDirectory()
    {
        var thread = new Thread(() =>
        {
            EnsureApplication();

            string outDir = @"C:\Users\HamB\.gemini\antigravity-ide\brain\a6e02d95-8bf4-47e5-864a-6d9ee3ef1384";
            Directory.CreateDirectory(outDir);

            var scenarios = new[]
            {
                (Name: "rover_2x2_pocket", SpanX: 2, SpanY: 2, W: 116, H: 116, Speech: false, Style: "FluentGlass"),
                (Name: "rover_4x2_speech", SpanX: 4, SpanY: 2, W: 248, H: 116, Speech: true, Style: "FluentGlass"),
                (Name: "rover_4x4_playpen", SpanX: 4, SpanY: 4, W: 248, H: 248, Speech: true, Style: "FluentGlass"),
                (Name: "rover_8x3_search", SpanX: 8, SpanY: 3, W: 504, H: 184, Speech: true, Style: "FluentGlass"),
                (Name: "rover_4x2_xpbliss", SpanX: 4, SpanY: 2, W: 248, H: 116, Speech: true, Style: "XPBliss")
            };

            foreach (var s in scenarios)
            {
                var tile = new TileModel { SpanX = s.SpanX, SpanY = s.SpanY };
                var vm = new RoverWidgetViewModel(tile)
                {
                    BackgroundStyle = s.Style
                };
                if (s.Speech)
                {
                    vm.ShowSpeech("Woof! How can I help you today?");
                }

                var view = new RoverWidgetView
                {
                    DataContext = vm,
                    Width = s.W,
                    Height = s.H
                };

                var container = new System.Windows.Controls.Border
                {
                    Width = s.W,
                    Height = s.H,
                    Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27)),
                    CornerRadius = new CornerRadius(8),
                    ClipToBounds = true,
                    Child = view
                };

                container.Measure(new Size(s.W, s.H));
                container.Arrange(new Rect(0, 0, s.W, s.H));
                container.UpdateLayout();

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(s.W, s.H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(container);

                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                string path = Path.Combine(outDir, $"{s.Name}.png");
                using var fs = File.Create(path);
                encoder.Save(fs);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}
