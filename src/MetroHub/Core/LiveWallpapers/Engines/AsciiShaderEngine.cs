using System;
using System.Runtime.CompilerServices;

namespace MetroHub.Core.LiveWallpapers.Engines;

public enum ShaderPreset
{
    MatrixRain,
    SynthwaveHorizon,
    DeepSpaceWarp,
    CyberPlasma,
    AuroraBorealis,
    RotatingTorus3D,
    OceanSwell,
    RadarSweep,
    FireStorm,
    CampfireUnderStars
}

public sealed class AsciiShaderEngine : IAsciiWallpaperScene
{
    public string SceneId { get; }
    public string DisplayName { get; }
    private readonly ShaderPreset _preset;
    private readonly byte _defaultPalette;

    private int _cols;
    private int _rows;
    private float _time;
    private AsciiGlyphLut _lut = null!;

    private float[] _stateA = [];
    private float[] _stateB = [];

    public AsciiShaderEngine(string sceneId, string displayName, ShaderPreset preset, byte defaultPalette = 0)
    {
        SceneId = sceneId;
        DisplayName = displayName;
        _preset = preset;
        _defaultPalette = defaultPalette;
    }

    public void Initialize(int columns, int rows, AsciiGlyphLut glyphLut)
    {
        _cols = columns;
        _rows = rows;
        _lut = glyphLut;
        _stateA = new float[columns * rows];
        _stateB = new float[columns * 4];

        uint seed = 0x9E3779B9u;
        for (int i = 0; i < _stateB.Length; i++)
        {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            _stateB[i] = (seed & 0xFFFF) / 65535f;
        }
    }

    public void Update(float dt) => _time += dt;

    public void Render(AsciiFrameBuffer buffer)
    {
        switch (_preset)
        {
            case ShaderPreset.MatrixRain:
                RenderMatrix(buffer);
                break;
            case ShaderPreset.SynthwaveHorizon:
                RenderSynthwave(buffer);
                break;
            case ShaderPreset.RotatingTorus3D:
                RenderTorus(buffer);
                break;
            case ShaderPreset.DeepSpaceWarp:
                RenderStarfield(buffer);
                break;
            case ShaderPreset.CampfireUnderStars:
                RenderCampfire(buffer);
                break;
            default:
                RenderFieldShader(buffer);
                break;
        }
    }

    private void RenderMatrix(AsciiFrameBuffer buffer)
    {
        ReadOnlySpan<char> glyphs = "012345789Z:・.=*+-<>¦｜";
        for (int x = 0; x < _cols; x++)
        {
            float speed = 8f + (_stateB[x] * 18f);
            float headY = ((_time * speed) + (_stateB[x + _cols] * _rows * 2f)) % (_rows + 24f);
            int trailLen = 8 + (int)(_stateB[x + (_cols * 2)] * 18f);

            for (int y = 0; y < _rows; y++)
            {
                float dist = headY - y;
                if (dist >= 0f && dist < trailLen)
                {
                    float norm = 1f - (dist / trailLen);
                    byte shade = dist < 1.0f ? (byte)15 : (byte)Math.Clamp((int)(norm * 13f), 1, 14);
                    int charIdx = (int)((x * 31 + y * 17 + (int)(_time * 12f)) & 0x7FFFFFFF) % glyphs.Length;
                    buffer.SetCell(x, y, _lut[glyphs[charIdx]], shade, 1);
                }
                else
                {
                    buffer.SetCell(x, y, 0, 0, 1);
                }
            }
        }
    }

    private void RenderSynthwave(AsciiFrameBuffer buffer)
    {
        int horizonY = (int)(_rows * 0.52f);
        float centerX = _cols * 0.5f;

        for (int y = 0; y < _rows; y++)
        {
            for (int x = 0; x < _cols; x++)
            {
                if (y < horizonY)
                {
                    float dx = (x - centerX) * 0.55f;
                    float dy = y - (horizonY - 12f);
                    float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                    if (dist < 14f)
                    {
                        bool scanGap = dy > 2f && ((int)(dy + (_time * 2f)) & 1) == 0;
                        if (!scanGap)
                        {
                            byte shade = (byte)Math.Clamp((int)(15f - (dist * 0.5f)), 4, 15);
                            buffer.SetCell(x, y, _lut['#'], shade, 2);
                            continue;
                        }
                    }
                    buffer.SetCell(x, y, 0, 0);
                }
                else if (y == horizonY)
                {
                    buffer.SetCell(x, y, _lut['='], 15, 3);
                }
                else
                {
                    float py = (y - horizonY) / (float)(_rows - horizonY);
                    float z = 1.0f / (py + 0.05f);
                    float perspectiveX = (x - centerX) / (py * _cols * 0.45f);

                    float gridX = MathF.Abs((perspectiveX * 4f) - MathF.Round(perspectiveX * 4f));
                    float gridZ = MathF.Abs((z + (_time * 3.5f)) - MathF.Round(z + (_time * 3.5f)));

                    if (gridX < 0.08f)
                    {
                        byte shade = (byte)Math.Clamp((int)(py * 15f), 3, 15);
                        buffer.SetCell(x, y, _lut['/'], shade, 3);
                    }
                    else if (gridZ < 0.12f)
                    {
                        byte shade = (byte)Math.Clamp((int)(py * 13f), 2, 13);
                        buffer.SetCell(x, y, _lut['-'], shade, 3);
                    }
                    else
                    {
                        buffer.SetCell(x, y, 0, 0);
                    }
                }
            }
        }
    }

    private void RenderTorus(AsciiFrameBuffer buffer)
    {
        buffer.Clear();
        Span<float> zBuffer = _stateA.AsSpan(0, _cols * _rows);
        zBuffer.Clear();

        float a = _time * 1.1f;
        float b = _time * 0.6f;
        float cosA = MathF.Cos(a), sinA = MathF.Sin(a);
        float cosB = MathF.Cos(b), sinB = MathF.Sin(b);

        for (float theta = 0; theta < MathF.Tau; theta += 0.09f)
        {
            float cosT = MathF.Cos(theta), sinT = MathF.Sin(theta);
            for (float phi = 0; phi < MathF.Tau; phi += 0.04f)
            {
                float cosP = MathF.Cos(phi), sinP = MathF.Sin(phi);
                float circleX = 2.0f + cosT;
                float circleY = sinT;

                float x = circleX * ((cosB * cosP) + (sinA * sinB * sinP)) - (circleY * cosA * sinB);
                float y = circleX * ((sinB * cosP) - (sinA * cosB * sinP)) + (circleY * cosA * cosB);
                float z = 5.0f + (cosA * circleX * sinP) + (circleY * sinA);
                float ooz = 1.0f / z;

                int xp = (int)((_cols * 0.5f) + (48.0f * ooz * x));
                int yp = (int)((_rows * 0.5f) - (24.0f * ooz * y));

                if ((uint)xp < (uint)_cols && (uint)yp < (uint)_rows)
                {
                    int idx = (yp * _cols) + xp;
                    if (ooz > zBuffer[idx])
                    {
                        zBuffer[idx] = ooz;
                        float lum = (cosP * cosT * sinB) - (cosA * cosT * sinP) - (sinA * sinT) + (cosB * ((cosA * sinT) - (cosT * sinA * sinP)));
                        int shadeIdx = Math.Clamp((int)((lum + 1.4f) * 5.5f), 1, 15);
                        buffer.SetCell(xp, yp, _lut.LuminanceRampGlyphs[shadeIdx], (byte)shadeIdx, _defaultPalette);
                    }
                }
            }
        }
    }

    private void RenderStarfield(AsciiFrameBuffer buffer)
    {
        buffer.Clear();
        int starCount = _cols;
        float cx = _cols * 0.5f;
        float cy = _rows * 0.5f;

        for (int i = 0; i < starCount; i++)
        {
            float sx = (_stateB[i] - 0.5f) * _cols * 1.6f;
            float sy = (_stateB[i + _cols] - 0.5f) * _rows * 1.6f;
            float z = 1.0f - (((_time * 0.45f) + _stateB[i + (_cols * 2)]) % 1.0f);
            if (z < 0.02f) z = 0.02f;

            int px = (int)(cx + (sx / z));
            int py = (int)(cy + (sy / z));

            if ((uint)px < (uint)_cols && (uint)py < (uint)_rows)
            {
                byte shade = (byte)Math.Clamp((int)((1.0f - z) * 15f), 2, 15);
                char glyph = z < 0.25f ? '@' : (z < 0.55f ? '*' : '.');
                buffer.SetCell(px, py, _lut[glyph], shade, _defaultPalette);
            }
        }
    }

    private void RenderCampfire(AsciiFrameBuffer buffer)
    {
        buffer.Clear();
        int fireBaseY = _rows - 6;
        float fireCenterX = _cols * 0.5f;

        // 1. Starry Sky with subtle twinkling
        for (int i = 0; i < _cols; i += 3)
        {
            float noise = AsciiKit.Perlin2D(i * 0.2f, _time * 0.15f);
            if (noise > 0.45f)
            {
                int starY = (int)((noise * 17f) % (fireBaseY - 14));
                buffer.SetCell(i, starY, _lut['·'], 4, 0);
            }
        }

        // 2. Procedural Flame Body via FBM noise
        for (int y = fireBaseY - 14; y <= fireBaseY; y++)
        {
            float normY = (fireBaseY - y) / 14f; // 0 at base, 1 at peak
            for (int x = (int)(fireCenterX - 18); x <= (int)(fireCenterX + 18); x++)
            {
                float dx = (x - fireCenterX) / 12f;
                float heat = AsciiKit.Fbm((x * 0.15f), (y * 0.25f) - (_time * 3.5f), 2);
                float shape = (1.0f - (dx * dx)) - (normY * 1.3f) + (heat * 0.7f);

                if (shape > 0.1f)
                {
                    byte shade = (byte)Math.Clamp((int)(shape * 16f), 2, 15);
                    ushort glyph = shape > 0.65f ? _lut.FullBlock : (shape > 0.35f ? _lut.DarkShade : _lut.MediumShade);
                    buffer.SetCell(x, y, glyph, shade, 2); // Amber fire palette
                }
            }
        }

        // 3. Wood Logs at Base
        for (int x = (int)(fireCenterX - 8); x <= (int)(fireCenterX + 8); x++)
        {
            buffer.SetCell(x, fireBaseY + 1, _lut['='], 7, 2);
            buffer.SetCell(x, fireBaseY + 2, _lut['#'], 4, 2);
        }
    }

    private void RenderFieldShader(AsciiFrameBuffer buffer)
    {
        for (int y = 0; y < _rows; y++)
        {
            float ny = (y / (float)_rows) * 2f - 1f;
            for (int x = 0; x < _cols; x++)
            {
                float nx = (x / (float)_cols) * 2f - 1f;
                float val = _preset switch
                {
                    ShaderPreset.CyberPlasma => MathF.Sin((nx * 6f) + _time) + MathF.Cos((ny * 6f) - (_time * 0.8f)) + MathF.Sin(((nx + ny) * 5f) + _time),
                    ShaderPreset.AuroraBorealis => MathF.Sin((nx * 4f) + MathF.Sin((ny * 3f) + _time)) * MathF.Exp(-MathF.Abs(ny * 2.2f)),
                    ShaderPreset.OceanSwell => MathF.Sin((x * 0.12f) + (_time * 2.2f) + (y * 0.25f)) * 0.5f + 0.5f,
                    ShaderPreset.RadarSweep => ComputeRadar(nx * 1.6f, ny, _time),
                    ShaderPreset.FireStorm => MathF.Max(0f, ((ny + 1f) * 0.6f) + (MathF.Sin((nx * 10f) + (_time * 5f)) * 0.25f)),
                    _ => MathF.Abs(MathF.Sin((nx * 8f) + _time) * MathF.Cos((ny * 8f) - _time))
                };

                int level = Math.Clamp((int)((val + 1.5f) * 4.8f), 0, 15);
                if (level <= 2)
                {
                    buffer.SetCell(x, y, 0, 0);
                }
                else
                {
                    buffer.SetCell(x, y, _lut.LuminanceRampGlyphs[level], (byte)level, _defaultPalette);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ComputeRadar(float x, float y, float t)
    {
        float r = MathF.Sqrt((x * x) + (y * y));
        if (r > 0.95f) return -2f;

        float angle = MathF.Atan2(y, x);
        float sweep = (t * 1.8f) % MathF.Tau;
        float trail = (sweep - angle + MathF.Tau) % MathF.Tau;

        if (trail < 0.45f)
            return 1.3f - (trail * 2.5f);

        if (MathF.Abs(r - 0.5f) < 0.02f || MathF.Abs(r - 0.9f) < 0.02f)
            return 0.15f;

        return -1f;
    }

    public void Dispose() { }
}

