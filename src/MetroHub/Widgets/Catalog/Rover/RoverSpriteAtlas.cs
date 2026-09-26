using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// Singleton manager for the Rover 2160x2160 sprite atlas texture.
/// Loads and freezes the texture exactly once into GPU memory, ensuring zero duplicated VRAM allocations.
/// </summary>
public static class RoverSpriteAtlas
{
    public const int FrameWidth = 80;
    public const int FrameHeight = 80;
    public const int AtlasWidth = 2160;
    public const int AtlasHeight = 2160;

    private static readonly Lazy<BitmapSource> _sharedAtlas = new(LoadAtlas);

    /// <summary>
    /// Frozen, immutable BitmapSource representing the entire sprite sheet.
    /// Thread-safe and shared across all Rover widget instances.
    /// </summary>
    public static BitmapSource SharedAtlas => _sharedAtlas.Value;

    /// <summary>
    /// Stack-allocated, zero-heap-allocation calculation of the absolute UV frame rectangle.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Rect GetFrameRect(int x, int y)
    {
        return new Rect(x, y, FrameWidth, FrameHeight);
    }

    /// <summary>
    /// Stack-allocated frame rect extraction from a RoverFrame model.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static Rect GetFrameRect(RoverFrame frame)
    {
        return new Rect(frame.X, frame.Y, FrameWidth, FrameHeight);
    }

    private static BitmapSource LoadAtlas()
    {
        using var stream = RoverManifest.TryOpenResource("Assets/Rover/map.png")
            ?? throw new FileNotFoundException("Rover sprite atlas 'map.png' not found: Assets/Rover/map.png");

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = stream;
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze(); // Crucial: Freezes the resource for multithread access and DirectX VRAM mapping
        return bmp;
    }
}
