using System;
using System.Collections.Generic;
using MetroHub.Core.Models;
using MetroHub.Core.Services;
using MetroHub.Widgets;
using MetroHub.Widgets.Catalog.Template;
using MetroHub.Widgets.Messaging;
using MetroHub.Widgets.Registry;
using Wpf.Ui.Controls;
using Xunit;

namespace MetroHub.Tests;

public sealed partial class WidgetFaultIsolationTests
{
    public sealed partial class ThrowingTickWidget : WidgetViewModelBase
    {
        public bool TickAttempted { get; private set; }

        public ThrowingTickWidget(TileModel model) : base(model) { }

        public override IReadOnlyList<WidgetSize> AllowedSizes => new[] { WidgetSize.Medium };

        public override void OnSecondTick(DateTime utcNow)
        {
            TickAttempted = true;
            throw new InvalidOperationException("Simulated catastrophic widget tick crash");
        }
    }

    public sealed partial class HealthyTickWidget : WidgetViewModelBase
    {
        public int TickCount { get; private set; }

        public HealthyTickWidget(TileModel model) : base(model) { }

        public override IReadOnlyList<WidgetSize> AllowedSizes => new[] { WidgetSize.Medium };

        public override void OnSecondTick(DateTime utcNow)
        {
            TickCount++;
        }
    }

    [Fact]
    public void HeartbeatPulse_WhenOneWidgetThrows_OtherWidgetsKeepTickingAndHostSurvives()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile1 = new TileModel { Id = "tile_bad", TargetPath = "stub" };
            var tile2 = new TileModel { Id = "tile_good", TargetPath = "stub" };

            using var badWidget = new ThrowingTickWidget(tile1);
            using var goodWidget = new HealthyTickWidget(tile2);

            var loggedErrors = new List<string>();
            var originalLogger = Safe.Logger;
            Safe.Logger = (ex, ctx) => loggedErrors.Add($"{ctx}: {ex.Message}");

            try
            {
                // Pulse heartbeat — bad widget throws inside its tick
                WidgetHeartbeatService.Pulse(DateTime.UtcNow);

                // Verification:
                // 1. Bad widget attempted tick
                Assert.True(badWidget.TickAttempted);
                // 2. Good widget successfully received tick despite bad widget throwing
                Assert.Equal(1, goodWidget.TickCount);
                // 3. Error was caught, logged, and isolated
                Assert.Contains(loggedErrors, err => err.Contains("Simulated catastrophic widget tick crash"));
            }
            finally
            {
                Safe.Logger = originalLogger;
            }
        });
    }

    [Fact]
    public void WidgetDefinition_CreateViewModel_WhenFactoryThrows_ReturnsErrorStubInsteadOfCrashing()
    {
        WpfTestHost.RunSta(() =>
        {
            var def = new WidgetDefinition(
                Id: "crashing_widget",
                DisplayName: "Crashing Test Widget",
                Description: "Throws during creation",
                Icon: SymbolRegular.Warning24,
                AllowedSizes: new[] { WidgetSize.Medium },
                ViewModelType: typeof(TemplateWidgetViewModel),
                Factory: _ => throw new DllNotFoundException("Missing simulated native hardware library")
            );

            var tile = new TileModel { Id = "test_tile", TargetPath = "crashing_widget" };

            var vm = def.CreateViewModel(tile);

            Assert.NotNull(vm);
            Assert.IsType<TemplateWidgetViewModel>(vm);
            var template = (TemplateWidgetViewModel)vm;
            Assert.Contains("Crashing Test Widget", template.Label);
            Assert.Equal("#DC2626", template.BoxColor);
        });
    }

    [Fact]
    public void WidgetViewModelBase_ReceiveVisibility_WhenResumeOrPauseThrows_DoesNotCrash()
    {
        WpfTestHost.RunSta(() =>
        {
            var tile = new TileModel { Id = "test_tile", TargetPath = "stub" };
            using var widget = new ThrowingLifecycleWidget(tile);

            // Should not throw
            widget.Receive(new HubVisibilityChangedMessage(true));
            widget.Receive(new HubVisibilityChangedMessage(false));

            Assert.True(widget.ResumeCalled);
            Assert.True(widget.PauseCalled);
        });
    }

    public sealed partial class ThrowingLifecycleWidget : WidgetViewModelBase
    {
        public bool ResumeCalled { get; private set; }
        public bool PauseCalled { get; private set; }

        public ThrowingLifecycleWidget(TileModel model) : base(model) { }

        public override IReadOnlyList<WidgetSize> AllowedSizes => new[] { WidgetSize.Medium };

        public override void Resume()
        {
            ResumeCalled = true;
            throw new Exception("Resume failure");
        }

        public override void Pause()
        {
            PauseCalled = true;
            throw new Exception("Pause failure");
        }
    }
}
