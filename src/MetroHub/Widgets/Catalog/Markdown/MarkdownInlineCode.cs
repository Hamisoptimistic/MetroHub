using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// WPF <see cref="Run"/> has no padding or border — a Run background hugs the glyphs
/// edge-to-edge, which is why inline code looked cramped, short, and ugly. This pass
/// swaps every CodeSpan Run for an <see cref="InlineUIContainer"/> holding a padded,
/// rounded <see cref="Border"/> chip (GitHub-style). Proven flat structure: the engine
/// emits CodeSpan as plain Runs directly in the paragraph (STA probe).
/// Runs AFTER <see cref="MarkdownTaskLists"/> (ballot detection needs plain Runs).
/// Two-phase by contract (collect → mutate): mutating inlines while enumerating blocks
/// throws "Collection was modified" — same trap as the editor swap.
/// One Border+TextBlock per code span — far lighter than the AvalonEdit editors that
/// were removed, bounded by the 100KB gate. Must run on the UI thread.
/// </summary>
public static class MarkdownInlineCode
{
    private static readonly SolidColorBrush _chipBackground = CreateFrozen("#2D2D33");
    private static readonly SolidColorBrush _chipForeground = CreateFrozen("#E8E8E8");
    // Same monospace stack the document style uses, so chips match code blocks on machines that
    // have Cascadia Code (and still fall back cleanly when they don't).
    private static readonly FontFamily _chipFont = new("Cascadia Code, Consolas, Segoe UI Mono, Courier New");

    private readonly record struct Target(InlineCollection Parent, Inline Old, string Text);

    private static SolidColorBrush CreateFrozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Swaps every CodeSpan for a padded chip. Returns the count.</summary>
    public static int ReplaceCodeSpans(FlowDocument doc)
    {
        var targets = new List<Target>();
        CollectFromBlocks(doc.Blocks, targets);
        foreach (Target target in targets)
        {
            string codeText = target.Text.Replace("&#124;", "|");
            target.Parent.InsertBefore(target.Old, MakeChip(codeText));
            target.Parent.Remove(target.Old);
        }
        RestorePipesInBlocks(doc.Blocks);
        return targets.Count;
    }

    /// <summary>Read-only walk — never mutates, so plain enumeration is safe everywhere.</summary>
    private static void CollectFromBlocks(BlockCollection blocks, List<Target> targets)
    {
        foreach (Block block in blocks)
        {
            if (block is Paragraph paragraph)
            {
                CollectFromInlines(paragraph.Inlines, targets);
            }
            else if (block is Section section)
            {
                CollectFromBlocks(section.Blocks, targets);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            CollectFromBlocks(cell.Blocks, targets);
                        }
                    }
                }
            }
            else if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                {
                    CollectFromBlocks(item.Blocks, targets);
                }
            }
        }
    }

    private static void CollectFromInlines(InlineCollection inlines, List<Target> targets)
    {
        foreach (Inline inline in inlines)
        {
            if (inline is Run run && Equals(run.Tag, "CodeSpan"))
            {
                targets.Add(new Target(inlines, inline, run.Text ?? string.Empty));
            }
            else if (inline is Span span && Equals(span.Tag, "CodeSpan"))
            {
                var text = new System.Text.StringBuilder();
                foreach (Inline inner in span.Inlines)
                {
                    if (inner is Run innerRun)
                    {
                        text.Append(innerRun.Text);
                    }
                }
                targets.Add(new Target(inlines, inline, text.ToString()));
            }
            else if (inline is Span container)
            {
                // Bold/italic/hyperlink wrappers: code may hide one level down.
                CollectFromInlines(container.Inlines, targets);
            }
        }
    }

    private static InlineUIContainer MakeChip(string code)
    {
        var label = new TextBlock
        {
            Text = code,
            FontFamily = _chipFont,
            FontSize = 12.5,
            Foreground = _chipForeground,
        };
        var chip = new Border
        {
            Background = _chipBackground,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Child = label,
        };
        return new InlineUIContainer(chip) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static void RestorePipesInBlocks(BlockCollection blocks)
    {
        foreach (Block block in blocks.ToList())
        {
            if (block is Paragraph paragraph)
            {
                RestorePipesInInlines(paragraph.Inlines);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup rg in table.RowGroups.ToList())
                {
                    foreach (TableRow r in rg.Rows.ToList())
                    {
                        foreach (TableCell c in r.Cells.ToList())
                        {
                            RestorePipesInBlocks(c.Blocks);
                        }
                    }
                }
            }
            else if (block is List list)
            {
                foreach (ListItem li in list.ListItems.ToList())
                {
                    RestorePipesInBlocks(li.Blocks);
                }
            }
            else if (block is Section section)
            {
                RestorePipesInBlocks(section.Blocks);
            }
        }
    }

    private static void RestorePipesInInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines.ToList())
        {
            if (inline is Run r && r.Text != null && r.Text.Contains("&#124;"))
            {
                r.Text = r.Text.Replace("&#124;", "|");
            }
            else if (inline is Span s)
            {
                RestorePipesInInlines(s.Inlines);
            }
        }
    }
}
