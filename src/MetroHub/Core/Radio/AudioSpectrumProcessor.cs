using System;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// High-performance, zero-allocation audio spectrum processor for MetroHub.
/// Samples real-time 512-point FFT data directly from the active Un4seen BASS audio stream,
/// groups frequency bins into 3 logarithmic bands (Bass, Mid, Treble), and applies an
/// asymmetrical Attack-Decay Exponential Moving Average (EMA) smoothing envelope filter.
/// </summary>
public sealed class AudioSpectrumProcessor
{
    // BASS FFT512 returns 256 frequency bins (N/2)
    public const int FftBinCount = 256;

    // Perceptual frequency bin boundaries
    // At 44.1 kHz, bin bandwidth ~ 86.13 Hz (bin 0 = DC offset)
    public const int BassStartBin = 1;
    public const int BassEndBin = 6;      // ~86 Hz to ~516 Hz

    public const int MidStartBin = 7;
    public const int MidEndBin = 68;     // ~600 Hz to ~5.8 kHz

    public const int TrebleStartBin = 69;
    public const int TrebleEndBin = 255;  // ~5.9 kHz to ~22 kHz

    // Visual gain scalars to map acoustic FFT RMS to vibrant 0.0 - 1.0 luminescence
    public const float BassGain = 2.8f;
    public const float MidGain = 4.2f;
    public const float TrebleGain = 6.5f;

    // Asymmetrical EMA envelope filter coefficients
    // Fast attack (~16ms) snaps to percussive transients; smooth decay (~200ms) produces liquid melt
    public const float AttackAlpha = 0.65f;
    public const float DecayAlpha = 0.08f;
    private const float MinThreshold = 0.001f;

    private readonly object _gate = new();
    private readonly float[] _fftBuffer = new float[512];

    private float _bass;
    private float _mid;
    private float _treble;

    /// <summary>Current smoothed sub-bass and bass energy [0.0 - 1.0].</summary>
    public float Bass
    {
        get { lock (_gate) return _bass; }
    }

    /// <summary>Current smoothed mid-range vocal/instrument energy [0.0 - 1.0].</summary>
    public float Mid
    {
        get { lock (_gate) return _mid; }
    }

    /// <summary>Current smoothed treble and high shimmer energy [0.0 - 1.0].</summary>
    public float Treble
    {
        get { lock (_gate) return _treble; }
    }

    /// <summary>
    /// Processes a pre-computed or mock FFT magnitude buffer (for testing and simulation).
    /// </summary>
    /// <param name="fftBins">Span containing frequency bin magnitudes.</param>
    /// <param name="bass">Output smoothed bass level.</param>
    /// <param name="mid">Output smoothed mid level.</param>
    /// <param name="treble">Output smoothed treble level.</param>
    public void ProcessFft(ReadOnlySpan<float> fftBins, out float bass, out float mid, out float treble)
    {
        float targetBass = CalculateBandRms(fftBins, BassStartBin, BassEndBin, BassGain);
        float targetMid = CalculateBandRms(fftBins, MidStartBin, MidEndBin, MidGain);
        float targetTreble = CalculateBandRms(fftBins, TrebleStartBin, TrebleEndBin, TrebleGain);

        lock (_gate)
        {
            _bass = Smooth(_bass, targetBass, AttackAlpha, DecayAlpha);
            _mid = Smooth(_mid, targetMid, AttackAlpha, DecayAlpha);
            _treble = Smooth(_treble, targetTreble, AttackAlpha, DecayAlpha);

            bass = _bass;
            mid = _mid;
            treble = _treble;
        }
    }

    /// <summary>
    /// Samples real-time 512-point FFT directly from a native BASS stream channel and updates smoothed levels.
    /// </summary>
    /// <param name="channelHandle">Native BASS channel handle.</param>
    /// <param name="bass">Output smoothed bass level.</param>
    /// <param name="mid">Output smoothed mid level.</param>
    /// <param name="treble">Output smoothed treble level.</param>
    /// <returns>True if live audio data was read; false if the stream was idle or ended.</returns>
    public bool ProcessChannel(int channelHandle, out float bass, out float mid, out float treble)
    {
        if (channelHandle == 0)
        {
            DecayToZero(out bass, out mid, out treble);
            return false;
        }

        int bytesRead;
        lock (_gate)
        {
            bytesRead = ManagedBass.Bass.ChannelGetData(channelHandle, _fftBuffer, (int)DataFlags.FFT512);
        }

        if (bytesRead <= 0)
        {
            DecayToZero(out bass, out mid, out treble);
            return false;
        }

        ProcessFft(_fftBuffer.AsSpan(0, FftBinCount), out bass, out mid, out treble);
        return true;
    }

    /// <summary>
    /// Smoothly decays the current values toward zero (e.g. when audio stalls or stops).
    /// </summary>
    public void DecayToZero(out float bass, out float mid, out float treble)
    {
        lock (_gate)
        {
            _bass = Smooth(_bass, 0f, AttackAlpha, DecayAlpha);
            _mid = Smooth(_mid, 0f, AttackAlpha, DecayAlpha);
            _treble = Smooth(_treble, 0f, AttackAlpha, DecayAlpha);

            bass = _bass;
            mid = _mid;
            treble = _treble;
        }
    }

    /// <summary>
    /// Immediately resets all smoothed frequency levels to zero.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _bass = 0f;
            _mid = 0f;
            _treble = 0f;
        }
    }

    private static float CalculateBandRms(ReadOnlySpan<float> bins, int startBin, int endBin, float gain)
    {
        if (bins.IsEmpty || startBin >= bins.Length) return 0f;

        int effectiveEnd = Math.Min(endBin, bins.Length - 1);
        if (startBin > effectiveEnd) return 0f;

        float sumSquares = 0f;
        int count = effectiveEnd - startBin + 1;

        for (int i = startBin; i <= effectiveEnd; i++)
        {
            float val = bins[i];
            sumSquares += val * val;
        }

        float rms = MathF.Sqrt(sumSquares / count);
        float scaled = rms * gain;
        return Math.Clamp(scaled, 0f, 1f);
    }

    private static float Smooth(float current, float target, float attackAlpha, float decayAlpha)
    {
        float alpha = target > current ? attackAlpha : decayAlpha;
        float next = current + alpha * (target - current);

        if (next < MinThreshold)
        {
            return 0f;
        }

        return Math.Clamp(next, 0f, 1f);
    }
}
