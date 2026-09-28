using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using MetroHub.Widgets.Common;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Layout-side payload for the Markdown widget. Written into <c>TileModel.SettingsJson</c> —
/// i.e. into <c>layout.json</c> and layout snapshots — so it must stay tiny: a schema marker plus
/// a pointer back at the widget's own state file. Document text lives in
/// <see cref="MarkdownWidgetState"/> under <c>config\widgets\markdown\</c>.
/// <para>
/// The properties below marked "legacy" are read-only compatibility surface: every payload written
/// before the split carried its tabs inline here, so the loader still accepts them and migrates
/// once. They are never populated again — a save writes only <see cref="SchemaVersion"/> and
/// <see cref="StateRef"/>, which is what strips document text out of <c>layout.json</c>.
/// </para>
/// </summary>
public sealed class MarkdownWidgetSettings
{
    /// <summary>Schema 2 = "state lives in the widget state file". Absent/1 = legacy inline payload.</summary>
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Tile id owning the state file (informational: the file is already keyed by tile id).</summary>
    [JsonPropertyName("stateRef")]
    public string? StateRef { get; set; }

    #region Legacy payload (read-only compatibility)

    /// <summary>Legacy inline tab collection (pre-split payloads).</summary>
    [JsonPropertyName("tabs")]
    public List<DocumentTabItem>? Tabs { get; set; }

    /// <summary>Legacy active tab index (also mirrored into the state file for schema 2).</summary>
    [JsonPropertyName("activeTabIndex")]
    public int ActiveTabIndex { get; set; }

    /// <summary>Legacy active mode: "Write" / "Read" / "Preview".</summary>
    [JsonPropertyName("activeMode")]
    public string? ActiveMode { get; set; }

    /// <summary>Legacy v1 raw markdown source.</summary>
    [JsonPropertyName("markdownText")]
    public string? MarkdownText { get; set; }

    /// <summary>Legacy v1 active tab name: "Write" or "Read".</summary>
    [JsonPropertyName("activeTab")]
    public string? ActiveTab { get; set; }

    /// <summary>Legacy v1 source .md path.</summary>
    [JsonPropertyName("sourceFilePath")]
    public string? SourceFilePath { get; set; }

    /// <summary>Legacy v1 file mtime (UTC ticks).</summary>
    [JsonPropertyName("sourceFileWriteUtc")]
    public long SourceFileWriteUtc { get; set; }

    #endregion

    /// <summary>True when this payload predates the state-file split (nothing else should be trusted).</summary>
    [JsonIgnore]
    public bool IsLegacyPayload => SchemaVersion < CurrentSchemaVersion;

    /// <summary>
    /// Builds a state object from a pre-split payload, or null when there is nothing to migrate.
    /// Handles both shipped shapes: the multi-tab collection and the original single-note fields.
    /// </summary>
    public MarkdownWidgetState? TryBuildLegacyState()
    {
        if (!IsLegacyPayload)
        {
            return null;
        }

        var state = new MarkdownWidgetState
        {
            ActiveTabIndex = ActiveTabIndex,
            ActiveMode = ResolveLegacyMode(),
        };

        if (Tabs is { Count: > 0 })
        {
            foreach (var tab in Tabs)
            {
                state.Tabs.Add(tab);
            }
        }
        else if (!string.IsNullOrEmpty(MarkdownText) || !string.IsNullOrWhiteSpace(SourceFilePath))
        {
            string title = !string.IsNullOrWhiteSpace(SourceFilePath)
                ? SafeFileName(SourceFilePath)
                : "Notes";
            state.Tabs.Add(new DocumentTabItem(title, MarkdownText ?? string.Empty, SourceFilePath)
            {
                SourceFileWriteUtc = SourceFileWriteUtc
            });
        }
        else
        {
            return null; // Nothing to migrate — the caller falls back to a fresh document.
        }

        state.Normalize();
        return state;
    }

    private string ResolveLegacyMode()
    {
        if (string.Equals(ActiveMode, "Read", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ActiveMode, "Preview", StringComparison.OrdinalIgnoreCase))
        {
            return "Read";
        }

        // Original payloads stored the mode under "activeTab" ("Write" / "Read").
        if (!string.IsNullOrWhiteSpace(ActiveMode))
        {
            return "Write";
        }

        return string.Equals(ActiveTab, "Read", StringComparison.OrdinalIgnoreCase) ? "Read" : "Write";
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "Notes";
        }
    }
}
