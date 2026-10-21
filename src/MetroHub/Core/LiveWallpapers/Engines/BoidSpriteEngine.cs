using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace MetroHub.Core.LiveWallpapers.Engines;

public enum SpritePackType
{
    DolphinSanctuary,
    KoiPond,
    CyberFleet
}

[StructLayout(LayoutKind.Sequential)]
internal struct BoidEntity
{
    public float X;
    public float Y;
    public float VX;
    public float VY;
    public float Phase;
    public float Scale;
    public byte Palette;
    public byte BaseShade;
}

public sealed class BoidSpriteEngine : IAsciiWallpaperScene
{
    public string SceneId { get; }
    public string DisplayName { get; }
    private readonly SpritePackType _packType;

    private int _cols;
    private int _rows;
    private float _time;
    private AsciiGlyphLut _lut = null!;
    private readonly BoidEntity[] _entities = new BoidEntity[14];

    private string[][] _posesRight = null!;

    public BoidSpriteEngine(string sceneId, string displayName, SpritePackType packType)
    {
        SceneId = sceneId;
        DisplayName = displayName;
        _packType = packType;
    }

    public void Initialize(int columns, int rows, AsciiGlyphLut glyphLut)
    {
        _cols = columns;
        _rows = rows;
        _lut = glyphLut;
        _posesRight = BuildPoseSheet(_packType);

        for (int i = 0; i < _entities.Length; i++)
        {
            float depthFactor = (i % 3) switch { 0 => 1.0f, 1 => 0.72f, _ => 0.48f };
            _entities[i] = new BoidEntity
            {
                X = (i * 23) % columns,
                Y = 10 + ((i * 11) % (rows - 18)),
                VX = (6.5f + ((i % 5) * 2.2f)) * depthFactor,
                VY = 0f,
                Phase = i * 1.3f,
                Scale = depthFactor,
                Palette = (byte)(_packType == SpritePackType.KoiPond ? 2 : 0),
                BaseShade = (byte)Math.Clamp((int)(15 * depthFactor), 5, 15)
            };
        }
    }

    public void Update(float dt)
    {
        _time += dt;

        for (int i = 0; i < _entities.Length; i++)
        {
            ref BoidEntity e = ref _entities[i];
            float wave = MathF.Sin((_time * 1.9f) + e.Phase);
            e.VY = wave * 4.8f * e.Scale;

            e.X += e.VX * dt;
            e.Y += e.VY * dt;

            if (e.X > _cols + 18)
            {
                e.X = -18f;
                e.Y = 12f + ((e.Phase * 17f) % (_rows - 22));
            }
            e.Y = Math.Clamp(e.Y, 6f, _rows - 8f);
        }
    }

    public void Render(AsciiFrameBuffer buffer)
    {
        buffer.Clear();

        if (_packType != SpritePackType.CyberFleet)
        {
            int surfaceY = 5;
            for (int x = 0; x < _cols; x++)
            {
                int waveOffset = (int)(MathF.Sin((x * 0.14f) + (_time * 2.4f)) * 1.5f);
                int sy = surfaceY + waveOffset;
                buffer.SetCell(x, sy, _lut['~'], 9, 0);
                buffer.SetCell(x, sy + 1, _lut['-'], 4, 0);
            }
        }

        for (int i = 0; i < _entities.Length; i++)
        {
            ref readonly BoidEntity e = ref _entities[i];
            float pitch = MathF.Atan2(e.VY, e.VX);

            int poseIdx = pitch switch
            {
                < -0.35f => 4,
                < -0.12f => 3,
                <= 0.12f => 2,
                <= 0.35f => 1,
                _ => 0
            };

            StampSpriteSubCell(buffer, e.X, e.Y, _posesRight[poseIdx], e.BaseShade, e.Palette);
        }
    }

    private void StampSpriteSubCell(
        AsciiFrameBuffer buffer,
        float worldX,
        float worldY,
        string[] lines,
        byte baseShade,
        byte palette)
    {
        int originX = (int)MathF.Floor(worldX);
        int originY = (int)MathF.Floor(worldY);
        float fracX = worldX - originX;

        for (int r = 0; r < lines.Length; r++)
        {
            string line = lines[r];
            int ty = originY + r;
            if ((uint)ty >= (uint)_rows) continue;

            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (ch == ' ') continue;

                ushort glyph = _lut[ch];
                int tx = originX + c;

                bool isLeftEdge = c == 0 || line[c - 1] == ' ';
                bool isRightEdge = c == line.Length - 1 || line[c + 1] == ' ';

                byte effectiveShade = isLeftEdge
                    ? (byte)Math.Clamp((int)(baseShade * (1.0f - (fracX * 0.55f))), 2, 15)
                    : baseShade;

                buffer.SetCell(tx, ty, glyph, effectiveShade, palette);

                if (isRightEdge && fracX > 0.2f)
                {
                    byte leadShade = (byte)Math.Clamp((int)(baseShade * fracX * 0.65f), 1, 12);
                    buffer.SetCell(tx + 1, ty, _lut['.'], leadShade, palette);
                }
            }
        }
    }

    public static string[] LoadRexPaintLayer(Stream gzStream)
    {
        using var gzip = new GZipStream(gzStream, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);

        int version = reader.ReadInt32();
        int layerCount = reader.ReadInt32();
        if (layerCount <= 0) return [];

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        if (width <= 0 || height <= 0 || width > 1024 || height > 1024) return [];

        char[,] grid = new char[height, width];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                uint charCode = reader.ReadUInt32();
                reader.ReadBytes(6);
                grid[y, x] = (charCode is >= 32 and <= 126) ? (char)charCode : ' ';
            }
        }

        string[] result = new string[height];
        char[] rowBuf = new char[width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) rowBuf[x] = grid[y, x];
            result[y] = new string(rowBuf);
        }
        return result;
    }

    private static string[][] BuildPoseSheet(SpritePackType pack)
    {
        if (pack == SpritePackType.CyberFleet)
        {
            return
            [
                [ @"  ___     ", @" \   \__  ", @"  `===>-` " ],
                [ @"  ____    ", @"  \   \__ ", @"   `====> " ],
                [ @"   /|___  ", @" =[_|___)>", @"   \|     " ],
                [ @"   ____/  ", @"  /   /   ", @" `====>   " ],
                [ @"    /\    ", @"   /  \   ", @"  /====\  " ]
            ];
        }

        return
        [
            // 0: Steep Dive
            [
                @"  _.-~-.      ",
                @" `-.    \     ",
                @"    \    \__  ",
                @"     `--._o ) "
            ],
            // 1: Shallow Dive
            [
                @"   _.-~~~-.    ",
                @" >`  `-.   \   ",
                @"        `--._> "
            ],
            // 2: Level Glide
            [
                @"        _      ",
                @"    _.-~ )_    ",
                @" >`(  .___o)   ",
                @"    `~         "
            ],
            // 3: Shallow Climb
            [
                @"         _.-~) ",
                @"    _.-~`  o/  ",
                @" >`(_.----~`   "
            ],
            // 4: Breach / Leap Up
            [
                @"       __     ",
                @"    .-~ o)    ",
                @"   /   /      ",
                @"  /  /`       ",
                @" /`~`         "
            ]
        ];
    }

    public void Dispose() { }
}

