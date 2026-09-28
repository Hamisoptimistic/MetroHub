using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MetroHub.Widgets.Catalog.Markdown;

/// <summary>
/// Pure, UI-free input gates for the Markdown widget. Deterministic string scans
/// (microseconds) that run BEFORE the expensive FlowDocument transform, cheapest first:
/// length → line endings → front matter → code mask → reference links → raw HTML →
/// task lists → images → tables → nesting → unmask. No WPF types here — fully unit-testable without an STA thread.
/// </summary>
public static partial class MarkdownRenderGate
{
    /// <summary>Docs longer than this are never rendered (Write tab still works).</summary>
    public const int MaxRenderChars = 100_000;

    /// <summary>Max images rendered per doc; extras become placeholders.</summary>
    public const int MaxImages = 10;

    /// <summary>Max data rows rendered per table; extras collapse to a "+N more" line.</summary>
    public const int MaxTableRows = 20;

    /// <summary>Max columns rendered per table; extras collapse to a "+N cols" line.</summary>
    public const int MaxTableColumns = 8;

    /// <summary>Nesting deeper than this is flattened (WPF layout cost goes nonlinear).</summary>
    public const int MaxQuoteDepth = 3;

    /// <summary>
    /// Fluent checkbox glyphs (empty box + box-with-check, a designed matched pair).
    /// PUA chars — meaningless outside the app — but checklists are display-only
    /// preview chrome, and matching the app's checkbox look beats copy-paste purity.
    /// Rendered in <see cref="MarkdownTaskLists"/> with Fluent/MDL2 fallback fonts.
    /// </summary>
    public const char TaskBoxUnchecked = '\uE739'; // Checkbox
    public const char TaskBoxChecked = '\uE73A';   // CheckboxComposite

    /// <summary>Leading-space indent deeper than this is clamped (fenced code excluded).</summary>
    public const int MaxIndentSpaces = 12;

    /// <summary>Cheap gate: only length is checked. Empty docs render trivially.</summary>
    public static bool ShouldRender(string? markdown)
        => markdown != null && markdown.Length <= MaxRenderChars;

    /// <summary>Applies every sanitize policy in cheap-first order.</summary>
    public static string Sanitize(string markdown, string? baseDir)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return markdown;
        }

        string result = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\0", string.Empty);
        result = StripFrontMatter(result);
        // Code (fences + inline spans) is masked FIRST: everything after it — HTML stripping,
        // task lists, image/table caps — must never see code contents, or `<tags>`, comparison
        // operators and sample tables inside code get rewritten (regression tests pin this).
        var code = new List<string>();
        result = MaskCode(result, code);
        result = FlattenNestedLinks(result);
        result = ResolveReferenceLinks(result);
        // Entities are decoded BEFORE the HTML strip: the strip must have the last word on what
        // counts as a tag, or `&lt;details&gt;` survives the gate as an entity and is re-materialized
        // as a live tag by the decode that used to run afterwards. Masked code is unaffected either
        // way (it is not visible to this step), and escaped HTML now renders as its inner text
        // instead of being handed to the engine as markup.
        result = DecodeHtmlEntities(result);
        result = StripRawHtml(result);
        result = NormalizeTaskLists(result);
        result = CapImages(result, baseDir);
        result = CapTables(result);
        result = CapNesting(result);
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            result = ResolveLocalPaths(result, baseDir);
        }
        return UnmaskCode(result, code);
    }

    /// <summary>
    /// Drops Obsidian-style YAML front matter. A `title:` key is reborn as an H1
    /// so the doc keeps its name instead of rendering `---` garbage.
    /// Only accepted when the body actually looks like YAML (key/list/comment lines):
    /// a doc that merely STARTS with a `---` rule and has another rule later is a
    /// themed document — swallowing its middle would silently delete paragraphs.
    /// </summary>
    public static string StripFrontMatter(string markdown)
    {
        string[] lines = markdown.Split('\n');
        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            return markdown;
        }
        int close = -1;
        for (int i = 1; i < Math.Min(lines.Length, 31); i++)
        {
            if (lines[i].Trim() is "---" or "...")
            {
                close = i;
                break;
            }
        }
        if (close < 0)
        {
            return markdown; // No closing fence — not front matter, leave alone.
        }
        string? title = null;
        bool hasKeys = false;
        for (int i = 1; i < close; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            if (trimmed.StartsWith("# ", StringComparison.Ordinal) || trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                return markdown; // Markdown heading inside fences → not YAML front matter.
            }
            if (!FrontMatterKeyRegex().IsMatch(trimmed))
            {
                return markdown; // Prose between two rules → not front matter.
            }
            hasKeys = true;
            if (title == null)
            {
                var m = FrontMatterTitleRegex().Match(lines[i]);
                if (m.Success)
                {
                    title = m.Groups["title"].Value.Trim().Trim('"', '\'');
                }
            }
        }
        if (!hasKeys)
        {
            return markdown;
        }
        string rest = string.Join("\n", lines, close + 1, lines.Length - close - 1);
        return title != null ? $"# {title}\n\n{rest}" : rest;
    }

    /// <summary>
    /// CommonMark forbids nested links: <c>[[text](inner)](outer)</c> must not crash.
    /// Left alone, the engine nests a Hyperlink inside a Hyperlink and WPF throws —
    /// killing the WHOLE preview (torture-doc line 118). Flatten to the outer link.
    /// Runs right after <see cref="MaskCode"/> so code is never touched.
    /// </summary>
    public static string FlattenNestedLinks(string markdown)
        => NestedLinkRegex().Replace(markdown, m => $"[{m.Groups["text"].Value}]({m.Groups["url"].Value})");

    /// <summary>
    /// MDXaml core has no reference-style link support: <c>[text][ref]</c> usages render
    /// literally AND the <c>[ref]: url</c> definition lines render as stray paragraphs
    /// (consecutive lines merging into one visible blob above the next separator).
    /// Resolve them in the gate: harvest up-to-3-space-indented definition lines, drop
    /// them, rewrite usages (full <c>[text][ref]</c>, collapsed <c>[text][]</c>, shortcut
    /// <c>[ref]</c>, and image <c>![alt][ref]</c>) to inline form. Runs AFTER
    /// <see cref="MaskCode"/> so code is never touched, BEFORE <see cref="CapImages"/>
    /// so resolved images still get existence checks. Unknown refs are left alone.
    /// </summary>
    public static string ResolveReferenceLinks(string markdown)
    {
        var defs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] lines = markdown.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (string line in lines)
        {
            Match def = LinkDefRegex().Match(line);
            if (def.Success)
            {
                string key = def.Groups["label"].Value.Trim();
                string dest = def.Groups["dest"].Value.Trim().Trim('<', '>');
                if (key.Length > 0 && dest.Length > 0)
                {
                    defs.TryAdd(key, dest);
                }
                continue; // Definition lines never render — drop (matched or not).
            }
            kept.Add(line);
        }
        if (defs.Count == 0)
        {
            return markdown;
        }
        string result = string.Join("\n", kept);
        result = RefImageRegex().Replace(result, m =>
        {
            string key = m.Groups["ref"].Value;
            if (key.Length == 0)
            {
                key = m.Groups["alt"].Value;
            }
            return defs.TryGetValue(key, out string? url) ? $"![{m.Groups["alt"].Value}]({url})" : m.Value;
        });
        result = RefFullRegex().Replace(result, m =>
        {
            string key = m.Groups["ref"].Value;
            if (key.Length == 0)
            {
                key = m.Groups["text"].Value; // Collapsed [text][] → key is the text.
            }
            return defs.TryGetValue(key, out string? url) ? $"[{m.Groups["text"].Value}]({url})" : m.Value;
        });
        result = RefShortcutRegex().Replace(result, m =>
        {
            string key = m.Groups["ref"].Value;
            return defs.TryGetValue(key, out string? url) ? $"[{key}]({url})" : m.Value;
        });
        return result;
    }

    /// <summary>
    /// MDXaml core has no HTML renderer — pasted web content would render as weird
    /// invisible output. Script/style blocks go with their contents; &lt;br&gt; becomes
    /// a markdown hard break (line separation survives inside table cells); other tags
    /// are stripped, inner text kept. Runs AFTER <see cref="MaskCode"/> so code contents
    /// (fences, inline spans) are never touched.
    /// The tag pattern deliberately requires a LOWERCASE letter after "&lt;": prose
    /// math/comparisons ("&lt; 1%", "a &lt; b") and generics ("EventHandler&lt;T&gt;")
    /// are not tags — the old &lt;[^&gt;]+&gt; deleted everything up to the next
    /// "&gt;" anywhere later in the doc (whole FOCUS sections vanished). Autolinks
    /// (&lt;https://…&gt;) match tag shape but are real markdown — kept for the engine.
    /// </summary>
    public static string StripRawHtml(string markdown)
    {
        string result = ScriptStyleRegex().Replace(markdown, string.Empty);
        string[] lines = result.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains('|'))
            {
                lines[i] = BreakRegex().Replace(lines[i], " ");
            }
            else
            {
                lines[i] = BreakRegex().Replace(lines[i], "  \n");
            }
        }
        result = string.Join("\n", lines);
        return HtmlTagRegex().Replace(result, m =>
            m.Value.Contains("://") || m.Value.Contains('@') ? m.Value : string.Empty);
    }

    /// <summary>
    /// Decodes HTML entities (e.g. &amp;quot;, &amp;lt;, &amp;gt;, &amp;amp;, &amp;#39;, &amp;#x1F525;)
    /// outside of code blocks so they render as proper characters.
    /// </summary>
    public static string DecodeHtmlEntities(string markdown)
    {
        return System.Net.WebUtility.HtmlDecode(markdown);
    }


    /// <summary>
    /// MDXaml core (MarkdownSharp lineage) has no GFM task-list support, so
    /// "- [ ]" renders literally. Rewrite to ballot-box glyphs before transform:
    /// "- [ ] buy milk" → "- [E739] buy milk", "- [x] done" → "- [E73A] done"
    /// (Fluent Checkbox / CheckboxComposite glyphs — see <see cref="TaskBoxUnchecked"/>).
    /// The bullet marker MUST be kept: stripping it demotes the line to plain text,
    /// and consecutive plain lines collapse into ONE flowing paragraph (the run-on
    /// checklist bug — every item floating mid-sentence after the first).
    /// Quote prefixes are matched and preserved: "> - [x] done" → "> - [E73A] done".
    /// Without this, tasks inside blockquotes (status docs, callouts) keep their
    /// literal "[x]" text and never show ticked.
    /// Display-only (not interactive) — matches preview semantics.
    /// </summary>
    public static string NormalizeTaskLists(string markdown)
    {
        string uncheckedResult = TaskUncheckedRegex().Replace(markdown,
            m => $"{m.Groups["quote"].Value}{m.Groups["indent"].Value}{m.Groups["marker"].Value} {TaskBoxUnchecked} ");
        return TaskCheckedRegex().Replace(uncheckedResult,
            m => $"{m.Groups["quote"].Value}{m.Groups["indent"].Value}{m.Groups["marker"].Value} {TaskBoxChecked} ");
    }

    /// <summary>
    /// Local-only image policy with existence checks:
    /// remote → placeholder, data: URIs → placeholder (never decode MBs of base64),
    /// missing files → placeholder, relative paths without a base dir → placeholder,
    /// beyond <see cref="MaxImages"/> → skipped placeholder. survivors pass through.
    /// When the image is wrapped in an outer link ([![alt](url)](target)), placeholders
    /// stay INLINE: a multiline blockquote there would shred the parent link syntax
    /// and leave dangling "](target)" text in the render.
    /// </summary>
    public static string CapImages(string markdown, string? baseDir)
    {
        int seen = 0;
        return ImageRegex().Replace(markdown, match =>
        {
            string alt = match.Groups["alt"].Value.Trim();
            string url = NormalizeImageUrl(match.Groups["url"].Value);
            seen++;

            // Link-wrapped image: "[![" — block syntax would break the outer link.
            // Inline fallback carries NO brackets of its own: the outer "[…](…)"
            // supplies them, otherwise link text shows "[[🖼 …]]" doubled.
            bool inlineOnly = match.Index > 0 && markdown[match.Index - 1] == '[';
            string Placeholder(string label, string reason)
                => inlineOnly ? $"🖼 {FirstLine(label)}" : $"\n> [Image: {FirstLine(label)} — {reason}]\n";

            if (seen > MaxImages)
            {
                return inlineOnly
                    ? $"🖼 {FirstLine(alt)}"
                    : $"\n> [Image skipped — {MaxImages} image limit]: {FirstLine(alt)}\n";
            }
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                string label = string.IsNullOrWhiteSpace(alt) ? url : alt;
                return Placeholder(label, "remote images are not loaded");
            }
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return Placeholder(alt, "embedded images are not decoded");
            }
            if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            bool rooted = Path.IsPathRooted(url);
            if (!rooted && string.IsNullOrWhiteSpace(baseDir))
            {
                return Placeholder(string.IsNullOrWhiteSpace(alt) ? url : alt, "open the file to resolve images");
            }
            try
            {
                string full = rooted ? url : Path.GetFullPath(Path.Combine(baseDir!, url));
                if (!File.Exists(full))
                {
                    string name = Path.GetFileName(url);
                    return Placeholder(string.IsNullOrWhiteSpace(alt) ? name : alt, "file not found");
                }
            }
            catch
            {
                return match.Value;
            }
            return match.Value;
        });
    }

    /// <summary>
    /// Truncates long/wide tables: keeps header + separator + first <see cref="MaxTableRows"/>
    /// data rows and <see cref="MaxTableColumns"/> columns, appends italic "+N more" notes
    /// (still valid markdown).
    /// </summary>
    public static string CapTables(string markdown)
    {
        string[] lines = markdown.Split('\n');
        var output = new List<string>(lines.Length);
        var block = new List<string>();

        void FlushBlock()
        {
            if (block.Count == 0)
            {
                return;
            }
            if (IsTableBlock(block))
            {
                int dataRows = block.Count - 2;
                int hiddenCols = 0;
                for (int i = 0; i < block.Count; i++)
                {
                    int cols = ColumnCount(block[i]);
                    if (cols > MaxTableColumns)
                    {
                        hiddenCols = Math.Max(hiddenCols, cols - MaxTableColumns);
                        block[i] = KeepColumns(block[i], MaxTableColumns);
                    }
                }
                if (dataRows > MaxTableRows)
                {
                    var kept = new List<string> { block[0], block[1] };
                    for (int i = 2; i < 2 + MaxTableRows; i++)
                    {
                        kept.Add(block[i]);
                    }
                    block.Clear();
                    block.AddRange(kept);
                    output.AddRange(block);
                    output.Add(string.Empty);
                    output.Add($"*+{dataRows - MaxTableRows} more rows hidden (preview limit).*");
                }
                else
                {
                    output.AddRange(block);
                }
                if (hiddenCols > 0)
                {
                    output.Add(string.Empty);
                    output.Add($"*+{hiddenCols} columns hidden (preview limit).*");
                }
            }
            else
            {
                output.AddRange(block);
            }
            block.Clear();
        }

        foreach (string line in lines)
        {
            if (line.Contains('|'))
            {
                block.Add(line);
            }
            else
            {
                FlushBlock();
                output.Add(line);
            }
        }
        FlushBlock();
        return string.Join("\n", output);
    }

    /// <summary>
    /// Flattens absurd nesting (WPF layout cost goes nonlinear with visual-tree depth).
    /// Quote runs deeper than <see cref="MaxQuoteDepth"/> and indents past
    /// <see cref="MaxIndentSpaces"/> are clamped. Fenced code blocks (``` / ~~~,
    /// including quote-prefixed fences) are excluded. List-item lines keep their
    /// indent at any depth — clamping them would collapse 4th+ nesting levels back
    /// up; only code-like indents are clamped.
    /// </summary>
    public static string CapNesting(string markdown)
    {
        string[] lines = markdown.Split('\n');
        bool inFence = false;
        char fenceChar = '\0';
        int fenceLen = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (TryGetFence(line, out char fc, out int fl))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceChar = fc;
                    fenceLen = fl;
                }
                else if (fc == fenceChar && fl >= fenceLen && FenceRemainderBlank(line, fl))
                {
                    inFence = false;
                }
                continue;
            }
            if (inFence)
            {
                continue;
            }
            line = DeepQuoteRegex().Replace(line, "> > > ");
            var indent = IndentRegex().Match(line);
            if (indent.Success && indent.Groups["s"].Length > MaxIndentSpaces)
            {
                string rest = line.Substring(indent.Groups["s"].Length);
                if (!ListMarkerLineRegex().IsMatch(rest))
                {
                    line = new string(' ', MaxIndentSpaces) + rest;
                }
            }
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Resolves relative local image paths against the source file's directory so MDXaml
    /// can load them. Remote URLs and already-absolute paths are untouched. Best effort.
    /// </summary>
    public static string ResolveLocalPaths(string markdown, string baseDir)
    {
        return ImageRegex().Replace(markdown, match =>
        {
            string url = NormalizeImageUrl(match.Groups["url"].Value);
            if (url.Length == 0 ||
                url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(url))
            {
                return match.Value;
            }
            try
            {
                string full = Path.GetFullPath(Path.Combine(baseDir, url));
                string fileUrl = new Uri(full).AbsoluteUri;
                // Rebuild from groups (never string.Replace: alt text can repeat the path).
                return $"![{match.Groups["alt"].Value}]({fileUrl})";
            }
            catch
            {
                return match.Value;
            }
        });
    }

    /// <summary>
    /// Image URLs arrive raw from the capture group and may carry CommonMark decoration:
    /// angle-bracket wrapping <c>&lt;path with spaces&gt;</c> and an optional trailing title
    /// (<c>path "title"</c>). Returns just the path. Keeps Windows paths with spaces whole —
    /// the old regex stopped at the first space and reported "file not found" for real files.
    /// </summary>
    private static string NormalizeImageUrl(string raw)
    {
        string url = raw.Trim();
        if (url.EndsWith('"'))
        {
            int cut = url.LastIndexOf(" \"", StringComparison.Ordinal);
            if (cut > 0)
            {
                url = url.Substring(0, cut).TrimEnd();
            }
        }
        else if (url.EndsWith('\''))
        {
            int cut = url.LastIndexOf(" '", StringComparison.Ordinal);
            if (cut > 0)
            {
                url = url.Substring(0, cut).TrimEnd();
            }
        }
        if (url.Length >= 2 && url[0] == '<' && url[^1] == '>')
        {
            url = url.Substring(1, url.Length - 2).Trim();
        }
        return url;
    }

    /// <summary>
    /// Replaces fenced blocks (``` / ~~~, info strings, indented openers, unclosed runs to
    /// end-of-doc, quote-prefixed fences like "> ```csharp") and inline code spans with
    /// inert placeholders so no later gate can see inside code. Restored by
    /// <see cref="UnmaskCode"/> as the final sanitize step.
    /// </summary>
    private static string MaskCode(string markdown, List<string> store)
    {
        string[] lines = markdown.Split('\n');
        var output = new List<string>(lines.Length);
        int i = 0;
        while (i < lines.Length)
        {
            if (!TryGetFence(lines[i], out char fenceChar, out int openLen))
            {
                output.Add(lines[i]);
                i++;
                continue;
            }
            int j = i + 1;
            bool closed = false;
            for (; j < lines.Length; j++)
            {
                if (TryGetFence(lines[j], out char closeChar, out int closeLen) &&
                    closeChar == fenceChar && closeLen >= openLen &&
                    FenceRemainderBlank(lines[j], closeLen))
                {
                    closed = true;
                    break;
                }
            }
            if (!closed)
            {
                j = lines.Length - 1; // CommonMark: unclosed fence runs to end of document.
            }
            store.Add(string.Join("\n", lines, i, j - i + 1));
            output.Add($"\0c{store.Count - 1}\0");
            i = j + 1;
        }
        return InlineCodeRegex().Replace(string.Join("\n", output), m =>
        {
            store.Add(m.Value);
            return $"\0c{store.Count - 1}\0";
        });
    }

    /// <summary>
    /// Strips blockquote prefixes so fence detection sees the real content:
    /// "> ```csharp" is a fence, not prose. Only used for detection — stored
    /// lines keep their original text verbatim.
    /// </summary>
    private static string StripQuotePrefixes(string line)
    {
        string current = line;
        while (true)
        {
            string trimmed = current.TrimStart();
            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                current = trimmed.Substring(1);
                continue;
            }
            return current;
        }
    }

    /// <summary>Detects ``` / ~~~ openers of length ≥ 3 (after quote prefixes).</summary>
    private static bool TryGetFence(string line, out char fenceChar, out int length)
    {
        string trimmed = StripQuotePrefixes(line).TrimStart();
        fenceChar = '\0';
        length = 0;
        if (trimmed.Length == 0 || (trimmed[0] != '`' && trimmed[0] != '~'))
        {
            return false;
        }
        fenceChar = trimmed[0];
        while (length < trimmed.Length && trimmed[length] == fenceChar)
        {
            length++;
        }
        return length >= 3;
    }

    /// <summary>True when nothing but whitespace follows the fence run (closers only).</summary>
    private static bool FenceRemainderBlank(string line, int fenceLen)
        => StripQuotePrefixes(line).TrimStart().AsSpan(fenceLen).Trim().IsEmpty;

    /// <summary>Restores code masked by <see cref="MaskCode"/>. Always the last sanitize step.</summary>
    private static string UnmaskCode(string markdown, List<string> store)
    {
        if (store.Count == 0)
        {
            return markdown;
        }
        string[] lines = markdown.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains('\0'))
            {
                bool isTable = lines[i].Contains('|');
                lines[i] = PlaceholderRegex().Replace(lines[i], m =>
                {
                    int index = int.Parse(m.Groups["i"].Value);
                    if (index >= 0 && index < store.Count)
                    {
                        string c = store[index];
                        if (isTable && c.StartsWith('`') && c.EndsWith('`') && c.Contains('|'))
                        {
                            return c.Replace("|", "&#124;");
                        }
                        return c;
                    }
                    return string.Empty;
                });
            }
        }
        return string.Join("\n", lines);
    }

    private static bool IsTableBlock(List<string> block)
    {
        if (block.Count < 3)
        {
            return false;
        }
        // Only classify a block the engine will actually render as a table. Prose that merely
        // brackets a `---` rule between two lines that happen to contain a pipe is not a table —
        // capping it injected "*+N columns hidden*" into the middle of a paragraph. A pipe in the
        // separator row (and at least two header cells) is what GFM needs to see.
        if (!block[1].Contains('|'))
        {
            return false;
        }
        if (ColumnCount(block[0]) < 2)
        {
            return false;
        }
        // Line 1 must be the :---: separator row.
        string sep = block[1].Trim().Trim('|').Trim();
        if (sep.Length == 0)
        {
            return false;
        }
        foreach (char c in sep)
        {
            if (c != '-' && c != ':' && c != '|' && c != ' ')
            {
                return false;
            }
        }
        return true;
    }

    private static int ColumnCount(string row)
    {
        string trimmed = row.Trim();
        string[] cells = TableCellSplitRegex().Split(trimmed);
        int count = cells.Length;
        if (cells.Length > 0 && string.IsNullOrWhiteSpace(cells[0]))
        {
            count--; // Leading border pipe.
        }
        if (cells.Length > 1 && string.IsNullOrWhiteSpace(cells[^1]))
        {
            count--; // Trailing border pipe.
        }
        return Math.Max(count, 0);
    }

    private static string KeepColumns(string row, int keep)
    {
        bool leading = row.TrimStart().StartsWith('|');
        bool trailing = row.TrimEnd().EndsWith('|');
        var cells = new List<string>();
        foreach (string cell in TableCellSplitRegex().Split(row))
        {
            if (string.IsNullOrWhiteSpace(cell) && (cells.Count == 0))
            {
                continue; // Leading border pipe.
            }
            cells.Add(cell);
        }
        // Drop a trailing empty from the border pipe.
        if (cells.Count > 0 && string.IsNullOrWhiteSpace(cells[^1]) && trailing)
        {
            cells.RemoveAt(cells.Count - 1);
        }
        while (cells.Count > keep)
        {
            cells.RemoveAt(cells.Count - 1);
        }
        string joined = string.Join("|", cells);
        if (leading)
        {
            joined = "|" + joined;
        }
        if (trailing)
        {
            joined += "|";
        }
        return joined;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "untitled";
        }
        int idx = text.IndexOf('\n');
        string first = (idx >= 0 ? text.Substring(0, idx) : text).Trim();
        return first.Length > 80 ? first.Substring(0, 80) + "…" : first;
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\(\s*(?<url>[^)\n]+?)\s*\)", RegexOptions.Compiled)]
    private static partial Regex ImageRegex();

    // Fenced code / inline code spans → placeholders (see MaskCode/UnmaskCode).
    // Variable-length delimiters per CommonMark: ``code with ` backtick`` masks whole.
    [GeneratedRegex(@"(?<!`)(`+)(?!`)([\s\S]*?[^`])\1(?!`)", RegexOptions.Compiled)]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\u0000c(?<i>\d+)\u0000", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    // A front-matter body line must be YAML-shaped: `key:`, `# comment`, or `- item`.
    [GeneratedRegex(@"^(?:[A-Za-z_][A-Za-z0-9_.\-]*\s*:|#|-\s+)", RegexOptions.Compiled)]
    private static partial Regex FrontMatterKeyRegex();

    [GeneratedRegex(@"(?m)^(?<quote>(?:\s*>\s*)*)(?<indent>\s*)(?<marker>[-*+])\s+\[\s\]\s+", RegexOptions.Compiled)]
    private static partial Regex TaskUncheckedRegex();

    [GeneratedRegex(@"(?m)^(?<quote>(?:\s*>\s*)*)(?<indent>\s*)(?<marker>[-*+])\s+\[[xX]\]\s+", RegexOptions.Compiled)]
    private static partial Regex TaskCheckedRegex();

    [GeneratedRegex(@"(?m)^title:\s*(?<title>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FrontMatterTitleRegex();

    [GeneratedRegex(@"<script\b[^>]*>.*?</script>|<style\b[^>]*>.*?</style>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    // Reference-style links: definitions + usages (MDXaml parses neither).
    // Nested links (flattened before they can nest Hyperlinks and kill the render).
    [GeneratedRegex(@"\[\[(?<text>[^\[\]\n]*)\]\((?<inner>[^)\n]*)\)\]\((?<url>[^)\n]*)\)", RegexOptions.Compiled)]
    private static partial Regex NestedLinkRegex();
    [GeneratedRegex(@"^[ ]{0,3}\[(?<label>[^\]\n]+)\]:\s*(?<dest><[^>\n]*>|\S+)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex LinkDefRegex();

    [GeneratedRegex(@"(?<!\!)\!\[(?<alt>[^\]\n]*)\]\[(?<ref>[^\]\n]*)\]", RegexOptions.Compiled)]
    private static partial Regex RefImageRegex();

    [GeneratedRegex(@"(?<!\!)\[(?<text>[^\]\n]+)\]\[(?<ref>[^\]\n]*)\]", RegexOptions.Compiled)]
    private static partial Regex RefFullRegex();

    [GeneratedRegex(@"(?<!\!)\[(?<ref>[^\]\n\[]+)\](?!\(|\[|:)", RegexOptions.Compiled)]
    private static partial Regex RefShortcutRegex();

    [GeneratedRegex(@"</?[a-z][^>]*>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    // Split table rows on UNESCAPED pipes only: "\|" is cell content, not a boundary.
    [GeneratedRegex(@"(?<!\\)\|", RegexOptions.Compiled)]
    private static partial Regex TableCellSplitRegex();

    [GeneratedRegex(@"^(?:\s*>\s*){4,}", RegexOptions.Compiled)]
    private static partial Regex DeepQuoteRegex();

    [GeneratedRegex(@"^(?:[-*+]|\d{1,9}[.)])\s", RegexOptions.Compiled)]
    private static partial Regex ListMarkerLineRegex();

    [GeneratedRegex(@"^(?<s> +)", RegexOptions.Compiled)]
    private static partial Regex IndentRegex();
}
