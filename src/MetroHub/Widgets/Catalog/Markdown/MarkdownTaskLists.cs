using System.Collections.Generic;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// GitHub-style checklists show ☐/☑ with NO bullet — but the bullet is exactly what
/// keeps task lines as real list items (the kept `-` marker; stripping it caused the
/// run-on-paragraph bug). Resolution: render normally, then hide markers afterwards.
/// Each nesting level is its own <see cref="List"/>, so the walk handles every depth
/// the same way — deep checklists stay bullet-free while indentation keeps the hierarchy.
/// Only lists whose items ALL start with a ballot box are affected (mixed lists keep
/// their bullets so plain items stay distinguishable). Read-only walk + property set,
/// no collection mutation. Must run on the UI thread (touches the document).
/// </summary>
public static class MarkdownTaskLists
{
    /// <summary>
    /// Both ballot glyphs pinned to one icon font where they are a designed matched
    /// pair (Fluent Checkbox / CheckboxComposite, MDL2 fallback for Win10).
    /// Without this, per-character font fallback resolves them from DIFFERENT fonts
    /// (ticked looks bold, unticked thin) — the "boxes don't match" complaint.
    /// Size/weight inherit from the paragraph, so only the box design unifies.
    /// </summary>
    private static readonly FontFamily _ballotFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly record struct BallotTarget(InlineCollection Parent, Run Old);

    /// <summary>Hides bullet markers on all-checkbox lists at any depth. Returns lists changed.</summary>
    public static int HideMarkers(FlowDocument doc) => HideInBlocks(doc.Blocks);

    private static int HideInBlocks(BlockCollection blocks)
    {
        int hidden = 0;
        foreach (Block block in blocks)
        {
            if (block is List list)
            {
                if (IsTaskList(list))
                {
                    list.MarkerStyle = TextMarkerStyle.None;
                    hidden++;
                }
                foreach (ListItem item in list.ListItems)
                {
                    hidden += HideInBlocks(item.Blocks);
                }
            }
            else if (block is Section section)
            {
                hidden += HideInBlocks(section.Blocks);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            hidden += HideInBlocks(cell.Blocks);
                        }
                    }
                }
            }
        }
        return hidden;
    }

    /// <summary>
    /// Pins every task-list ballot glyph to <see cref="_ballotFont"/> so ticked and
    /// unticked boxes render as a matched pair. Splits the leading ☐/☑ off the item's
    /// first Run (item text keeps the document font). Two-phase collect→mutate —
    /// same "Collection was modified" contract as the other passes.
    /// Returns the number of ballots normalized.
    /// </summary>
    public static int NormalizeBallotFont(FlowDocument doc)
    {
        var targets = new List<BallotTarget>();
        CollectBallots(doc.Blocks, targets);
        foreach (BallotTarget target in targets)
        {
            string text = target.Old.Text ?? string.Empty;
            var ballot = new Run(text.Substring(0, 1)) { FontFamily = _ballotFont };
            target.Parent.InsertBefore(target.Old, ballot);
            if (text.Length == 1)
            {
                target.Parent.Remove(target.Old);
            }
            else
            {
                target.Old.Text = text.Substring(1);
            }
        }
        return targets.Count;
    }

    /// <summary>Read-only walk — never mutates, so plain enumeration is safe everywhere.</summary>
    private static void CollectBallots(BlockCollection blocks, List<BallotTarget> targets)
    {
        foreach (Block block in blocks)
        {
            if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                {
                    CollectItemBallot(item, targets);
                    CollectBallots(item.Blocks, targets);
                }
            }
            else if (block is Section section)
            {
                CollectBallots(section.Blocks, targets);
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            CollectBallots(cell.Blocks, targets);
                        }
                    }
                }
            }
        }
    }

    private static void CollectItemBallot(ListItem item, List<BallotTarget> targets)
    {
        foreach (Block block in item.Blocks)
        {
            if (block is not Paragraph paragraph)
            {
                return; // Keep in sync with StartsWithBallotBox: paragraph must lead.
            }
            foreach (Inline inline in paragraph.Inlines)
            {
                if (inline is not Run run)
                {
                    return;
                }
                if (run.Text.Length == 0)
                {
                    continue;
                }
                if (run.Text[0] == MarkdownRenderGate.TaskBoxUnchecked ||
                    run.Text[0] == MarkdownRenderGate.TaskBoxChecked)
                {
                    targets.Add(new BallotTarget(paragraph.Inlines, run));
                }
                return; // Only the leading Run ever carries the ballot.
            }
            return;
        }
    }

    private static bool IsTaskList(List list)
    {
        if (list.ListItems.Count == 0)
        {
            return false;
        }
        foreach (ListItem item in list.ListItems)
        {
            if (!StartsWithBallotBox(item))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>First content of the item must open with a task glyph. Anything else → keep bullets.</summary>
    private static bool StartsWithBallotBox(ListItem item)
    {
        foreach (Block block in item.Blocks)
        {
            if (block is not Paragraph paragraph)
            {
                return false; // Nested list / quote first → conservative, keep markers.
            }
            foreach (Inline inline in paragraph.Inlines)
            {
                if (inline is not Run run)
                {
                    return false; // Emphasis/link leads → conservative.
                }
                if (run.Text.Length == 0)
                {
                    continue;
                }
                return run.Text[0] == MarkdownRenderGate.TaskBoxUnchecked ||
                    run.Text[0] == MarkdownRenderGate.TaskBoxChecked;
            }
            return false;
        }
        return false;
    }


}
