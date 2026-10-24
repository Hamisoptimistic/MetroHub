using System;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Turns the radio's log-spaced spectrum into musical intent: per-band automatic
/// gain, spectral-flux onset detection, tempo tracking and a set of drifting
/// wave emitters. Each emitter owns a frequency band group and a fixed spatial
/// home, so bass shakes wide, slow regions while treble adds fine, fast detail.
/// </summary>
public sealed class ReactiveDynamics
{
    public const int BandCount = 16;
    public const int EmitterCount = 7;

    private const float TwoPi = MathF.PI * 2f;

    /// <summary>Acceleration per unit of band energy. Sized so the field settles near -1..1.</summary>
    private const float DriveGain = 0.011f;

    // Display frequencies, in Hz. Combined with the solver's wave speed these give
    // wavelengths from ~120 px (sub) down to ~25 px (air).
    private static readonly float[] EmitterFrequency = [1.45f, 2.05f, 2.85f, 3.9f, 5.2f, 6.7f, 8.5f];
    private static readonly float[] EmitterSigma = [3.6f, 3.1f, 2.7f, 2.3f, 1.9f, 1.55f, 1.25f];
    private static readonly float[] EmitterHomeX = [0.17f, 0.35f, 0.54f, 0.79f, 0.48f, 0.11f, 0.87f];
    private static readonly float[] EmitterHomeY = [0.71f, 0.26f, 0.79f, 0.33f, 0.52f, 0.37f, 0.65f];

    private readonly float[] _envelope = new float[BandCount];
    private readonly float[] _peak = new float[BandCount];
    private readonly float[] _previous = new float[BandCount];
    private readonly float[] _flux = new float[BandCount];
    private readonly float[] _lastOnset = new float[BandCount];

    private readonly float[] _emitterLevel = new float[EmitterCount];
    private readonly float[] _emitterTransient = new float[EmitterCount];
    private readonly float[] _emitterPhase = new float[EmitterCount];

    private readonly float[] _beatTimes = new float[8];
    private readonly float[] _intervals = new float[8];
    private int _beatCount;

    private float _time;
    private float _pulse;
    private float _bass;
    private float _mid;
    private float _treble;
    private float _energy;
    private float _silenceSeconds;
    private float _idle;
    private float _beatPeriod = 0.5f;
    private float _beatPhase;
    private float _breathPhase;
    private bool _hasTempo;

    /// <summary>Smoothed musical parameters from the most recent update.</summary>
    public ReactiveState Current { get; private set; } = ReactiveState.Rest;

    /// <summary>
    /// Consumes one frame of spectrum bands (0..1), drives the wave field and
    /// advances the solver. Zero allocations.
    /// </summary>
    public ReactiveState Update(ReadOnlySpan<float> bands, float deltaSeconds, WaveField field)
    {
        ArgumentNullException.ThrowIfNull(field);

        float dt = Math.Clamp(deltaSeconds, 0.001f, 0.1f);
        _time += dt;

        float attackRate = 1f - MathF.Exp(-dt / 0.018f);
        float decayFactor = MathF.Exp(-dt / 0.24f);
        float transientDecay = MathF.Exp(-dt / 0.10f);

        Array.Clear(_emitterLevel);

        for (int band = 0; band < BandCount; band++)
        {
            float value = band < bands.Length ? Math.Clamp(bands[band], 0f, 1f) : 0f;

            // Slow automatic gain keeps quiet stations as lively as loud ones.
            _peak[band] = Math.Max(value, _peak[band] * 0.9995f);
            float normalized = value / Math.Max(0.10f, _peak[band]);

            float delta = normalized - _previous[band];
            _previous[band] = normalized;

            bool isOnset = delta > 0.085f
                && normalized > 0.30f
                && delta > (_flux[band] * 1.8f) + 0.015f
                && (_time - _lastOnset[band]) >= 0.085f;

            _flux[band] = (_flux[band] * 0.82f) + (Math.Max(0f, delta) * 0.18f);

            _envelope[band] = normalized > _envelope[band]
                ? _envelope[band] + ((normalized - _envelope[band]) * attackRate)
                : _envelope[band] * decayFactor;

            int emitter = EmitterOfBand(band);
            if (_envelope[band] > _emitterLevel[emitter])
            {
                _emitterLevel[emitter] = _envelope[band];
            }

            if (!isOnset)
            {
                continue;
            }

            _lastOnset[band] = _time;
            _emitterTransient[emitter] = Math.Min(1.2f, _emitterTransient[emitter] + 0.55f + delta);

            if (band <= 4)
            {
                _pulse = Math.Max(_pulse, Math.Clamp(delta * 3f, 0f, 1f));
                RegisterBeat();
            }
        }

        _pulse *= MathF.Exp(-dt / 0.15f);

        _bass = Math.Max(_envelope[0], Math.Max(_envelope[1], Math.Max(_envelope[2], _envelope[3])));
        _mid = Math.Max(_envelope[4], Math.Max(_envelope[5], Math.Max(_envelope[6], _envelope[7])));
        _treble = Math.Max(_envelope[11], Math.Max(_envelope[12], Math.Max(_envelope[13], _envelope[14])));
        _energy = Math.Clamp((0.5f * _bass) + (0.32f * _mid) + (0.18f * _treble), 0f, 1f);

        if (_energy < 0.045f)
        {
            _silenceSeconds += dt;
        }
        else if (_energy > 0.06f)
        {
            _silenceSeconds = 0f;
        }

        _idle = SmoothStep(0.8f, 2.6f, _silenceSeconds);

        AdvanceTempo(dt);

        float active = 1f - _idle;
        for (int emitter = 0; emitter < EmitterCount; emitter++)
        {
            _emitterTransient[emitter] *= transientDecay;

            float level = (_emitterLevel[emitter] * active)
                + (_emitterTransient[emitter] * 0.55f * active);

            // Idle drift: only the three slow emitters breathe, so the picture
            // keeps a calm swell instead of a busy flicker.
            float idleLevel = (0.15f + (0.08f * MathF.Sin((_time * 0.6f) + (emitter * 1.9f))))
                * _idle
                * (emitter < 3 ? 1f : 0.3f);

            float amplitude = level + idleLevel;
            if (amplitude > 0.0005f)
            {
                _emitterPhase[emitter] += TwoPi * EmitterFrequency[emitter] * dt;
                if (_emitterPhase[emitter] > TwoPi)
                {
                    _emitterPhase[emitter] -= TwoPi;
                }

                float positionX = EmitterHomeX[emitter] + (0.05f * MathF.Sin((_time * 0.13f) + (emitter * 1.7f)));
                float positionY = EmitterHomeY[emitter] + (0.05f * MathF.Cos((_time * 0.11f) + (emitter * 2.3f)));
                float force = amplitude * DriveGain * MathF.Sin(_emitterPhase[emitter]);
                field.AddForce(positionX, positionY, force, EmitterSigma[emitter]);
            }
        }

        field.Step(dt);

        float breath = 0.5f - (0.5f * MathF.Cos(TwoPi * _breathPhase));

        Current = new ReactiveState(
            _bass,
            _mid,
            _treble,
            _pulse,
            _energy,
            breath,
            _idle,
            _idle > 0.99f);

        return Current;
    }

    /// <summary>Clears all musical and tempo state, keeping the field's curl pattern.</summary>
    public void Reset()
    {
        Array.Clear(_envelope);
        Array.Clear(_peak);
        Array.Clear(_previous);
        Array.Clear(_flux);
        Array.Clear(_lastOnset);
        Array.Clear(_emitterLevel);
        Array.Clear(_emitterTransient);
        Array.Clear(_emitterPhase);
        Array.Clear(_beatTimes);
        Array.Clear(_intervals);
        _beatCount = 0;
        _time = 0f;
        _pulse = 0f;
        _bass = 0f;
        _mid = 0f;
        _treble = 0f;
        _energy = 0f;
        _silenceSeconds = 0f;
        _idle = 0f;
        _beatPeriod = 0.5f;
        _beatPhase = 0f;
        _breathPhase = 0f;
        _hasTempo = false;
        Current = ReactiveState.Rest;
    }

    /// <summary>Band index to emitter group: bass drives the first emitters, air the last.</summary>
    private static int EmitterOfBand(int band) => (band * EmitterCount) / BandCount;

    private void RegisterBeat()
    {
        for (int i = 0; i < _beatTimes.Length - 1; i++)
        {
            _beatTimes[i] = _beatTimes[i + 1];
        }
        _beatTimes[^1] = _time;
        if (_beatCount < _beatTimes.Length)
        {
            _beatCount++;
        }

        if (_beatCount < 4)
        {
            return;
        }

        int intervalCount = 0;
        for (int i = 1; i < _beatCount; i++)
        {
            float interval = _beatTimes[i] - _beatTimes[i - 1];
            if (interval is > 0.25f and < 1.6f)
            {
                _intervals[intervalCount++] = interval;
            }
        }

        if (intervalCount < 2)
        {
            return;
        }

        for (int i = 1; i < intervalCount; i++)
        {
            float value = _intervals[i];
            int j = i - 1;
            while (j >= 0 && _intervals[j] > value)
            {
                _intervals[j + 1] = _intervals[j];
                j--;
            }
            _intervals[j + 1] = value;
        }

        float median = _intervals[intervalCount / 2];
        _beatPeriod = _hasTempo ? (_beatPeriod * 0.8f) + (median * 0.2f) : median;
        _hasTempo = true;
    }

    private void AdvanceTempo(float dt)
    {
        _beatPhase += dt / _beatPeriod;
        if (_beatPhase >= 1f)
        {
            _beatPhase -= MathF.Floor(_beatPhase);
        }

        float breathPeriod = _hasTempo ? _beatPeriod * 16f : 9f;
        _breathPhase += dt / breathPeriod;
        if (_breathPhase >= 1f)
        {
            _breathPhase -= MathF.Floor(_breathPhase);
        }
    }

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}
