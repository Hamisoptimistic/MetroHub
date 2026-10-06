using System;
using Wpf.Ui.Controls;

namespace MetroHub.Presentation.Controls;

/// <summary>
/// Represents a selectable icon or emoji option in the universal glyph palette.
/// </summary>
public sealed record GlyphOption(string Name, string Glyph, bool IsSymbol = true);

/// <summary>
/// Curated centralized catalog of Fluent vector symbols and unicode emojis.
/// Shared across Workspace dialogs, Habit Tracker widgets, and shell elements.
/// </summary>
public static class GlyphCatalog
{
    public static readonly GlyphOption[] FluentIcons =
    [
        // General & Workspace
        new("Desktop", "Desktop24"),
        new("Work", "Briefcase24"),
        new("Code", "Code24"),
        new("Terminal", "WindowConsole20"),
        new("Folder", "Folder24"),
        new("Rocket", "Rocket24"),
        new("Target", "TargetArrow24"),
        new("Dashboard", "Grid24"),

        // Focus & Creative
        new("Star", "Star24"),
        new("Creative", "Sparkle24"),
        new("Study", "Book24"),
        new("Audio", "Headphones24"),
        new("Gaming", "Games24"),
        new("Design", "PaintBrush24"),
        new("Clock", "Clock24"),
        new("Brain", "BrainCircuit24"),

        // Health & Daily Habits
        new("Health", "Heart24"),
        new("Water", "Drop24"),
        new("Running", "PersonRunning20"),
        new("Workout", "Dumbbell24"),
        new("Nutrition", "Food24"),
        new("Day", "WeatherSunny24"),
        new("Rest", "Bed24"),
        new("Avoid", "Prohibited24"),

        // Social & Connect
        new("Home", "Home24"),
        new("Chat", "Chat24"),
        new("Web", "Globe24"),
        new("Done", "CheckmarkCircle24"),
        new("Flag", "Flag24"),
        new("Trophy", "Trophy24"),
        new("Calendar", "Calendar24"),
        new("Fire", "Fire24"),
    ];

    public static readonly GlyphOption[] Emojis =
    [
        // Productivity & Focus
        new("Target", "🎯", false),
        new("Rocket", "🚀", false),
        new("Fire", "🔥", false),
        new("Star", "⭐", false),
        new("Lightning", "⚡", false),
        new("Lightbulb", "💡", false),
        new("Laptop", "💻", false),
        new("Trophy", "🏆", false),

        // Health & Habits
        new("Water", "💧", false),
        new("Running", "🏃", false),
        new("Workout", "🏋️", false),
        new("Meditation", "🧘", false),
        new("Apple", "🍎", false),
        new("Sleep", "💤", false),
        new("Plant", "🌿", false),
        new("Cycling", "🚴", false),

        // Creativity & Study
        new("Books", "📚", false),
        new("Brain", "🧠", false),
        new("Writing", "✍️", false),
        new("Tea", "🍵", false),
        new("Sparkles", "✨", false),
        new("Music", "🎸", false),
        new("Art", "🎨", false),
        new("Headphones", "🎧", false),
    ];

    public static bool IsSymbol(string? glyph)
    {
        return !string.IsNullOrWhiteSpace(glyph) &&
               Enum.TryParse<SymbolRegular>(glyph, true, out _);
    }
}
