using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Industrial-grade wrapper around the MDXaml transform: ONE bad construct must cost
/// exactly one block, never the whole preview (the nested-link outage took everything).
/// Fast path (single transform) is untouched — this only pays on failure:
/// split the source into blank-line blocks, render each through <paramref name="transform"/>
/// separately, bisect once more on block failure, and swap anything still failing for
/// a styled placeholder that shows the raw source (never lost, never a crash page).
/// All placeholder shapes reuse existing document styles (RenderWarning/CodeBlock) —
/// zero new visuals, zero retained trees (transient sub-docs are released).
/// Must run on the UI thread (creates WPF objects).
/// </summary>
public static partial class MarkdownQuarantine
{
    /// <summary>Raw source shown per quarantined chunk is truncated here (memory bound).</summary>
    private const int MaxPlaceholderChars = 1500;

    /// <summary>
    /// Renders sanitized markdown to a FlowDocument, quarantining failing chunks.
    /// <paramref name="docStyle"/> is applied to merged/placeholder docs (sub-docs
    /// created by <paramref name="transform"/> carry their own binding already).
    /// </summary>
    public static FlowDocument RenderSafe(
        Func<string, FlowDocument> transform, Style? docStyle, string sanitized, out int quarantined)
    {
        quarantined = 0;
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return EmptyHint(docStyle);
        }
        try
        {
            return transform(sanitized);
        }
        catch
        {
            // Fast path failed — quarantine mode. Never throws: every chunk ends
            // as rendered blocks or a placeholder (see Bisect).
        }

        var final = new FlowDocument();
        if (docStyle != null)
        {
            final.Style = docStyle;
        }
        foreach (string chunk in SplitBlocks(sanitized))
        {
            try
            {
                MoveBlocks(transform(chunk), final);
            }
            catch
            {
                quarantined += Bisect(transform, chunk, final);
            }
        }
        return final;
    }

    /// <summary>Half-half retry; anything still failing becomes a placeholder. Returns placeholders made.</summary>
    private static int Bisect(Func<string, FlowDocument> transform, string chunk, FlowDocument final)
    {
        string[] lines = chunk.Split('\n');
        if (lines.Length < 2)
        {
            AddPlaceholder(final, chunk);
            return 1;
        }
        int made = 0;
        int mid = lines.Length / 2;
        foreach (string half in new[]
        {
            string.Join("\n", lines, 0, mid),
            string.Join("\n", lines, mid, lines.Length - mid),
        })
        {
            if (string.IsNullOrWhiteSpace(half))
            {
                continue;
            }
            try
            {
                MoveBlocks(transform(half), final);
            }
            catch
            {
                AddPlaceholder(final, half);
                made++;
            }
        }
        return made;
    }

    private static void MoveBlocks(FlowDocument source, FlowDocument final)
    {
        var snapshot = new Block[source.Blocks.Count];
        source.Blocks.CopyTo(snapshot, 0);
        foreach (Block block in snapshot)
        {
            source.Blocks.Remove(block);
            final.Blocks.Add(block);
        }
    }

    private static IEnumerable<string> SplitBlocks(string sanitized)
    {
        foreach (string chunk in BlockSplitRegex().Split(sanitized))
        {
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>Warning + raw source (truncated). Source is never lost — worst case looks deliberate.</summary>
    private static void AddPlaceholder(FlowDocument final, string rawChunk)
    {
        string raw = rawChunk.Trim();
        if (raw.Length > MaxPlaceholderChars)
        {
            raw = raw.Substring(0, MaxPlaceholderChars) + "…";
        }
        final.Blocks.Add(new Paragraph(new Run("Preview unavailable for this section — showing source:"))
        {
            Tag = "RenderWarning",
        });
        final.Blocks.Add(new Paragraph(new Run(raw))
        {
            Tag = "CodeBlock",
        });
    }

    private static FlowDocument EmptyHint(Style? docStyle)
    {
        var doc = new FlowDocument(new Paragraph(new Run("Nothing to preview yet — your text lives in the Write tab."))
        {
            Tag = "EmptyHint",
        });
        if (docStyle != null)
        {
            doc.Style = docStyle;
        }
        return doc;
    }

    [GeneratedRegex(@"\n[ \t]*\n", RegexOptions.Compiled)]
    private static partial Regex BlockSplitRegex();
}
