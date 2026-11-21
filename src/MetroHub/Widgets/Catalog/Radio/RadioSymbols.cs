using System;
using System.Collections.Generic;
using Wpf.Ui.Controls;

namespace MetroHub.Widgets.Catalog.Radio;

/// <summary>
/// Curated Fluent icon descriptor for radio category selection.
/// </summary>
public sealed record RadioSymbolItem(string Key, SymbolRegular Symbol, string Label)
{
    public static implicit operator SymbolRegular(RadioSymbolItem item) => item?.Symbol ?? SymbolRegular.Empty;
    public override string ToString() => Label;
}

/// <summary>
/// Curated catalog of Fluent regular symbols for radio categories.
/// </summary>
public static class RadioSymbols
{
    private static readonly List<RadioSymbolItem> Items = new()
    {
        new("Headphones24", SymbolRegular.Headphones24, "Headphones"),
        new("WeatherMoon24", SymbolRegular.WeatherMoon24, "Moon"),
        new("LeafTwo24", SymbolRegular.LeafTwo24, "Leaf"),
        new("Code24", SymbolRegular.Code24, "Code"),
        new("Sparkle24", SymbolRegular.Sparkle24, "Sparkle"),
        new("Speaker224", SymbolRegular.Speaker224, "Speaker"),
        new("SoundWaveCircle24", SymbolRegular.SoundWaveCircle24, "Sound Wave"),
        new("MusicNote224", SymbolRegular.MusicNote224, "Music"),
        new("WeatherRain24", SymbolRegular.WeatherRain24, "Rain"),
        new("WeatherSunny24", SymbolRegular.WeatherSunny24, "Sun"),
        new("DrinkCoffee24", SymbolRegular.DrinkCoffee24, "Coffee"),
        new("BrainCircuit24", SymbolRegular.BrainCircuit24, "Brain"),
        new("Book24", SymbolRegular.Book24, "Book"),
        new("Fire24", SymbolRegular.Fire24, "Fire"),
        new("HeadphonesSoundWave24", SymbolRegular.HeadphonesSoundWave24, "Wave"),
        new("Heart24", SymbolRegular.Heart24, "Heart")
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
