using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Documents;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// MDXaml 1.27.0 renders EVERY fenced/indented code block as a full AvalonEdit
/// TextEditor (own light theme, own highlighting) inside a BlockUIContainer —
/// unreachable by document styles and one editor visual-tree per block.
/// This widget chose plain code blocks, so after each transform the editors are
/// swapped for plain Paragraph+Run blocks tagged CodeBlock, which the dark
/// document style themes. Also fixes mouse-wheel (no editor swallowing it) and
/// cuts per-render memory (editors carry document models + highlighters).
/// Two-phase by contract: collect first (read-only walk), mutate second — mutating
/// Blocks while enumerating ListItems throws "Collection was modified" (any list
/// containing a code block crashed the whole preview).
/// Must run on the UI thread (touches the visual tree).
/// </summary>
public static class MarkdownCodeBlocks
{
    private readonly record struct Target(BlockCollection Parent, BlockUIContainer Container, string Text);

    /// <summary>Replaces every editor-backed code block with a plain paragraph. Returns the count.</summary>
    public static int ReplaceEditors(FlowDocument doc)
    {
        var targets = new List<Target>();
        Collect(doc.Blocks, targets);
        foreach (Target target in targets)
        {
            var plain = new Paragraph(new Run(target.Text))
            {
                Tag = "CodeBlock",
            };
            target.Parent.InsertBefore(target.Container, plain);
            target.Parent.Remove(target.Container);
        }
        return targets.Count;
    }

    /// <summary>Read-only walk — never mutates, so plain enumeration is safe everywhere.</summary>
    private static void Collect(BlockCollection blocks, List<Target> targets)
    {
        foreach (Block block in blocks)
        {
            if (block is BlockUIContainer container &&
                container.Child is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                targets.Add(new Target(blocks, container, editor.Text ?? string.Empty));
            }
            else if (block is Section section)
            {
                Collect(section.Blocks, targets);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            Collect(cell.Blocks, targets);
                        }
                    }
                }
            }
            else if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                {
                    Collect(item.Blocks, targets);
                }
            }
        }
    }
}
