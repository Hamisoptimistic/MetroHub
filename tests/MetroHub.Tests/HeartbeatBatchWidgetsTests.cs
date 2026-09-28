using System;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.CaffeineSleep;
using MetroHub.Widgets.Catalog.Power;
using MetroHub.Widgets.Catalog.Rover;
using Xunit;

namespace MetroHub.Tests;

public class HeartbeatBatchWidgetsTests
{
    private static TileModel CreateTile(string targetPath, int spanX, int spanY)
    {
        return new TileModel
        {
            TileType = TileType.Widget,
            TargetPath = targetPath,
            SpanX = spanX,
            SpanY = spanY
        };
    }

    [Fact]
    public void PowerWidget_HeartbeatTicksCountdownToZero()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateTile("power", 8, 2);
            using var vm = new PowerWidgetViewModel(model);

            // Execute Restart action (starts 5-second countdown)
            var executeMethod = typeof(PowerWidgetViewModel).GetMethod("StartCountdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.NotNull(executeMethod);
            executeMethod.Invoke(vm, new object[] { "Restart" });

            Assert.Contains("5s", vm.RestartHeader);

            // Pulse 1 second forward
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(1));
            Assert.Contains("4s", vm.RestartHeader);

            // Pulse 2 more seconds forward
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(2));
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(3));
            Assert.Contains("2s", vm.RestartHeader);
        });
    }

    [Fact]
    public void PowerWidget_CancelCountdownStopsHeartbeatConsumption()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateTile("power", 8, 2);
            using var vm = new PowerWidgetViewModel(model);

            var startMethod = typeof(PowerWidgetViewModel).GetMethod("StartCountdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            startMethod?.Invoke(vm, new object[] { "Shutdown" });
            Assert.Contains("5s", vm.ShutdownHeader);

            var cancelMethod = typeof(PowerWidgetViewModel).GetMethod("CancelPendingCountdown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            cancelMethod?.Invoke(vm, null);

            Assert.Equal("Shut Down", vm.ShutdownHeader);

            // Pulse forward: header should remain standard "Shut Down"
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(5));
            Assert.Equal("Shut Down", vm.ShutdownHeader);
        });
    }

    [Fact]
    public void CaffeineSleepWidget_HeartbeatHandlesIdleAndDisposalCleanly()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateTile("caffeine_sleep", 8, 3);
            var vm = new CaffeineSleepWidgetViewModel(model);

            // In standard mode, heartbeat pulse should not crash or produce countdown text
            WidgetHeartbeatService.Pulse(DateTime.UtcNow);
            Assert.Equal(string.Empty, vm.CountdownText);

            vm.Dispose();
            // Disposed widget should not throw on pulse
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(10));
        });
    }

    [Fact]
    public void RoverWidget_HeartbeatTicksAndDisposalCleanly()
    {
        MarkdownTestHost.RunSta(() =>
        {
            var model = CreateTile("rover", 4, 2);
            var vm = new RoverWidgetViewModel(model);

            // Pulse 10 seconds through heartbeat
            for (int i = 0; i < 10; i++)
            {
                WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(i));
            }

            Assert.True(vm.State == RoverState.Idle || vm.State == RoverState.Sleeping);

            vm.Dispose();
            // Disposed Rover detaches cleanly
            WidgetHeartbeatService.Pulse(DateTime.UtcNow.AddSeconds(15));
        });
    }
}
