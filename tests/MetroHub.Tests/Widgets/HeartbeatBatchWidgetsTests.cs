using System;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets.Catalog.CaffeineSleep;
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
    public void CaffeineSleepWidget_HeartbeatHandlesIdleAndDisposalCleanly()
    {
        WpfTestHost.RunSta(() =>
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
        WpfTestHost.RunSta(() =>
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
