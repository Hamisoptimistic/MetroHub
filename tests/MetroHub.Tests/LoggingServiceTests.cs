using System;
using MetroHub.Core.Services;
using Serilog;
using Xunit;

namespace MetroHub.Tests;

public sealed class LoggingServiceTests
{
    [Fact]
    public void Initialize_ConfiguresLoggerWithoutThrowing()
    {
        // Act
        var exception = Record.Exception(() => LoggingService.Initialize());

        // Assert
        Assert.Null(exception);
        Assert.NotNull(Log.Logger);
    }

    [Fact]
    public void Initialize_Idempotent_CanBeCalledMultipleTimes()
    {
        // Act & Assert
        var exception = Record.Exception(() =>
        {
            LoggingService.Initialize();
            LoggingService.Initialize();
        });

        Assert.Null(exception);
    }

    [Fact]
    public void Shutdown_CanBeCalledWithoutThrowing()
    {
        // Arrange
        LoggingService.Initialize();

        // Act & Assert
        var exception = Record.Exception(() => LoggingService.Shutdown());
        Assert.Null(exception);
    }

    [Fact]
    public void HiddenDiagnosticsLogger_OperationsExecuteSafely()
    {
        // Arrange
        LoggingService.Initialize();

        // Act & Assert
        var exTransition = Record.Exception(() => HiddenDiagnosticsLogger.LogTransition(true));
        var exHiddenEvent = Record.Exception(() => HiddenDiagnosticsLogger.LogHiddenEvent("UnitTest", "TestEvent", "Sample detail"));
        var exLog = Record.Exception(() => HiddenDiagnosticsLogger.Log("Direct test diagnostic line"));

        Assert.Null(exTransition);
        Assert.Null(exHiddenEvent);
        Assert.Null(exLog);
    }

    [Fact]
    public void HiddenDiagnosticsLogger_Disabled_DoesNotThrow()
    {
        // Arrange
        bool previous = HiddenDiagnosticsLogger.IsEnabled;
        try
        {
            HiddenDiagnosticsLogger.IsEnabled = false;

            // Act & Assert
            var exTransition = Record.Exception(() => HiddenDiagnosticsLogger.LogTransition(false));
            var exLog = Record.Exception(() => HiddenDiagnosticsLogger.Log("Ignored line"));

            Assert.Null(exTransition);
            Assert.Null(exLog);
        }
        finally
        {
            HiddenDiagnosticsLogger.IsEnabled = previous;
        }
    }
}
