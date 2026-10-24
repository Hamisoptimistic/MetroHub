using System;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Controls real-time ferro-acoustic membrane displacement via a Cross-Band Veto
/// spectral flux onset gate and a Duffing nonlinear spring-damper oscillator.
/// </summary>
public sealed class FerroAcousticBassGovernor
{
    private float _subEnergySlowAvg;
    private float _zPiston;      // [0.0 to +1.0] inward press, [-0.28 to 0.0] outward rebound thump
    private float _zVelocity;
    private float _refractoryTimer;

    private const float VetoWeight = 1.35f;
    private const float OnsetThreshold = 0.08f;
    private const float RefractoryPeriodSec = 0.125f;

    public float ZPiston => _zPiston;
    public bool IsIdle => MathF.Abs(_zPiston) < 0.001f && MathF.Abs(_zVelocity) < 0.001f;

    public void Reset()
    {
        _subEnergySlowAvg = 0f;
        _zPiston = 0f;
        _zVelocity = 0f;
        _refractoryTimer = 0f;
    }

    /// <summary>
    /// Steps the Duffing physical oscillator and evaluates the cross-band veto kick gate.
    /// Returns true if motion occurred; false when the system is completely settled at rest.
    /// </summary>
    public bool StepPhysics(float subBassBand, float midHighBand, float dt)
    {
        dt = Math.Clamp(dt, 0.001f, 0.05f);
        _refractoryTimer = MathF.Max(0f, _refractoryTimer - dt);

        // 1. Slow moving average of sub-bass (tracks sustained bass so only transients punch)
        float alpha = 1.0f - MathF.Exp(-dt * 8.0f);
        _subEnergySlowAvg += (subBassBand - _subEnergySlowAvg) * alpha;

        // 2. Cross-band veto: suppress trigger if mids/highs (vocals, snares, crackle) are present
        float denom = subBassBand + (VetoWeight * midHighBand) + 0.0001f;
        float bassDominance = subBassBand / denom;
        bassDominance *= bassDominance;

        // 3. Half-wave rectified spectral flux
        float flux = MathF.Max(0f, subBassBand - _subEnergySlowAvg) * bassDominance;

        // 4. Hysteresis Gate + Refractory Lock
        if (flux > OnsetThreshold && _refractoryTimer <= 0f)
        {
            float punchImpulse = Math.Clamp((flux - OnsetThreshold) * 3.2f, 0.25f, 1.0f);
            if (punchImpulse > _zPiston)
            {
                // 0ms inward press + downward velocity for the elastic rebound thump
                _zPiston = punchImpulse;
                _zVelocity = -punchImpulse * 6.5f;
                _refractoryTimer = RefractoryPeriodSec;
            }
        }

        // 5. Silence Sleep Gate: 0% CPU when settled
        if (IsIdle)
        {
            _zPiston = 0f;
            _zVelocity = 0f;
            return false; // Signal AsciiArtWallpaper worker to skip re-rendering
        }

        // 6. Duffing Nonlinear Spring-Damper (Linear k1 + Cubic hardening k3 + Damping c)
        const float K1 = 950.0f;
        const float K3 = 1800.0f;
        const float Damping = 42.0f;

        float restoringForce = -(K1 * _zPiston) - (K3 * _zPiston * _zPiston * _zPiston);
        float dampingForce = -Damping * _zVelocity;

        _zVelocity += (restoringForce + dampingForce) * dt;
        _zPiston = Math.Clamp(_zPiston + (_zVelocity * dt), -0.28f, 1.0f);

        return true;
    }
}

