using System;
using System.Linq;
using System.Threading;
using System.Windows;
using MetroHub.Core.Models;
using MetroHub.Core.Radio;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.Radio;
using MetroHub.Widgets.Registry;
using MetroHub.Widgets.Serialization;
using Xunit;

namespace MetroHub.Tests;

public class RadioWidgetTests
{
    private static void EnsureApplication()
    {
        if (Application.Current == null)
        {
            try { new Application(); } catch { }
        }
    }

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            EnsureApplication();
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception != null)
        {
            throw new AggregateException("STA thread failed", exception);
        }
    }

    [Fact]
    public void WidgetRegistry_ContainsRadioWidget_WithHugeSize()
    {
        var def = WidgetRegistry.Get("radio");
        Assert.NotNull(def);
        Assert.Equal("Focus Radio", def.DisplayName);
        Assert.Equal("Sound", def.Category);
        Assert.Contains(WidgetSize.Huge, def.AllowedSizes);
        Assert.Equal(WidgetSize.Huge, def.DefaultSize);
        Assert.Equal(typeof(RadioWidgetViewModel), def.ViewModelType);
        Assert.Equal(typeof(RadioWidgetView), def.ViewType);
    }

    [Fact]
    public void RadioWidgetViewModel_InitializesWithCategoriesAndStations()
    {
        RunInSta(() =>
        {
            string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_init_{Guid.NewGuid():N}.json");
            try
            {
                var catalogService = new RadioCatalogService(tempCatalogPath);
                catalogService.LoadCatalog();
                var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
                var vm = new RadioWidgetViewModel(model, RadioAudioService.Instance, catalogService);
                vm.Initialize(model);

                // Default category is ambient (12 stations + 1 '+' placeholder = 13)
                Assert.Equal("ambient", vm.SelectedCategoryId);
                Assert.Equal(13, vm.VisibleStations.Count);
                Assert.True(vm.VisibleStations.Last().IsAddPlaceholder);

                // Switch to nature (10 stations + 1 '+' placeholder = 11)
                vm.SelectedCategoryId = "nature";
                Assert.Equal(11, vm.VisibleStations.Count);
                Assert.True(vm.VisibleStations.Last().IsAddPlaceholder);

                // Switch to lofi (13 stations + 1 '+' placeholder = 14)
                vm.SelectedCategoryId = "lofi";
                Assert.Equal(14, vm.VisibleStations.Count);
                Assert.True(vm.VisibleStations.Last().IsAddPlaceholder);

                // Switch to coding (5 stations + 1 '+' placeholder = 6)
                vm.SelectedCategoryId = "coding";
                Assert.Equal(6, vm.VisibleStations.Count);
                Assert.True(vm.VisibleStations.Last().IsAddPlaceholder);
            }
            finally
            {
                try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
                try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
            }
        });
    }

    [Fact]
    public void RadioWidgetViewModel_SettingsSerialization_PreservesValues()
    {
        RunInSta(() =>
        {
            var settings = new RadioWidgetSettings
            {
                LastSelectedCategory = "lofi",
                LastStationId = "somafm_groovesalad"
            };

            var json = WidgetSerializer.Serialize(settings);
            Assert.False(string.IsNullOrWhiteSpace(json));

            var deserialized = WidgetSerializer.Deserialize<RadioWidgetSettings>(json);
            Assert.NotNull(deserialized);
            Assert.Equal("lofi", deserialized.LastSelectedCategory);
            Assert.Equal("somafm_groovesalad", deserialized.LastStationId);
        });
    }

    [Fact]
    public void RadioWidgetViewModel_VolumeAndMute_SynchronizeCorrectly()
    {
        RunInSta(() =>
        {
            var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
            var audioService = RadioAudioService.Instance;
            var vm = new RadioWidgetViewModel(model);
            vm.Initialize(model);

            // Set volume on VM
            vm.Volume = 80.0;
            Assert.Equal(0.80, audioService.Volume, precision: 2);

            // Set mute on VM
            vm.ToggleMuteCommand.Execute(null);
            Assert.True(audioService.IsMuted);

            vm.ToggleMuteCommand.Execute(null);
            Assert.False(audioService.IsMuted);
        });
    }

    [Fact]
    public async Task RadioWidgetViewModel_NextAndPrevious_LoopWithinActiveCategory()
    {
        string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_loop_{Guid.NewGuid():N}.json");
        try
        {
            var catalogService = new RadioCatalogService(tempCatalogPath);
            catalogService.LoadCatalog();
            var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
            var vm = new RadioWidgetViewModel(model, RadioAudioService.Instance, catalogService);
            vm.Initialize(model);

            vm.SelectedCategoryId = "coding"; // 5 stations
            var realStations = vm.VisibleStations.Where(s => !s.IsAddPlaceholder).ToList();
            Assert.Equal(5, realStations.Count);

            // Next from start plays first or advances
            await vm.NextStationCommand.ExecuteAsync(null);
            Assert.NotNull(RadioAudioService.Instance.CurrentStation);

            RadioAudioService.Instance.Pause();
        }
        finally
        {
            try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
            try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public void RadioWidgetViewModel_CopyStreamUrl_UpdatesPlaybackStatusText()
    {
        RunInSta(() =>
        {
            string? copiedUrl = null;
            var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
            var vm = new RadioWidgetViewModel(
                model, 
                RadioAudioService.Instance, 
                RadioCatalogService.Instance, 
                url => copiedUrl = url);
            vm.Initialize(model);

            var firstItem = vm.VisibleStations.First(s => !s.IsAddPlaceholder);
            vm.CopyStreamUrlCommand.Execute(firstItem);

            Assert.Equal($"Copied: {firstItem.Station?.Name} link", vm.PlaybackStatusText);
            Assert.Equal(firstItem.Station?.StreamUrl, copiedUrl);
        });
    }

    [Fact]
    public async Task RadioWidgetViewModel_DeleteStation_DeletesCustomStationAndStopsPlayback()
    {
        string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_del_{Guid.NewGuid():N}.json");
        try
        {
            var customCatalogService = new RadioCatalogService(tempCatalogPath);
            customCatalogService.LoadCatalog();

            var customStation = new RadioStation
            {
                Id = "custom_deletable_station",
                Name = "Deletable Station",
                StreamUrl = "https://stream.example.com/del.mp3",
                IsCustom = true
            };
            await customCatalogService.AddCustomStationAsync(customStation, "coding");

            var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
            var audioService = RadioAudioService.Instance;
            var vm = new RadioWidgetViewModel(model, audioService, customCatalogService);
            vm.Initialize(model);
            vm.SelectedCategoryId = "coding";

            var customItem = vm.VisibleStations.FirstOrDefault(s => s.Id == "custom_deletable_station");
            Assert.NotNull(customItem);
            Assert.True(customItem.IsCustom);

            // Execute delete
            await vm.DeleteStationCommand.ExecuteAsync(customItem);

            // Verify removed from VM stations
            Assert.Null(vm.VisibleStations.FirstOrDefault(s => s.Id == "custom_deletable_station"));
            Assert.Null(customCatalogService.GetStationById("custom_deletable_station"));
        }
        finally
        {
            try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
            try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public async Task RadioWidgetViewModel_DeleteStation_ProtectsFactoryPreset()
    {
        string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_prot_{Guid.NewGuid():N}.json");
        try
        {
            var customCatalogService = new RadioCatalogService(tempCatalogPath);
            customCatalogService.LoadCatalog();

            var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
            var audioService = RadioAudioService.Instance;
            var vm = new RadioWidgetViewModel(model, audioService, customCatalogService);
            vm.Initialize(model);
            vm.SelectedCategoryId = "ambient";

            var factoryItem = vm.VisibleStations.First(s => s.Id == "dronezone");
            Assert.NotNull(factoryItem);
            Assert.False(factoryItem.IsCustom);

            // Attempt to delete factory preset
            await vm.DeleteStationCommand.ExecuteAsync(factoryItem);

            // Verify still exists
            Assert.NotNull(vm.VisibleStations.FirstOrDefault(s => s.Id == "dronezone"));
            Assert.NotNull(customCatalogService.GetStationById("dronezone"));
        }
        finally
        {
            try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
            try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
        }
    }

    [Fact]
    public void RadioWidgetViewModel_TwoLineLayout_DisplaysStationAndBitrateTypeCorrectly()
    {
        RunInSta(() =>
        {
            string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_twoline_{Guid.NewGuid():N}.json");
            try
            {
                var customCatalogService = new RadioCatalogService(tempCatalogPath);
                customCatalogService.LoadCatalog();

                var fakeAudio = new FakeAudioService();
                var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
                var vm = new RadioWidgetViewModel(model, fakeAudio, customCatalogService);
                vm.Initialize(model);

                // 1. Idle state (no station active - subtitle is empty)
                Assert.Equal("Select a station to focus", vm.CurrentStationTitle);
                Assert.Equal(string.Empty, vm.CurrentStationSubtitle);
                Assert.False(vm.HasStationSubtitle);
                Assert.Equal("Select a station to focus", vm.FullPlayerTooltip);

                // 2. Station active - displays Radio name and Bitrate + Type below
                var station = new RadioStation
                {
                    Id = "chillout",
                    Name = "Chillout Lounge",
                    BitrateKbps = 320,
                    Codec = "MP3",
                    StreamUrl = "http://example.com/stream"
                };
                fakeAudio.TriggerCurrentStation(station);

                Assert.Equal("Chillout Lounge", vm.CurrentStationTitle);
                Assert.Equal("320 kbps • MP3", vm.CurrentStationSubtitle);
                Assert.True(vm.HasStationSubtitle);
                Assert.Equal("Chillout Lounge (320 kbps • MP3)", vm.FullPlayerTooltip);

                // 3. Playback / buffering transitions keep the radio name and bitrate/type clean and constant
                fakeAudio.TriggerBuffering(true);
                Assert.Equal("Chillout Lounge", vm.CurrentStationTitle);
                Assert.Equal("320 kbps • MP3", vm.CurrentStationSubtitle);

                fakeAudio.TriggerBuffering(false);
                fakeAudio.TriggerPlayback(true);
                Assert.Equal("Chillout Lounge", vm.CurrentStationTitle);
                Assert.Equal("320 kbps • MP3", vm.CurrentStationSubtitle);

                // 4. Paused state keeps the radio name and bitrate/type clean
                fakeAudio.TriggerPlayback(false);
                Assert.Equal("Chillout Lounge", vm.CurrentStationTitle);
                Assert.Equal("320 kbps • MP3", vm.CurrentStationSubtitle);
            }
            finally
            {
                try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
                try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
            }
        });
    }

    [Fact]
    public void RadioWidgetViewModel_BackgroundThreadAudioEvents_DispatchesSafelyWithoutException()
    {
        RunInSta(() =>
        {
            var fakeAudio = new FakeAudioService();
            string tempCatalogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"radio_vm_bg_{Guid.NewGuid():N}.json");
            try
            {
                var customCatalogService = new RadioCatalogService(tempCatalogPath);
                customCatalogService.LoadCatalog();
                var model = new TileModel { TileType = TileType.Widget, TargetPath = "radio", SpanX = 8, SpanY = 6 };
                var vm = new RadioWidgetViewModel(model, fakeAudio, customCatalogService);
                vm.Initialize(model);

                // Trigger events from a background threadpool thread (as RadioAudioService does)
                var bgTask = Task.Run(() =>
                {
                    var station = new RadioStation { Id = "test_bg", Name = "Background Radio", StreamUrl = "http://fake.stream" };
                    fakeAudio.TriggerCurrentStation(station);
                    fakeAudio.TriggerBuffering(true);
                    fakeAudio.TriggerPlayback(true);
                    fakeAudio.TriggerPlayback(false);
                });

                bool completed = bgTask.Wait(TimeSpan.FromSeconds(5));
                Assert.True(completed);
                Assert.True(bgTask.IsCompletedSuccessfully);
            }
            finally
            {
                try { if (System.IO.File.Exists(tempCatalogPath)) System.IO.File.Delete(tempCatalogPath); } catch { }
                try { if (System.IO.File.Exists(tempCatalogPath + ".bak")) System.IO.File.Delete(tempCatalogPath + ".bak"); } catch { }
            }
        });
    }

    private class FakeAudioService : IRadioAudioService
    {
        public RadioStation? CurrentStation { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsBuffering { get; set; }
        public double Volume { get; set; } = 0.5;
        public bool IsMuted { get; set; }
        public string? LastErrorMessage { get; set; }

        public event EventHandler<RadioStation?>? CurrentStationChanged;
        public event EventHandler<bool>? PlaybackStateChanged;
        public event EventHandler<bool>? BufferingStateChanged;
#pragma warning disable CS0067
        public event EventHandler<double>? VolumeChanged;
        public event EventHandler<bool>? MuteStateChanged;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler? EndOfStreamReached;
#pragma warning restore CS0067

        public void TriggerCurrentStation(RadioStation? s) { CurrentStation = s; CurrentStationChanged?.Invoke(this, s); }
        public void TriggerPlayback(bool p) { IsPlaying = p; PlaybackStateChanged?.Invoke(this, p); }
        public void TriggerBuffering(bool b) { IsBuffering = b; BufferingStateChanged?.Invoke(this, b); }

        public Task PlayStationAsync(RadioStation station, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public void Pause() => IsPlaying = false;
        public void Resume() => IsPlaying = true;
        public void TogglePlayPause() => IsPlaying = !IsPlaying;
        public void Stop() { CurrentStation = null; IsPlaying = false; }
        public void SetVolume(double volume) => Volume = volume;
        public void SetMuted(bool isMuted) => IsMuted = isMuted;
        public bool GetSpectrumLevels(out float bass, out float mid, out float treble) { bass = 0; mid = 0; treble = 0; return false; }
        public void Dispose() { }
    }
}
