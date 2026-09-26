using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace MetroHub.Widgets.Catalog.Rover;

/// <summary>
/// A probabilistic Markov transition branch in Rover's animation sequence.
/// </summary>
public sealed record RoverBranch(
    [property: JsonPropertyName("frameIndex")] int FrameIndex,
    [property: JsonPropertyName("weight")] int Weight
);

/// <summary>
/// Container for weighted branches on an animation frame.
/// </summary>
public sealed record RoverBranching(
    [property: JsonPropertyName("branches")] RoverBranch[]? Branches
);

/// <summary>
/// A discrete frame in an animation sequence with duration, sprite sheet pixel coordinates,
/// optional sound trigger, exit branch, and probabilistic branching.
/// </summary>
public sealed record RoverFrame(
    [property: JsonPropertyName("duration")] int Duration,
    [property: JsonPropertyName("images")] int[][]? Images,
    [property: JsonPropertyName("sound")] string? Sound,
    [property: JsonPropertyName("exitBranch")] int? ExitBranch,
    [property: JsonPropertyName("branching")] RoverBranching? Branching
)
{
    public int X => Images is { Length: > 0 } && Images[0].Length > 0 ? Images[0][0] : 0;
    public int Y => Images is { Length: > 0 } && Images[0].Length > 1 ? Images[0][1] : 0;
    public int DurationMs => Duration > 0 ? Duration : 100;
}

/// <summary>
/// Represents a named animation sequence containing an array of frames.
/// </summary>
public sealed record RoverAnimation(
    [property: JsonPropertyName("frames")] RoverFrame[] Frames
);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, RoverAnimation>))]
internal sealed partial class RoverManifestJsonContext : JsonSerializerContext;

/// <summary>
/// Static catalog providing pre-parsed, zero-overhead access to Rover's 29 animation sequences.
/// Deserialized once on first access using high-performance System.Text.Json source generation.
/// </summary>
public static class RoverManifest
{
    public const int FrameSize = 80;
    public const int AtlasSize = 2160;

    private static readonly Lazy<FrozenDictionary<string, RoverAnimation>> _animations = new(LoadManifest);

    public static IReadOnlyDictionary<string, RoverAnimation> Animations => _animations.Value;

    public static RoverAnimation? GetAnimation(string name)
    {
        return _animations.Value.GetValueOrDefault(name);
    }

    private static FrozenDictionary<string, RoverAnimation> LoadManifest()
    {
        using var stream = TryOpenResource("Assets/Rover/rover_manifest.json")
            ?? throw new FileNotFoundException("Rover manifest JSON resource not found: Assets/Rover/rover_manifest.json");

        var dict = JsonSerializer.Deserialize(stream, RoverManifestJsonContext.Default.DictionaryStringRoverAnimation)
            ?? new Dictionary<string, RoverAnimation>(StringComparer.OrdinalIgnoreCase);

        // Inject canonical resting and sleep animations
        dict["RestPose"] = new RoverAnimation(new[] { CreateFrame(0, 0) });
        dict["Sleeping"] = new RoverAnimation(new[] { CreateFrame(240, 1040) });
        dict["LieDown"] = new RoverAnimation(new[]
        {
            CreateFrame(1520, 960, 90),
            CreateFrame(1680, 960, 90),
            CreateFrame(1840, 960, 90),
            CreateFrame(2000, 960, 90),
            CreateFrame(0, 1040, 90),
            CreateFrame(80, 1040, 90),
            CreateFrame(160, 1040, 90),
            CreateFrame(240, 1040, 100)
        });
        dict["WakeUp"] = new RoverAnimation(new[]
        {
            CreateFrame(160, 1040, 90),
            CreateFrame(80, 1040, 90),
            CreateFrame(0, 1040, 90),
            CreateFrame(1840, 960, 90),
            CreateFrame(1520, 960, 90),
            CreateFrame(0, 0, 100)
        });

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static RoverFrame CreateFrame(int x, int y, int durationMs = 100, string? sound = null) =>
        new(durationMs, new[] { new[] { x, y } }, sound, null, null);

    internal static Stream? TryOpenResource(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/').TrimStart('/');

        // 1. Try component-qualified pack URI (works across assemblies and test runners)
        try
        {
            var uri = new Uri($"pack://application:,,,/MetroHub;component/{normalized}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info != null) return info.Stream;
        }
        catch { }

        // 2. Try simple pack URI
        try
        {
            var uri = new Uri($"pack://application:,,,/{normalized}", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info != null) return info.Stream;
        }
        catch { }

        // 3. Walk up the directory tree to find source or output files
        string? current = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 6 && !string.IsNullOrEmpty(current); i++)
        {
            string direct = Path.Combine(current, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(direct)) return File.OpenRead(direct);

            string inSrc = Path.Combine(current, "src", "MetroHub", normalized.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(inSrc)) return File.OpenRead(inSrc);

            current = Directory.GetParent(current)?.FullName;
        }

        return null;
    }
}
