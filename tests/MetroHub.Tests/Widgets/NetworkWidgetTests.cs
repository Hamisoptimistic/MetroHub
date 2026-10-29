using MetroHub.Core.Models;
using MetroHub.Core.Network;
using MetroHub.Widgets.Catalog.Network;
using Xunit;

namespace MetroHub.Tests;

public class NetworkWidgetTests
{
    private static TileModel CreateTestTile()
    {
        return new TileModel
        {
            Id = System.Guid.NewGuid().ToString("N"),
            TileType = TileType.Widget,
            TargetPath = "network",
            SpanX = 8,
            SpanY = 6
        };
    }

    [Fact]
    public void SpeedTest_DefaultMetricsDisplays_AreDashes()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            Assert.Equal("--", vm.SpeedTestDownloadDisplay);
            Assert.Equal("--", vm.SpeedTestUploadDisplay);
            Assert.Equal("--", vm.SpeedTestPingNumberDisplay);
            Assert.Equal("--", vm.SpeedTestJitterNumberDisplay);
        });
    }

    [Fact]
    public void SpeedTest_MetricsDisplays_FormatNumbersWithoutUnits()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            vm.SpeedTestPingMs = 14.2;
            vm.SpeedTestJitterMs = 2.1;
            vm.SpeedTestDownloadMbps = 124.5;
            vm.SpeedTestUploadMbps = 50.3;

            Assert.Equal("14.2", vm.SpeedTestPingNumberDisplay);
            Assert.Equal("2.1", vm.SpeedTestJitterNumberDisplay);
            Assert.Equal("124.5", vm.SpeedTestDownloadDisplay);
            Assert.Equal("50.3", vm.SpeedTestUploadDisplay);
        });
    }

    [Fact]
    public void SpeedTest_DownloadPhaseActive_ShowsInstantaneousSpeed()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            vm.SpeedTestPhase = SpeedTestPhase.Download;
            vm.SpeedTestInstantaneousMbps = 88.4;

            Assert.True(vm.IsDownloadPhaseActive);
            Assert.Equal("88.4", vm.SpeedTestDownloadDisplay);
        });
    }

    [Fact]
    public void SpeedTest_SwitchingToSpeedPanel_DoesNotProbeCdnServer()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            vm.CurrentPanel = "Speed";

            Assert.True(vm.IsSpeedPanel);
            Assert.False(vm.IsSpeedTestRunning);
        });
    }

    [Fact]
    public void SpeedTest_PropertyChanged_RaisedForDownloadAndUploadDisplay_OnPhaseAndSpeedChanges()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            var changedProps = new System.Collections.Generic.List<string>();
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName != null)
                    changedProps.Add(e.PropertyName);
            };

            vm.SpeedTestPhase = SpeedTestPhase.Download;
            Assert.Contains(nameof(NetworkWidgetViewModel.SpeedTestDownloadDisplay), changedProps);
            Assert.Contains(nameof(NetworkWidgetViewModel.SpeedTestUploadDisplay), changedProps);

            changedProps.Clear();
            vm.SpeedTestInstantaneousMbps = 42.5;
            Assert.Contains(nameof(NetworkWidgetViewModel.SpeedTestDownloadDisplay), changedProps);
            Assert.Contains(nameof(NetworkWidgetViewModel.SpeedTestUploadDisplay), changedProps);
        });
    }

    [Fact]
    public void SpeedTest_CompletedPhase_SetsIsSpeedTestCompletedTrue()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            vm.SpeedTestPhase = SpeedTestPhase.Completed;
            Assert.True(vm.IsSpeedTestCompleted);
            Assert.True(vm.IsSpeedTestCompletedOrFailed);
            Assert.False(vm.IsSpeedTestRunning);
        });
    }

    [Fact]
    public void SpeedTest_Reset_RestoresDefaultDashState()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);

            vm.SpeedTestPingMs = 22.0;
            vm.SpeedTestJitterMs = 3.5;
            vm.SpeedTestDownloadMbps = 100.0;
            vm.SpeedTestUploadMbps = 50.0;

            vm.ResetSpeedTest();

            Assert.Equal("--", vm.SpeedTestDownloadDisplay);
            Assert.Equal("--", vm.SpeedTestUploadDisplay);
            Assert.Equal("--", vm.SpeedTestPingNumberDisplay);
            Assert.Equal("--", vm.SpeedTestJitterNumberDisplay);
            Assert.Equal(SpeedTestPhase.Idle, vm.SpeedTestPhase);
        });
    }

    [Fact]
    public void SpeedTest_VisualTree_NoCloudflareButtonAndContainsQuadrantLabels()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = CreateTestTile();
            using var vm = new NetworkWidgetViewModel(tile);
            vm.CurrentPanel = "Speed";

            var view = new NetworkWidgetView
            {
                DataContext = vm
            };

            view.Measure(new System.Windows.Size(508, 380));
            view.Arrange(new System.Windows.Rect(0, 0, 508, 380));

            // Verify dot borders exist in view
            var dotLatency = view.FindName("DotLatency") as System.Windows.FrameworkElement;
            var dotDownload = view.FindName("DotDownload") as System.Windows.FrameworkElement;
            var dotUpload = view.FindName("DotUpload") as System.Windows.FrameworkElement;

            Assert.NotNull(dotLatency);
            Assert.NotNull(dotDownload);
            Assert.NotNull(dotUpload);

            // Verify Cloudflare button does not exist anywhere in visual tree
            var allButtons = FindVisualChildren<System.Windows.Controls.Button>(view);
            foreach (var btn in allButtons)
            {
                var contentStr = btn.Content?.ToString() ?? "";
                var tipStr = btn.ToolTip?.ToString() ?? "";
                Assert.DoesNotContain("Cloudflare", contentStr, System.StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Cloudflare", tipStr, System.StringComparison.OrdinalIgnoreCase);
            }

            // Verify quadrant text blocks exist
            var allTextBlocks = FindVisualChildren<System.Windows.Controls.TextBlock>(view);
            var texts = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            foreach (var tb in allTextBlocks)
            {
                if (!string.IsNullOrEmpty(tb.Text))
                    texts.Add(tb.Text);
            }

            Assert.Contains("Download", texts);
            Assert.Contains("Upload", texts);
            Assert.Contains("Ping", texts);
            Assert.Contains("Jitter", texts);
            Assert.Contains("Mbps", texts);
            Assert.Contains("ms", texts);
        });
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject depObj) where T : System.Windows.DependencyObject
    {
        if (depObj == null) yield break;

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(depObj); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(depObj, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }
}

