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

public class WaveFieldTests
{
    [Fact]
    public void Step_WithDrivenEmitter_StaysFiniteAndBounded()
    {
        WaveField field = new();

        for (int i = 0; i < 600; i++)
        {
            field.AddForce(0.5f, 0.5f, 0.05f, 3f);
            field.AddForce(0.25f, 0.7f, -0.04f, 1.5f);
            field.Step(1f / 60f);
        }

        field.Sample(0.5f, 0.5f, out float height, out float gradientX, out float gradientY, out float curlX, out float curlY);

        Assert.False(float.IsNaN(height));
        Assert.False(float.IsNaN(gradientX) || float.IsNaN(gradientY));
        Assert.False(float.IsNaN(curlX) || float.IsNaN(curlY));
        Assert.InRange(height, -4f, 4f);
        Assert.InRange(field.Peak, 0f, 4f);
    }

    [Fact]
    public void Step_AfterImpulse_DecaysWhenUndriven()
    {
        WaveField field = new();
        field.AddForce(0.5f, 0.5f, 0.08f, 2f);
        field.Step(1f / 60f);

        float initial = field.Peak;
        Assert.True(initial > 0f, "an impulse should move the field");

        for (int i = 0; i < 300; i++)
        {
            field.Step(1f / 60f);
        }

        Assert.True(field.Peak < initial, $"field should decay: {field.Peak} vs {initial}");
    }

    [Fact]
    public void Sample_ClampsOutsideTheGrid()
    {
        WaveField field = new();
        field.AddForce(0.5f, 0.5f, 0.05f, 4f);
        field.Step(1f / 60f);

        field.Sample(-5f, 5f, out float height, out _, out _, out _, out _);
        Assert.False(float.IsNaN(height));
        Assert.InRange(height, -4f, 4f);

        field.Sample(0.5f, 0.5f, out float centre, out _, out _, out _, out _);
        Assert.True(centre > 0f, "the impulse centre should be a crest");
    }

    [Fact]
    public void Step_LargeDeltaTime_DoesNotExplode()
    {
        WaveField field = new();
        field.AddForce(0.5f, 0.5f, 0.5f, 2f);

        for (int i = 0; i < 60; i++)
        {
            field.Step(0.5f);
        }

        Assert.InRange(field.Peak, 0f, 4f);
    }

    [Fact]
    public void Curl_IsNonZeroAndVariesAcrossTheGrid()
    {
        WaveField field = new();
        field.Sample(0.1f, 0.1f, out _, out _, out _, out float firstX, out float firstY);
        field.Sample(0.8f, 0.6f, out _, out _, out _, out float secondX, out float secondY);

        Assert.True(MathF.Abs(firstX) + MathF.Abs(firstY) > 0f);
        Assert.True(MathF.Abs(firstX - secondX) + MathF.Abs(firstY - secondY) > 0f);
    }
}

public class ReactiveDynamicsTests
{
    [Fact]
    public void Update_WithSilence_RampsIntoIdleDrift()
    {
        ReactiveDynamics dynamics = new();
        WaveField field = new();
        Span<float> bands = stackalloc float[ReactiveDynamics.BandCount];

        for (int frame = 0; frame < 300; frame++)
        {
            dynamics.Update(bands, 1f / 60f, field);
        }

        ReactiveState state = dynamics.Current;
        Assert.True(state.Idle > 0.9f, $"idle should ramp up, was {state.Idle}");
        Assert.True(state.IsSilent);
        Assert.Equal(0f, state.Energy);
        Assert.True(field.Peak > 0f, "idle drift should still move the field");
    }

    [Fact]
    public void Update_WithKickTrain_ProducesPulses()
    {
        ReactiveDynamics dynamics = new();
        WaveField field = new();
        float[] bands = new float[ReactiveDynamics.BandCount];

        float maximumPulse = 0f;
        for (int frame = 0; frame < 300; frame++)
        {
            bool kick = frame % 28 == 0;
            for (int band = 0; band < 4; band++)
            {
                bands[band] = kick ? 0.9f : 0f;
            }

            maximumPulse = Math.Max(maximumPulse, dynamics.Update(bands, 1f / 60f, field).Pulse);
        }

        Assert.True(maximumPulse > 0.4f, $"kick train should raise the pulse, was {maximumPulse}");
        Assert.True(field.Peak > 0.0005f);
        Assert.False(dynamics.Current.IsSilent);
    }

    [Fact]
    public void Update_WithLoudBands_RaisesBandEnergies()
    {
        ReactiveDynamics dynamics = new();
        WaveField field = new();
        float[] bands = new float[ReactiveDynamics.BandCount];
        Array.Fill(bands, 0.8f);

        ReactiveState state = ReactiveState.Rest;
        for (int frame = 0; frame < 120; frame++)
        {
            state = dynamics.Update(bands, 1f / 60f, field);
        }

        Assert.True(state.Bass > 0.5f);
        Assert.True(state.Treble > 0.5f);
        Assert.True(state.Energy > 0.5f);
        Assert.Equal(0f, state.Idle);
    }

    [Fact]
    public void Reset_ClearsMusicalState()
    {
        ReactiveDynamics dynamics = new();
        WaveField field = new();
        float[] bands = new float[ReactiveDynamics.BandCount];
        Array.Fill(bands, 0.9f);
        dynamics.Update(bands, 1f / 60f, field);

        dynamics.Reset();

        Assert.Equal(0f, dynamics.Current.Energy);
        Assert.Equal(0f, dynamics.Current.Pulse);
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

                WaveField field = new();

                uint[] bufferRest = new uint[64 * 64];
                bufferRest.AsSpan().Fill(Canvas);
                engine.Render(bufferRest, ReactiveState.Rest, field);

                int inkedRest = 0;
                foreach (uint p in bufferRest)
                {
                    if (p != Canvas) inkedRest++;
                }

                Assert.True(inkedRest > 0, "Engine should stamp inked dots for bright image.");

                // Drive the membrane so refraction actually moves ink.
                for (int i = 0; i < 24; i++)
                {
                    field.AddForce(0.25f, 0.5f, 0.06f, 3f);
                    field.Step(1f / 60f);
                }

                uint[] bufferMoved = new uint[64 * 64];
                bufferMoved.AsSpan().Fill(Canvas);
                ReactiveState loudFrame = new(1f, 1f, 1f, 1f, 1f, 0.5f, 0f, false);
                engine.Render(bufferMoved, loudFrame, field);

                int inkedMoved = 0;
                foreach (uint p in bufferMoved)
                {
                    if (p != Canvas) inkedMoved++;
                }

                Assert.True(inkedMoved > 0);
                Assert.False(bufferRest.AsSpan().SequenceEqual(bufferMoved), "Reactive frame should differ from the rest frame.");
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
