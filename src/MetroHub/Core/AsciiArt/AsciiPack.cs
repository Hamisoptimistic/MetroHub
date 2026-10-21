using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using Serilog;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Header information for one <c>.mhp</c> frame pack. Used by the scene menu
/// without decoding the frame body.
/// </summary>
public readonly record struct AsciiPackInfo(string Slug, string Name);

/// <summary>How a pack draws one frame.</summary>
public enum AsciiRenderMode : byte
{
    /// <summary>Monospace glyphs from the glyph table.</summary>
    Glyphs = 0,

    /// <summary>Halftone dots on a density grid, sized by cell brightness.</summary>
    Halftone = 1,
}

/// <summary>How a pack wraps from its last frame back to the first.</summary>
public enum AsciiLoopMode : byte
{
    /// <summary>Frame N-1 is followed by frame 0.</summary>
    Loop = 0,

    /// <summary>Play 0..N-1 then back to 1, so the wrap never jumps or repeats a frame.</summary>
    PingPong = 1,
}

/// <summary>
/// Halftone tuning baked into a v2 pack by <c>tools/ascii-pack</c>, taken from
/// the piece's <c>meta.dots</c> and the ascii-magic-art engine defaults.
/// </summary>
public readonly record struct AsciiHalftoneOptions(
    float Density,
    float DotScale,
    float ToneLow,
    float ToneHigh,
    float ToneCurve,
    float Saturation);

/// <summary>
/// Reads a <c>.mhp</c> frame pack written by <c>tools/ascii-pack</c>.
/// A pack holds every frame of one ascii-magic piece as glyph ids and palette ids.
/// Version 2 packs declare a render mode: text glyphs, or halftone dots with
/// baked tuning (<see cref="AsciiHalftoneOptions"/>). Version 3 adds a loop mode
/// (<see cref="AsciiLoopMode"/>). No JavaScript is involved at run time.
/// </summary>
public sealed class AsciiPack
{
    private const uint Magic = 0x3150484D; // "MHP1", little-endian
    private const ushort MinFormatVersion = 1;
    private const ushort FormatVersion = 3;
    private const int MaxCells = 65535;
    private const int MaxDecodedBytes = 64 * 1024 * 1024;

    private static readonly Color DefaultInk = Color.FromRgb(0xE4, 0xE4, 0xE7);

    public string Slug { get; private init; } = string.Empty;
    public string Name { get; private init; } = string.Empty;
    public int Cols { get; private init; }
    public int Rows { get; private init; }
    public int Fps { get; private init; }
    public int FrameCount { get; private init; }

    /// <summary>How the pack renders: text glyphs, or halftone dots.</summary>
    public AsciiRenderMode RenderMode { get; private init; }

    /// <summary>How playback wraps from the last frame back to the first.</summary>
    public AsciiLoopMode LoopMode { get; private init; }

    /// <summary>Halftone tuning, only meaningful when <see cref="RenderMode"/> is halftone.</summary>
    public AsciiHalftoneOptions HalftoneOptions { get; private init; }

    /// <summary>Cell height divided by cell width. 1.661 for text pieces, 1.0 for photo pieces.</summary>
    public double CellAspect { get; private init; } = 1.0;

    /// <summary>Palette entry 0 is the default ink. Entry N maps to palette key N-1.</summary>
    public Color[] Colors { get; private init; } = [];

    public char[] Glyphs { get; private init; } = [];

    /// <summary>Frame-major glyph ids, <see cref="FrameCount"/> * <see cref="Cells"/> entries.</summary>
    public byte[] GlyphIds { get; private init; } = [];

    /// <summary>Frame-major palette ids, <see cref="FrameCount"/> * <see cref="Cells"/> entries.</summary>
    public byte[] ColorIds { get; private init; } = [];

    public int Cells => Cols * Rows;

    /// <summary>
    /// Maps a monotonically increasing playback tick to a frame index. A forward
    /// loop wraps N-1 to 0; a ping-pong pack plays 0..N-1 then back to 1, so the
    /// wrap never jumps or shows the same frame twice.
    /// </summary>
    public static int GetPlaybackFrame(long tick, int frameCount, AsciiLoopMode loopMode)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(tick);

        if (loopMode != AsciiLoopMode.PingPong || frameCount <= 2)
        {
            return (int)(tick % frameCount);
        }

        int period = (frameCount * 2) - 2;
        int position = (int)(tick % period);
        return position < frameCount ? position : period - position;
    }

    /// <summary>Folder that holds the shipped packs, next to the app executable.</summary>
    public static string PackDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "AsciiArt");

    public static string GetPackPath(string slug) => Path.Combine(PackDirectory, slug + ".mhp");

    /// <summary>Lists every valid pack in <see cref="PackDirectory"/>, sorted by display name.</summary>
    public static IReadOnlyList<AsciiPackInfo> EnumerateAvailable()
    {
        string directory = PackDirectory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<AsciiPackInfo> packs = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.mhp"))
        {
            try
            {
                packs.Add(ReadInfo(file));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Skipping invalid ASCII scene pack {File}", file);
            }
        }

        packs.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return packs;
    }

    /// <summary>Reads the header and the name table only.</summary>
    public static AsciiPackInfo ReadInfo(string path)
    {
        using FileStream file = File.OpenRead(path);
        using BinaryReader reader = new(file, Encoding.UTF8, leaveOpen: false);

        _ = ReadCoreHeader(reader, path);

        int paletteCount = reader.ReadUInt16();
        reader.BaseStream.Seek(paletteCount * 3L, SeekOrigin.Current);
        int glyphCount = reader.ReadUInt16();
        reader.BaseStream.Seek(glyphCount * 2L, SeekOrigin.Current);

        string slug = ReadShortString(reader, path);
        string name = ReadShortString(reader, path);
        return new AsciiPackInfo(slug, name);
    }

    /// <summary>Reads and decodes one pack. Throws <see cref="InvalidDataException"/> on bad input.</summary>
    public static AsciiPack Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        using BinaryReader reader = new(file, Encoding.UTF8, leaveOpen: false);

        CoreHeader header = ReadCoreHeader(reader, path);

        int paletteCount = reader.ReadUInt16();
        if (paletteCount > 255)
        {
            throw new InvalidDataException($"{path}: palette has {paletteCount} entries, max is 255.");
        }

        Color[] colors = new Color[paletteCount + 1];
        colors[0] = DefaultInk;
        for (int i = 0; i < paletteCount; i++)
        {
            byte r = reader.ReadByte();
            byte g = reader.ReadByte();
            byte b = reader.ReadByte();
            colors[i + 1] = Color.FromRgb(r, g, b);
        }

        int glyphCount = reader.ReadUInt16();
        if (glyphCount is < 1 or > 256)
        {
            throw new InvalidDataException($"{path}: glyph table has {glyphCount} entries, max is 256.");
        }

        char[] glyphs = new char[glyphCount];
        for (int i = 0; i < glyphCount; i++)
        {
            glyphs[i] = (char)reader.ReadUInt16();
        }

        string slug = ReadShortString(reader, path);
        string name = ReadShortString(reader, path);

        uint rawLength = reader.ReadUInt32();
        uint gzipLength = reader.ReadUInt32();
        if (rawLength == 0 || rawLength > MaxDecodedBytes)
        {
            throw new InvalidDataException($"{path}: frame data has an invalid size ({rawLength} bytes).");
        }

        byte[] compressed = reader.ReadBytes(checked((int)gzipLength));
        if (compressed.Length != gzipLength)
        {
            throw new InvalidDataException($"{path}: frame data is truncated.");
        }

        byte[] body = new byte[rawLength];
        using (MemoryStream compressedStream = new(compressed, writable: false))
        using (GZipStream gzip = new(compressedStream, CompressionMode.Decompress))
        {
            int read = 0;
            while (read < body.Length)
            {
                int chunk = gzip.Read(body, read, body.Length - read);
                if (chunk == 0)
                {
                    break;
                }
                read += chunk;
            }

            if (read != body.Length)
            {
                throw new InvalidDataException($"{path}: frame data is shorter than declared.");
            }
        }

        int cells = header.Cols * header.Rows;
        long totalBytes = (long)cells * header.FrameCount;
        if (totalBytes > MaxDecodedBytes)
        {
            throw new InvalidDataException($"{path}: decoded frames are too large ({totalBytes} bytes).");
        }

        byte[] glyphIds = new byte[(int)totalBytes];
        byte[] colorIds = new byte[(int)totalBytes];
        DecodeFrames(body, cells, header.FrameCount, glyphIds, colorIds);

        return new AsciiPack
        {
            Slug = slug,
            Name = name,
            Cols = header.Cols,
            Rows = header.Rows,
            Fps = header.Fps,
            FrameCount = header.FrameCount,
            CellAspect = header.AspectMilli / 1000.0,
            RenderMode = header.RenderMode,
            LoopMode = header.LoopMode,
            HalftoneOptions = header.HalftoneOptions,
            Colors = colors,
            Glyphs = glyphs,
            GlyphIds = glyphIds,
            ColorIds = colorIds,
        };
    }

    private static CoreHeader ReadCoreHeader(BinaryReader reader, string path)
    {
        if (reader.ReadUInt32() != Magic)
        {
            throw new InvalidDataException($"{path}: not a MetroHub ASCII pack.");
        }

        ushort version = reader.ReadUInt16();
        if (version is < MinFormatVersion or > FormatVersion)
        {
            throw new InvalidDataException($"{path}: unsupported pack version {version}.");
        }

        AsciiRenderMode renderMode = AsciiRenderMode.Glyphs;
        AsciiHalftoneOptions halftone = default;
        if (version >= 2)
        {
            byte rawMode = reader.ReadByte();
            if (rawMode > (byte)AsciiRenderMode.Halftone)
            {
                throw new InvalidDataException($"{path}: unknown render mode {rawMode}.");
            }

            renderMode = (AsciiRenderMode)rawMode;
            if (renderMode == AsciiRenderMode.Halftone)
            {
                halftone = ReadHalftoneOptions(reader, path);
            }
        }

        AsciiLoopMode loopMode = AsciiLoopMode.Loop;
        if (version >= 3)
        {
            byte rawLoop = reader.ReadByte();
            if (rawLoop > (byte)AsciiLoopMode.PingPong)
            {
                throw new InvalidDataException($"{path}: unknown loop mode {rawLoop}.");
            }

            loopMode = (AsciiLoopMode)rawLoop;
        }

        int cols = reader.ReadUInt16();
        int rows = reader.ReadUInt16();
        int fps = reader.ReadUInt16();
        int frameCount = reader.ReadUInt16();
        int aspectMilli = reader.ReadUInt16();

        if (cols < 1 || rows < 1 || cols * rows > MaxCells)
        {
            throw new InvalidDataException($"{path}: invalid grid {cols}x{rows}.");
        }
        if (fps is < 1 or > 60)
        {
            throw new InvalidDataException($"{path}: invalid frame rate {fps}.");
        }
        if (frameCount < 1)
        {
            throw new InvalidDataException($"{path}: pack has no frames.");
        }
        if (aspectMilli is < 500 or > 3000)
        {
            throw new InvalidDataException($"{path}: invalid cell aspect {aspectMilli}.");
        }

        return new CoreHeader(cols, rows, fps, frameCount, aspectMilli, renderMode, halftone, loopMode);
    }

    private static AsciiHalftoneOptions ReadHalftoneOptions(BinaryReader reader, string path)
    {
        float density = reader.ReadUInt16() / 100f;
        float dotScale = reader.ReadUInt16() / 1000f;
        float toneLow = reader.ReadUInt16() / 1000f;
        float toneHigh = reader.ReadUInt16() / 1000f;
        float toneCurve = reader.ReadUInt16() / 100f;
        float saturation = reader.ReadUInt16() / 1000f;

        if (density is < 0.3f or > 4f
            || dotScale is < 0f or > 3f
            || toneLow is < 0f or > 1f
            || toneHigh is < 0f or > 1f
            || toneCurve is < 0.1f or > 4f
            || saturation is < 0f or > 2f)
        {
            throw new InvalidDataException($"{path}: halftone tuning is out of range.");
        }

        return new AsciiHalftoneOptions(density, dotScale, toneLow, toneHigh, toneCurve, saturation);
    }

    private readonly record struct CoreHeader(
        int Cols,
        int Rows,
        int Fps,
        int FrameCount,
        int AspectMilli,
        AsciiRenderMode RenderMode,
        AsciiHalftoneOptions HalftoneOptions,
        AsciiLoopMode LoopMode);

    private static string ReadShortString(BinaryReader reader, string path)
    {
        int length = reader.ReadByte();
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException($"{path}: name table is truncated.");
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static void DecodeFrames(byte[] body, int cells, int frameCount, byte[] glyphIds, byte[] colorIds)
    {
        int maskLength = (cells + 7) >> 3;
        int cursor = 0;

        for (int frame = 0; frame < frameCount; frame++)
        {
            byte flag = ReadBodyByte(body, ref cursor);
            int frameBase = frame * cells;

            if (flag == 1)
            {
                for (int i = 0; i < cells; i++)
                {
                    glyphIds[frameBase + i] = ReadBodyByte(body, ref cursor);
                    colorIds[frameBase + i] = ReadBodyByte(body, ref cursor);
                }
                continue;
            }

            if (flag != 0)
            {
                throw new InvalidDataException($"Bad frame flag {flag} at frame {frame}.");
            }
            if (frame == 0)
            {
                throw new InvalidDataException("The first frame must be a full frame.");
            }

            Buffer.BlockCopy(glyphIds, frameBase - cells, glyphIds, frameBase, cells);
            Buffer.BlockCopy(colorIds, frameBase - cells, colorIds, frameBase, cells);

            // All mask bytes come first, then the changed glyph/color pairs.
            int maskStart = cursor;
            if (maskStart + maskLength > body.Length)
            {
                throw new InvalidDataException($"Delta mask is truncated at frame {frame}.");
            }
            cursor += maskLength;

            for (int cell = 0; cell < cells; cell++)
            {
                byte mask = body[maskStart + (cell >> 3)];
                if ((mask & (1 << (cell & 7))) == 0)
                {
                    continue;
                }

                glyphIds[frameBase + cell] = ReadBodyByte(body, ref cursor);
                colorIds[frameBase + cell] = ReadBodyByte(body, ref cursor);
            }
        }

        if (cursor != body.Length)
        {
            throw new InvalidDataException($"Frame data has {body.Length - cursor} unused bytes.");
        }
    }

    private static byte ReadBodyByte(byte[] body, ref int cursor)
    {
        if ((uint)cursor >= (uint)body.Length)
        {
            throw new InvalidDataException("Frame data ended early.");
        }
        return body[cursor++];
    }
}
