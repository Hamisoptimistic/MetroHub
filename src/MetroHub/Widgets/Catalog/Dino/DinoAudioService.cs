using System;
using System.Diagnostics;
using System.IO;
using System.Media;

namespace MetroHub.Widgets.Catalog.Dino;

/// <summary>
/// Audio service for the Chrome Dino Game widget (Implementation Plan v2, Section 8).
/// Synthesizes 16-bit PCM square-wave sounds in-memory at 22,050 Hz with zero external asset files.
/// Features a single-voice priority rule, phase-accumulated waveform generation, and silent fail-safe error handling.
/// </summary>
public sealed class DinoAudioService : IDisposable
{
    private const int SampleRate = 22050;
    private const double Amplitude = 0.25 * 32767.0; // 25% of full-scale 16-bit range to prevent harshness
    private const double FadeDurationSeconds = 0.003; // 3ms linear anti-click fade-in/fade-out

    private SoundPlayer? _jumpPlayer;
    private SoundPlayer? _milestonePlayer;
    private SoundPlayer? _gameOverPlayer;

    private MemoryStream? _jumpStream;
    private MemoryStream? _milestoneStream;
    private MemoryStream? _gameOverStream;

    private bool _audioAvailable = true;
    private long _protectedUntilTimestamp = 0; // Stopwatch ticks
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public bool IsMuted { get; set; } = false;

    public DinoAudioService(bool isMuted = false)
    {
        IsMuted = isMuted;
        InitializeAudioBuffers();
    }

    private void InitializeAudioBuffers()
    {
        try
        {
            // 1. Jump Sound: 600 Hz -> 800 Hz rising chirp (35 ms)
            byte[] jumpBytes = SynthesizeChirp(600.0, 800.0, 0.035);
            _jumpStream = new MemoryStream(jumpBytes);
            _jumpPlayer = new SoundPlayer(_jumpStream);
            _jumpPlayer.Load();

            // 2. Milestone Sound: 600 Hz (60 ms), then 800 Hz (80 ms)
            byte[] milestoneBytes = SynthesizeTwoTone(600.0, 0.060, 800.0, 0.080);
            _milestoneStream = new MemoryStream(milestoneBytes);
            _milestonePlayer = new SoundPlayer(_milestoneStream);
            _milestonePlayer.Load();

            // 3. Game Over Sound: 250 Hz -> 150 Hz descending thud (110 ms)
            byte[] gameOverBytes = SynthesizeChirp(250.0, 150.0, 0.110);
            _gameOverStream = new MemoryStream(gameOverBytes);
            _gameOverPlayer = new SoundPlayer(_gameOverStream);
            _gameOverPlayer.Load();
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    public void PlayJump()
    {
        if (IsMuted || !_audioAvailable) return;

        // Priority rule: Jump is dropped if a milestone or game-over sound is currently protected
        if (_stopwatch.ElapsedTicks < _protectedUntilTimestamp)
        {
            return;
        }

        try
        {
            _jumpStream!.Position = 0;
            _jumpPlayer?.Play();
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    public void PlayMilestone()
    {
        if (IsMuted || !_audioAvailable) return;

        try
        {
            // Protect milestone sound for 140ms
            _protectedUntilTimestamp = _stopwatch.ElapsedTicks + (long)(0.140 * Stopwatch.Frequency);
            _milestoneStream!.Position = 0;
            _milestonePlayer?.Play();
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    public void PlayGameOver()
    {
        if (IsMuted || !_audioAvailable) return;

        try
        {
            // Protect game over sound for 110ms
            _protectedUntilTimestamp = _stopwatch.ElapsedTicks + (long)(0.110 * Stopwatch.Frequency);
            _gameOverStream!.Position = 0;
            _gameOverPlayer?.Play();
        }
        catch
        {
            _audioAvailable = false;
        }
    }

    private static byte[] SynthesizeChirp(double startFreq, double endFreq, double durationSec)
    {
        int totalSamples = (int)(SampleRate * durationSec);
        int fadeSamples = Math.Min((int)(SampleRate * FadeDurationSeconds), totalSamples / 2);
        short[] samples = new short[totalSamples];

        double phase = 0.0;
        for (int i = 0; i < totalSamples; i++)
        {
            double t = (double)i / totalSamples;
            double freq = startFreq + (endFreq - startFreq) * t;
            phase += 2.0 * Math.PI * freq / SampleRate;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;

            double sampleVal = (phase < Math.PI) ? 1.0 : -1.0;

            // Apply linear fade-in and fade-out to prevent audio pops/clicks
            double envelope = 1.0;
            if (i < fadeSamples)
            {
                envelope = (double)i / fadeSamples;
            }
            else if (i > totalSamples - fadeSamples)
            {
                envelope = (double)(totalSamples - i) / fadeSamples;
            }

            samples[i] = (short)(sampleVal * Amplitude * envelope);
        }

        return CreateWavBytes(samples);
    }

    private static byte[] SynthesizeTwoTone(double freq1, double dur1, double freq2, double dur2)
    {
        int samples1 = (int)(SampleRate * dur1);
        int samples2 = (int)(SampleRate * dur2);
        int totalSamples = samples1 + samples2;
        int fadeSamples = Math.Min((int)(SampleRate * FadeDurationSeconds), samples1 / 2);
        short[] samples = new short[totalSamples];

        // Tone 1
        double phase = 0.0;
        for (int i = 0; i < samples1; i++)
        {
            phase += 2.0 * Math.PI * freq1 / SampleRate;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;

            double sampleVal = (phase < Math.PI) ? 1.0 : -1.0;
            double envelope = 1.0;
            if (i < fadeSamples) envelope = (double)i / fadeSamples;
            else if (i > samples1 - fadeSamples) envelope = (double)(samples1 - i) / fadeSamples;

            samples[i] = (short)(sampleVal * Amplitude * envelope);
        }

        // Tone 2
        phase = 0.0;
        for (int i = 0; i < samples2; i++)
        {
            phase += 2.0 * Math.PI * freq2 / SampleRate;
            if (phase > 2.0 * Math.PI) phase -= 2.0 * Math.PI;

            double sampleVal = (phase < Math.PI) ? 1.0 : -1.0;
            double envelope = 1.0;
            if (i < fadeSamples) envelope = (double)i / fadeSamples;
            else if (i > samples2 - fadeSamples) envelope = (double)(samples2 - i) / fadeSamples;

            samples[samples1 + i] = (short)(sampleVal * Amplitude * envelope);
        }

        return CreateWavBytes(samples);
    }

    private static byte[] CreateWavBytes(short[] samples)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        int subChunk2Size = samples.Length * 2; // 16-bit mono = 2 bytes per sample
        int chunkSize = 36 + subChunk2Size;

        // RIFF header
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(chunkSize);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });

        // fmt subchunk
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16); // SubChunk1Size (16 for PCM)
        bw.Write((short)1); // AudioFormat (1 for PCM)
        bw.Write((short)1); // NumChannels (1 for Mono)
        bw.Write(SampleRate);
        bw.Write(SampleRate * 2); // ByteRate (SampleRate * NumChannels * BitsPerSample/8)
        bw.Write((short)2); // BlockAlign (NumChannels * BitsPerSample/8)
        bw.Write((short)16); // BitsPerSample

        // data subchunk
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(subChunk2Size);

        for (int i = 0; i < samples.Length; i++)
        {
            bw.Write(samples[i]);
        }

        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _jumpPlayer?.Dispose();
        _milestonePlayer?.Dispose();
        _gameOverPlayer?.Dispose();

        _jumpStream?.Dispose();
        _milestoneStream?.Dispose();
        _gameOverStream?.Dispose();
    }
}
