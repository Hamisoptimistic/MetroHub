using System;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Pomodoro;
using Xunit;

namespace MetroHub.Tests;

public class PomodoroHeartbeatTests
{
    private static TileModel CreatePomodoroTile()
    {
        return new TileModel
        {
            TileType = TileType.Widget,
            TargetPath = "pomodoro",
            SpanX = 8,
            SpanY = 4
        };
    }

    [Fact]
    public void WhenStopped_HeartbeatPulseDoesNotChangeRemainingTime()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreatePomodoroTile();
            using var vm = new PomodoroWidgetViewModel(model);

            Assert.False(vm.IsRunning);
            Assert.Equal("25:00", vm.TimeString);

            // Pulse the heartbeat 5 times
            var now = DateTime.UtcNow;
            for (int i = 0; i < 5; i++)
            {
                now = now.AddSeconds(1);
                WidgetHeartbeatService.Pulse(now);
            }

            // Since it's stopped, time should remain 25:00
            Assert.Equal("25:00", vm.TimeString);
            Assert.Equal(0.0, vm.ProgressRatio);
        });
    }

    [Fact]
    public void WhenRunning_HeartbeatPulseCountsDownAccurately()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreatePomodoroTile();
            using var vm = new PomodoroWidgetViewModel(model);

            // Start the focus timer
            vm.TogglePlayPause();
            Assert.True(vm.IsRunning);

            // Pulse 1 second forward
            var now = DateTime.UtcNow.AddSeconds(1);
            WidgetHeartbeatService.Pulse(now);

            // Should count down to 24:59
            Assert.Equal("24:59", vm.TimeString);
            Assert.True(vm.ProgressRatio > 0.0);

            // Pulse 10 seconds forward
            now = now.AddSeconds(9);
            WidgetHeartbeatService.Pulse(now);

            Assert.Equal("24:50", vm.TimeString);
        });
    }

    [Fact]
    public void WhenTimerReachesZero_PhaseCompletesAndAdvances()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreatePomodoroTile();
            using var vm = new PomodoroWidgetViewModel(model);

            vm.SetPreset(1, 1, 1); // 1-minute focus
            Assert.Equal(PomodoroPhase.Focus, vm.Phase);
            Assert.Equal("01:00", vm.TimeString);

            vm.TogglePlayPause();
            Assert.True(vm.IsRunning);

            // Pulse forward 60 seconds to reach target end time
            var targetEnd = DateTime.UtcNow.AddSeconds(60);
            WidgetHeartbeatService.Pulse(targetEnd);

            // Timer should complete, stop running, and advance to ShortBreak
            Assert.False(vm.IsRunning);
            Assert.Equal(PomodoroPhase.ShortBreak, vm.Phase);
            Assert.Equal(1, vm.CompletedSessions);
        });
    }

    [Fact]
    public void DisposedWidget_DetachesFromHeartbeatCleanly()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreatePomodoroTile();
            var vm = new PomodoroWidgetViewModel(model);
            vm.TogglePlayPause();

            vm.Dispose();

            // Heartbeat pulse after disposal should not throw or affect disposed VM
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(10));
        });
    }
}
