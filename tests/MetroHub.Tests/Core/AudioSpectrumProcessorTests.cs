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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];

        processor.ProcessFft(buffer, out float kick, out float bass, out float mid, out float treble);

        Assert.Equal(0f, kick);
        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_KickSpike_OutputsInstantaneousKick_WithoutSmoothingDelay()
    {
        var processor = new AudioSpectrumProcessor();
        var spikeBuffer = new float[AudioSpectrumProcessor.FftBinCount];
        var silenceBuffer = new float[AudioSpectrumProcessor.FftBinCount];

        // Populate kick bins (2 to 6: ~43 Hz to ~129 Hz)
        for (int i = AudioSpectrumProcessor.KickStartBin; i <= AudioSpectrumProcessor.KickEndBin; i++)
        {
            spikeBuffer[i] = 0.5f;
        }

        // Frame 1: Kick should be instant 1.0 (no attack lag) while Bass begins its smoothed EMA rise
        processor.ProcessFft(spikeBuffer, out float kick, out float bass, out float mid, out float treble);

        Assert.True(kick > 0.9f, $"Instantaneous Kick should immediately register full hit. Actual: {kick}");
        Assert.True(bass > 0.5f && bass < 0.8f, $"Bass should be smoothed with AttackAlpha (~0.65). Actual: {bass}");
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);

        // Frame 2: On silence, Kick drops instantly to 0 with zero lag, while Bass retains warm decay
        processor.ProcessFft(silenceBuffer, out float nextKick, out float nextBass, out _, out _);

        Assert.Equal(0f, nextKick);
        Assert.True(nextBass > 0.4f, $"Bass should decay smoothly (~0.08 alpha). Actual: {nextBass}");
    }

    [Fact]
    public void ProcessFft_BassSpike_UpdatesBassWithHighValue_MidAndTrebleZero()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];

        // Populate bass bins (2 to 12)
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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];

        // Populate mid bins (13 to 232)
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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];

        // Populate treble bins (233 to 743)
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
        var spikeBuffer = new float[AudioSpectrumProcessor.FftBinCount];
        var silenceBuffer = new float[AudioSpectrumProcessor.FftBinCount];

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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];

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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];
        Array.Fill(buffer, 0.5f);

        processor.ProcessFft(buffer, out _, out _, out _);
        Assert.True(processor.Bass > 0f);

        processor.Reset();

        Assert.Equal(0f, processor.Kick);
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

        bool active = service.GetSpectrumLevels(out float kick, out float bass, out float mid, out float treble);

        Assert.False(active);
        Assert.Equal(0f, kick);
        Assert.Equal(0f, bass);
        Assert.Equal(0f, mid);
        Assert.Equal(0f, treble);
    }

    [Fact]
    public void ProcessFft_ZeroAllocations_PerFrame()
    {
        var processor = new AudioSpectrumProcessor();
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];
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
        var buffer = new float[AudioSpectrumProcessor.FftBinCount];
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

    [Fact]
    public void ProcessChannel_LiveBassStream_Diagnostic()
    {
        _ = RadioAudioService.Instance; // Ensures Bass.Init
        // Create a push stream generating a 60 Hz sine wave (kick range)
        int stream = ManagedBass.Bass.CreateStream(44100, 2, ManagedBass.BassFlags.Float | ManagedBass.BassFlags.Decode, ManagedBass.StreamProcedureType.Push);
        Assert.NotEqual(0, stream);

        try
        {
            // Generate 44100 samples of 60 Hz sine wave
            float[] audioData = new float[2048 * 2];
            for (int i = 0; i < 2048; i++)
            {
                float sample = MathF.Sin(2f * MathF.PI * 60f * (i / 44100f)) * 0.8f;
                audioData[i * 2] = sample;
                audioData[i * 2 + 1] = sample;
            }

            int pushed = ManagedBass.Bass.StreamPutData(stream, audioData, audioData.Length * 4);
            Assert.True(pushed > 0, $"StreamPutData failed: {ManagedBass.Bass.LastError}");

            var processor = new AudioSpectrumProcessor();
            bool result = processor.ProcessChannel(stream, out float kick, out float bass, out float mid, out float treble);

            Assert.True(result, $"ProcessChannel returned false. BASS Error: {ManagedBass.Bass.LastError}");
            Assert.True(kick > 0.05f, $"Kick was 0 or too low! kick={kick}, bass={bass}, mid={mid}, treble={treble}");
        }
        finally
        {
            ManagedBass.Bass.StreamFree(stream);
        }
    }
}
