using System;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Manages real-time audio dynamics for halftone rendering:
/// kick transient detection with refractory hold, up to 4 concurrent shockwaves,
/// exponential decay envelope smoothing, and silence/rest detection with hysteresis.
/// Zero per-frame allocations.
/// </summary>
public sealed class HalftoneAudioGovernor
{
    public const int MaxConcurrentWaves = 4;
    public const float WaveLifetimeSeconds = 0.40f;
    public const float MinEpsilon = 0.005f;
    public const float SleepThreshold = 0.015f;
    public const float WakeThreshold = 0.030f;
    public const float SilenceDurationSeconds = 0.50f;

    private readonly HalftoneAudioWave[] _wavePool = new HalftoneAudioWave[MaxConcurrentWaves];
    private int _waveCount;

    private float _kickMovingAvg = 0.20f;
    private float _lastKickTime;
    private float _runningTime;
    private float _silenceElapsed = SilenceDurationSeconds;
    private bool _isSilent = true;

    private float _smoothedKick;
    private float _smoothedBass;
    private float _smoothedMid;
    private float _smoothedTreble;

    private float _defaultCenterX = 960f;
    private float _defaultCenterY = 540f;

    /// <summary>Sets the center coordinates for automatic beat ripples.</summary>
    public void SetCenter(float cx, float cy)
    {
        _defaultCenterX = cx;
        _defaultCenterY = cy;
    }

    /// <summary>True when audio has decayed to zero and silence duration has elapsed.</summary>
    public bool IsAtRest => _isSilent && _waveCount == 0 &&
                            _smoothedKick <= 0f && _smoothedBass <= 0f && _smoothedTreble <= 0f;

    /// <summary>
    /// Updates physics state with current spectrum levels and advances time.
    /// </summary>
    public AudioFrame Update(float rawKick, float rawBass, float rawMid, float rawTreble, float deltaSeconds)
    {
        _runningTime += deltaSeconds;

        // 1. Advance and prune active waves
        int activeCount = 0;
        for (int i = 0; i < _waveCount; i++)
        {
            ref readonly HalftoneAudioWave oldWave = ref _wavePool[i];
            float newElapsed = oldWave.ElapsedSeconds + deltaSeconds;
            if (newElapsed < WaveLifetimeSeconds)
            {
                _wavePool[activeCount++] = new HalftoneAudioWave(oldWave.CenterX, oldWave.CenterY, newElapsed, oldWave.Intensity);
            }
        }
        _waveCount = activeCount;

        // 2. Dynamic rising-edge kick detection
        // Update 1s moving average: EMA factor for ~1s at 60Hz: alpha ~ 0.05
        _kickMovingAvg = (_kickMovingAvg * 0.95f) + (rawKick * 0.05f);

        bool kickTriggered = rawKick > 1.6f * _kickMovingAvg &&
                             rawKick > 0.35f &&
                             (_runningTime - _lastKickTime) >= 0.35f;

        if (kickTriggered)
        {
            _lastKickTime = _runningTime;
            TriggerBeat();
        }

        // 3. Asymmetric exponential decay: instant attack, tau = 0.18s decay
        float decayFactor = MathF.Exp(-deltaSeconds / 0.18f);

        _smoothedKick = rawKick > _smoothedKick ? rawKick : _smoothedKick * decayFactor;
        _smoothedBass = rawBass > _smoothedBass ? rawBass : _smoothedBass * decayFactor;
        _smoothedMid = rawMid > _smoothedMid ? rawMid : _smoothedMid * decayFactor;
        _smoothedTreble = rawTreble > _smoothedTreble ? rawTreble : _smoothedTreble * decayFactor;

        // Snap to zero below epsilon
        if (_smoothedKick < MinEpsilon) _smoothedKick = 0f;
        if (_smoothedBass < MinEpsilon) _smoothedBass = 0f;
        if (_smoothedMid < MinEpsilon) _smoothedMid = 0f;
        if (_smoothedTreble < MinEpsilon) _smoothedTreble = 0f;

        // 4. Silence tracking with hysteresis
        float totalEnergy = _smoothedKick + _smoothedBass + _smoothedTreble;
        if (totalEnergy < SleepThreshold)
        {
            _silenceElapsed += deltaSeconds;
            if (_silenceElapsed >= SilenceDurationSeconds)
            {
                _isSilent = true;
            }
        }
        else if (totalEnergy > WakeThreshold)
        {
            _silenceElapsed = 0f;
            _isSilent = false;
        }

        ReadOnlyMemory<HalftoneAudioWave> waves = _waveCount > 0
            ? new ReadOnlyMemory<HalftoneAudioWave>(_wavePool, 0, _waveCount)
            : ReadOnlyMemory<HalftoneAudioWave>.Empty;

        return new AudioFrame(_smoothedKick, _smoothedBass, _smoothedMid, _smoothedTreble, _isSilent, waves);
    }

    /// <summary>
    /// Spawns a radial shockwave at the given coordinates (default: configured center).
    /// </summary>
    public void TriggerBeat(float centerX = -1f, float centerY = -1f)
    {
        float x = centerX >= 0 ? centerX : _defaultCenterX;
        float y = centerY >= 0 ? centerY : _defaultCenterY;

        if (_waveCount < MaxConcurrentWaves)
        {
            _wavePool[_waveCount++] = new HalftoneAudioWave(x, y, 0f, 1f);
        }
        else
        {
            // Replace oldest wave if pool is full
            int oldestIdx = 0;
            float maxElapsed = _wavePool[0].ElapsedSeconds;
            for (int i = 1; i < MaxConcurrentWaves; i++)
            {
                if (_wavePool[i].ElapsedSeconds > maxElapsed)
                {
                    maxElapsed = _wavePool[i].ElapsedSeconds;
                    oldestIdx = i;
                }
            }
            _wavePool[oldestIdx] = new HalftoneAudioWave(x, y, 0f, 1f);
        }
    }

    /// <summary>Resets all physics states to at-rest defaults.</summary>
    public void Reset()
    {
        _waveCount = 0;
        _silenceElapsed = SilenceDurationSeconds;
        _isSilent = true;
        _smoothedKick = 0f;
        _smoothedBass = 0f;
        _smoothedMid = 0f;
        _smoothedTreble = 0f;
        _lastKickTime = 0f;
        _runningTime = 0f;
    }
}

