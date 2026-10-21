using System;
using System.Collections.Generic;
using MetroHub.Core.LiveWallpapers.Scenes;

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
        Register(new("campfire_stars", "Campfire Under Stars", "Nature",
            () => new CampfireUnderStarsScene()));
    }

    public static void Register(AsciiWallpaperDefinition definition) =>
        Registry[definition.Id] = definition;

    public static IReadOnlyCollection<AsciiWallpaperDefinition> All => Registry.Values;

    public static IAsciiWallpaperScene Create(string id) =>
        Registry.TryGetValue(id, out var def)
            ? def.Factory()
            : Registry["campfire_stars"].Factory();
}
