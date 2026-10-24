using System;
using System.Runtime.CompilerServices;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// A 160 x 90 damped wave-equation solver that turns the radio spectrum into a
/// physical displacement field for the live halftone wallpaper.
/// <para>
/// The field obeys <c>u_tt + 2y*u_t - c^2 * laplacian(u) = F</c> on a fixed grid.
/// Spectral bands drive oscillators at different frequencies, so bass produces
/// long slow swells while treble produces short fast ripples; because every
/// emitter shares one medium, the waves interfere, form standing nodes and
/// travel across the whole canvas instead of pulsing from the centre.
/// </para>
/// <para>
/// A precomputed, seamlessly tiling curl-noise field adds a divergence-free
/// drift so the image keeps flowing between beats. Both fields are sampled
/// bilinearly per halftone dot from one interleaved array, so a sample touches
/// two cache lines instead of ten. Zero allocations per frame.
/// </para>
/// </summary>
public sealed class WaveField
{
    public const int GridWidth = 160;
    public const int GridHeight = 90;

    private const float FixedStep = 1f / 60f;
    private const int MaxSubSteps = 4;

    // CFL for the explicit 2D scheme is 1/sqrt(2); stay well below it.
    private const float WaveSpeed = 0.55f;
    private const float WaveSpeedSquared = WaveSpeed * WaveSpeed;
    private const float Damping = 0.010f;

    // Amplitude below which the field is considered dead and snapped to zero.
    private const float SleepAmplitude = 0.0004f;

    // Slow translation of the curl pattern, in normalized units per second.
    private const float FlowSpeedU = 0.0016f;
    private const float FlowSpeedV = -0.0011f;

    // Per-cell layout of the sampled field: height, gradient X/Y, curl X/Y.
    private const int FieldStride = 5;

    private readonly int _width;
    private readonly int _height;
    private readonly int _rowStride;
    private readonly float _inverseWidth;
    private readonly float _inverseHeight;

    private readonly float[] _field;
    private float[] _heightCurrent;
    private float[] _heightPrevious;
    private float[] _heightNext;
    private readonly float[] _force;

    private float _accumulator;
    private float _flowTime;
    private float _flowU;
    private float _flowV;

    /// <summary>Creates a solver on the default 16:9 grid.</summary>
    public WaveField()
        : this(GridWidth, GridHeight)
    {
    }

    /// <summary>Creates a solver on a custom grid; dimensions must be at least 8 x 8.</summary>
    public WaveField(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 8);

        _width = width;
        _height = height;
        _rowStride = width * FieldStride;
        _inverseWidth = 1f / width;
        _inverseHeight = 1f / height;

        int count = width * height;
        _heightCurrent = new float[count];
        _heightPrevious = new float[count];
        _heightNext = new float[count];
        _force = new float[count];
        _field = new float[count * FieldStride];

        BuildCurlField();
    }

    /// <summary>Largest absolute displacement currently present in the field.</summary>
    public float Peak { get; private set; }

    /// <summary>True when the field has decayed to practical silence.</summary>
    public bool IsAtRest => Peak < SleepAmplitude;

    /// <summary>
    /// Advances the solver by <paramref name="deltaSeconds"/> using fixed 1/60 s
    /// substeps so the simulation never becomes unstable on a slow frame.
    /// </summary>
    public void Step(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
        {
            return;
        }

        _flowTime += deltaSeconds;
        _flowU = _flowTime * FlowSpeedU;
        _flowU -= MathF.Floor(_flowU);
        _flowV = _flowTime * FlowSpeedV;
        _flowV -= MathF.Floor(_flowV);

        _accumulator += Math.Min(deltaSeconds, 0.25f);
        int steps = 0;
        while (_accumulator >= FixedStep && steps < MaxSubSteps)
        {
            Advance();
            _accumulator -= FixedStep;
            steps++;
        }

        if (steps == MaxSubSteps)
        {
            _accumulator = 0f;
        }
    }

    /// <summary>
    /// Adds an acceleration impulse with a Gaussian footprint. Coordinates and
    /// sigma are normalized (0..1); a wide sigma shakes a large area, a narrow
    /// one produces fine, fast-moving ripples.
    /// </summary>
    public void AddForce(float normalizedX, float normalizedY, float force, float sigma)
    {
        if (force == 0f)
        {
            return;
        }

        int width = _width;
        int height = _height;
        float centerX = Math.Clamp(normalizedX, 0f, 1f) * (width - 1);
        float centerY = Math.Clamp(normalizedY, 0f, 1f) * (height - 1);
        float spread = Math.Max(0.75f, sigma);
        int reach = (int)MathF.Ceiling(spread * 2.5f);

        int minX = Math.Max(1, (int)centerX - reach);
        int maxX = Math.Min(width - 2, (int)centerX + reach);
        int minY = Math.Max(1, (int)centerY - reach);
        int maxY = Math.Min(height - 2, (int)centerY + reach);

        float inverseVariance = 1f / (2f * spread * spread);

        for (int y = minY; y <= maxY; y++)
        {
            float dy = y - centerY;
            float dySquared = dy * dy;
            int rowBase = y * width;

            for (int x = minX; x <= maxX; x++)
            {
                float dx = x - centerX;
                float weight = MathF.Exp(-((dx * dx) + dySquared) * inverseVariance);
                _force[rowBase + x] += force * weight;
            }
        }
    }

    /// <summary>
    /// Bilinearly samples displacement, surface gradient and curl at normalized
    /// coordinates. The gradient points uphill; the curl field is divergence-free
    /// and slowly drifting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Sample(
        float normalizedX,
        float normalizedY,
        out float height,
        out float gradientX,
        out float gradientY,
        out float curlX,
        out float curlY)
    {
        int width = _width;
        int heightCount = _height;
        float[] field = _field;

        // Callers are expected to pass normalized coordinates, but clamping here keeps
        // an out-of-range sample from walking off the array.
        float u = Math.Clamp(normalizedX, 0f, 1f);
        float v = Math.Clamp(normalizedY, 0f, 1f);
        float px = u * (width - 1);
        float py = v * (heightCount - 1);
        int x0 = (int)px;
        int y0 = (int)py;
        if (x0 >= width - 1)
        {
            x0 = width - 2;
        }
        if (y0 >= heightCount - 1)
        {
            y0 = heightCount - 2;
        }

        float tx = px - x0;
        float ty = py - y0;
        int rowStride = _rowStride;
        int i00 = ((y0 * width) + x0) * FieldStride;
        int i10 = i00 + FieldStride;
        int i01 = i00 + rowStride;
        int i11 = i01 + FieldStride;

        float w00 = (1f - tx) * (1f - ty);
        float w10 = tx * (1f - ty);
        float w01 = (1f - tx) * ty;
        float w11 = tx * ty;

        height = (field[i00] * w00) + (field[i10] * w10)
            + (field[i01] * w01) + (field[i11] * w11);
        gradientX = (field[i00 + 1] * w00) + (field[i10 + 1] * w10)
            + (field[i01 + 1] * w01) + (field[i11 + 1] * w11);
        gradientY = (field[i00 + 2] * w00) + (field[i10 + 2] * w10)
            + (field[i01 + 2] * w01) + (field[i11 + 2] * w11);

        // Curl tiles seamlessly, so it scrolls with a wrapping lookup. Both flow
        // offsets are pre-wrapped into 0..1 by Step, which turns the wrap into one
        // compare instead of a floor per component per dot.
        float curlU = u + _flowU;
        if (curlU >= 1f)
        {
            curlU -= 1f;
        }

        float curlV = v + _flowV;
        if (curlV >= 1f)
        {
            curlV -= 1f;
        }

        float cx = curlU * width;
        float cy = curlV * heightCount;
        int cx0 = (int)cx;
        int cy0 = (int)cy;
        if (cx0 >= width)
        {
            cx0 = 0;
        }
        if (cy0 >= heightCount)
        {
            cy0 = 0;
        }

        int cx1 = cx0 + 1 == width ? 0 : cx0 + 1;
        int cy1 = cy0 + 1 == heightCount ? 0 : cy0 + 1;
        float ctx = cx - cx0;
        float cty = cy - cy0;

        int c00 = ((cy0 * width) + cx0) * FieldStride;
        int c10 = ((cy0 * width) + cx1) * FieldStride;
        int c01 = ((cy1 * width) + cx0) * FieldStride;
        int c11 = ((cy1 * width) + cx1) * FieldStride;

        float cw00 = (1f - cty) * (1f - ctx);
        float cw10 = (1f - cty) * ctx;
        float cw01 = cty * (1f - ctx);
        float cw11 = cty * ctx;

        curlX = (field[c00 + 3] * cw00) + (field[c10 + 3] * cw10)
            + (field[c01 + 3] * cw01) + (field[c11 + 3] * cw11);
        curlY = (field[c00 + 4] * cw00) + (field[c10 + 4] * cw10)
            + (field[c01 + 4] * cw01) + (field[c11 + 4] * cw11);
    }

    /// <summary>Clears all motion, keeping the curl pattern.</summary>
    public void Reset()
    {
        Array.Clear(_heightCurrent);
        Array.Clear(_heightPrevious);
        Array.Clear(_heightNext);
        Array.Clear(_force);

        for (int cell = 0; cell < _heightCurrent.Length; cell++)
        {
            int index = cell * FieldStride;
            _field[index] = 0f;
            _field[index + 1] = 0f;
            _field[index + 2] = 0f;
        }

        Peak = 0f;
        _accumulator = 0f;
    }

    private void Advance()
    {
        int width = _width;
        int height = _height;
        float[] current = _heightCurrent;
        float[] previous = _heightPrevious;
        float[] next = _heightNext;

        for (int y = 1; y < height - 1; y++)
        {
            int row = y * width;
            for (int x = 1; x < width - 1; x++)
            {
                int index = row + x;
                float value = current[index];
                float velocity = value - previous[index];
                float laplacian = current[index - 1] + current[index + 1]
                    + current[index - width] + current[index + width] - (4f * value);

                float result = value + velocity + (WaveSpeedSquared * laplacian)
                    - (Damping * velocity) + _force[index];

                // Guard against a pathological NaN/Inf poisoning the whole field.
                if (result > 4f)
                {
                    result = 4f;
                }
                else if (result < -4f)
                {
                    result = -4f;
                }
                else if (float.IsNaN(result))
                {
                    result = 0f;
                }

                next[index] = result;
            }
        }

        // Boundaries stay pinned at zero (Dirichlet), which reflects waves back in.
        _heightPrevious = current;
        _heightCurrent = next;
        _heightNext = previous;

        Array.Clear(_force);
        RebuildGradients();
    }

    private void RebuildGradients()
    {
        int width = _width;
        int height = _height;
        float[] values = _heightCurrent;
        float[] field = _field;

        double sum = 0.0;
        float peak = 0f;

        for (int i = 0; i < values.Length; i++)
        {
            float value = values[i];
            sum += value;

            float magnitude = MathF.Abs(value);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        Peak = peak;

        // Remove the DC offset so the picture never drifts uniformly.
        float mean = (float)(sum / values.Length);
        if (MathF.Abs(mean) > 1e-7f)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] -= mean;
            }
        }

        // Publish height and slope into the interleaved sample array.
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int rowAbove = (y > 0 ? y - 1 : y) * width;
            int rowBelow = (y < height - 1 ? y + 1 : y) * width;

            for (int x = 0; x < width; x++)
            {
                int left = x > 0 ? x - 1 : x;
                int right = x < width - 1 ? x + 1 : x;
                int index = ((row + x) * FieldStride);

                field[index] = values[row + x];
                field[index + 1] = (values[row + right] - values[row + left]) * 0.5f;
                field[index + 2] = (values[rowBelow + x] - values[rowAbove + x]) * 0.5f;
            }
        }
    }

    /// <summary>
    /// Precomputes a seamlessly tiling curl-noise pattern. Three integer-period
    /// octaves keep the field divergence-free and give it a large, fluid scale.
    /// </summary>
    private void BuildCurlField()
    {
        int width = _width;
        int height = _height;
        float[] potential = new float[width * height];

        float maximum = 0f;
        for (int y = 0; y < height; y++)
        {
            float v = y * _inverseHeight;
            for (int x = 0; x < width; x++)
            {
                float u = x * _inverseWidth;
                float value = (0.55f * ValueNoise(u * 3f, v * 2f, 3, 2, 0, 0))
                    + (0.30f * ValueNoise(u * 7f, v * 5f, 7, 5, 3, 2))
                    + (0.15f * ValueNoise(u * 13f, v * 9f, 13, 9, 5, 4));

                potential[(y * width) + x] = value;
                float magnitude = MathF.Abs(value);
                if (magnitude > maximum)
                {
                    maximum = magnitude;
                }
            }
        }

        float normalizer = maximum > 1e-5f ? 1f / maximum : 0f;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            int rowAbove = (y > 0 ? y - 1 : height - 1) * width;
            int rowBelow = (y < height - 1 ? y + 1 : 0) * width;

            for (int x = 0; x < width; x++)
            {
                int left = x > 0 ? x - 1 : width - 1;
                int right = x < width - 1 ? x + 1 : 0;
                int index = (row + x) * FieldStride;

                // curl(potential) = (d/dy, -d/dx); scaled to roughly [-1, 1].
                _field[index + 3] = (potential[rowBelow + x] - potential[rowAbove + x]) * 2.5f * normalizer;
                _field[index + 4] = -(potential[row + right] - potential[row + left]) * 2.5f * normalizer;
            }
        }
    }

    private static float ValueNoise(float x, float y, int periodX, int periodY, int offsetX, int offsetY)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = x - x0;
        float fy = y - y0;
        float sx = fx * fx * (3f - (2f * fx));
        float sy = fy * fy * (3f - (2f * fy));

        int xa = x0 + offsetX;
        int ya = y0 + offsetY;

        float v00 = LatticeHash(xa, ya, periodX, periodY);
        float v10 = LatticeHash(xa + 1, ya, periodX, periodY);
        float v01 = LatticeHash(xa, ya + 1, periodX, periodY);
        float v11 = LatticeHash(xa + 1, ya + 1, periodX, periodY);

        float top = v00 + ((v10 - v00) * sx);
        float bottom = v01 + ((v11 - v01) * sx);
        return top + ((bottom - top) * sy);
    }

    private static float LatticeHash(int x, int y, int periodX, int periodY)
    {
        int wrappedX = ((x % periodX) + periodX) % periodX;
        int wrappedY = ((y % periodY) + periodY) % periodY;

        uint hash = (uint)((wrappedX * 0x1F1F1F1F) ^ (wrappedY * 0x9E3779B1));
        hash ^= hash >> 15;
        hash *= 0x2C1B3C6D;
        hash ^= hash >> 12;
        hash *= 0x297A2D39;
        hash ^= hash >> 15;
        return ((hash & 0xFFFF) * (1f / 32768f)) - 1f;
    }
}
