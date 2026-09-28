using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MetroHub.Widgets.Catalog.Markdown;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Pins the passes that run on the engine's output (they cannot be checked without WPF objects, so
/// they run on an STA thread) plus the quarantine fallback that keeps one bad construct from taking
/// the whole preview down.
/// </summary>
public class MarkdownRegressionTests
{
    private static FlowDocument Transform(string markdown) => new MdXaml.Markdown().Transform(markdown);

    private static IEnumerable<Block> AllBlocks(FlowDocument doc) => AllBlocks(doc.Blocks);

    private static IEnumerable<Block> AllBlocks(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            yield return block;
            if (block is Section section)
            {
                foreach (Block inner in AllBlocks(section.Blocks)) yield return inner;
            }
            else if (block is List list)
            {
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block inner in item.Blocks) yield return inner;
                }
            }
            else if (block is Table table)
            {
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block inner in cell.Blocks) yield return inner;
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<Inline> AllInlines(FlowDocument doc)
    {
        foreach (Block block in AllBlocks(doc))
        {
            if (block is Paragraph paragraph)
            {
                foreach (Inline inline in WalkInlines(paragraph.Inlines)) yield return inline;
            }
        }
    }

    private static IEnumerable<Inline> WalkInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (Inline inner in WalkInlines(span.Inlines)) yield return inner;
            }
        }
    }

    private static string TextOf(FlowDocument doc) => new TextRange(doc.ContentStart, doc.ContentEnd).Text;

    [Fact]
    public void FencedCode_RendersAsPlainTextWithoutAnEmbeddedEditor()
    {
        MarkdownTestHost.RunSta(() =>
        {
            FlowDocument doc = Transform("```csharp\nvar answer = 42;\n```");

            // MDXaml 1.27 embeds an AvalonEdit TextEditor per fence; the widget swaps those out.
            int replaced = MarkdownCodeBlocks.ReplaceEditors(doc);
            Assert.True(replaced >= 1, "expected the fenced block to arrive as an editor-backed container");
            Assert.DoesNotContain(AllBlocks(doc), b => b is BlockUIContainer c && c.Child is ICSharpCode.AvalonEdit.TextEditor);
            Assert.Contains("var answer = 42;", TextOf(doc));

            Paragraph? code = AllBlocks(doc).OfType<Paragraph>().FirstOrDefault(p => Equals(p.Tag, "CodeBlock"));
            Assert.NotNull(code);
        });
    }

    [Fact]
    public void FencedCode_InsideAList_DoesNotThrow()
    {
        MarkdownTestHost.RunSta(() =>
        {
            // Mutating blocks while enumerating their parent list used to crash the whole preview.
            FlowDocument doc = Transform("- item\n\n```\ncode line\n```\n\n- another item");
            int replaced = MarkdownCodeBlocks.ReplaceEditors(doc);
            Assert.True(replaced >= 0);
            Assert.Contains("code line", TextOf(doc));
        });
    }

    [Fact]
    public void TaskList_LosesBulletsAndUnifiesTheBallotFont()
    {
        MarkdownTestHost.RunSta(() =>
        {
            string source = MarkdownRenderGate.NormalizeTaskLists("- [x] done\n- [ ] todo");
            FlowDocument doc = Transform(source);

            Assert.True(MarkdownTaskLists.HideMarkers(doc) >= 1);
            Assert.Contains(AllBlocks(doc).OfType<List>(), list => list.MarkerStyle == TextMarkerStyle.None);

            Assert.True(MarkdownTaskLists.NormalizeBallotFont(doc) >= 2);
            var ballotRuns = AllInlines(doc).OfType<Run>()
                .Where(r => r.Text.Length > 0 &&
                    (r.Text[0] == MarkdownRenderGate.TaskBoxUnchecked || r.Text[0] == MarkdownRenderGate.TaskBoxChecked))
                .ToList();
            Assert.Equal(2, ballotRuns.Count);
            Assert.All(ballotRuns, r => Assert.Contains("Segoe", r.FontFamily.Source));
        });
    }

    [Fact]
    public void MixedList_KeepsItsBullets()
    {
        MarkdownTestHost.RunSta(() =>
        {
            string source = MarkdownRenderGate.NormalizeTaskLists("- [ ] task\n- plain item");
            FlowDocument doc = Transform(source);
            MarkdownTaskLists.HideMarkers(doc);
            Assert.Contains(AllBlocks(doc).OfType<List>(), list => list.MarkerStyle != TextMarkerStyle.None);
        });
    }

    [Fact]
    public void InlineCode_BecomesAPaddedChip()
    {
        MarkdownTestHost.RunSta(() =>
        {
            FlowDocument doc = Transform("some `inline code` here");
            Assert.True(MarkdownInlineCode.ReplaceCodeSpans(doc) >= 1);

            bool chipFound = AllInlines(doc).OfType<InlineUIContainer>()
                .Any(c => c.Child is Border { Child: TextBlock block } && block.Text.Contains("inline code"));
            Assert.True(chipFound, "expected the code span to become a Border chip");
        });
    }

    [Fact]
    public void InlineCodeInsideEmphasis_IsStillSwapped()
    {
        MarkdownTestHost.RunSta(() =>
        {
            FlowDocument doc = Transform("**bold `code` here**");
            Assert.True(MarkdownInlineCode.ReplaceCodeSpans(doc) >= 1);
        });
    }

    [Fact]
    public void Quarantine_KeepsTheRestOfTheDocument()
    {
        MarkdownTestHost.RunSta(() =>
        {
            FlowDocument Quarantine(string chunk)
            {
                if (chunk.Contains("BOOM", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("synthetic transform failure");
                }
                return new FlowDocument(new Paragraph(new Run(chunk)));
            }

            FlowDocument doc = MarkdownQuarantine.RenderSafe(
                Quarantine, null, "first fine block\n\nBOOM block\n\nlast fine block", out int quarantined);

            Assert.Equal(1, quarantined);
            string text = TextOf(doc);
            Assert.Contains("first fine block", text);
            Assert.Contains("last fine block", text);
            Assert.Contains("Preview unavailable", text);
            Assert.Contains("BOOM block", text); // raw source is never lost
        });
    }

    [Fact]
    public void Quarantine_EmptyDocumentGetsAHint()
    {
        MarkdownTestHost.RunSta(() =>
        {
            FlowDocument doc = MarkdownQuarantine.RenderSafe(
                chunk => new FlowDocument(new Paragraph(new Run(chunk))), null, "   ", out int quarantined);

            Assert.Equal(0, quarantined);
            Assert.Contains("Nothing to preview yet", TextOf(doc));
        });
    }
}
