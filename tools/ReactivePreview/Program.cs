using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MetroHub.Core.AsciiArt;

namespace ReactivePreview;

/// <summary>
/// Renders the audio-reactive halftone to PNG files with scripted musical input,
/// so the look can be tuned without launching MetroHub.
/// </summary>
internal static class Program
{
    private const uint CanvasPixel = 0xFF0B0B0F;

    [STAThread]
    private static int Main(string[] args)
    {
        string image = args.Length > 0
            ? args[0]
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Pictures",
                "Downloads",
                "wallhaven-lywpjl_1920x1080.png");

        string output = args.Length > 1
            ? args[1]
            : Path.Combine(Path.GetTempPath(), "metrohub-reactive-preview");

        if (!File.Exists(image))
        {
            Console.Error.WriteLine($"Image not found: {image}");
            return 1;
        }

        if (args.Length > 2 && args[2] == "bench")
        {
            RunPerf(image, iterations: 1200);
            return 0;
        }

        Directory.CreateDirectory(output);
        Console.WriteLine($"Image : {image}");
        Console.WriteLine($"Output: {output}");

        const int width = 1280;
        const int height = 720;

        Stopwatch buildTimer = Stopwatch.StartNew();
        TrueColorHalftoneEngine engine = new(image, width, height);
        buildTimer.Stop();
        Console.WriteLine($"Build : {buildTimer.ElapsedMilliseconds} ms ({width}x{height})");

        uint[] buffer = new uint[width * height];

        RunScenario("rest", engine, buffer, output, width, height, static (frame, bands) =>
        {
            _ = frame;
            Array.Clear(bands);
        }, captureFrames: [120, 150]);

        RunScenario("kick", engine, buffer, output, width, height, static (frame, bands) =>
        {
            Array.Clear(bands);
            bool hit = frame % 28 is 0 or 1 or 2;
            for (int band = 0; band < 4; band++)
            {
                bands[band] = hit ? 0.92f : 0.04f;
            }
        }, captureFrames: [196, 198, 200]);

        RunScenario("drop", engine, buffer, output, width, height, static (frame, bands) =>
        {
            for (int band = 0; band < bands.Length; band++)
            {
                bands[band] = 0.35f + (0.25f * MathF.Sin((frame * 0.07f) + band));
            }

            bool hit = frame % 28 is 0 or 1;
            if (hit)
            {
                for (int band = 0; band < 6; band++)
                {
                    bands[band] = 0.95f;
                }
            }
        }, captureFrames: [196, 198, 200]);

        RunScenario("hats", engine, buffer, output, width, height, static (frame, bands) =>
        {
            Array.Clear(bands);
            bool hit = frame % 14 is 0 or 1;
            for (int band = 11; band < bands.Length; band++)
            {
                bands[band] = hit ? 0.95f : 0.02f;
            }
        }, captureFrames: [196, 198]);

        RunPerf(image);
        return 0;
    }

    private static void RunScenario(
        string name,
        TrueColorHalftoneEngine engine,
        uint[] buffer,
        string outputDirectory,
        int width,
        int height,
        Action<int, float[]> feed,
        int[] captureFrames)
    {
        WaveField field = new();
        ReactiveDynamics dynamics = new();
        float[] bands = new float[ReactiveDynamics.BandCount];
        ReactiveState state = ReactiveState.Rest;

        int lastFrame = captureFrames[^1];
        for (int frame = 0; frame <= lastFrame; frame++)
        {
            feed(frame, bands);
            state = dynamics.Update(bands, 1f / 60f, field);

            if (!captureFrames.Contains(frame))
            {
                continue;
            }

            Stopwatch timer = Stopwatch.StartNew();
            buffer.AsSpan().Fill(CanvasPixel);
            engine.Render(buffer, state, field);
            timer.Stop();

            string path = Path.Combine(outputDirectory, $"{name}_{frame:000}.png");
            SavePng(path, buffer, width, height);

            Console.WriteLine(
                $"{name,-6} f{frame,4}  bass={state.Bass:0.00} mid={state.Mid:0.00} "
                + $"treb={state.Treble:0.00} pulse={state.Pulse:0.00} energy={state.Energy:0.00} "
                + $"idle={state.Idle:0.00} peak={field.Peak:0.000}  render={timer.Elapsed.TotalMilliseconds:0.0} ms");
        }
    }

    private static void RunPerf(string image, int iterations = 30)
    {
        const int width = 1920;
        const int height = 1080;

        TrueColorHalftoneEngine engine = new(image, width, height);
        WaveField field = new();
        ReactiveDynamics dynamics = new();
        float[] bands = new float[ReactiveDynamics.BandCount];
        uint[] buffer = new uint[width * height];

        for (int frame = 0; frame < 30; frame++)
        {
            bands[0] = 0.8f;
            bands[3] = 0.6f;
            bands[8] = 0.5f;
            bands[13] = 0.5f;
            dynamics.Update(bands, 1f / 60f, field);
        }

        ReactiveState state = dynamics.Current;

        double[] renderSamples = new double[iterations];
        double[] frameSamples = new double[iterations];

        for (int i = 0; i < iterations; i++)
        {
            long start = Stopwatch.GetTimestamp();
            buffer.AsSpan().Fill(CanvasPixel);
            long mid = Stopwatch.GetTimestamp();
            engine.Render(buffer, state, field);
            long end = Stopwatch.GetTimestamp();

            renderSamples[i] = (end - mid) * 1000.0 / Stopwatch.Frequency;
            frameSamples[i] = (end - start) * 1000.0 / Stopwatch.Frequency;
        }

        Array.Sort(renderSamples);
        Array.Sort(frameSamples);

        Console.WriteLine($"Perf  : 1920x1080 over {iterations} iterations (sorted percentiles)");
        Console.WriteLine($"  engine render: p10 {Percentile(renderSamples, 0.10):0.00}  "
            + $"p50 {Percentile(renderSamples, 0.50):0.00}  p90 {Percentile(renderSamples, 0.90):0.00} ms");
        Console.WriteLine($"  worker frame : p10 {Percentile(frameSamples, 0.10):0.00}  "
            + $"p50 {Percentile(frameSamples, 0.50):0.00}  p90 {Percentile(frameSamples, 0.90):0.00} ms");
        Console.WriteLine($"  cpu at 60fps : p10 {(Percentile(frameSamples, 0.10) / 16.667 * 100):0}%  "
            + $"p50 {(Percentile(frameSamples, 0.50) / 16.667 * 100):0}% of one core");
        Console.WriteLine($"  managed heap : {GC.GetTotalMemory(forceFullCollection: true) / 1048576.0:0.00} MB");

        MeasureBlit(pixelCount: width * height);
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)(sorted.Length * fraction);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    /// <summary>Measures the UI-thread copy into the WriteableBitmap back buffer.</summary>
    private static unsafe void MeasureBlit(int pixelCount)
    {
        nuint byteCount = (nuint)pixelCount * sizeof(uint);
        void* source = NativeMemory.AlignedAlloc(byteCount, 64);
        void* destination = NativeMemory.AlignedAlloc(byteCount, 64);
        try
        {
            new Span<uint>(source, pixelCount).Fill(CanvasPixel);

            const int copies = 200;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < copies; i++)
            {
                Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }

            long end = Stopwatch.GetTimestamp();
            double perCopy = (end - start) * 1000.0 / Stopwatch.Frequency / copies;
            Console.WriteLine($"  ui blit copy : {perCopy:0.00} ms ({byteCount / 1048576.0:0.0} MB)");
        }
        finally
        {
            NativeMemory.AlignedFree(source);
            NativeMemory.AlignedFree(destination);
        }

        MeasureBitmapMemory(pixelCount);
    }

    /// <summary>Reports what a 1080p WriteableBitmap costs the process in private bytes.</summary>
    private static void MeasureBitmapMemory(int pixelCount)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = Process.GetCurrentProcess().PrivateMemorySize64;

        WriteableBitmap bitmap = new(1920, 1080, 96, 96, PixelFormats.Pbgra32, null);
        bitmap.Lock();
        bitmap.Unlock();

        long after = Process.GetCurrentProcess().PrivateMemorySize64;
        double megabytes = (after - before) / 1048576.0;
        Console.WriteLine($"  writeablebmp : {megabytes:0.0} MB private bytes for {pixelCount / 1048576.0:0.0} MPx");
        GC.KeepAlive(bitmap);
    }

    private static void SavePng(string path, uint[] pixels, int width, int height)
    {
        byte[] bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        BitmapSource bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bytes,
            width * 4);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}
