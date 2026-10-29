using System;
using System.Threading.Tasks;
using MetroHub.Core.Services;
using Xunit;

namespace MetroHub.Tests;

public sealed class SafeTests : IDisposable
{
    private readonly Action<Exception, string> _originalLogger;

    public SafeTests()
    {
        _originalLogger = Safe.Logger;
    }

    public void Dispose()
    {
        Safe.Logger = _originalLogger;
    }

    [Fact]
    public void Try_Action_ExecutesSuccessfully_ReturnsTrue()
    {
        bool executed = false;
        bool result = Safe.Try(() => executed = true, "test-context");

        Assert.True(result);
        Assert.True(executed);
    }

    [Fact]
    public void Try_Action_WhenThrows_LogsAndReturnsFalse()
    {
        Exception? capturedEx = null;
        string? capturedContext = null;
        Safe.Logger = (ex, ctx) =>
        {
            capturedEx = ex;
            capturedContext = ctx;
        };

        Action action = () => throw new InvalidOperationException("boom");
        bool result = Safe.Try(action, "action-context");

        Assert.False(result);
        Assert.NotNull(capturedEx);
        Assert.IsType<InvalidOperationException>(capturedEx);
        Assert.Equal("boom", capturedEx.Message);
        Assert.Equal("action-context", capturedContext);
    }

    [Fact]
    public void Try_Action_WhenNull_ReturnsFalse()
    {
        bool result = Safe.Try((Action)null!, "null-action");
        Assert.False(result);
    }

    [Fact]
    public void Try_Func_ExecutesSuccessfully_ReturnsValue()
    {
        int result = Safe.Try(() => 42, fallback: -1, "compute-value");
        Assert.Equal(42, result);
    }

    [Fact]
    public void Try_Func_WhenThrows_LogsAndReturnsFallback()
    {
        Exception? capturedEx = null;
        string? capturedContext = null;
        Safe.Logger = (ex, ctx) =>
        {
            capturedEx = ex;
            capturedContext = ctx;
        };

        string result = Safe.Try<string>(() => throw new FormatException("bad format"), fallback: "fallback-value", "parse-context");

        Assert.Equal("fallback-value", result);
        Assert.NotNull(capturedEx);
        Assert.IsType<FormatException>(capturedEx);
        Assert.Equal("parse-context", capturedContext);
    }

    [Fact]
    public void Try_Func_WhenNull_ReturnsFallback()
    {
        int result = Safe.Try((Func<int>)null!, fallback: 99, "null-func");
        Assert.Equal(99, result);
    }

    [Fact]
    public async Task TryAsync_Action_ExecutesSuccessfully_ReturnsTrue()
    {
        bool executed = false;
        bool result = await Safe.TryAsync(async () =>
        {
            await Task.Yield();
            executed = true;
        }, "async-action");

        Assert.True(result);
        Assert.True(executed);
    }

    [Fact]
    public async Task TryAsync_Action_WhenThrows_LogsAndReturnsFalse()
    {
        Exception? capturedEx = null;
        string? capturedContext = null;
        Safe.Logger = (ex, ctx) =>
        {
            capturedEx = ex;
            capturedContext = ctx;
        };

        Func<Task> actionAsync = async () =>
        {
            await Task.Yield();
            throw new ArgumentException("invalid argument");
        };

        bool result = await Safe.TryAsync(actionAsync, "async-throw");

        Assert.False(result);
        Assert.NotNull(capturedEx);
        Assert.IsType<ArgumentException>(capturedEx);
        Assert.Equal("async-throw", capturedContext);
    }

    [Fact]
    public async Task TryAsync_Action_WhenNull_ReturnsFalse()
    {
        bool result = await Safe.TryAsync((Func<Task>)null!, "null-async-action");
        Assert.False(result);
    }

    [Fact]
    public async Task TryAsync_Func_ExecutesSuccessfully_ReturnsValue()
    {
        int result = await Safe.TryAsync(async () =>
        {
            await Task.Yield();
            return 100;
        }, fallback: 0, "async-compute");

        Assert.Equal(100, result);
    }

    [Fact]
    public async Task TryAsync_Func_WhenThrows_LogsAndReturnsFallback()
    {
        Exception? capturedEx = null;
        string? capturedContext = null;
        Safe.Logger = (ex, ctx) =>
        {
            capturedEx = ex;
            capturedContext = ctx;
        };

        int result = await Safe.TryAsync<int>(async () =>
        {
            await Task.Yield();
            throw new TimeoutException("timed out");
        }, fallback: -1, "async-timeout");

        Assert.Equal(-1, result);
        Assert.NotNull(capturedEx);
        Assert.IsType<TimeoutException>(capturedEx);
        Assert.Equal("async-timeout", capturedContext);
    }

    [Fact]
    public async Task TryAsync_Func_WhenNull_ReturnsFallback()
    {
        int result = await Safe.TryAsync((Func<Task<int>>)null!, fallback: 55, "null-async-func");
        Assert.Equal(55, result);
    }

    [Fact]
    public void Try_WhenLoggerThrows_DoesNotCrashHost()
    {
        Safe.Logger = (ex, ctx) => throw new Exception("Logger internal failure");

        Action action = () => throw new InvalidOperationException();
        bool actionResult = Safe.Try(action, "logger-test");
        Assert.False(actionResult);

        int funcResult = Safe.Try<int>(() => throw new InvalidOperationException(), fallback: 7, "logger-test");
        Assert.Equal(7, funcResult);
    }
}
