using System;
using System.Threading;
using MetroHub;
using Xunit;

namespace MetroHub.Tests;

public sealed class SingleInstanceStartupTests
{
    [Fact]
    public void ProgramConstants_HaveExpectedLocalNamespacePrefixes()
    {
        Assert.Equal(@"Local\MetroHub_App_SingleInstance_Mutex", Program.MutexName);
        Assert.Equal(@"Local\MetroHub_App_Wake_Event", Program.EventName);
    }

    [Fact]
    public void ReleaseSingleInstanceMutex_WhenNotAcquired_DoesNotThrow()
    {
        // Safe idempotent release invocation
        var exception = Record.Exception(Program.ReleaseSingleInstanceMutex);
        Assert.Null(exception);
    }

    [Fact]
    public void WakeEvent_CanBeOpenedAndSignaled_UsingPattern()
    {
        string testEventName = @"Local\MetroHub_TestWake_" + Guid.NewGuid().ToString("N");
        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, testEventName);
        Assert.True(EventWaitHandle.TryOpenExisting(testEventName, out var openedHandle));
        using (openedHandle)
        {
            Assert.True(openedHandle.Set());
        }
        Assert.True(wakeEvent.WaitOne(2000));
    }

    [Fact]
    public void ReleaseSingleInstanceMutex_IsIdempotentAcrossMultipleInvocations()
    {
        var ex1 = Record.Exception(Program.ReleaseSingleInstanceMutex);
        var ex2 = Record.Exception(Program.ReleaseSingleInstanceMutex);
        Assert.Null(ex1);
        Assert.Null(ex2);
    }
}


