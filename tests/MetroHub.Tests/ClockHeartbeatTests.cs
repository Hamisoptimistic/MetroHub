using System;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.Clock;
using Xunit;

namespace MetroHub.Tests;

public class ClockHeartbeatTests
{
    private static TileModel CreateClockTile()
    {
        return new TileModel
        {
            TileType = TileType.Widget,
            TargetPath = "clock",
            SpanX = 4,
            SpanY = 2
        };
    }

    [Fact]
    public void ClockInitializes_WithCurrentTimeDigits()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateClockTile();
            using var vm = new ClockWidgetViewModel(model);

            Assert.False(string.IsNullOrEmpty(vm.TimeString));
            Assert.False(string.IsNullOrEmpty(vm.HoursString));
            Assert.False(string.IsNullOrEmpty(vm.MinutesString));
            Assert.Contains(":", vm.TimeString);
        });
    }

    [Fact]
    public void HeartbeatPulse_UpdatesTimeAcrossMinuteBoundary()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateClockTile();
            using var vm = new ClockWidgetViewModel(model);
            vm.SetTimeFormat(is24Hour: true);

            // Set fixed test time: 14:05:00 UTC
            var t1 = new DateTime(2026, 9, 29, 14, 5, 0, DateTimeKind.Utc);
            WidgetHeartbeatService.Pulse(t1);

            var localT1 = t1.ToLocalTime();
            Assert.Equal($"{localT1.Hour:D2}:{localT1.Minute:D2}", vm.TimeString);

            // Advance by 1 minute: 14:06:00 UTC
            var t2 = new DateTime(2026, 9, 29, 14, 6, 0, DateTimeKind.Utc);
            WidgetHeartbeatService.Pulse(t2);

            var localT2 = t2.ToLocalTime();
            Assert.Equal($"{localT2.Hour:D2}:{localT2.Minute:D2}", vm.TimeString);
        });
    }

    [Fact]
    public void TwelveHourFormat_FormatsCorrectlyViaHeartbeat()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateClockTile();
            using var vm = new ClockWidgetViewModel(model);
            vm.SetTimeFormat(is24Hour: false);

            var t = new DateTime(2026, 9, 29, 13, 15, 0, DateTimeKind.Utc);
            WidgetHeartbeatService.Pulse(t);

            var localT = t.ToLocalTime();
            int expectedHour = localT.Hour % 12;
            if (expectedHour == 0) expectedHour = 12;

            Assert.Equal(expectedHour.ToString(), vm.HoursString);
            Assert.Equal($"{expectedHour}:{localT.Minute:D2}", vm.TimeString);
        });
    }

    [Fact]
    public void DisposedClock_DetachesFromHeartbeatCleanly()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateClockTile();
            var vm = new ClockWidgetViewModel(model);

            vm.Dispose();

            // Heartbeat pulse after disposal should not throw
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddMinutes(5));
        });
    }
}
