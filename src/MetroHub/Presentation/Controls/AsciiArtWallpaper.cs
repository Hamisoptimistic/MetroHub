using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MetroHub.Core.AsciiArt;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Draws an <see cref="AsciiPack"/> as animated text or halftone dots. One
/// glyph atlas and one bitmap are built per size, then each frame paints
/// straight into the bitmap's back buffer. The bitmap is presented through a
/// child <see cref="DrawingVisual"/>, so stopping playback releases the surface
/// synchronously. The element has no JavaScript, no video decoder, and no child
/// window.
/// </summary>
public sealed class AsciiArtWallpaper : FrameworkElement
{
    private const double ProbeFontSize = 100.0;

    // Opaque near-black canvas, close to the app's dark tint. Pbgra32 little-endian.
    private const byte CanvasRed = 0x0B;
    private const byte CanvasGreen = 0x0B;
    private const byte CanvasBlue = 0x0F;
    private const uint CanvasPixel = 0xFF0B0B0F;

    private static readonly FontFamily MonoFontFamily = new("Consolas, Cascadia Code, Segoe UI Mono");

    private readonly DrawingVisual _sceneVisual = new();

    private AsciiPack? _pack;
    private DispatcherTimer? _timer;
    private WriteableBitmap? _bitmap;
    private HalftonePainter? _halftone;

    private byte[] _atlas = [];
    private uint[] _colorLookup = [];
    private int _cellWidth;
    private int _cellHeight;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _drawWidth;
    private double _drawHeight;
    private double _offsetX;
    private double _offsetY;
    private long _frameTick;
    private int _frameIndex;

    public AsciiArtWallpaper()
    {
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetBitmapScalingMode(_sceneVisual, BitmapScalingMode.NearestNeighbor);
        AddVisualChild(_sceneVisual);
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += OnUnloaded;
    }

    public bool HasScene => _pack is not null;

    /// <summary>Loads a pack and starts playback. Replaces any current scene.</summary>
    public void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        AsciiPack pack = AsciiPack.Load(path);
        Unload();
        _pack = pack;
        _frameTick = 0;
        _frameIndex = 0;
        RebuildSurface();
        StartClock();
    }

    /// <summary>Stops playback and releases the frame buffers.</summary>
    public void Unload()
    {
        StopClock();
        _pack = null;
        _bitmap = null;
        _halftone = null;
        _atlas = [];
        _colorLookup = [];
        _cellWidth = 0;
        _cellHeight = 0;
        _pixelWidth = 0;
        _pixelHeight = 0;
        _drawWidth = 0;
        _drawHeight = 0;
        _offsetX = 0;
        _offsetY = 0;
        _frameTick = 0;
        _frameIndex = 0;
        PresentScene();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(index, 0);
        return _sceneVisual;
    }

    /// <summary>
    /// Draws the current bitmap into the child visual, or clears it when no
    /// bitmap is loaded. Clearing happens synchronously, so <see cref="Unload"/>
    /// releases the surface even while the element is collapsed.
    /// </summary>
    private void PresentScene()
    {
        using DrawingContext context = _sceneVisual.RenderOpen();
        if (_bitmap is null || _drawWidth <= 0 || _drawHeight <= 0)
        {
            return;
        }

        context.DrawImage(_bitmap, new Rect(_offsetX, _offsetY, _drawWidth, _drawHeight));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RebuildSurface();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RebuildSurface();
    }

    private void StartClock()
    {
        if (_pack is null)
        {
            return;
        }

        int fps = Math.Clamp(_pack.Fps, 1, 60);
        _timer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1.0 / fps),
        };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void StopClock()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            if (_pack is not null && _timer is null)
            {
                StartClock();
            }
        }
        else
        {
            StopClock();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopClock();

    private void OnTick(object? sender, EventArgs e)
    {
        AsciiPack? pack = _pack;
        if (pack is null || _bitmap is null || !IsVisible || ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        Window? window = Window.GetWindow(this);
        if (window is null || window.WindowState == WindowState.Minimized)
        {
            return;
        }

        _frameIndex = AsciiPack.GetPlaybackFrame(++_frameTick, pack.FrameCount, pack.LoopMode);
        RenderFrame();
    }

    /// <summary>Sizes the bitmap to whole device pixels and rebuilds the frame renderer.</summary>
    private void RebuildSurface()
    {
        AsciiPack? pack = _pack;
        _bitmap = null;
        _halftone = null;
        _atlas = [];
        _colorLookup = [];
        PresentScene();

        if (pack is null)
        {
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double availableWidth = ActualWidth * dpi.DpiScaleX;
        double availableHeight = ActualHeight * dpi.DpiScaleY;
        if (availableWidth < 2 || availableHeight < 2)
        {
            return;
        }

        // Sizing for UniformToFill (Cover): size cells so that when scaled to cover the
        // window, the backing bitmap has at least native device resolution.
        double scale = Math.Max(availableWidth / pack.Cols, availableHeight / (pack.Rows * pack.CellAspect));
        int cellWidth = Math.Clamp((int)Math.Ceiling(scale), 2, 256);
        int cellHeight = Math.Clamp((int)Math.Round(cellWidth * pack.CellAspect), 2, 512);

        _cellWidth = cellWidth;
        _cellHeight = cellHeight;
        _pixelWidth = cellWidth * pack.Cols;
        _pixelHeight = cellHeight * pack.Rows;
        _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96, 96, PixelFormats.Pbgra32, null);

        double bitmapW = _pixelWidth / dpi.DpiScaleX;
        double bitmapH = _pixelHeight / dpi.DpiScaleY;
        double fillScale = Math.Max(ActualWidth / bitmapW, ActualHeight / bitmapH);
        _drawWidth = Math.Round(bitmapW * fillScale, 2);
        _drawHeight = Math.Round(bitmapH * fillScale, 2);
        _offsetX = Math.Round((ActualWidth - _drawWidth) / 2.0, 2);
        _offsetY = Math.Round((ActualHeight - _drawHeight) / 2.0, 2);

        if (pack.RenderMode == AsciiRenderMode.Halftone)
        {
            _halftone = new HalftonePainter(pack, cellWidth, cellHeight);
        }
        else
        {
            BuildAtlas(pack, cellWidth, cellHeight);
            BuildColorLookup(pack);
        }

        RenderFrame();
        PresentScene();
    }

    /// <summary>Precomposites every palette color over the canvas for each glyph alpha.</summary>
    private void BuildColorLookup(AsciiPack pack)
    {
        uint[] lookup = new uint[pack.Colors.Length * 256];
        for (int colorIndex = 0; colorIndex < pack.Colors.Length; colorIndex++)
        {
            Color color = pack.Colors[colorIndex];
            int baseIndex = colorIndex * 256;

            for (int alpha = 0; alpha < 256; alpha++)
            {
                int inverse = 255 - alpha;
                uint red = (uint)(((color.R * alpha) + (CanvasRed * inverse)) / 255);
                uint green = (uint)(((color.G * alpha) + (CanvasGreen * inverse)) / 255);
                uint blue = (uint)(((color.B * alpha) + (CanvasBlue * inverse)) / 255);
                lookup[baseIndex + alpha] = 0xFF000000u | (red << 16) | (green << 8) | blue;
            }
        }

        _colorLookup = lookup;
    }

    /// <summary>Rasterizes each glyph once, at the current cell size, as an alpha mask.</summary>
    private void BuildAtlas(AsciiPack pack, int cellWidth, int cellHeight)
    {
        Typeface typeface = new(MonoFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        FormattedText probe = new("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, ProbeFontSize, Brushes.White, 1.0);
        double advancePerEm = Math.Max(0.1, probe.WidthIncludingTrailingWhitespace / ProbeFontSize);
        double emSize = Math.Min(cellWidth / advancePerEm, cellHeight);

        // The packer assumes a 0.602 em advance. Fit the actual font advance to
        // the cell so block glyphs touch and text keeps the source grid.
        double scaleX = cellWidth / (advancePerEm * emSize);
        bool stretch = Math.Abs(scaleX - 1.0) > 0.005;

        int glyphCount = pack.Glyphs.Length;
        int atlasWidth = glyphCount * cellWidth;

        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            for (int i = 0; i < glyphCount; i++)
            {
                char glyph = pack.Glyphs[i];
                if (glyph is ' ' or '\0')
                {
                    continue;
                }

                FormattedText text = new(glyph.ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, emSize, Brushes.White, 1.0);
                double glyphWidth = text.WidthIncludingTrailingWhitespace;
                double x = (i * cellWidth) + ((cellWidth - glyphWidth) / 2.0);
                double y = (cellHeight - text.Height) / 2.0;

                if (stretch)
                {
                    context.PushTransform(new ScaleTransform(scaleX, 1.0, x + (glyphWidth / 2.0), 0));
                }
                context.DrawText(text, new Point(x, y));
                if (stretch)
                {
                    context.Pop();
                }
            }
        }

        RenderTargetBitmap target = new(atlasWidth, cellHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        int atlasStride = atlasWidth * 4;
        byte[] bgra = new byte[atlasStride * cellHeight];
        target.CopyPixels(bgra, atlasStride, 0);

        _atlas = new byte[atlasWidth * cellHeight];
        for (int i = 0; i < _atlas.Length; i++)
        {
            _atlas[i] = bgra[(i * 4) + 3];
        }
    }

    /// <summary>Paints one frame straight into the bitmap's back buffer.</summary>
    private unsafe void RenderFrame()
    {
        AsciiPack? pack = _pack;
        WriteableBitmap? bitmap = _bitmap;
        if (pack is null || bitmap is null)
        {
            return;
        }

        bitmap.Lock();
        try
        {
            Span<uint> target = new(bitmap.BackBuffer.ToPointer(), _pixelWidth * _pixelHeight);
            target.Fill(CanvasPixel);

            HalftonePainter? halftone = _halftone;
            if (halftone is not null)
            {
                halftone.Render(target, _frameIndex);
            }
            else
            {
                DrawGlyphs(pack, target);
            }

            bitmap.AddDirtyRect(new Int32Rect(0, 0, _pixelWidth, _pixelHeight));
        }
        finally
        {
            bitmap.Unlock();
        }
    }

    /// <summary>Blits every cell of the current frame from the atlas and colour lookup.</summary>
    private void DrawGlyphs(AsciiPack pack, Span<uint> target)
    {
        byte[] atlas = _atlas;
        uint[] lookup = _colorLookup;
        int rowStride = _pixelWidth;
        int cellWidth = _cellWidth;
        int cellHeight = _cellHeight;
        int cellArea = cellWidth * cellHeight;
        int frameBase = _frameIndex * pack.Cells;
        int colorCount = pack.Colors.Length;
        ref uint targetRef = ref MemoryMarshal.GetReference(target);

        for (int row = 0; row < pack.Rows; row++)
        {
            int pixelRowTop = row * cellHeight * rowStride;
            int cellRow = frameBase + (row * pack.Cols);

            for (int col = 0; col < pack.Cols; col++)
            {
                int cell = cellRow + col;
                int alphaBase = pack.GlyphIds[cell] * cellArea;
                if (alphaBase + cellArea > atlas.Length)
                {
                    continue;
                }

                int colorId = pack.ColorIds[cell];
                if (colorId >= colorCount)
                {
                    colorId = 0;
                }
                int colorBase = colorId * 256;
                int destination = pixelRowTop + (col * cellWidth);

                for (int y = 0; y < cellHeight; y++)
                {
                    int alphaRow = alphaBase + (y * cellWidth);
                    int destinationRow = destination + (y * rowStride);

                    for (int x = 0; x < cellWidth; x++)
                    {
                        int alpha = atlas[alphaRow + x];
                        if (alpha == 0)
                        {
                            continue;
                        }

                        Unsafe.Add(ref targetRef, destinationRow + x) = lookup[colorBase + alpha];
                    }
                }
            }
        }
    }
}
