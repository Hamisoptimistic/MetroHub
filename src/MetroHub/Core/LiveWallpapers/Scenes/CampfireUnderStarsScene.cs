using System;

namespace MetroHub.Core.LiveWallpapers.Scenes;

/// <summary>
/// A crackling campfire throwing sparks up toward the Milky Way, with a tent beside it.
/// Ported from the ascii-magic.com / ascii.rest procedural scene.
/// </summary>
public sealed class CampfireUnderStarsScene : IAsciiWallpaperScene
{
    public string SceneId => "campfire_stars";
    public string DisplayName => "Campfire Under Stars";

    private readonly record struct Star(int X, int Y, float Brightness, float Phase);
    private readonly record struct Spark(float Phase, float Speed, float Offset, float Sway);

    private int _cols;
    private int _rows;
    private float _time;
    private AsciiGlyphLut _lut = null!;

    private Star[] _stars = [];
    private int[] _trees = [];
    private Spark[] _sparks = [];

    public void Initialize(int columns, int rows, AsciiGlyphLut glyphLut)
    {
        _cols = columns;
        _rows = rows;
        _lut = glyphLut;

        var rng = new Random(17);
        float scaleY = rows / 36f;
        int hz = (int)(24 * scaleY);

        _stars = new Star[180];
        for (int i = 0; i < _stars.Length; i++)
        {
            int sx = rng.Next(columns);
            int sy = rng.Next(Math.Max(1, hz));
            float b = (float)rng.NextDouble();
            float ph = (float)rng.NextDouble() * MathF.Tau;
            _stars[i] = new Star(sx, sy, b, ph);
        }

        _trees = new int[columns];
        for (int x = 0; x < columns; x++)
        {
            float normX = x * (100f / columns);
            float pine = MathF.Abs(((normX * 0.37f) % 1f) - 0.5f) * 2f;
            float treeHeight = (1f - pine) * (3f + (AsciiKit.Fbm(normX * 0.1f, 0f, 2, 5f) * 6f)) * scaleY;
            _trees[x] = hz - 1 - (int)MathF.Round(treeHeight);
        }

        _sparks = new Spark[42];
        for (int i = 0; i < _sparks.Length; i++)
        {
            float ph = (float)rng.NextDouble() * MathF.Tau;
            float sp = 1f + ((float)rng.NextDouble() * 2f);
            float off = (float)rng.NextDouble() * 3f;
            float sway = (float)rng.NextDouble();
            _sparks[i] = new Spark(ph, sp, off, sway);
        }
    }

    public void Update(float dt) => _time += dt;

    public void Render(AsciiFrameBuffer buffer)
    {
        buffer.Clear();

        float scaleX = _cols / 100f;
        float scaleY = _rows / 36f;
        int hz = (int)(24 * scaleY);
        int fx = (int)(58 * scaleX);
        int fy = (int)(30 * scaleY);

        float flick = 0.85f + (0.15f * MathF.Sin(_time * 13f) * MathF.Sin(_time * 7.3f));

        // 1. Night sky with the Milky Way across it
        for (int y = 0; y < hz; y++)
        {
            float normY = y / scaleY;
            for (int x = 0; x < _cols; x++)
            {
                float normX = x / scaleX;
                float bandD = (normY - ((normX * 0.32f) - 4f)) / 6f;
                float n = AsciiKit.Fbm((normX * 0.08f) + (_time * 0.01f), normY * 0.2f, 4, 3f);
                float dust = MathF.Exp(-bandD * bandD) * (0.4f + n);
                bool dark = AsciiKit.Fbm(normX * 0.15f, normY * 0.4f, 3, 9f) > 0.62f && MathF.Abs(bandD) < 0.6f;

                if (dust > 0.4f && !dark)
                {
                    ushort glyph = dust > 1f ? _lut.DarkShade : (dust > 0.7f ? _lut.MediumShade : _lut.LightShade);
                    byte shade = (byte)Math.Clamp((int)((dust - 0.3f) * 11f) + 3, 3, 15);
                    buffer.SetCell(x, y, glyph, shade, 3); // Palette 3: Deep Space Violet (Milky Way)
                }
                else
                {
                    ushort glyph = n > 0.6f ? _lut.LightShade : _lut[' '];
                    byte shade = (byte)Math.Clamp((int)(((normY / 24f) * 4f) + (n * 3f)) + 1, 1, 5);
                    buffer.SetCell(x, y, glyph, shade, 0); // Palette 0: Cyan/Night sky
                }
            }
        }

        // 2. Stars twinkling in the night sky
        for (int i = 0; i < _stars.Length; i++)
        {
            ref readonly var s = ref _stars[i];
            float tw = 0.5f + (0.5f * MathF.Sin((_time * 2.2f) + s.Phase));
            float v = (s.Brightness * 0.6f) + (tw * 0.4f);
            if (v > 0.35f)
            {
                char starChar = v > 0.85f ? '*' : (v > 0.6f ? '•' : '·');
                byte starShade = (byte)(v > 0.85f ? 15 : (v > 0.6f ? 11 : 6));
                buffer.SetCell(s.X, s.Y, _lut[starChar], starShade, 0);
            }
        }

        // 3. Treeline silhouette
        for (int x = 0; x < _cols; x++)
        {
            int treeTop = _trees[x];
            for (int y = treeTop; y <= hz; y++)
            {
                byte treeShade = (byte)(y > hz - 2 ? 4 : 2);
                buffer.SetCell(x, y, _lut.FullBlock, treeShade, 1); // Palette 1: Emerald/Dark Forest
            }
        }

        // 4. Ground lit by the campfire
        for (int y = hz; y < _rows; y++)
        {
            float normY = (y - hz) / scaleY;
            for (int x = 0; x < _cols; x++)
            {
                float normX = x / scaleX;
                float d = MathF.Sqrt(MathF.Pow((normX - 58f) / 2.2f, 2f) + MathF.Pow((normY - 6f) * 1.1f, 2f));
                float n = AsciiKit.Fbm(normX * 0.2f, normY * 0.6f, 2, 4f);
                float v = Math.Clamp((MathF.Exp(-d / 14f) * flick * 1.15f) + ((n - 0.5f) * 0.25f) + (normY * 0.01f), 0f, 1f);
                ushort glyph = v > 0.6f ? _lut.DarkShade : (v > 0.3f ? _lut.MediumShade : _lut.LightShade);
                byte shade = (byte)Math.Clamp((int)(v * 14f), 1, 14);
                buffer.SetCell(x, y, glyph, shade, 2); // Palette 2: Warm Amber Ground
            }
        }

        // 5. Tent on the left, glowing with warm firelight
        int tx = (int)(24 * scaleX);
        int tb = (int)(32 * scaleY);
        int th = (int)(13 * scaleY);
        int tentTop = tb - th;
        for (int y = tentTop; y <= tb; y++)
        {
            float hw = (y - tentTop) * 1.5f * (scaleX / scaleY);
            int leftX = (int)MathF.Round(tx - hw);
            int rightX = (int)MathF.Round(tx + hw);
            for (int x = leftX; x <= rightX; x++)
            {
                float side = (x - tx) / MathF.Max(1f, hw);
                float v = Math.Clamp(0.25f + (side * 0.4f * flick) + ((y - tentTop) * 0.015f), 0f, 1f);
                bool door = MathF.Abs(x - tx) < ((y - tentTop) * 0.42f) && y > tb - (int)(7 * scaleY);
                ushort glyph = door ? _lut.MediumShade : (side > 0.9f ? _lut['\\'] : (side < -0.9f ? _lut['/'] : _lut.FullBlock));
                byte shade = door ? (byte)4 : (byte)Math.Clamp((int)(v * 11f) + 2, 2, 13);
                buffer.SetCell(x, y, glyph, shade, 2);
            }
        }
        buffer.SetCell(tx, tentTop - 1, _lut['│'], 6, 2); // Center pole
        DrawLine(buffer, (int)(tx + (18 * scaleX)), tb + 1, tx + 1, tentTop, _lut['·'], 5, 2); // Guy rope

        // 6. Logs and fire-ring stones
        DrawText(buffer, fx - 8, fy + 1, "▄██▀▀▄▄  ▄▄▀▀██▄", 7, 2);
        DrawText(buffer, fx - 8, fy + 2, "●▀▀●▀▀●▀▀●▀▀●▀▀●", 5, 2);

        // 7. Campfire flames: noisy leaping teardrop
        int flameH = (int)(15 * scaleY);
        int flameW = (int)(9 * scaleX);
        for (int y = fy - flameH + 1; y <= fy + 1; y++)
        {
            float h = (fy + 1 - y) / (float)flameH;
            float width = ((1f - h) * 8f * (0.7f + (0.3f * h)) + 0.5f) * scaleX;
            for (int x = fx - flameW; x <= fx + flameW; x++)
            {
                float normX = x / scaleX;
                float normY = y / scaleY;
                float n = (AsciiKit.Noise2(normX * 0.5f, (normY * 0.45f) + (_time * 6f), 3f) * 0.6f) +
                          (AsciiKit.Noise2(normX * 1.1f, (normY * 0.9f) + (_time * 9f), 8f) * 0.4f);
                float e = 1f - (MathF.Abs(x - fx - (MathF.Sin((_time * 3f) + (normY * 0.4f)) * h * 1.5f * scaleX)) / width);
                float v = (e * 1.2f) - (h * 0.6f) + ((n - 0.5f) * 0.9f);
                if (v > 0.05f)
                {
                    ushort glyph = v > 0.7f ? _lut.FullBlock : (v > 0.4f ? _lut.DarkShade : (v > 0.2f ? _lut.MediumShade : _lut.LightShade));
                    byte shade = (byte)Math.Clamp((int)(v * 15f * flick), 3, 15);
                    buffer.SetCell(x, y, glyph, shade, 2); // Palette 2: Amber flame
                }
            }
        }

        // 8. Rising sparks drifting up into the stars
        for (int i = 0; i < _sparks.Length; i++)
        {
            ref readonly var s = ref _sparks[i];
            float life = ((_time * 0.35f * s.Speed) + s.Offset) % 1f;
            float sparkX = fx + (MathF.Sin(s.Phase + (_time * 1.5f)) * (1f + (life * 6f)) * scaleX) + ((s.Sway - 0.5f) * life * 14f * scaleX);
            float sparkY = fy - 6 - (life * 24f * scaleY);
            if (MathF.Sin((_time * 20f) + (s.Phase * 7f)) > -0.3f)
            {
                char sparkChar = life < 0.3f ? '*' : (life < 0.7f ? '•' : '·');
                byte sparkShade = (byte)Math.Clamp((int)((1f - life) * 12f) + 3, 3, 15);
                buffer.SetCell((int)MathF.Round(sparkX), (int)MathF.Round(sparkY), _lut[sparkChar], sparkShade, 2);
            }
        }
    }

    private void DrawText(AsciiFrameBuffer buffer, int startX, int y, string text, byte shade, byte palette)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c != ' ')
            {
                buffer.SetCell(startX + i, y, _lut[c], shade, palette);
            }
        }
    }

    private static void DrawLine(AsciiFrameBuffer buffer, int x0, int y0, int x1, int y1, ushort glyph, byte shade, byte palette)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        while (true)
        {
            buffer.SetCell(x0, y0, glyph, shade, palette);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    public void Dispose()
    {
        _stars = [];
        _trees = [];
        _sparks = [];
    }
}

