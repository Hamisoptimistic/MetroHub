using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Offline-safe tests for <see cref="ChunkedAudioBuffer"/>. The contract that matters:
/// the BASS file reader blocks for late data, returns 0 only at a genuine end of stream,
/// and consumes bytes in order across segment boundaries.
/// </summary>
public sealed class ChunkedAudioBufferTests
{
    static ChunkedAudioBufferTests() => JukeboxLog.Enabled = false;

    [Fact]
    public void FileRead_Returns_Appended_Bytes_In_Order_Across_Segments()
    {
        using var buffer = new ChunkedAudioBuffer();
        byte[] data = new byte[300 * 1024]; // larger than one 256 KB segment
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 251);
        }

        buffer.Append(data, 0, data.Length);
        buffer.Complete();

        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        try
        {
            int n = buffer.FileRead(ptr, data.Length, 1000);
            Assert.Equal(data.Length, n);

            byte[] result = new byte[n];
            Marshal.Copy(ptr, result, 0, n);
            Assert.Equal(data, result);

            Assert.Equal(0, buffer.FileRead(ptr, 16, 1000)); // genuine EOF
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public async Task FileRead_Blocks_Until_Data_Arrives()
    {
        using var buffer = new ChunkedAudioBuffer();
        IntPtr ptr = Marshal.AllocHGlobal(16);
        try
        {
            Task<int> readTask = Task.Run(() => buffer.FileRead(ptr, 16, 5000));
            await Task.Delay(100);
            Assert.False(readTask.IsCompleted);

            buffer.Append(new byte[] { 1, 2, 3, 4 }, 0, 4);
            int n = await readTask;

            Assert.Equal(4, n);
            byte[] result = new byte[4];
            Marshal.Copy(ptr, result, 0, 4);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, result);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void FileRead_Returns_Zero_On_Stall_Timeout()
    {
        using var buffer = new ChunkedAudioBuffer();
        IntPtr ptr = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(0, buffer.FileRead(ptr, 8, 100));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void CompleteFaulted_Ends_Stream_And_Exposes_Fault()
    {
        using var buffer = new ChunkedAudioBuffer();
        buffer.Append(new byte[] { 9, 9 }, 0, 2);
        buffer.CompleteFaulted(new IOException("boom"));

        IntPtr ptr = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(2, buffer.FileRead(ptr, 8, 1000));
            Assert.Equal(0, buffer.FileRead(ptr, 8, 1000)); // EOF even though faulted
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }

        Assert.True(buffer.IsCompleted);
        Assert.NotNull(buffer.Fault);
    }

    [Fact]
    public void FileSeek_Moves_The_Read_Position()
    {
        using var buffer = new ChunkedAudioBuffer();
        buffer.Append(new byte[] { 10, 20, 30, 40 }, 0, 4);
        Assert.True(buffer.FileSeek(2));

        IntPtr ptr = Marshal.AllocHGlobal(8);
        try
        {
            Assert.Equal(2, buffer.FileRead(ptr, 8, 1000));
            byte[] result = new byte[2];
            Marshal.Copy(ptr, result, 0, 2);
            Assert.Equal(new byte[] { 30, 40 }, result);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void BufferedSeconds_Uses_The_Bitrate()
    {
        using var buffer = new ChunkedAudioBuffer(bytesPerSecond: 16000);
        buffer.Append(new byte[32000], 0, 32000);
        Assert.Equal(2.0, buffer.BufferedSeconds, 3);
        Assert.Equal(32000, buffer.BufferedBytes);
        Assert.Equal(32000, buffer.AvailableBytes);
    }
}
