namespace MetroHub.Core.AsciiArt;

/// <summary>
/// Tuning for the "Liquid Print" look: the wallpaper is treated as ink resting
/// on a vibrating membrane. Displacement is the primary expressive channel,
/// because shifting dots changes both motion and local ink density while the
/// photograph underneath stays recognisable.
/// </summary>
public sealed record LiquidPrintLook
{
    /// <summary>The single look currently shipped. Future looks are separate instances.</summary>
    public static LiquidPrintLook Default { get; } = new();

    /// <summary>Constant refraction offset along the wave gradient, in pixels per unit gradient.</summary>
    public float DisplacementPixels { get; init; } = 16f;

    /// <summary>Extra refraction added on beats, in pixels per unit gradient.</summary>
    public float TransientDisplacementPixels { get; init; } = 26f;

    /// <summary>Slow divergence-free drift, in pixels.</summary>
    public float CurlPixels { get; init; } = 3.4f;

    /// <summary>Dot radius response to the wave height.</summary>
    public float RadiusWaveGain { get; init; } = 0.22f;

    /// <summary>Global dot swell on a kick.</summary>
    public float PulseSwell { get; init; } = 0.13f;

    /// <summary>Peak strength of the travelling specular sheen.</summary>
    public float SpecularStrength { get; init; } = 0.30f;

    /// <summary>Gradient exaggeration used to build a surface normal for the sheen.</summary>
    public float SpecularExaggeration { get; init; } = 11f;

    /// <summary>Motion weight of the flattest areas of the photograph.</summary>
    public float DetailFloor { get; init; } = 0.34f;

    /// <summary>Motion multiplier applied while the radio is idle.</summary>
    public float IdleScale { get; init; } = 0.45f;
}
