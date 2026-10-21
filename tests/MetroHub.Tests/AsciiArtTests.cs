using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using MetroHub.Core.AsciiArt;
using Xunit;

namespace MetroHub.Tests;

public class AsciiPackFormatTests
{
    [Fact]
    public void V2HalftonePack_ExposesModeAndTuning()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(1, 1), (1, 1), (2, 1), (2, 1)]));
        AsciiPack pack = AsciiPack.Load(file.Path);

        Assert.Equal(AsciiRenderMode.Halftone, pack.RenderMode);
        Assert.Equal(1.31f, pack.HalftoneOptions.Density, 3);
        Assert.Equal(0.6f, pack.HalftoneOptions.DotScale, 3);
        Assert.Equal(0.04f, pack.HalftoneOptions.ToneLow, 3);
        Assert.Equal(0.985f, pack.HalftoneOptions.ToneHigh, 3);
        Assert.Equal(1.1f, pack.HalftoneOptions.ToneCurve, 3);
        Assert.Equal(0.82f, pack.HalftoneOptions.Saturation, 3);
        Assert.Equal(4, pack.Cols);
        Assert.Equal(1, pack.Rows);
        Assert.Equal(1, pack.FrameCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void GlyphPack_ReportsGlyphMode(ushort version)
    {
        using TestPackFile file = new(TestPack.BuildGlyphs(version));
        AsciiPack pack = AsciiPack.Load(file.Path);

        Assert.Equal(AsciiRenderMode.Glyphs, pack.RenderMode);
    }

    [Fact]
    public void UnknownVersion_IsRejected()
    {
        using TestPackFile file = new(TestPack.BuildGlyphs(version: 4));

        Assert.Throws<InvalidDataException>(() => AsciiPack.Load(file.Path));
    }

    [Fact]
    public void UnknownRenderMode_IsRejected()
    {
        using TestPackFile file = new(TestPack.BuildGlyphs(version: 2, mode: 2));

        Assert.Throws<InvalidDataException>(() => AsciiPack.Load(file.Path));
    }

    [Fact]
    public void V3PingPongPack_ExposesLoopMode()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(1, 1), (1, 1), (2, 1), (2, 1)], version: 3, loopMode: 1));

        AsciiPack pack = AsciiPack.Load(file.Path);

        Assert.Equal(AsciiLoopMode.PingPong, pack.LoopMode);
    }

    [Fact]
    public void V2Pack_DefaultsToForwardLoop()
    {
        using TestPackFile file = new(TestPack.BuildGlyphs(version: 2));

        AsciiPack pack = AsciiPack.Load(file.Path);

        Assert.Equal(AsciiLoopMode.Loop, pack.LoopMode);
    }

    [Fact]
    public void UnknownLoopMode_IsRejected()
    {
        using TestPackFile file = new(TestPack.BuildGlyphs(version: 3, loopMode: 2));

        Assert.Throws<InvalidDataException>(() => AsciiPack.Load(file.Path));
    }

    [Fact]
    public void ReadInfo_ReportsNamesOfHalftonePacks()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(0, 1), (0, 1), (0, 1), (0, 1)]));

        AsciiPackInfo info = AsciiPack.ReadInfo(file.Path);

        Assert.Equal("test-scene", info.Slug);
        Assert.Equal("Test Scene", info.Name);
    }

    [Theory]
    [InlineData("moon-jellyfish", 0.85f)]
    [InlineData("switchback-dusk-drive", 0.82f)]
    [InlineData("towering-cumulus-sky", 0.85f)]
    public void ShippedDotsPack_IsHalftone(string slug, float saturation)
    {
        string path = AsciiPack.GetPackPath(slug);
        Assert.True(File.Exists(path), $"Shipped pack not found at {path}.");

        AsciiPack pack = AsciiPack.Load(path);

        Assert.Equal(AsciiRenderMode.Halftone, pack.RenderMode);
        Assert.Equal(AsciiLoopMode.Loop, pack.LoopMode);
        Assert.Equal(1.31f, pack.HalftoneOptions.Density, 2);
        Assert.Equal(0.6f, pack.HalftoneOptions.DotScale, 2);
        Assert.Equal(saturation, pack.HalftoneOptions.Saturation, 2);
    }
}

public class AsciiPlaybackTests
{
    [Fact]
    public void ForwardLoop_WrapsAfterTheLastFrame()
    {
        int[] expected = [0, 1, 2, 3, 0, 1];

        for (int tick = 0; tick < expected.Length; tick++)
        {
            Assert.Equal(expected[tick], AsciiPack.GetPlaybackFrame(tick, 4, AsciiLoopMode.Loop));
        }
    }

    [Fact]
    public void PingPong_PlaysForwardThenBackWithoutDuplicates()
    {
        int[] expected = [0, 1, 2, 3, 2, 1, 0, 1];

        for (int tick = 0; tick < expected.Length; tick++)
        {
            Assert.Equal(expected[tick], AsciiPack.GetPlaybackFrame(tick, 4, AsciiLoopMode.PingPong));
        }
    }

    [Fact]
    public void PingPong_WithTwoFramesStaysAForwardLoop()
    {
        Assert.Equal(1, AsciiPack.GetPlaybackFrame(1, 2, AsciiLoopMode.PingPong));
        Assert.Equal(0, AsciiPack.GetPlaybackFrame(2, 2, AsciiLoopMode.PingPong));
        Assert.Equal(1, AsciiPack.GetPlaybackFrame(3, 2, AsciiLoopMode.PingPong));
    }

    [Fact]
    public void Playback_RejectsInvalidInput()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AsciiPack.GetPlaybackFrame(0, 0, AsciiLoopMode.Loop));
        Assert.Throws<ArgumentOutOfRangeException>(() => AsciiPack.GetPlaybackFrame(-1, 4, AsciiLoopMode.Loop));
    }
}

public class HalftonePainterTests
{
    private const uint Canvas = 0xFF0B0B0F;

    [Fact]
    public void Render_EmptyFrame_LeavesCanvasUntouched()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(0, 1), (0, 1), (0, 1), (0, 1)]));
        AsciiPack pack = AsciiPack.Load(file.Path);
        HalftonePainter painter = new(pack, cellWidth: 8, cellHeight: 8);
        uint[] target = NewCanvas(pack);

        painter.Render(target, 0);

        Assert.Equal(0, CountInked(target, 32, 0, 32));
    }

    [Fact]
    public void Render_DrawsDotsForBrightCellsOnly()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(1, 1), (1, 1), (2, 1), (2, 1)]));
        AsciiPack pack = AsciiPack.Load(file.Path);
        HalftonePainter painter = new(pack, cellWidth: 8, cellHeight: 8);
        uint[] target = NewCanvas(pack);

        painter.Render(target, 0);

        Assert.True(CountInked(target, 32, 20, 28) > 0, "Bright cells should draw dots.");
        Assert.True(CountInked(target, 32, 20, 28) > CountInked(target, 32, 0, 4), "Bright cells should draw bigger dots than dim cells.");
    }

    [Fact]
    public void Render_RejectsFrameIndexOutsideThePack()
    {
        using TestPackFile file = new(TestPack.BuildHalftone(TestPack.Ramp, [(2, 1), (2, 1), (2, 1), (2, 1)]));
        AsciiPack pack = AsciiPack.Load(file.Path);
        HalftonePainter painter = new(pack, cellWidth: 8, cellHeight: 8);

        Assert.Throws<ArgumentOutOfRangeException>(() => painter.Render(NewCanvas(pack), 1));
    }

    private static uint[] NewCanvas(AsciiPack pack)
    {
        uint[] target = new uint[pack.Cols * 8 * pack.Rows * 8];
        target.AsSpan().Fill(Canvas);
        return target;
    }

    private static int CountInked(uint[] target, int width, int fromX, int toX)
    {
        int count = 0;
        for (int y = 0; y < target.Length / width; y++)
        {
            for (int x = fromX; x < toX; x++)
            {
                if (target[(y * width) + x] != Canvas)
                {
                    count++;
                }
            }
        }
        return count;
    }
}

/// <summary>Builds minimal packs for the format and painter tests.</summary>
internal static class TestPack
{
    /// <summary>Glyphs " ", "▁", "█" as ids 0, 1 and 2.</summary>
    public const string Ramp = " ▁█";

    private static readonly ushort[] HalftoneTuning = [131, 600, 40, 985, 110, 820];

    public static byte[] BuildHalftone(string glyphs, (int Glyph, int Color)[] cells, ushort version = 2, byte loopMode = 0) =>
        Build(version, 1, HalftoneTuning, glyphs, cells, 4, loopMode);

    public static byte[] BuildGlyphs(ushort version, byte? mode = null, byte loopMode = 0) =>
        Build(version, mode, null, "A", [(0, 1)], 1, loopMode);

    private static byte[] Build(ushort version, byte? mode, ushort[]? tuning, string glyphs, (int Glyph, int Color)[] cells, int cols, byte loopMode = 0)
    {
        int rows = cells.Length / cols;

        using MemoryStream raw = new();
        raw.WriteByte(1); // single full frame
        foreach ((int glyph, int color) in cells)
        {
            raw.WriteByte((byte)glyph);
            raw.WriteByte((byte)color);
        }

        byte[] rawBytes = raw.ToArray();
        byte[] gzipBytes;
        using (MemoryStream compressed = new())
        {
            using (GZipStream gzip = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write(rawBytes);
            }
            gzipBytes = compressed.ToArray();
        }

        using MemoryStream file = new();
        using (BinaryWriter writer = new(file, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("MHP1"u8);
            writer.Write(version);
            if (version >= 2)
            {
                writer.Write(mode ?? 0);
            }
            if (tuning is not null)
            {
                foreach (ushort value in tuning)
                {
                    writer.Write(value);
                }
            }
            if (version >= 3)
            {
                writer.Write(loopMode);
            }
            writer.Write((ushort)cols);
            writer.Write((ushort)rows);
            writer.Write((ushort)24);
            writer.Write((ushort)1);
            writer.Write((ushort)1000);

            writer.Write((ushort)1); // one palette colour: white
            writer.Write((byte)0xFF);
            writer.Write((byte)0xFF);
            writer.Write((byte)0xFF);

            writer.Write((ushort)glyphs.Length);
            foreach (char glyph in glyphs)
            {
                writer.Write((ushort)glyph);
            }

            writer.Write((byte)"test-scene".Length);
            writer.Write(Encoding.UTF8.GetBytes("test-scene"));
            writer.Write((byte)"Test Scene".Length);
            writer.Write(Encoding.UTF8.GetBytes("Test Scene"));

            writer.Write((uint)rawBytes.Length);
            writer.Write((uint)gzipBytes.Length);
            writer.Write(gzipBytes);
        }

        return file.ToArray();
    }
}

/// <summary>A temp <c>.mhp</c> file that deletes itself at the end of a test.</summary>
internal sealed class TestPackFile : IDisposable
{
    private static readonly string DirectoryPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MetroHub.AsciiArt.Tests");

    public TestPackFile(byte[] bytes)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);
        Path = System.IO.Path.Combine(DirectoryPath, Guid.NewGuid().ToString("N") + ".mhp");
        File.WriteAllBytes(Path, bytes);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
