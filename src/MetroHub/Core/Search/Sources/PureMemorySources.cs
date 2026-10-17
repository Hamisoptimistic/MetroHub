using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;

namespace MetroHub.Core.Search;

/// <summary>
/// Helper for safe cross-thread clipboard interactions.
/// </summary>
internal static class ClipboardHelper
{
    public static void SetClipboardText(string text)
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(() => System.Windows.Clipboard.SetText(text));
            }
            else
            {
                System.Windows.Clipboard.SetText(text);
            }
        }
        catch (Exception ex)
        {
            Safe.Log("ClipboardHelper.SetClipboardText", ex);
        }
    }
}

/// <summary>
/// High-speed in-memory table source for Windows settings, operating system power commands,
/// and web search bangs. Operates synchronously with sub-millisecond execution.
/// </summary>
public sealed class TableSource : ISearchSource
{
    public string SourceId => "table";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public TableSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var route = _router.Route(query.RawText);
        var results = new List<Candidate>();

        // 1. Settings Hard Route (e.g. "set:bluetooth" or "set:")
        if (route.Kind == RouteKind.Settings)
        {
            string remainder = route.Remainder.Trim();
            if (string.IsNullOrEmpty(remainder))
            {
                int count = Math.Min(8, PureMemoryData.WindowsSettings.Length);
                for (int i = 0; i < count; i++)
                {
                    results.Add(CreateSettingsCandidate(PureMemoryData.WindowsSettings[i]));
                }
            }
            else
            {
                string norm = SearchRanker.NormalizeText(remainder);
                for (int i = 0; i < PureMemoryData.WindowsSettings.Length; i++)
                {
                    var setting = PureMemoryData.WindowsSettings[i];
                    if (MatchesSetting(setting, norm))
                    {
                        results.Add(CreateSettingsCandidate(setting));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // 2. Additive Web Bangs (e.g. "g eazy", "gh metrohub", "yt lo-fi", "ddg privacy")
        if (route.IsAdditive)
        {
            Candidate? bangCandidate = route.Kind switch
            {
                RouteKind.WebBangGoogle => CreateBangCandidate("Google", "https://www.google.com/search?q=", "https://www.google.com", query.RawText.Trim(), route.Remainder),
                RouteKind.WebBangGitHub => CreateBangCandidate("GitHub", "https://github.com/search?q=", "https://github.com", query.RawText.Trim(), route.Remainder),
                RouteKind.WebBangYouTube => CreateBangCandidate("YouTube", "https://www.youtube.com/results?search_query=", "https://www.youtube.com", query.RawText.Trim(), route.Remainder),
                RouteKind.WebBangDuckDuckGo => CreateBangCandidate("DuckDuckGo", "https://duckduckgo.com/?q=", "https://duckduckgo.com", query.RawText.Trim(), route.Remainder),
                _ => null
            };

            if (bangCandidate != null)
            {
                results.Add(bangCandidate);
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 3. Universal Mode (System Commands & Direct Settings Matches)
        if (route.Kind == RouteKind.Universal)
        {
            string normQuery = query.NormalizedText;
            if (!string.IsNullOrEmpty(normQuery))
            {
                for (int i = 0; i < PureMemoryData.SystemCommands.Length; i++)
                {
                    var cmd = PureMemoryData.SystemCommands[i];
                    if (MatchesCommand(cmd, normQuery))
                    {
                        results.Add(CreateCommandCandidate(cmd));
                    }
                }

                if (normQuery.Length >= 3)
                {
                    int addedSettings = 0;
                    for (int i = 0; i < PureMemoryData.WindowsSettings.Length && addedSettings < 3; i++)
                    {
                        var setting = PureMemoryData.WindowsSettings[i];
                        if (MatchesSetting(setting, normQuery))
                        {
                            results.Add(CreateSettingsCandidate(setting));
                            addedSettings++;
                        }
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    private static bool MatchesSetting(SettingsItem setting, string normQuery)
    {
        string titleNorm = SearchRanker.NormalizeText(setting.Title);
        if (titleNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        for (int k = 0; k < setting.Keywords.Length; k++)
        {
            if (setting.Keywords[k].Contains(normQuery, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static bool MatchesCommand(SystemCommandItem cmd, string normQuery)
    {
        string titleNorm = SearchRanker.NormalizeText(cmd.Title);
        if (titleNorm.StartsWith(normQuery, StringComparison.Ordinal)) return true;

        for (int k = 0; k < cmd.Keywords.Length; k++)
        {
            string kw = cmd.Keywords[k];
            if (kw.StartsWith(normQuery, StringComparison.OrdinalIgnoreCase) ||
                normQuery.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private Candidate CreateSettingsCandidate(SettingsItem item)
    {
        return new Candidate(
            Id: "setting:" + item.Uri,
            DisplayName: item.Title,
            FullPathOrKey: item.Uri,
            Category: SearchCategory.Apps,
            SourceId: SourceId)
        {
            PrimaryAction = new CandidateAction(
                Id: "open_setting",
                Title: "Open Windows Setting",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ProcessLauncherService.LaunchTarget(item.Uri);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                })
        };
    }

    private Candidate CreateCommandCandidate(SystemCommandItem cmd)
    {
        return new Candidate(
            Id: cmd.Id,
            DisplayName: cmd.Title,
            FullPathOrKey: cmd.Description,
            Category: SearchCategory.Apps,
            SourceId: SourceId)
        {
            PrimaryAction = new CandidateAction(
                Id: cmd.Id,
                Title: cmd.Title,
                KeyGestureHint: "Enter",
                ExecuteAsync: cmd.ExecuteAsync)
        };
    }

    private Candidate CreateBangCandidate(string engine, string searchUrlPrefix, string homeUrl, string rawText, string queryText)
    {
        string trimmedQuery = queryText.Trim();
        string targetUrl = string.IsNullOrEmpty(trimmedQuery)
            ? homeUrl
            : searchUrlPrefix + Uri.EscapeDataString(trimmedQuery);

        string displayName = string.IsNullOrEmpty(trimmedQuery)
            ? $"Search {engine}"
            : $"{rawText} — Search {engine}";

        return new Candidate(
            Id: $"bang:{engine.ToLowerInvariant()}:{trimmedQuery}",
            DisplayName: displayName,
            FullPathOrKey: targetUrl,
            Category: SearchCategory.Other,
            SourceId: SourceId)
        {
            PrimaryAction = new CandidateAction(
                Id: $"search_{engine.ToLowerInvariant()}",
                Title: $"Search {engine}",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ProcessLauncherService.LaunchTarget(targetUrl);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                })
        };
    }
}

/// <summary>
/// Sub-millisecond math calculator utilizing a bounded Pratt parser.
/// Gated to guarantee zero heap allocations for non-mathematical queries.
/// Supports operators (+, -, *, /, %, ^, ×, ÷), functions (sqrt, cbrt, abs, round, floor, ceil, sin, cos, tan, log, ln, exp, pow),
/// and constants (pi, e). Max input length: 256 chars, max recursion depth: 32.
/// </summary>
public sealed class CalculatorSource : ISearchSource
{
    public string SourceId => "calculator";
    public SourceState State => SourceState.Ready;

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        ReadOnlySpan<char> span = query.RawText.AsSpan().Trim();
        if (!IsCandidateMathExpression(span))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        if (!MathPrattParser.TryEvaluate(span, out double val))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        double rounded = Math.Round(val, 10);
        string formatted = rounded.ToString("0.##########", CultureInfo.InvariantCulture);

        var action = new CandidateAction(
            Id: "copy_calc_result",
            Title: "Copy result to clipboard",
            KeyGestureHint: "Enter",
            ExecuteAsync: _ =>
            {
                ClipboardHelper.SetClipboardText(formatted);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });

        var candidate = new Candidate(
            Id: "calc:" + formatted,
            DisplayName: $"= {formatted}",
            FullPathOrKey: $"{query.RawText.Trim()} = {formatted}",
            Category: SearchCategory.Other,
            SourceId: SourceId)
        {
            PrimaryAction = action
        };

        return Task.FromResult<IReadOnlyList<Candidate>>(new[] { candidate });
    }

    internal static bool IsCandidateMathExpression(ReadOnlySpan<char> input)
    {
        if (input.Length == 0 || input.Length > 256) return false;

        bool hasDigit = false;
        bool hasOperator = false;
        bool hasFunctionOrConstant = false;

        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsAsciiDigit(c))
            {
                hasDigit = true;
                i++;
            }
            else if (c is '+' or '-' or '*' or '/' or '%' or '^' or '×' or '÷')
            {
                hasOperator = true;
                i++;
            }
            else if (c is '(' or ')' or '.' or ',' or ' ')
            {
                i++;
            }
            else if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < input.Length && char.IsAsciiLetter(input[i]))
                {
                    i++;
                }

                ReadOnlySpan<char> word = input.Slice(start, i - start);
                if (word.Equals("sqrt", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("cbrt", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("abs", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("round", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("floor", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("ceil", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("sin", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("cos", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("tan", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("log", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("ln", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("exp", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("pow", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("pi", StringComparison.OrdinalIgnoreCase) ||
                    word.Equals("e", StringComparison.OrdinalIgnoreCase))
                {
                    hasFunctionOrConstant = true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        return (hasDigit && hasOperator) || hasFunctionOrConstant;
    }
}

/// <summary>
/// Bounded Pratt parser for mathematical expressions with depth limit of 32 and input limit of 256.
/// </summary>
internal static class MathPrattParser
{
    public static bool TryEvaluate(ReadOnlySpan<char> input, out double result)
    {
        result = 0;
        if (input.Length == 0 || input.Length > 256) return false;

        try
        {
            var parser = new ParserState(input);
            return parser.TryParse(out result);
        }
        catch
        {
            return false;
        }
    }

    private ref struct ParserState
    {
        private readonly ReadOnlySpan<char> _text;
        private int _pos;
        private int _depth;

        public ParserState(ReadOnlySpan<char> text)
        {
            _text = text;
            _pos = 0;
            _depth = 0;
        }

        public bool TryParse(out double result)
        {
            result = 0;
            SkipWhitespace();
            if (_pos >= _text.Length) return false;

            if (!TryParseExpression(0, out result)) return false;

            SkipWhitespace();
            if (_pos < _text.Length) return false;

            return !double.IsNaN(result) && !double.IsInfinity(result);
        }

        private bool TryParseExpression(int minPrecedence, out double result)
        {
            result = 0;
            _depth++;
            if (_depth > 32) return false;

            try
            {
                if (!TryParsePrefix(out result)) return false;

                while (true)
                {
                    SkipWhitespace();
                    if (_pos >= _text.Length) break;

                    char op = _text[_pos];
                    int precedence = GetBinaryPrecedence(op);
                    if (precedence < minPrecedence || precedence == 0) break;

                    _pos++;

                    int nextMin = (op == '^') ? precedence : precedence + 1;
                    if (!TryParseExpression(nextMin, out double right)) return false;

                    if (!ApplyBinary(op, result, right, out result)) return false;
                }

                return true;
            }
            finally
            {
                _depth--;
            }
        }

        private bool TryParsePrefix(out double result)
        {
            result = 0;
            SkipWhitespace();
            if (_pos >= _text.Length) return false;

            char c = _text[_pos];

            if (c is '+' or '-')
            {
                _pos++;
                if (!TryParseExpression(4, out double operand)) return false;
                result = (c == '-') ? -operand : operand;
                return true;
            }

            if (c == '(')
            {
                _pos++;
                if (!TryParseExpression(0, out result)) return false;
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')') return false;
                _pos++;
                return true;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos + 1])))
            {
                return TryParseNumber(out result);
            }

            if (char.IsAsciiLetter(c))
            {
                return TryParseIdentifierOrFunction(out result);
            }

            return false;
        }

        private bool TryParseNumber(out double result)
        {
            result = 0;
            Span<char> buffer = stackalloc char[64];
            int bIdx = 0;

            bool hasDot = false;
            while (_pos < _text.Length)
            {
                char c = _text[_pos];
                if (char.IsAsciiDigit(c))
                {
                    if (bIdx < buffer.Length) buffer[bIdx++] = c;
                    _pos++;
                }
                else if (c == '.' && !hasDot)
                {
                    hasDot = true;
                    if (bIdx < buffer.Length) buffer[bIdx++] = '.';
                    _pos++;
                }
                else if (c == ',')
                {
                    _pos++;
                }
                else
                {
                    break;
                }
            }

            if (bIdx == 0) return false;
            return double.TryParse(buffer.Slice(0, bIdx), NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        }

        private bool TryParseIdentifierOrFunction(out double result)
        {
            result = 0;
            int start = _pos;
            while (_pos < _text.Length && char.IsAsciiLetter(_text[_pos]))
            {
                _pos++;
            }

            ReadOnlySpan<char> id = _text.Slice(start, _pos - start);

            if (id.Equals("pi", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.PI;
                return true;
            }
            if (id.Equals("e", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.E;
                return true;
            }

            SkipWhitespace();
            if (_pos >= _text.Length || _text[_pos] != '(') return false;
            _pos++;

            if (!TryParseExpression(0, out double arg1)) return false;

            double arg2 = 0;
            if (id.Equals("pow", StringComparison.OrdinalIgnoreCase))
            {
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ',') return false;
                _pos++;
                if (!TryParseExpression(0, out arg2)) return false;
            }

            SkipWhitespace();
            if (_pos >= _text.Length || _text[_pos] != ')') return false;
            _pos++;

            if (id.Equals("sqrt", StringComparison.OrdinalIgnoreCase))
            {
                if (arg1 < 0) return false;
                result = Math.Sqrt(arg1);
                return true;
            }
            if (id.Equals("cbrt", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Cbrt(arg1);
                return true;
            }
            if (id.Equals("abs", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Abs(arg1);
                return true;
            }
            if (id.Equals("round", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Round(arg1);
                return true;
            }
            if (id.Equals("floor", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Floor(arg1);
                return true;
            }
            if (id.Equals("ceil", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Ceiling(arg1);
                return true;
            }
            if (id.Equals("sin", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Sin(arg1);
                return true;
            }
            if (id.Equals("cos", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Cos(arg1);
                return true;
            }
            if (id.Equals("tan", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Tan(arg1);
                return true;
            }
            if (id.Equals("log", StringComparison.OrdinalIgnoreCase))
            {
                if (arg1 <= 0) return false;
                result = Math.Log10(arg1);
                return true;
            }
            if (id.Equals("ln", StringComparison.OrdinalIgnoreCase))
            {
                if (arg1 <= 0) return false;
                result = Math.Log(arg1);
                return true;
            }
            if (id.Equals("exp", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Exp(arg1);
                return true;
            }
            if (id.Equals("pow", StringComparison.OrdinalIgnoreCase))
            {
                result = Math.Pow(arg1, arg2);
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }

            return false;
        }

        private static int GetBinaryPrecedence(char op) => op switch
        {
            '+' or '-' => 1,
            '*' or '/' or '%' or '×' or '÷' => 2,
            '^' => 3,
            _ => 0
        };

        private static bool ApplyBinary(char op, double left, double right, out double result)
        {
            result = 0;
            switch (op)
            {
                case '+':
                    result = left + right;
                    return true;
                case '-':
                    result = left - right;
                    return true;
                case '*' or '×':
                    result = left * right;
                    return true;
                case '/' or '÷':
                    if (Math.Abs(right) < 1e-15) return false;
                    result = left / right;
                    return !double.IsNaN(result) && !double.IsInfinity(result);
                case '%':
                    if (Math.Abs(right) < 1e-15) return false;
                    result = left % right;
                    return !double.IsNaN(result) && !double.IsInfinity(result);
                case '^':
                    result = Math.Pow(left, right);
                    return !double.IsNaN(result) && !double.IsInfinity(result);
                default:
                    return false;
            }
        }

        private void SkipWhitespace()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            {
                _pos++;
            }
        }
    }
}

/// <summary>
/// Source adapter providing string case formatting, slugification, and string metrics.
/// Triggers on "case ", "slug ", "len " prefixes.
/// </summary>
public sealed class TextTransformSource : ISearchSource
{
    public string SourceId => "text_transform";
    public SourceState State => SourceState.Ready;

    private readonly QueryRouter _router;

    public TextTransformSource(QueryRouter? router = null)
    {
        _router = router ?? new QueryRouter();
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || string.IsNullOrWhiteSpace(query.RawText))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string raw = query.RawText.Trim();
        var route = _router.Route(raw);

        // 1. Configured case route or "case "
        if (route.Kind == RouteKind.TextTransform || raw.StartsWith("case ", StringComparison.OrdinalIgnoreCase))
        {
            string text = route.Kind == RouteKind.TextTransform ? route.Remainder : raw[5..].Trim();
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());

            var list = new List<Candidate>
            {
                CreateTransformCandidate("camelCase", ToCamelCase(text)),
                CreateTransformCandidate("PascalCase", ToPascalCase(text)),
                CreateTransformCandidate("snake_case", ToSnakeCase(text)),
                CreateTransformCandidate("kebab-case", ToKebabCase(text)),
                CreateTransformCandidate("UPPERCASE", text.ToUpperInvariant()),
                CreateTransformCandidate("lowercase", text.ToLowerInvariant())
            };
            return Task.FromResult<IReadOnlyList<Candidate>>(list);
        }

        // 2. "slug "
        if (raw.StartsWith("slug ", StringComparison.OrdinalIgnoreCase))
        {
            string text = raw[5..].Trim();
            if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());

            var item = CreateTransformCandidate("slug", ToKebabCase(text));
            return Task.FromResult<IReadOnlyList<Candidate>>(new[] { item });
        }

        // 3. "len "
        if (raw.StartsWith("len ", StringComparison.OrdinalIgnoreCase))
        {
            string text = raw[4..].Trim();
            int chars = text.Length;
            int words = string.IsNullOrWhiteSpace(text) ? 0 : text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            string summary = $"{chars} characters, {words} words";

            var item = CreateTransformCandidate("Length", summary);
            return Task.FromResult<IReadOnlyList<Candidate>>(new[] { item });
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
    }

    private Candidate CreateTransformCandidate(string label, string transformed)
    {
        return new Candidate(
            Id: $"trans:{label.ToLowerInvariant()}:{transformed}",
            DisplayName: $"{label}: {transformed}",
            FullPathOrKey: transformed,
            Category: SearchCategory.Other,
            SourceId: SourceId)
        {
            PrimaryAction = new CandidateAction(
                Id: "copy_transformed",
                Title: $"Copy {label}",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(transformed);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                })
        };
    }

    internal static string ToCamelCase(string text)
    {
        var pascal = ToPascalCase(text);
        if (string.IsNullOrEmpty(pascal)) return string.Empty;
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    internal static string ToPascalCase(string text)
    {
        var words = SplitIntoWords(text);
        var sb = new StringBuilder();
        foreach (var word in words)
        {
            if (word.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(word[0]));
            if (word.Length > 1) sb.Append(word[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    internal static string ToSnakeCase(string text)
    {
        var words = SplitIntoWords(text);
        return string.Join('_', words.Select(w => w.ToLowerInvariant()));
    }

    internal static string ToKebabCase(string text)
    {
        var words = SplitIntoWords(text);
        return string.Join('-', words.Select(w => w.ToLowerInvariant()));
    }

    private static List<string> SplitIntoWords(string text)
    {
        var list = new List<string>();
        var sb = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString());
        }

        return list;
    }
}
