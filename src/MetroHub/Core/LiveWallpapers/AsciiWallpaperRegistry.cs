using System;
using System.Collections.Generic;
using MetroHub.Core.LiveWallpapers.Engines;

namespace MetroHub.Core.LiveWallpapers;

public sealed record AsciiWallpaperDefinition(
    string Id,
    string DisplayName,
    string Category,
    Func<IAsciiWallpaperScene> Factory);

public static class AsciiWallpaperRegistry
{
    private static readonly Dictionary<string, AsciiWallpaperDefinition> Registry =
        new(StringComparer.OrdinalIgnoreCase);

    static AsciiWallpaperRegistry()
    {
        // Engine 1: Boid Multi-Cell Articulated Sprites
        Register(new("ocean_dolphins", "Dolphin Sanctuary", "Nature",
            () => new BoidSpriteEngine("ocean_dolphins", "Dolphin Sanctuary", SpritePackType.DolphinSanctuary)));
        Register(new("koi_pond", "Zen Koi Sanctuary", "Nature",
            () => new BoidSpriteEngine("koi_pond", "Zen Koi Sanctuary", SpritePackType.KoiPond)));
        Register(new("cyber_fleet", "Orbital Convoy", "Sci-Fi",
            () => new BoidSpriteEngine("cyber_fleet", "Orbital Convoy", SpritePackType.CyberFleet)));

        // Engine 2: Procedural Field, Raymarching, & Math Shaders
        Register(new("matrix_rain", "Digital Rain", "Sci-Fi",
            () => new AsciiShaderEngine("matrix_rain", "Digital Rain", ShaderPreset.MatrixRain, 1)));
        Register(new("synthwave_sun", "Neon Horizon", "Retro",
            () => new AsciiShaderEngine("synthwave_sun", "Neon Horizon", ShaderPreset.SynthwaveHorizon, 2)));
        Register(new("starfield_warp", "Deep Space Warp", "Sci-Fi",
            () => new AsciiShaderEngine("starfield_warp", "Deep Space Warp", ShaderPreset.DeepSpaceWarp, 0)));
        Register(new("torus_3d", "Hyperbolic Torus", "Geometry",
            () => new AsciiShaderEngine("torus_3d", "Hyperbolic Torus", ShaderPreset.RotatingTorus3D, 0)));
        Register(new("cyber_plasma", "Quantum Plasma", "Abstract",
            () => new AsciiShaderEngine("cyber_plasma", "Quantum Plasma", ShaderPreset.CyberPlasma, 3)));
        Register(new("aurora_sky", "Arctic Aurora", "Nature",
            () => new AsciiShaderEngine("aurora_sky", "Arctic Aurora", ShaderPreset.AuroraBorealis, 1)));
        Register(new("ocean_swell", "Midnight Swell", "Nature",
            () => new AsciiShaderEngine("ocean_swell", "Midnight Swell", ShaderPreset.OceanSwell, 0)));
        Register(new("radar_sweep", "Tactical Radar", "HUD",
            () => new AsciiShaderEngine("radar_sweep", "Tactical Radar", ShaderPreset.RadarSweep, 1)));
        Register(new("fire_storm", "Thermal Core", "Abstract",
            () => new AsciiShaderEngine("fire_storm", "Thermal Core", ShaderPreset.FireStorm, 2)));
        Register(new("campfire_stars", "Campfire Under Stars", "Nature",
            () => new AsciiShaderEngine("campfire_stars", "Campfire Under Stars", ShaderPreset.CampfireUnderStars, 2)));

        // Engine 3: Sobel Edge-Mapped Video / Luminance Sampler
        Register(new("pulse_singularity", "Gravity Singularity", "Sci-Fi",
            () => new LuminanceSamplerEngine("pulse_singularity", "Gravity Singularity", null, 3)));
    }

    public static void Register(AsciiWallpaperDefinition definition) =>
        Registry[definition.Id] = definition;

    public static IReadOnlyCollection<AsciiWallpaperDefinition> All => Registry.Values;

    public static IAsciiWallpaperScene Create(string id) =>
        Registry.TryGetValue(id, out var def)
            ? def.Factory()
            : Registry["ocean_dolphins"].Factory();
}

