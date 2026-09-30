using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MetroHub.Widgets.Models;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Bulk payload for the Markdown widget, persisted by <see cref="MetroHub.Core.Services.WidgetStateStore"/>
/// in <c>config\widgets\markdown\{tileId}.json</c> — never inside <c>layout.json</c>.
/// This is the widget's autosave mirror: it holds unsaved edits so a crash or a re-open
/// restores exactly what was on screen.</summary>
public sealed class MarkdownWidgetState
{
    /// <summary>Highest tab count a widget may hold. Single source of truth for the cap.</summary>
    public const int MaxTabs = 10;

    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("tabs")]
    public List<DocumentTabItem> Tabs { get; set; } = new();

    [JsonPropertyName("activeTabIndex")]
    public int ActiveTabIndex { get; set; }

    [JsonPropertyName("activeMode")]
    public string ActiveMode { get; set; } = "Write";

    /// <summary>
    /// Sanitizes a payload that came off disk: guarantees at least one tab, caps the collection,
    /// clamps the active index, and clears transient per-tab flags (nothing loaded is "dirty",
    /// and the view model re-derives "active" from the index).
    /// </summary>
    public void Normalize()
    {
        Tabs ??= new List<DocumentTabItem>();
        foreach (var tab in Tabs)
        {
            tab.IsDirty = false;
        }

        if (Tabs.Count == 0)
        {
            Tabs.Add(new DocumentTabItem("Notes", DefaultNoteText));
        }

        if (Tabs.Count > MaxTabs)
        {
            Tabs.RemoveRange(MaxTabs, Tabs.Count - MaxTabs);
        }

        if (ActiveTabIndex < 0 || ActiveTabIndex >= Tabs.Count)
        {
            ActiveTabIndex = 0;
        }

        for (int i = 0; i < Tabs.Count; i++)
        {
            Tabs[i].IsActive = i == ActiveTabIndex;
        }

        if (string.IsNullOrWhiteSpace(ActiveMode))
        {
            ActiveMode = "Write";
        }
    }

    /// <summary>Copy that is safe to hand to serialization (callers keep their live tab objects).</summary>
    public MarkdownWidgetState CloneForSave(int activeTabIndex, string activeMode)
    {
        var clone = new MarkdownWidgetState
        {
            SchemaVersion = CurrentSchemaVersion,
            ActiveTabIndex = activeTabIndex,
            ActiveMode = activeMode,
            Tabs = new List<DocumentTabItem>(Tabs.Count),
        };
        foreach (var tab in Tabs)
        {
            clone.Tabs.Add(tab.Clone());
        }
        return clone;
    }

    /// <summary>Seed document for a brand-new widget or an unreadable state file.</summary>
    public const string DefaultNoteText = "# Notes\n\nWrite markdown here, then click **Preview**.";
}
