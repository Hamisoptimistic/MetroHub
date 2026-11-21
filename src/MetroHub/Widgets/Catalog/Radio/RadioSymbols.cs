using System;
using System.Collections.Generic;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Curated Fluent icon descriptor for radio category selection.
/// </summary>
public sealed record RadioSymbolItem(string Key, SymbolRegular Symbol)
{
    public static implicit operator SymbolRegular(RadioSymbolItem item) => item?.Symbol ?? SymbolRegular.Empty;
    public override string ToString() => Key;
}

/// <summary>
/// Curated catalog of Fluent regular symbols for radio categories.
/// </summary>
public static class RadioSymbols
{
    private static readonly List<RadioSymbolItem> Items = new()
    {
        new("Headphones24", SymbolRegular.Headphones24),
        new("WeatherMoon24", SymbolRegular.WeatherMoon24),
        new("LeafTwo24", SymbolRegular.LeafTwo24),
        new("Code24", SymbolRegular.Code24),
        new("Sparkle24", SymbolRegular.Sparkle24),
        new("Speaker224", SymbolRegular.Speaker224),
        new("SoundWaveCircle24", SymbolRegular.SoundWaveCircle24),
        new("MusicNote224", SymbolRegular.MusicNote224),
        new("WeatherRain24", SymbolRegular.WeatherRain24),
        new("WeatherSunny24", SymbolRegular.WeatherSunny24),
        new("DrinkCoffee24", SymbolRegular.DrinkCoffee24),
        new("BrainCircuit24", SymbolRegular.BrainCircuit24),
        new("Book24", SymbolRegular.Book24),
        new("Fire24", SymbolRegular.Fire24),
        new("HeadphonesSoundWave24", SymbolRegular.HeadphonesSoundWave24),
        new("Heart24", SymbolRegular.Heart24)
    };

    public static readonly IReadOnlyList<RadioSymbolItem> CuratedIcons = Items;

    public static readonly IReadOnlyList<SymbolRegular> CuratedSymbols =
        Items.ConvertAll(i => i.Symbol);

    /// <summary>
    /// Parses an icon name into a SymbolRegular enum value, with safe fallback.
    /// </summary>
    public static SymbolRegular ParseSymbol(string? iconName, SymbolRegular fallback = SymbolRegular.Headphones24)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return fallback;
        }

        if (Enum.TryParse<SymbolRegular>(iconName, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
