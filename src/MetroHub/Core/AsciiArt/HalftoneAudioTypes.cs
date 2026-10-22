using System;

namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Represents an active circular acoustic shockwave traveling across the halftone grid.
/// </summary>
public readonly record struct HalftoneAudioWave(
    float CenterX,
    float CenterY,
    float ElapsedSeconds,
    float Intensity);

/// <summary>
/// Real-time audio physics parameters passed to <see cref="HalftonePainter"/>.
/// </summary>
public readonly record struct AudioFrame(
    float Kick,
    float Bass,
    float Mid,
    float Treble,
    bool IsSilent,
    ReadOnlyMemory<HalftoneAudioWave> ActiveWaves)
{
    public static readonly AudioFrame Default = new(0f, 0f, 0f, 0f, true, ReadOnlyMemory<HalftoneAudioWave>.Empty);
}

