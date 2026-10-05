using System;
using ManagedBass;

namespace MetroHub.Core.Radio;

/// <summary>
/// High-performance, zero-allocation audio spectrum processor for MetroHub.
/// Samples real-time 2048-point FFT data (1024 frequency bins) directly from the active Un4seen BASS audio stream,
/// computes frequency bands based on the stream's native sample rate, maps acoustic RMS to decibels (-60 dB to -10 dB),
/// and outputs both an unsmoothed instantaneous <see cref="Kick"/> transient for beat detection
/// and smoothed <see cref="Bass"/>, <see cref="Mid"/>, and <see cref="Treble"/> envelopes for fluid visual swells.
/// </summary>
public sealed class AudioSpectrumProcessor
{
    // BASS FFT2048 returns 1024 frequency bins (N/2)
    public const int FftBinCount = 1024;

    // Nominal 44.1 kHz frequency bin boundaries for reference and unit tests:
    // At 44.1 kHz, bin bandwidth is ~21.533 Hz (bin 0 = DC offset)
    public const int KickStartBin = 2;    // ~43 Hz
    public const int KickEndBin = 4;      // ~86 Hz

    public const int BassStartBin = 2;    // ~43 Hz
    public const int BassEndBin = 12;     // ~258 Hz

    public const int MidStartBin = 13;    // ~280 Hz
    public const int MidEndBin = 232;     // ~5000 Hz

    public const int TrebleStartBin = 233;// ~5017 Hz
    public const int TrebleEndBin = 743;  // ~16000 Hz

    // Asymmetrical EMA envelope filter coefficients for continuous swells
    public const float AttackAlpha = 0.65f;
    public const float DecayAlpha = 0.08f;
    private const float MinThreshold = 0.001f;

    private readonly object _gate = new();
    private readonly float[] _fftBuffer = new float[FftBinCount];

    private float _binHz = 44100f / 2048f;
    private float _kick;
    private float _bass;
    private float _mid;
    private float _treble;

    /// <summary>Current frequency bin bandwidth in Hz (SampleRate / 2048).</summary>
    public float BinHz => _binHz;

    /// <summary>Instantaneous, unsmoothed kick drum transient energy [0.0 - 1.0] (35 - 120 Hz, dB mapped).</summary>
    public float Kick
    {
        get { lock (_gate) return _kick; }
    }

    /// <summary>Current smoothed sub-bass and bass energy [0.0 - 1.0] (35 - 250 Hz).</summary>
    public float Bass
    {
        get { lock (_gate) return _bass; }
    }

    /// <summary>Current smoothed mid-range vocal/instrument energy [0.0 - 1.0] (250 Hz - 5 kHz).</summary>
    public float Mid
    {
        get { lock (_gate) return _mid; }
    }

    /// <summary>Current smoothed treble and high shimmer energy [0.0 - 1.0] (5 kHz - 16 kHz).</summary>
    public float Treble
    {
        get { lock (_gate) return _treble; }
    }

    /// <summary>
    /// Updates the sample rate used to calculate frequency-to-bin mappings.
    /// </summary>
    public void SetSampleRate(int sampleRate)
    {
        if (sampleRate > 0)
        {
            _binHz = (float)sampleRate / 2048f;
        }
    }

    /// <summary>
    /// Computes the bin index for a given frequency in Hz.
    /// </summary>
    public int Bin(float hz) => Math.Max(1, (int)MathF.Round(hz / _binHz));

    /// <summary>
    /// Maps acoustic RMS magnitude to a normalized 0.0 - 1.0 range using logarithmic decibels (-60 dB to -10 dB).
    /// </summary>
    public static float ToUnitDb(float rms)
    {
        if (rms <= 1e-5f) return 0f;
        float db = 20f * MathF.Log10(rms);
        return Math.Clamp((db + 60f) / 50f, 0f, 1f);
    }

    /// <summary>
    /// Processes a pre-computed FFT magnitude buffer and outputs instantaneous kick and smoothed bands.
    /// </summary>
    public void ProcessFft(ReadOnlySpan<float> fftBins, out float kick, out float bass, out float mid, out float treble)
    {
        int kickStart = Bin(35f);
        int kickEnd = Bin(85f);
        int bassStart = Bin(35f);
        int bassEnd = Bin(250f);
        int midStart = bassEnd + 1;
        int midEnd = Bin(5000f);
        int trebleStart = midEnd + 1;
        int trebleEnd = Bin(16000f);

        float rawKick = CalculateBandRmsDb(fftBins, kickStart, kickEnd);
        float targetBass = CalculateBandRmsDb(fftBins, bassStart, bassEnd);
        float targetMid = CalculateBandRmsDb(fftBins, midStart, midEnd);
        float targetTreble = CalculateBandRmsDb(fftBins, trebleStart, trebleEnd);

        lock (_gate)
        {
            _kick = rawKick;
            _bass = Smooth(_bass, targetBass, AttackAlpha, DecayAlpha);
            _mid = Smooth(_mid, targetMid, AttackAlpha, DecayAlpha);
            _treble = Smooth(_treble, targetTreble, AttackAlpha, DecayAlpha);

            kick = _kick;
            bass = _bass;
            mid = _mid;
            treble = _treble;
        }
    }

    /// <summary>
    /// Backward-compatible overload for processing an FFT magnitude buffer without the kick out parameter.
    /// </summary>
    public void ProcessFft(ReadOnlySpan<float> fftBins, out float bass, out float mid, out float treble)
    {
        ProcessFft(fftBins, out _, out bass, out mid, out treble);
    }

    /// <summary>
    /// Samples real-time 2048-point FFT directly from a native BASS stream channel.
    /// </summary>
    public bool ProcessChannel(int channelHandle, out float kick, out float bass, out float mid, out float treble)
    {
        if (channelHandle == 0)
        {
            DecayToZero(out kick, out bass, out mid, out treble);
            return false;
        }

        if (ManagedBass.Bass.ChannelGetInfo(channelHandle, out var info) && info.Frequency > 0)
        {
            _binHz = (float)info.Frequency / 2048f;
        }

        int bytesRead;
        lock (_gate)
        {
            bytesRead = ManagedBass.Bass.ChannelGetData(channelHandle, _fftBuffer, (int)DataFlags.FFT2048);
        }

        if (bytesRead <= 0)
        {
            DecayToZero(out kick, out bass, out mid, out treble);
            return false;
        }

        ProcessFft(_fftBuffer.AsSpan(0, FftBinCount), out kick, out bass, out mid, out treble);
        return true;
    }

    /// <summary>
    /// Backward-compatible overload for sampling a native BASS channel without the kick out parameter.
    /// </summary>
    public bool ProcessChannel(int channelHandle, out float bass, out float mid, out float treble)
    {
        return ProcessChannel(channelHandle, out _, out bass, out mid, out treble);
    }

    /// <summary>
    /// Smoothly decays the current values toward zero (e.g. when audio stalls or stops).
    /// </summary>
    public void DecayToZero(out float kick, out float bass, out float mid, out float treble)
    {
        lock (_gate)
        {
            _kick = 0f;
            _bass = Smooth(_bass, 0f, AttackAlpha, DecayAlpha);
            _mid = Smooth(_mid, 0f, AttackAlpha, DecayAlpha);
            _treble = Smooth(_treble, 0f, AttackAlpha, DecayAlpha);

            kick = _kick;
            bass = _bass;
            mid = _mid;
            treble = _treble;
        }
    }

    /// <summary>
    /// Backward-compatible overload for decaying smoothed values toward zero.
    /// </summary>
    public void DecayToZero(out float bass, out float mid, out float treble)
    {
        DecayToZero(out _, out bass, out mid, out treble);
    }

    /// <summary>
    /// Immediately resets all frequency levels to zero.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _kick = 0f;
            _bass = 0f;
            _mid = 0f;
            _treble = 0f;
        }
    }

    private static float CalculateBandRmsDb(ReadOnlySpan<float> bins, int startBin, int endBin)
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
        return ToUnitDb(rms);
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
