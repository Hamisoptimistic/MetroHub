using System;
using System.Linq;
using MetroHub.Widgets.Catalog.Markdown;
using Xunit;

namespace MetroHub.Tests;

/// <summary>
/// Pure, UI-free pins for <see cref="MarkdownRenderGate"/>: the gate is the only thing standing
/// between untrusted pasted markdown and the WPF transform, and every step in it has been the
/// cause of a shipped bug at least once. These tests need no STA thread.
/// </summary>
public class MarkdownWidgetTests
{
    // ── Length gate ──────────────────────────────────────────

    [Fact]
    public void ShouldRender_AcceptsUpToTheLimit_AndRefusesBeyond()
    {
        Assert.False(MarkdownRenderGate.ShouldRender(null));
        Assert.True(MarkdownRenderGate.ShouldRender(string.Empty));
        Assert.True(MarkdownRenderGate.ShouldRender(new string('a', MarkdownRenderGate.MaxRenderChars)));
        Assert.False(MarkdownRenderGate.ShouldRender(new string('a', MarkdownRenderGate.MaxRenderChars + 1)));
    }

    // ── Code masking (must be invisible to every later gate) ──

    [Fact]
    public void Sanitize_LeavesFencedCodeContentsAlone()
    {
        string result = MarkdownRenderGate.Sanitize("```\n<div>a & b</div>\n```", null);
        Assert.Contains("<div>a & b</div>", result);
    }

    [Fact]
    public void Sanitize_LeavesInlineCodeContentsAlone()
    {
        string result = MarkdownRenderGate.Sanitize("use `a|b` here", null);
        Assert.Contains("`a|b`", result);
    }

    [Fact]
    public void Sanitize_SurvivesAnUnclosedFence()
    {
        string result = MarkdownRenderGate.Sanitize("```\nhello world", null);
        Assert.Contains("hello world", result);
    }

    [Fact]
    public void Sanitize_MasksCodeBeforeTheTableGate()
    {
        // A fenced "table" must not be row/column capped: gates never see code contents.
        string fenced = "```\n| a | b |\n| --- | --- |\n| 1 | 2 |\n```";
        string result = MarkdownRenderGate.Sanitize(fenced, null);
        Assert.DoesNotContain("columns hidden", result);
        Assert.Contains("| 1 | 2 |", result);
    }

    // ── HTML: decode must happen BEFORE the strip (regression) ──

    [Fact]
    public void Sanitize_StripsTagsThatWereEscapedInTheSource()
    {
        // `&lt;details&gt;` used to survive the strip as an entity and be re-materialized as a live
        // tag by the decode that ran afterwards — handing raw markup to the engine.
        string result = MarkdownRenderGate.Sanitize("&lt;details&gt;\ninner text\n&lt;/details&gt;", null);
        Assert.DoesNotContain("<details>", result);
        Assert.Contains("inner text", result);
    }

    [Fact]
    public void Sanitize_DecodesEntities()
    {
        Assert.Contains("a & b", MarkdownRenderGate.Sanitize("a &amp; b", null));
    }

    [Fact]
    public void Sanitize_DropsScriptBlocksWithTheirContents()
    {
        string result = MarkdownRenderGate.Sanitize("before\n\n<script>alert(1)</script>\n\nafter", null);
        Assert.DoesNotContain("alert(1)", result);
        Assert.Contains("after", result);
    }

    [Fact]
    public void Sanitize_KeepsAutolinksAndPlainComparisons()
    {
        string source = "Visit <https://example.com> and check a < b and EventHandler<T>.";
        string result = MarkdownRenderGate.Sanitize(source, null);
        Assert.Contains("<https://example.com>", result);
        Assert.Contains("a < b", result);
        Assert.Contains("EventHandler<T>", result);
    }

    // ── Images: local-only policy ────────────────────────────

    [Fact]
    public void Sanitize_ReplacesRemoteImages()
    {
        string result = MarkdownRenderGate.Sanitize("![alt](https://example.com/x.png)", null);
        Assert.Contains("remote images are not loaded", result);
        Assert.DoesNotContain("https://example.com/x.png", result);
    }

    [Fact]
    public void Sanitize_ReplacesDataUriImages()
    {
        string result = MarkdownRenderGate.Sanitize("![alt](data:image/png;base64,AAAA)", null);
        Assert.Contains("embedded images are not decoded", result);
        Assert.DoesNotContain("base64,AAAA", result);
    }

    [Fact]
    public void Sanitize_ReportsMissingLocalFiles()
    {
        string result = MarkdownRenderGate.Sanitize("![pic](definitely-missing.png)", @"C:\definitely-not-here");
        Assert.Contains("file not found", result);
    }

    [Fact]
    public void Sanitize_AsksForAFileWhenRelativePathHasNoBase()
    {
        string result = MarkdownRenderGate.Sanitize("![pic](diagram.png)", null);
        Assert.Contains("open the file to resolve images", result);
    }

    [Fact]
    public void Sanitize_CapsTheImageCount()
    {
        string source = string.Join("\n", Enumerable.Range(1, MarkdownRenderGate.MaxImages + 1)
            .Select(i => $"![img{i}](https://example.com/{i}.png)"));
        string result = MarkdownRenderGate.Sanitize(source, null);
        Assert.Contains("image limit", result);
    }

    // ── Tables ───────────────────────────────────────────────

    private static string BuildTable(int rows)
    {
        var lines = new System.Collections.Generic.List<string> { "| a | b |", "| --- | --- |" };
        for (int i = 1; i <= rows; i++)
        {
            lines.Add($"| r{i} | v{i} |");
        }
        return string.Join("\n", lines);
    }

    [Fact]
    public void Sanitize_CapsTableRows()
    {
        string result = MarkdownRenderGate.Sanitize(BuildTable(MarkdownRenderGate.MaxTableRows + 5), null);
        Assert.Contains($"+5 more rows hidden", result);
        Assert.DoesNotContain($"| r{MarkdownRenderGate.MaxTableRows + 5} |", result);
    }

    [Fact]
    public void Sanitize_CapsTableColumns()
    {
        var header = "| " + string.Join(" | ", Enumerable.Range(1, MarkdownRenderGate.MaxTableColumns + 2).Select(i => $"h{i}")) + " |";
        var separator = "| " + string.Join(" | ", Enumerable.Range(1, MarkdownRenderGate.MaxTableColumns + 2).Select(_ => "---")) + " |";
        var row = "| " + string.Join(" | ", Enumerable.Range(1, MarkdownRenderGate.MaxTableColumns + 2).Select(i => $"c{i}")) + " |";
        string result = MarkdownRenderGate.Sanitize($"{header}\n{separator}\n{row}", null);
        Assert.Contains("+2 columns hidden", result);
    }

    [Fact]
    public void Sanitize_DoesNotTreatPipeProseAsATable()
    {
        // Regression: this shape used to be capped, injecting "*+N columns hidden*" into a paragraph.
        string source = "alpha | beta\n---\ngamma | delta";
        string result = MarkdownRenderGate.Sanitize(source, null);
        Assert.Contains("alpha | beta", result);
        Assert.Contains("gamma | delta", result);
        Assert.DoesNotContain("columns hidden", result);
        Assert.DoesNotContain("more rows hidden", result);
    }

    // ── Nesting ──────────────────────────────────────────────

    [Fact]
    public void Sanitize_ClampsAbsurdQuoteDepth()
    {
        string result = MarkdownRenderGate.Sanitize("> > > > > deep", null).Trim();
        Assert.Equal("> > > deep", result);
    }

    [Fact]
    public void Sanitize_KeepsDeepListIndentation()
    {
        string source = "                    - deeply indented item";
        string result = MarkdownRenderGate.Sanitize(source, null);
        Assert.Contains("- deeply indented item", result);
    }

    // ── Front matter ─────────────────────────────────────────

    [Fact]
    public void Sanitize_PromotesFrontMatterTitleToHeading()
    {
        string result = MarkdownRenderGate.Sanitize("---\ntitle: My Doc\n---\n\nbody text", null);
        Assert.Contains("# My Doc", result);
        Assert.Contains("body text", result);
        Assert.DoesNotContain("title:", result);
    }

    [Fact]
    public void Sanitize_LeavesAThemedDocumentAlone()
    {
        // Two rules with prose between them is not front matter — the middle must survive.
        string source = "---\n\nSome prose here\n\n---\n\nMore prose";
        string result = MarkdownRenderGate.Sanitize(source, null);
        Assert.Contains("Some prose here", result);
        Assert.Contains("More prose", result);
    }

    // ── Links ────────────────────────────────────────────────

    [Fact]
    public void Sanitize_FlattensNestedLinksToTheOuterOne()
    {
        string result = MarkdownRenderGate.Sanitize("[[text](https://inner.example)](https://outer.example)", null);
        Assert.Contains("[text](https://outer.example)", result);
        Assert.DoesNotContain("inner.example", result);
    }

    [Fact]
    public void Sanitize_ResolvesReferenceLinksAndDropsDefinitions()
    {
        string result = MarkdownRenderGate.Sanitize("See [docs][ref] for more.\n\n[ref]: https://example.com/docs", null);
        Assert.Contains("[docs](https://example.com/docs)", result);
        Assert.DoesNotContain("[ref]:", result);
    }

    [Fact]
    public void Sanitize_LeavesUnknownReferenceLinksAlone()
    {
        string result = MarkdownRenderGate.Sanitize("[docs][missing]", null);
        Assert.Contains("[docs][missing]", result);
    }

    // ── Task lists ───────────────────────────────────────────

    [Fact]
    public void NormalizeTaskLists_KeepsTheBulletAndSwapsTheBox()
    {
        string result = MarkdownRenderGate.NormalizeTaskLists("- [ ] buy milk\n- [x] done");
        Assert.Contains($"- {MarkdownRenderGate.TaskBoxUnchecked} buy milk", result);
        Assert.Contains($"- {MarkdownRenderGate.TaskBoxChecked} done", result);
        Assert.DoesNotContain("[ ]", result);
    }

    [Fact]
    public void NormalizeTaskLists_KeepsQuotePrefixes()
    {
        string result = MarkdownRenderGate.NormalizeTaskLists("> - [x] quoted task");
        Assert.Contains(MarkdownRenderGate.TaskBoxChecked.ToString(), result);
        Assert.StartsWith("> ", result);
    }

    // ── Local path resolution ────────────────────────────────

    [Fact]
    public void ResolveLocalPaths_RewritesRelativeImageAgainstTheBaseDirectory()
    {
        using var sandbox = new MarkdownSandbox();
        string image = System.IO.Path.Combine(sandbox.DocsDir, "diagram.png");
        System.IO.File.WriteAllBytes(image, new byte[] { 1, 2, 3 });

        string result = MarkdownRenderGate.ResolveLocalPaths("![d](diagram.png)", sandbox.DocsDir);
        Assert.Contains("file:///", result);
        Assert.Contains("diagram.png", result);
    }

    [Fact]
    public void Sanitize_KeepsWindowsPathsWithSpacesWhole()
    {
        // Exercised through the public image gate: a Windows path with spaces must survive whole.
        using var sandbox = new MarkdownSandbox();
        string image = System.IO.Path.Combine(sandbox.DocsDir, "my diagram.png");
        System.IO.File.WriteAllBytes(image, new byte[] { 1 });

        string result = MarkdownRenderGate.Sanitize("![d](my diagram.png \"a title\")", sandbox.DocsDir);
        Assert.DoesNotContain("file not found", result);
    }
}
