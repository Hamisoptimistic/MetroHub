using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;

namespace MetroHub.Core.LiveWallpapers.Engines;

public sealed class LuminanceSamplerEngine : IAsciiWallpaperScene
{
    public string SceneId { get; }
    public string DisplayName { get; }
    private readonly string? _assetPath;
    private readonly byte _palette;

    private int _cols;
    private int _rows;
    private AsciiGlyphLut _lut = null!;

    private byte[] _videoFrames = [];
    private int _frameCount;
    private float _fps = 24f;
    private float _time;

    private ushort _glyphPipe;
    private ushort _glyphUnderscore;
    private ushort _glyphSlash;
    private ushort _glyphBackslash;

    public LuminanceSamplerEngine(string sceneId, string displayName, string? assetPath = null, byte palette = 0)
    {
        SceneId = sceneId;
        DisplayName = displayName;
        _assetPath = assetPath;
        _palette = palette;
    }

    public void Initialize(int columns, int rows, AsciiGlyphLut glyphLut)
    {
        _cols = columns;
        _rows = rows;
        _lut = glyphLut;

        _glyphPipe = glyphLut['|'];
        _glyphUnderscore = glyphLut['_'];
        _glyphSlash = glyphLut['/'];
        _glyphBackslash = glyphLut['\\'];

        if (!string.IsNullOrEmpty(_assetPath) && File.Exists(_assetPath))
        {
            LoadBrotliStream(_assetPath);
        }
        else
        {
            GenerateFallbackProceduralLoop();
        }
    }

    private void LoadBrotliStream(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        using var brotli = new BrotliStream(fs, CompressionMode.Decompress);
        using var br = new BinaryReader(brotli);

        int frames = br.ReadInt32();
        int fps = Math.Clamp(br.ReadInt32(), 10, 60);
        int totalBytes = frames * _cols * _rows;

        byte[] data = new byte[totalBytes];
        int bytesRead = brotli.ReadAtLeast(data, totalBytes, throwOnEndOfStream: false);
        if (frames <= 0 || bytesRead < totalBytes)
        {
            GenerateFallbackProceduralLoop();
            return;
        }

        _frameCount = frames;
        _fps = fps;
        _videoFrames = data;
    }

    private void GenerateFallbackProceduralLoop()
    {
        _frameCount = 60;
        _fps = 30f;
        int frameSize = _cols * _rows;
        _videoFrames = new byte[_frameCount * frameSize];

        for (int f = 0; f < _frameCount; f++)
        {
            float t = (f / (float)_frameCount) * MathF.Tau;
            int baseOffset = f * frameSize;
            for (int y = 0; y < _rows; y++)
            {
                for (int x = 0; x < _cols; x++)
                {
                    float dx = (x - (_cols * 0.5f)) * 0.55f;
                    float dy = y - (_rows * 0.5f);
                    float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                    float ring = MathF.Sin((dist * 0.45f) - t) * MathF.Exp(-dist * 0.035f);
                    byte lum = (byte)Math.Clamp((int)((ring + 0.5f) * 210f), 0, 255);
                    _videoFrames[baseOffset + (y * _cols) + x] = lum;
                }
            }
        }
    }

    public void Update(float dt) => _time += dt;

    public void Render(AsciiFrameBuffer buffer)
    {
        if (_frameCount == 0) return;

        int frameIdx = (int)(_time * _fps) % _frameCount;
        int frameSize = _cols * _rows;
        ReadOnlySpan<byte> frame = _videoFrames.AsSpan(frameIdx * frameSize, frameSize);

        for (int y = 1; y < _rows - 1; y++)
        {
            int rowOffset = y * _cols;
            for (int x = 1; x < _cols - 1; x++)
            {
                byte center = frame[rowOffset + x];
                if (center < 18)
                {
                    buffer.SetCell(x, y, 0, 0);
                    continue;
                }

                int gx =
                    (frame[rowOffset - _cols + x + 1] + (2 * frame[rowOffset + x + 1]) + frame[rowOffset + _cols + x + 1]) -
                    (frame[rowOffset - _cols + x - 1] + (2 * frame[rowOffset + x - 1]) + frame[rowOffset + _cols + x - 1]);

                int gy =
                    (frame[rowOffset + _cols + x - 1] + (2 * frame[rowOffset + _cols + x]) + frame[rowOffset + _cols + x + 1]) -
                    (frame[rowOffset - _cols + x - 1] + (2 * frame[rowOffset - _cols + x]) + frame[rowOffset - _cols + x + 1]);

                int mag = Math.Abs(gx) + Math.Abs(gy);
                byte shade = (byte)Math.Clamp(center >> 4, 1, 15);

                if (mag > 320)
                {
                    ushort edgeGlyph = SelectEdgeGlyph(gx, gy);
                    buffer.SetCell(x, y, edgeGlyph, (byte)Math.Min(15, shade + 2), _palette);
                }
                else
                {
                    buffer.SetCell(x, y, _lut.LuminanceRampGlyphs[shade], shade, _palette);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort SelectEdgeGlyph(int gx, int gy)
    {
        int ax = Math.Abs(gx);
        int ay = Math.Abs(gy);

        if (ax > ay * 2) return _glyphPipe;
        if (ay > ax * 2) return _glyphUnderscore;
        return (gx * gy) > 0 ? _glyphBackslash : _glyphSlash;
    }

    public void Dispose() { }
}

