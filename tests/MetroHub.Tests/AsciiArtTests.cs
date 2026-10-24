using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    [InlineData("dusk-mountain-drive", 0.82f)]
    [InlineData("sunny-summer-sky", 0.85f)]
    [InlineData("rainy-window-hearth", 0.82f)]
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

public class FerroAcousticBassGovernorTests
{
    [Fact]
    public void StepPhysics_WithSilence_QuicklyEntersIdleSleep()
    {
        FerroAcousticBassGovernor governor = new();
        bool active = false;

        // Step with zero input for 180 ms (11 frames at 60 FPS)
        for (int frame = 0; frame < 11; frame++)
        {
            active = governor.StepPhysics(0f, 0f, 1f / 60f);
        }

        Assert.False(active, "Governor should return false (idle) within 180 ms of silence.");
        Assert.True(governor.IsIdle, "Governor should report IsIdle == true.");
        Assert.Equal(0f, governor.ZPiston);
    }

    [Fact]
    public void StepPhysics_WithVetoMidHigh_SuppressesFalseTriggers()
    {
        FerroAcousticBassGovernor governor = new();

        // Feed strong sub-bass accompanied by loud veto mid/high (vocal/snare/crackle)
        for (int frame = 0; frame < 60; frame++)
        {
            governor.StepPhysics(subBassBand: 0.60f, midHighBand: 0.90f, dt: 1f / 60f);
        }

        Assert.Equal(0f, governor.ZPiston);
        Assert.True(governor.IsIdle);
    }

    [Fact]
    public void StepPhysics_WithSubBassKick_TriggersInstantPistonPress()
    {
        FerroAcousticBassGovernor governor = new();

        // Baseline silence for a few frames
        for (int frame = 0; frame < 10; frame++)
        {
            governor.StepPhysics(0f, 0f, 1f / 60f);
        }

        // Clean sub-bass kick hit
        bool motion = governor.StepPhysics(subBassBand: 0.85f, midHighBand: 0.02f, dt: 1f / 60f);

        Assert.True(motion, "Sub-bass kick should trigger motion.");
        Assert.True(governor.ZPiston > 0.2f, $"Piston should punch inward on kick, was {governor.ZPiston}");
        Assert.False(governor.IsIdle);
    }

    [Fact]
    public void StepPhysics_DuffingOscillator_ProducesElasticRebound()
    {
        FerroAcousticBassGovernor governor = new();

        // Trigger an initial kick
        governor.StepPhysics(subBassBand: 0.95f, midHighBand: 0.01f, dt: 1f / 60f);
        Assert.True(governor.ZPiston > 0f);

        float minZ = 0f;
        // Observe Duffing spring oscillation over subsequent frames
        for (int frame = 0; frame < 30; frame++)
        {
            governor.StepPhysics(0f, 0f, 1f / 60f);
            minZ = MathF.Min(minZ, governor.ZPiston);
        }

        Assert.True(minZ < -0.05f, $"Duffing oscillator should rebound below 0 into negative thump, was {minZ}");
    }

    [Fact]
    public void Reset_ClearsAllPhysicsState()
    {
        FerroAcousticBassGovernor governor = new();
        governor.StepPhysics(subBassBand: 0.95f, midHighBand: 0.01f, dt: 1f / 60f);
        Assert.False(governor.IsIdle);

        governor.Reset();

        Assert.True(governor.IsIdle);
        Assert.Equal(0f, governor.ZPiston);
    }
}

public class AsciiPackFromImageTests
{
    [Fact]
    public void FromImage_RejectsNonExistentFile()
    {
        Assert.Throws<FileNotFoundException>(() => AsciiPack.FromImage(@"C:\non_existent_metrohub_test_img.jpg"));
    }

    [Fact]
    public void FromImage_ProducesValidHalftonePack()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempPng = Path.Combine(Path.GetTempPath(), $"metrohub_test_{Guid.NewGuid():N}.png");
            try
            {
                // Write a tiny 16x16 PNG file
                WriteSolidPng(tempPng, 16, 16, 200, 100, 50);

                AsciiPack pack = AsciiPack.FromImage(tempPng, targetCols: 16, targetRows: 9);

                Assert.Equal(AsciiRenderMode.Halftone, pack.RenderMode);
                Assert.Equal(16, pack.Cols);
                Assert.Equal(9, pack.Rows);
                Assert.Equal(144, pack.Cells);
                Assert.Equal(256, pack.Colors.Length);
                Assert.Equal(144, pack.GlyphIds.Length);
                Assert.Equal(144, pack.ColorIds.Length);
            }
            finally
            {
                if (File.Exists(tempPng))
                {
                    File.Delete(tempPng);
                }
            }
        });
    }

    private static void WriteSolidPng(string path, int width, int height, byte r, byte g, byte b)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        BitmapSource bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        using FileStream fs = File.Create(path);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
    }
}

public class TrueColorHalftoneEngineTests
{
    private const uint Canvas = 0xFF0B0B0F;

    [Fact]
    public void TrueColorHalftone_RendersAntialiasedDotsOverCanvas()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempPng = Path.Combine(Path.GetTempPath(), $"metrohub_truecolor_test_{Guid.NewGuid():N}.png");
            try
            {
                // Write a 64x64 bright cyan image
                WriteSolidPng(tempPng, 64, 64, 0, 200, 255);

                TrueColorHalftoneEngine engine = new(tempPng, targetWidth: 64, targetHeight: 64, cellSize: 6.0f);
                Assert.Equal(64, engine.Width);
                Assert.Equal(64, engine.Height);

                uint[] bufferRest = new uint[64 * 64];
                bufferRest.AsSpan().Fill(Canvas);
                engine.Render(bufferRest, zPiston: 0f);

                int inkedRest = 0;
                foreach (uint p in bufferRest)
                {
                    if (p != Canvas) inkedRest++;
                }

                Assert.True(inkedRest > 0, "Engine should stamp inked dots for bright image.");

                // Render with active toroidal press (zPiston = 0.8f)
                uint[] bufferPressed = new uint[64 * 64];
                bufferPressed.AsSpan().Fill(Canvas);
                engine.Render(bufferPressed, zPiston: 0.8f);

                int inkedPressed = 0;
                foreach (uint p in bufferPressed)
                {
                    if (p != Canvas) inkedPressed++;
                }

                Assert.True(inkedPressed > 0);
                Assert.False(bufferRest.AsSpan().SequenceEqual(bufferPressed), "Toroidal press frame should differ from the rest frame.");

                // Render with rebound thump (zPiston = -0.2f)
                uint[] bufferRebound = new uint[64 * 64];
                bufferRebound.AsSpan().Fill(Canvas);
                engine.Render(bufferRebound, zPiston: -0.2f);

                Assert.False(bufferRest.AsSpan().SequenceEqual(bufferRebound), "Rebound thump frame should differ from the rest frame.");
            }
            finally
            {
                if (File.Exists(tempPng))
                {
                    File.Delete(tempPng);
                }
            }
        });
    }

    [Fact]
    public void TrueColorHalftone_DarkBackground_RemainsAtRestDuringKick()
    {
        WpfTestHost.RunSta(() =>
        {
            string tempPng = Path.Combine(Path.GetTempPath(), $"metrohub_truecolor_dark_test_{Guid.NewGuid():N}.png");
            try
            {
                // Write a pure black 32x32 image (representing dark backdrop behind widgets)
                WriteSolidPng(tempPng, 32, 32, 0, 0, 0);

                TrueColorHalftoneEngine engine = new(tempPng, targetWidth: 32, targetHeight: 32, cellSize: 6.0f);

                uint[] bufferRest = new uint[32 * 32];
                bufferRest.AsSpan().Fill(Canvas);
                engine.Render(bufferRest, zPiston: 0f);

                uint[] bufferKick = new uint[32 * 32];
                bufferKick.AsSpan().Fill(Canvas);
                engine.Render(bufferKick, zPiston: 1.0f);

                // For pure dark background, susceptibility is 0, so kick does not alter the canvas
                Assert.True(bufferRest.AsSpan().SequenceEqual(bufferKick), "Dark background dots should have susceptibility ~0 and remain completely still during kick.");
            }
            finally
            {
                if (File.Exists(tempPng))
                {
                    File.Delete(tempPng);
                }
            }
        });
    }

    private static void WriteSolidPng(string path, int width, int height, byte r, byte g, byte b)
    {
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        BitmapSource bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        using FileStream fs = File.Create(path);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        encoder.Save(fs);
    }
}

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
