using System;
using MetroHub.Core.Radio;
using Xunit;

namespace MetroHub.Tests;

public class AudioSpectrumProcessorTests
{
    [Fact]
    public void ProcessFft_ZeroInput_OutputsZeroLevels()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];

        processor.ProcessFft(buffer, out float bass, out float mid, out float treble);

        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_BassSpike_UpdatesBassWithHighValue_MidAndTrebleZero()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];

        // Populate bass bins (1 to 6)
        for (int i = AudioSpectrumProcessor.BassStartBin; i <= AudioSpectrumProcessor.BassEndBin; i++)
        {
            buffer[i] = 0.5f;
        }

        processor.ProcessFft(buffer, out float bass, out float mid, out float treble);

        Assert.True(bass > 0.3f, $"Bass should react strongly to bass frequencies. Actual: {bass}");
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_MidSpike_UpdatesMidWithHighValue_BassAndTrebleZero()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];

        // Populate mid bins (7 to 68)
        for (int i = AudioSpectrumProcessor.MidStartBin; i <= AudioSpectrumProcessor.MidEndBin; i++)
        {
            buffer[i] = 0.3f;
        }

        processor.ProcessFft(buffer, out float bass, out float mid, out float treble);

        Assert.Equal(0f, bass);
        Assert.True(mid > 0.3f, $"Mid should react strongly to mid frequencies. Actual: {mid}");
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_TrebleSpike_UpdatesTrebleWithHighValue_BassAndMidZero()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];

        // Populate treble bins (69 to 255)
        for (int i = AudioSpectrumProcessor.TrebleStartBin; i <= AudioSpectrumProcessor.TrebleEndBin; i++)
        {
            buffer[i] = 0.2f;
        }

        processor.ProcessFft(buffer, out float bass, out float mid, out float treble);

        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.True(treble > 0.3f, $"Treble should react strongly to high frequencies. Actual: {treble}");
    }

    [Fact]
    public void Attack_IsSignificantlyFasterThan_Decay()
    {
        var processor = new AudioSpectrumProcessor();
        var spikeBuffer = new float[256];
        var silenceBuffer = new float[256];

        for (int i = AudioSpectrumProcessor.BassStartBin; i <= AudioSpectrumProcessor.BassEndBin; i++)
        {
            spikeBuffer[i] = 0.6f;
        }

        // 1. Attack frame: rises rapidly with AttackAlpha (~0.65)
        processor.ProcessFft(spikeBuffer, out float attackBass, out _, out _);
        Assert.True(attackBass > 0.5f, $"Attack should jump to > 0.5 on first beat frame. Actual: {attackBass}");

        // 2. Decay frame: decays gently with DecayAlpha (~0.08)
        processor.ProcessFft(silenceBuffer, out float decayBass, out _, out _);
        float decayDrop = attackBass - decayBass;

        Assert.True(decayBass > 0.4f, $"Decay should retain warmth and not drop abruptly to zero. Actual: {decayBass}");
        Assert.True(decayDrop < attackBass * 0.2f, "Decay drop per frame should be gradual (~8%)");
    }

    [Fact]
    public void Values_AreClamped_BetweenZeroAndOne()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];

        // Extreme signal that would exceed 1.0 without clamping
        Array.Fill(buffer, 10.0f);

        // Apply multiple frames to allow attack to saturate
        for (int frame = 0; frame < 5; frame++)
        {
            processor.ProcessFft(buffer, out float bass, out float mid, out float treble);
            Assert.InRange(bass, 0f, 1f);
            Assert.InRange(mid, 0f, 1f);
            Assert.InRange(treble, 0f, 1f);
        }
    }

    [Fact]
    public void Reset_ImmediatelyClearsAllLevels()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];
        Array.Fill(buffer, 0.5f);

        processor.ProcessFft(buffer, out _, out _, out _);
        Assert.True(processor.Bass > 0f);

        processor.Reset();

        Assert.Equal(0f, processor.Bass);
        Assert.Equal(0f, processor.Mid);
        Assert.Equal(0f, processor.Treble);
    }

    [Fact]
    public void ProcessChannel_WithZeroHandle_ReturnsFalseAndDecaysLevels()
    {
        var processor = new AudioSpectrumProcessor();

        bool active = processor.ProcessChannel(0, out float bass, out float mid, out float treble);

        Assert.False(active);
        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void RadioAudioService_GetSpectrumLevels_WhenPaused_ReturnsFalseAndDecaysToZero()
    {
        var service = RadioAudioService.Instance;
        service.Pause();

        bool active = service.GetSpectrumLevels(out float bass, out float mid, out float treble);

        Assert.False(active);
        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_ZeroAllocations_PerFrame()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];
        Array.Fill(buffer, 0.25f);

        // Warmup JIT
        for (int i = 0; i < 50; i++)
        {
            processor.ProcessFft(buffer, out _, out _, out _);
        }

        long bytesBefore = GC.GetAllocatedBytesForCurrentThread();

        // Run 1,000 frames (~16 seconds of continuous 60fps streaming)
        for (int i = 0; i < 1000; i++)
        {
            processor.ProcessFft(buffer, out _, out _, out _);
        }

        long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
        long allocated = bytesAfter - bytesBefore;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ProcessFft_ExecutionTime_WellUnderBudget()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[256];
        Array.Fill(buffer, 0.35f);

        // Warmup JIT
        for (int i = 0; i < 100; i++)
        {
            processor.ProcessFft(buffer, out _, out _, out _);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        const int iterations = 10000;
        for (int i = 0; i < iterations; i++)
        {
            processor.ProcessFft(buffer, out _, out _, out _);
        }
        sw.Stop();

        double msPerFrame = sw.Elapsed.TotalMilliseconds / iterations;

        // Must execute in < 0.05ms (50 microseconds) per frame
        Assert.True(msPerFrame < 0.05, $"ProcessFft must take < 0.05ms per frame. Actual: {msPerFrame:F5} ms");
    }
}
