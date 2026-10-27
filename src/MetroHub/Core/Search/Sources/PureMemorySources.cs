using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Common;
using MetroHub.Core.Services;
using Wpf.Ui.Controls;

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
/// sysadmin diagnostic applets (.cpl / .msc), and web search bangs. Operates synchronously with sub-millisecond execution.
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

        // 0. Interactive Shortcuts & Help Route (e.g. "?", "help", "? vol")
        if (route.Kind == RouteKind.Help)
        {
            string remainder = route.Remainder.Trim();
            if (string.IsNullOrEmpty(remainder))
            {
                for (int i = 0; i < PureMemoryData.SearchTips.Length; i++)
                {
                    results.Add(CreateTipCandidate(PureMemoryData.SearchTips[i]));
                }
            }
            else
            {
                string norm = SearchRanker.NormalizeText(remainder);
                for (int i = 0; i < PureMemoryData.SearchTips.Length; i++)
                {
                    var tip = PureMemoryData.SearchTips[i];
                    if (MatchesTip(tip, norm, query.Tokens))
                    {
                        results.Add(CreateTipCandidate(tip));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // 1. Settings Hard Route (e.g. "set:bluetooth" or "set:")
        if (route.Kind == RouteKind.Settings)
        {
            string remainder = route.Remainder.Trim();
            if (string.IsNullOrEmpty(remainder))
            {
                int count = Math.Min(12, PureMemoryData.WindowsSettings.Length);
                for (int i = 0; i < count; i++)
                {
                    results.Add(CreateSettingsCandidate(PureMemoryData.WindowsSettings[i]));
                }
            }
            else
            {
                string norm = SearchRanker.NormalizeText(remainder);
                var tokens = SearchRanker.Tokenize(remainder);
                for (int i = 0; i < PureMemoryData.WindowsSettings.Length; i++)
                {
                    var setting = PureMemoryData.WindowsSettings[i];
                    if (MatchesSetting(setting, norm, tokens))
                    {
                        results.Add(CreateSettingsCandidate(setting));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // 1b. Control Panel Hard Route (e.g. "cp:mouse" or "cp:" or "cpl:")
        if (route.Kind == RouteKind.ControlPanel)
        {
            string remainder = route.Remainder.Trim();
            if (string.IsNullOrEmpty(remainder))
            {
                for (int i = 0; i < PureMemoryData.SystemCommands.Length; i++)
                {
                    var cmd = PureMemoryData.SystemCommands[i];
                    if (string.Equals(cmd.KindLabel, "Control Panel", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(CreateCommandCandidate(cmd));
                    }
                }
            }
            else
            {
                string norm = SearchRanker.NormalizeText(remainder);
                var tokens = SearchRanker.Tokenize(remainder);
                for (int i = 0; i < PureMemoryData.SystemCommands.Length; i++)
                {
                    var cmd = PureMemoryData.SystemCommands[i];
                    if (string.Equals(cmd.KindLabel, "Control Panel", StringComparison.OrdinalIgnoreCase) &&
                        MatchesCommand(cmd, norm, tokens))
                    {
                        results.Add(CreateCommandCandidate(cmd));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // 2. Additive Web Bangs (e.g. "g query", "gh query", "w query", "so query", "npm query")
        if (route.IsAdditive)
        {
            string? bangPrefix = route.Kind switch
            {
                RouteKind.WebBangGoogle => "g",
                RouteKind.WebBangGitHub => "gh",
                RouteKind.WebBangYouTube => "yt",
                RouteKind.WebBangDuckDuckGo => "ddg",
                RouteKind.WebBangGeneric => route.Extra,
                _ => null
            };

            if (bangPrefix != null)
            {
                var bangItem = PureMemoryData.WebBangs.FirstOrDefault(b => string.Equals(b.Prefix, bangPrefix, StringComparison.OrdinalIgnoreCase));
                if (bangItem != null)
                {
                    results.Add(CreateBangCandidate(bangItem, query.RawText.Trim(), route.Remainder));
                    return Task.FromResult<IReadOnlyList<Candidate>>(results);
                }
            }
        }

        // 3. Universal Mode (System Commands, Control Panel Applets & Direct Settings Matches)
        if (route.Kind == RouteKind.Universal)
        {
            string normQuery = query.NormalizedText;
            if (!string.IsNullOrEmpty(normQuery))
            {
                // Match system commands and sysadmin tools
                for (int i = 0; i < PureMemoryData.SystemCommands.Length; i++)
                {
                    var cmd = PureMemoryData.SystemCommands[i];
                    if (MatchesCommand(cmd, normQuery, query.Tokens))
                    {
                        results.Add(CreateCommandCandidate(cmd));
                    }
                }

                // Match Windows Settings
                if (normQuery.Length >= 2)
                {
                    int addedSettings = 0;
                    for (int i = 0; i < PureMemoryData.WindowsSettings.Length && addedSettings < 6; i++)
                    {
                        var setting = PureMemoryData.WindowsSettings[i];
                        if (MatchesSetting(setting, normQuery, query.Tokens))
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

    private static bool MatchesKeyword(string keyword, string query)
    {
        if (string.Equals(keyword, query, StringComparison.OrdinalIgnoreCase)) return true;

        // Keyword starts with query (e.g. query "ref" matches "refresh rate")
        if (query.Length >= 2 && keyword.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return true;

        // Multi-word keyword: any individual word in keyword starts with query (e.g. query "rate" matches "refresh rate")
        if (query.Length >= 3 && keyword.IndexOf(' ') >= 0)
        {
            int idx = 0;
            while (idx < keyword.Length)
            {
                int nextSpace = keyword.IndexOf(' ', idx);
                int len = (nextSpace >= 0 ? nextSpace : keyword.Length) - idx;
                if (len >= query.Length && keyword.AsSpan(idx, len).StartsWith(query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (nextSpace < 0) break;
                idx = nextSpace + 1;
            }
        }

        return false;
    }

    private static bool MatchesSetting(SettingsItem setting, string normQuery, IReadOnlyList<string> tokens)
    {
        string titleNorm = SearchRanker.NormalizeText(setting.Title);
        if (titleNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        for (int k = 0; k < setting.Keywords.Length; k++)
        {
            if (MatchesKeyword(setting.Keywords[k], normQuery))
            {
                return true;
            }
        }

        if (tokens.Count > 1)
        {
            for (int t = 0; t < tokens.Count; t++)
            {
                string tok = tokens[t];
                if (tok.Length == 0) continue;
                bool tokMatched = titleNorm.Contains(tok, StringComparison.Ordinal);
                if (!tokMatched)
                {
                    for (int k = 0; k < setting.Keywords.Length; k++)
                    {
                        if (MatchesKeyword(setting.Keywords[k], tok))
                        {
                            tokMatched = true;
                            break;
                        }
                    }
                }
                if (!tokMatched) return false;
            }
            return true;
        }

        return false;
    }

    private static bool MatchesCommand(SystemCommandItem cmd, string normQuery, IReadOnlyList<string> tokens)
    {
        string titleNorm = SearchRanker.NormalizeText(cmd.Title);
        if (titleNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        for (int k = 0; k < cmd.Keywords.Length; k++)
        {
            if (MatchesKeyword(cmd.Keywords[k], normQuery))
            {
                return true;
            }
        }

        if (tokens.Count > 1)
        {
            for (int t = 0; t < tokens.Count; t++)
            {
                string tok = tokens[t];
                if (tok.Length == 0) continue;
                bool tokMatched = titleNorm.Contains(tok, StringComparison.Ordinal);
                if (!tokMatched)
                {
                    for (int k = 0; k < cmd.Keywords.Length; k++)
                    {
                        if (MatchesKeyword(cmd.Keywords[k], tok))
                        {
                            tokMatched = true;
                            break;
                        }
                    }
                }
                if (!tokMatched) return false;
            }
            return true;
        }

        return false;
    }

    private Candidate CreateSettingsCandidate(SettingsItem item)
    {
        return new Candidate(
            Id: "setting:" + item.Uri,
            DisplayName: item.Title,
            FullPathOrKey: item.Uri,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: item.Keywords)
        {
            Subtitle = item.Description,
            Icon = item.Icon,
            KindLabel = "Windows Setting",
            PrimaryAction = new CandidateAction(
                Id: "open_setting",
                Title: "Open Windows Setting",
                KeyGestureHint: "Enter",
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Settings24),
                ExecuteAsync: _ =>
                {
                    ProcessLauncherService.LaunchTarget(item.Uri);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: "copy_setting_uri",
                    Title: $"Copy URI ({item.Uri})",
                    KeyGestureHint: "Ctrl+Shift+C",
                    Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        ClipboardHelper.SetClipboardText(item.Uri);
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    })
            }
        };
    }

    private Candidate CreateCommandCandidate(SystemCommandItem cmd)
    {
        return new Candidate(
            Id: cmd.Id,
            DisplayName: cmd.Title,
            FullPathOrKey: cmd.Description,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: cmd.Keywords)
        {
            Subtitle = cmd.Description,
            Icon = cmd.Icon,
            KindLabel = cmd.KindLabel,
            PrimaryAction = new CandidateAction(
                Id: cmd.Id,
                Title: cmd.Title,
                KeyGestureHint: "Enter",
                Icon: cmd.Icon,
                ExecuteAsync: cmd.ExecuteAsync),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: cmd.Id + "_copy",
                    Title: $"Copy command ({cmd.Description})",
                    KeyGestureHint: "Ctrl+Shift+C",
                    Icon: SymbolRegular.Copy24,
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        ClipboardHelper.SetClipboardText(cmd.Description);
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    })
            }
        };
    }

    private Candidate CreateBangCandidate(WebBangItem bang, string rawText, string queryText)
    {
        string trimmedQuery = queryText.Trim();
        string homeUrl = bang.HomeUrl ?? bang.UrlTemplate;
        string targetUrl = string.IsNullOrEmpty(trimmedQuery)
            ? homeUrl
            : bang.UrlTemplate + Uri.EscapeDataString(trimmedQuery);

        string displayName = string.IsNullOrEmpty(trimmedQuery)
            ? $"Search {bang.Name}"
            : $"{rawText} — Search {bang.Name}";

        return new Candidate(
            Id: $"bang:{bang.Name.ToLowerInvariant()}:{trimmedQuery}",
            DisplayName: displayName,
            FullPathOrKey: targetUrl,
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Icon = bang.Icon,
            KindLabel = "Web Search",
            PrimaryAction = new CandidateAction(
                Id: $"search_{bang.Name.ToLowerInvariant()}",
                Title: $"Search {bang.Name}",
                KeyGestureHint: "Enter",
                Icon: bang.Icon,
                ExecuteAsync: _ =>
                {
                    ProcessLauncherService.LaunchTarget(targetUrl);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                }),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: $"copy_bang_{bang.Name.ToLowerInvariant()}",
                    Title: "Copy search URL",
                    KeyGestureHint: "Ctrl+Shift+C",
                    Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        ClipboardHelper.SetClipboardText(targetUrl);
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    })
            }
        };
    }

    private Candidate CreateTipCandidate(SearchTipItem tip)
    {
        return new Candidate(
            Id: "tip:" + tip.Prefix.Trim(),
            DisplayName: tip.Title,
            FullPathOrKey: tip.Description,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: tip)
        {
            Icon = tip.Icon,
            KindLabel = "Shortcut",
            PrimaryAction = new CandidateAction(
                Id: "fill_shortcut",
                Title: "Use shortcut",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ => Task.FromResult(new ActionResult(ActionOutcome.PopulateSearchBox, tip.Prefix)))
        };
    }

    private static bool MatchesTip(SearchTipItem tip, string normQuery, IReadOnlyList<string> tokens)
    {
        string titleNorm = SearchRanker.NormalizeText(tip.Title);
        if (titleNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        string descNorm = SearchRanker.NormalizeText(tip.Description);
        if (descNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        string sectionNorm = SearchRanker.NormalizeText(tip.Section);
        if (sectionNorm.Contains(normQuery, StringComparison.Ordinal)) return true;

        for (int k = 0; k < tip.Keywords.Length; k++)
        {
            if (tip.Keywords[k].Contains(normQuery, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}

/// <summary>
/// Sub-millisecond math calculator utilizing an enhanced bounded Pratt parser.
/// Gated to guarantee zero heap allocations for non-mathematical queries.
/// Supports percentages, implicit multiplication, hex/binary literals, bitwise operations,
/// base conversions, and dual degree/radian trigonometric evaluations.
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

        // Quick Base Conversion (e.g. "255 to hex", "0xFF to dec", "42 to bin")
        if (TryEvaluateBaseConversion(span, out string? convResult, out string? convDesc))
        {
            var action = new CandidateAction(
                Id: "copy_calc_base",
                Title: "Copy result to clipboard",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(convResult!);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                });

            var cand = new Candidate(
                Id: "calc:base:" + convResult,
                DisplayName: $"= {convResult}",
                FullPathOrKey: convDesc ?? $"{query.RawText.Trim()} = {convResult}",
                Category: SearchCategory.Actions,
                SourceId: SourceId)
            {
                Icon = SymbolRegular.Calculator24,
                KindLabel = "Calculator",
                PrimaryAction = action,
                SecondaryActions = new[]
                {
                    new CandidateAction(
                        Id: "copy_calc_base_input",
                        Title: $"Copy input ({query.RawText.Trim()})",
                        KeyGestureHint: "Ctrl+Shift+C",
                        Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                        BeginGroup: true,
                        ExecuteAsync: _ =>
                        {
                            ClipboardHelper.SetClipboardText(query.RawText.Trim());
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                        })
                }
            };

            return Task.FromResult<IReadOnlyList<Candidate>>(new[] { cand });
        }

        if (!MathPrattParser.TryEvaluate(span, out double val, out bool hasTrig, out double radVal))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var results = new List<Candidate>(2);
        double rounded = Math.Round(val, 10);
        string formatted = rounded.ToString("0.##########", CultureInfo.InvariantCulture);

        // If integer, also append hex representation if positive and reasonable
        string fullDesc = $"{query.RawText.Trim()} = {formatted}";
        if (rounded >= 0 && rounded <= long.MaxValue && Math.Abs(rounded - Math.Round(rounded)) < 1e-9 && (span.Contains("0x", StringComparison.OrdinalIgnoreCase) || span.Contains("0b", StringComparison.OrdinalIgnoreCase) || span.Contains('&') || span.Contains('|') || span.Contains("xor", StringComparison.OrdinalIgnoreCase) || span.Contains("<<", StringComparison.Ordinal) || span.Contains(">>", StringComparison.Ordinal)))
        {
            long intVal = (long)rounded;
            formatted = $"{formatted} (0x{intVal:X})";
        }

        var primaryAction = new CandidateAction(
            Id: "copy_calc_result",
            Title: "Copy result to clipboard",
            KeyGestureHint: "Enter",
            Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
            ExecuteAsync: _ =>
            {
                ClipboardHelper.SetClipboardText(formatted);
                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
            });

        string displayPrimary = hasTrig ? $"= {formatted} (degrees)" : $"= {formatted}";

        results.Add(new Candidate(
            Id: "calc:" + formatted,
            DisplayName: displayPrimary,
            FullPathOrKey: fullDesc,
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Icon = SymbolRegular.Calculator24,
            KindLabel = "Calculator",
            PrimaryAction = primaryAction,
            SecondaryActions = new[]
            {
                new CandidateAction(
                    Id: "copy_calc_formula",
                    Title: $"Copy formula ({query.RawText.Trim()})",
                    KeyGestureHint: "Ctrl+Shift+C",
                    Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                    BeginGroup: true,
                    ExecuteAsync: _ =>
                    {
                        ClipboardHelper.SetClipboardText(query.RawText.Trim());
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    })
            }
        });

        // If trig function without explicit deg/rad units was computed, also output radians candidate
        if (hasTrig && Math.Abs(val - radVal) > 1e-6)
        {
            double radRounded = Math.Round(radVal, 10);
            string radFormatted = radRounded.ToString("0.##########", CultureInfo.InvariantCulture);
            var radAction = new CandidateAction(
                Id: "copy_calc_rad",
                Title: "Copy radians result to clipboard",
                KeyGestureHint: "Enter",
                Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(radFormatted);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                });

            results.Add(new Candidate(
                Id: "calc:rad:" + radFormatted,
                DisplayName: $"= {radFormatted} (radians)",
                FullPathOrKey: $"{query.RawText.Trim()} (rad) = {radFormatted}",
                Category: SearchCategory.Actions,
                SourceId: SourceId)
            {
                Icon = SymbolRegular.Calculator24,
                KindLabel = "Calculator",
                PrimaryAction = radAction,
                SecondaryActions = new[]
                {
                    new CandidateAction(
                        Id: "copy_calc_rad_formula",
                        Title: $"Copy formula ({query.RawText.Trim()})",
                        KeyGestureHint: "Ctrl+Shift+C",
                        Icon: new CandidateIcon(CandidateIconKind.Symbol, Symbol: SymbolRegular.Copy24),
                        BeginGroup: true,
                        ExecuteAsync: _ =>
                        {
                            ClipboardHelper.SetClipboardText(query.RawText.Trim());
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                        })
                }
            });
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(results);
    }

    private static bool TryEvaluateBaseConversion(ReadOnlySpan<char> input, out string? result, out string? desc)
    {
        result = null;
        desc = null;

        int toIdx = input.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        int inIdx = input.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        int sepIdx = toIdx >= 0 ? toIdx : inIdx;
        int sepLen = toIdx >= 0 ? 4 : 4;

        if (sepIdx <= 0) return false;

        ReadOnlySpan<char> sourcePart = input[..sepIdx].Trim();
        ReadOnlySpan<char> targetPart = input[(sepIdx + sepLen)..].Trim();

        if (!MathPrattParser.TryEvaluate(sourcePart, out double rawVal, out _, out _)) return false;
        long num = (long)Math.Round(rawVal);

        if (targetPart.Equals("hex", StringComparison.OrdinalIgnoreCase))
        {
            result = $"0x{num:X}";
            desc = $"{num} in hexadecimal = {result}";
            return true;
        }
        if (targetPart.Equals("bin", StringComparison.OrdinalIgnoreCase) || targetPart.Equals("binary", StringComparison.OrdinalIgnoreCase))
        {
            result = $"0b{Convert.ToString(num, 2)}";
            desc = $"{num} in binary = {result}";
            return true;
        }
        if (targetPart.Equals("dec", StringComparison.OrdinalIgnoreCase) || targetPart.Equals("decimal", StringComparison.OrdinalIgnoreCase))
        {
            result = num.ToString(CultureInfo.InvariantCulture);
            desc = $"{sourcePart.ToString()} in decimal = {result}";
            return true;
        }
        if (targetPart.Equals("oct", StringComparison.OrdinalIgnoreCase) || targetPart.Equals("octal", StringComparison.OrdinalIgnoreCase))
        {
            result = Convert.ToString(num, 8);
            desc = $"{num} in octal = {result}";
            return true;
        }

        return false;
    }

    internal static bool IsCandidateMathExpression(ReadOnlySpan<char> input)
    {
        if (input.Length == 0 || input.Length > 256) return false;

        bool hasDigitOrHex = false;
        bool hasOperator = false;
        bool hasFunctionOrConstant = false;

        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsAsciiDigit(c))
            {
                hasDigitOrHex = true;
                i++;
            }
            else if (c is '+' or '-' or '*' or '/' or '%' or '^' or '×' or '÷' or '&' or '|' or '~' or '<' or '>')
            {
                hasOperator = true;
                i++;
            }
            else if (c is '(' or ')' or '.' or ',' or ' ' or '°')
            {
                i++;
            }
            else if (char.IsAsciiLetter(c))
            {
                int start = i;
                while (i < input.Length && (char.IsAsciiLetterOrDigit(input[i]) || input[i] == '_'))
                {
                    i++;
                }

                ReadOnlySpan<char> word = input.Slice(start, i - start);

                // Check for hex or binary prefix (e.g. "0xFF" handled when 'x' followed by digits)
                if (word.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || word.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                {
                    hasDigitOrHex = true;
                    continue;
                }

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
                else if (word.Equals("of", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("deg", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("rad", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("to", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("in", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("hex", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("dec", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("oct", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("xor", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("and", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("or", StringComparison.OrdinalIgnoreCase) ||
                         word.Equals("not", StringComparison.OrdinalIgnoreCase))
                {
                    hasOperator = true;
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

        return (hasDigitOrHex && hasOperator) || hasFunctionOrConstant || (hasDigitOrHex && input.Contains('%'));
    }
}

/// <summary>
/// High-performance Pratt parser for mathematical expressions supporting percentages,
/// implicit multiplication, hex/binary literals, bitwise operations, and trigonometric functions.
/// </summary>
internal static class MathPrattParser
{
    public static bool TryEvaluate(ReadOnlySpan<char> input, out double result)
    {
        return TryEvaluate(input, out result, out _, out _);
    }

    public static bool TryEvaluate(ReadOnlySpan<char> input, out double result, out bool hasTrig, out double radResult)
    {
        result = 0;
        hasTrig = false;
        radResult = 0;
        if (input.Length == 0 || input.Length > 256) return false;

        try
        {
            var parser = new ParserState(input);
            bool success = parser.TryParse(out result, out hasTrig, out radResult);
            return success && !double.IsNaN(result) && !double.IsInfinity(result);
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
        public bool HasTrig;
        public double RadResult;

        public ParserState(ReadOnlySpan<char> text)
        {
            _text = text;
            _pos = 0;
            _depth = 0;
            HasTrig = false;
            RadResult = 0;
        }

        public bool TryParse(out double result, out bool hasTrig, out double radResult)
        {
            result = 0;
            hasTrig = false;
            radResult = 0;
            SkipWhitespace();
            if (_pos >= _text.Length) return false;

            if (!TryParseExpression(0, out result)) return false;

            SkipWhitespace();
            if (_pos < _text.Length) return false;

            hasTrig = HasTrig;
            radResult = RadResult;
            return true;
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

                    // Check for implicit multiplication: e.g. "2(3+4)", "(2)(3)", "2pi", "3sqrt(16)"
                    char peek = _text[_pos];
                    bool isImplicitMult = peek == '(' || char.IsAsciiDigit(peek) ||
                                          (char.IsAsciiLetter(peek) && !IsOperatorWord(peek));

                    char op;
                    int precedence;

                    if (isImplicitMult)
                    {
                        op = '*';
                        precedence = 2; // Multiplication precedence
                    }
                    else
                    {
                        if (!TryGetBinaryOperator(out op, out precedence)) break;
                    }

                    if (precedence < minPrecedence || precedence == 0) break;

                    if (!isImplicitMult)
                    {
                        ConsumeBinaryOperator(op);
                    }

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

            if (c is '+' or '-' or '~')
            {
                _pos++;
                if (!TryParseExpression(4, out double operand)) return false;
                result = c switch
                {
                    '-' => -operand,
                    '~' => ~((long)operand),
                    _ => operand
                };
                return true;
            }

            if (c == '(')
            {
                _pos++;
                if (!TryParseExpression(0, out result)) return false;
                SkipWhitespace();
                if (_pos >= _text.Length || _text[_pos] != ')') return false;
                _pos++;

                // Check for trailing percent on parenthesized expression (e.g. "(50 + 50)%")
                CheckPostfixPercent(ref result);
                return true;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos + 1])))
            {
                if (!TryParseNumber(out result)) return false;
                CheckPostfixPercent(ref result);
                return true;
            }

            if (char.IsAsciiLetter(c))
            {
                if (!TryParseIdentifierOrFunction(out result)) return false;
                CheckPostfixPercent(ref result);
                return true;
            }

            return false;
        }

        private void CheckPostfixPercent(ref double value)
        {
            SkipWhitespace();
            if (_pos < _text.Length && _text[_pos] == '%')
            {
                // Check if this is a postfix percentage (not a binary modulo)
                // If followed by an operator, word ("of"), parenthesis, or end of text, it's a percent!
                int nextPos = _pos + 1;
                while (nextPos < _text.Length && char.IsWhiteSpace(_text[nextPos])) nextPos++;

                bool isModulo = nextPos < _text.Length && (char.IsAsciiDigit(_text[nextPos]) || _text[nextPos] == '(');
                if (!isModulo)
                {
                    _pos++;
                    value /= 100.0;
                }
            }
        }

        private bool TryParseNumber(out double result)
        {
            result = 0;
            SkipWhitespace();
            if (_pos >= _text.Length) return false;

            // Check for Hex (0x...) or Binary (0b...) literals
            if (_text[_pos] == '0' && _pos + 1 < _text.Length)
            {
                char prefix = _text[_pos + 1];
                if (prefix is 'x' or 'X')
                {
                    _pos += 2;
                    int start = _pos;
                    while (_pos < _text.Length && char.IsAsciiHexDigit(_text[_pos]))
                    {
                        _pos++;
                    }
                    if (_pos == start) return false;
                    return long.TryParse(_text.Slice(start, _pos - start), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hexVal)
                        ? (result = hexVal) == hexVal
                        : false;
                }
                if (prefix is 'b' or 'B')
                {
                    _pos += 2;
                    int start = _pos;
                    while (_pos < _text.Length && (_text[_pos] == '0' || _text[_pos] == '1'))
                    {
                        _pos++;
                    }
                    if (_pos == start) return false;
                    try
                    {
                        long binVal = Convert.ToInt64(_text.Slice(start, _pos - start).ToString(), 2);
                        result = binVal;
                        return true;
                    }
                    catch { return false; }
                }
            }

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
            while (_pos < _text.Length && (char.IsAsciiLetter(_text[_pos]) || _text[_pos] == '°'))
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

            // Check if trig argument has explicit "deg" or "rad" inside
            SkipWhitespace();
            bool explicitDeg = false;
            bool explicitRad = false;
            if (_pos < _text.Length && _text[_pos] == '°')
            {
                explicitDeg = true;
                _pos++;
            }
            else if (_pos + 3 <= _text.Length && _text.Slice(_pos, 3).Equals("deg", StringComparison.OrdinalIgnoreCase))
            {
                explicitDeg = true;
                _pos += 3;
            }
            else if (_pos + 3 <= _text.Length && _text.Slice(_pos, 3).Equals("rad", StringComparison.OrdinalIgnoreCase))
            {
                explicitRad = true;
                _pos += 3;
            }

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
                HasTrig = true;
                double rad = explicitDeg ? (arg1 * Math.PI / 180.0) : (explicitRad ? arg1 : (arg1 * Math.PI / 180.0));
                RadResult = Math.Sin(explicitRad ? arg1 : (explicitDeg ? (arg1 * Math.PI / 180.0) : arg1));
                result = Math.Sin(rad);
                return true;
            }
            if (id.Equals("cos", StringComparison.OrdinalIgnoreCase))
            {
                HasTrig = true;
                double rad = explicitDeg ? (arg1 * Math.PI / 180.0) : (explicitRad ? arg1 : (arg1 * Math.PI / 180.0));
                RadResult = Math.Cos(explicitRad ? arg1 : (explicitDeg ? (arg1 * Math.PI / 180.0) : arg1));
                result = Math.Cos(rad);
                return true;
            }
            if (id.Equals("tan", StringComparison.OrdinalIgnoreCase))
            {
                HasTrig = true;
                double rad = explicitDeg ? (arg1 * Math.PI / 180.0) : (explicitRad ? arg1 : (arg1 * Math.PI / 180.0));
                RadResult = Math.Tan(explicitRad ? arg1 : (explicitDeg ? (arg1 * Math.PI / 180.0) : arg1));
                result = Math.Tan(rad);
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

        private bool TryGetBinaryOperator(out char op, out int precedence)
        {
            op = '\0';
            precedence = 0;
            SkipWhitespace();
            if (_pos >= _text.Length) return false;

            char c = _text[_pos];

            if (c is '+' or '-')
            {
                op = c;
                precedence = 1;
                return true;
            }
            if (c is '*' or '/' or '%' or '×' or '÷')
            {
                op = c;
                precedence = 2;
                return true;
            }
            if (c == '^')
            {
                op = '^';
                precedence = 3;
                return true;
            }
            if (c is '&' or '|')
            {
                op = c;
                precedence = 1;
                return true;
            }
            if (c == '<' && _pos + 1 < _text.Length && _text[_pos + 1] == '<')
            {
                op = 'L'; // LShift
                precedence = 2;
                return true;
            }
            if (c == '>' && _pos + 1 < _text.Length && _text[_pos + 1] == '>')
            {
                op = 'R'; // RShift
                precedence = 2;
                return true;
            }

            // Word operators: "of", "xor", "and", "or"
            if (char.IsAsciiLetter(c))
            {
                ReadOnlySpan<char> remaining = _text.Slice(_pos);
                if (remaining.StartsWith("of ", StringComparison.OrdinalIgnoreCase) || remaining.StartsWith("of(", StringComparison.OrdinalIgnoreCase))
                {
                    op = '*'; // "of" acts as multiplication
                    precedence = 2;
                    return true;
                }
                if (remaining.StartsWith("xor ", StringComparison.OrdinalIgnoreCase))
                {
                    op = 'X';
                    precedence = 1;
                    return true;
                }
            }

            return false;
        }

        private void ConsumeBinaryOperator(char op)
        {
            if (op is 'L' or 'R')
            {
                _pos += 2;
                return;
            }
            if (_pos + 2 <= _text.Length && _text.Slice(_pos, 2).Equals("of", StringComparison.OrdinalIgnoreCase))
            {
                _pos += 2;
                return;
            }
            if (_pos + 3 <= _text.Length && _text.Slice(_pos, 3).Equals("xor", StringComparison.OrdinalIgnoreCase))
            {
                _pos += 3;
                return;
            }
            _pos++;
        }

        private static bool IsOperatorWord(char c)
        {
            return c is 'o' or 'x' or 'a';
        }

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
                case '&':
                    result = ((long)left) & ((long)right);
                    return true;
                case '|':
                    result = ((long)left) | ((long)right);
                    return true;
                case 'X': // xor
                    result = ((long)left) ^ ((long)right);
                    return true;
                case 'L': // <<
                    result = ((long)left) << (int)right;
                    return true;
                case 'R': // >>
                    result = ((long)left) >> (int)right;
                    return true;
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
/// Triggers on "case ", "slug ", "len " prefixes with camelCase, PascalCase, snake_case,
/// kebab-case, CONSTANT_CASE, and Title Case detection.
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
                CreateTransformCandidate("CONSTANT_CASE", ToConstantCase(text)),
                CreateTransformCandidate("Title Case", ToTitleCase(text)),
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
            Category: SearchCategory.Actions,
            SourceId: SourceId)
        {
            Icon = SymbolRegular.TextField24,
            KindLabel = "Transform",
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

    internal static string ToConstantCase(string text)
    {
        var words = SplitIntoWords(text);
        return string.Join('_', words.Select(w => w.ToUpperInvariant()));
    }

    internal static string ToTitleCase(string text)
    {
        var words = SplitIntoWords(text);
        var sb = new StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToUpperInvariant(w[0]));
            if (w.Length > 1) sb.Append(w[1..].ToLowerInvariant());
        }
        return sb.ToString();
    }

    internal static List<string> SplitIntoWords(string text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        var sb = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char curr = text[i];
            char prev = i > 0 ? text[i - 1] : '\0';

            if (!char.IsLetterOrDigit(curr))
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            // Boundary transitions:
            // 1. camelCase / PascalCase: lower -> UPPER (e.g. "myVariableName" -> "my", "Variable")
            bool isLowerToUpper = prev != '\0' && char.IsLower(prev) && char.IsUpper(curr);

            // 2. Acronym transition: UPPER -> UPPER followed by lower (e.g. "XMLParser" -> "XML", "Parser")
            bool isAcronymTransition = prev != '\0' && char.IsUpper(prev) && char.IsUpper(curr) &&
                                       i + 1 < text.Length && char.IsLower(text[i + 1]);

            // 3. Digit <-> Letter boundary (e.g. "win32App" -> "win", "32", "App")
            bool isDigitBoundary = prev != '\0' && ((char.IsDigit(prev) && char.IsLetter(curr)) || (char.IsLetter(prev) && char.IsDigit(curr)));

            if (isLowerToUpper || isAcronymTransition || isDigitBoundary)
            {
                if (sb.Length > 0)
                {
                    list.Add(sb.ToString());
                    sb.Clear();
                }
            }

            sb.Append(curr);
        }

        if (sb.Length > 0)
        {
            list.Add(sb.ToString());
        }

        return list;
    }
}

/// <summary>
/// Sub-millisecond zero-allocation ReadOnlySpan unit converter for Length, Mass,
/// Temperature, Data Size, Speed, Volume, Time, and Currency.
/// </summary>
public sealed class UnitConverterSource : ISearchSource
{
    public string SourceId => "unit_converter";
    public SourceState State => SourceState.Ready;

    // Offline EUR base currency reference rates
    private static readonly FrozenDictionary<string, double> DefaultEurRates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
    {
        ["EUR"] = 1.0, ["USD"] = 1.08, ["GBP"] = 0.86, ["JPY"] = 165.0,
        ["CAD"] = 1.48, ["AUD"] = 1.65, ["CHF"] = 0.96, ["INR"] = 90.0,
        ["CNY"] = 7.8, ["NZD"] = 1.78, ["BRL"] = 5.95, ["MXN"] = 21.5,
        ["KRW"] = 1480.0, ["SEK"] = 11.5, ["NOK"] = 11.8
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        ReadOnlySpan<char> span = query.RawText.AsSpan().Trim();
        if (span.StartsWith("conv ", StringComparison.OrdinalIgnoreCase))
        {
            span = span[5..].Trim();
        }

        int toIdx = span.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        int inIdx = span.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
        int sepIdx = toIdx >= 0 ? toIdx : inIdx;
        int sepLen = 4;

        if (sepIdx <= 0)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        ReadOnlySpan<char> sourceSpan = span[..sepIdx].Trim();
        ReadOnlySpan<char> targetUnitSpan = span[(sepIdx + sepLen)..].Trim();

        if (sourceSpan.Length == 0 || targetUnitSpan.Length == 0)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        if (!TryParseSourceValueAndUnit(sourceSpan, out double val, out string fromUnit))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string toUnit = targetUnitSpan.ToString().ToLowerInvariant();
        var converted = TryConvert(val, fromUnit, toUnit);
        if (converted is null)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var (resVal, resText, category, note) = converted.Value;
        string display = $"≈ {resText}";
        string subtitle = $"{val.ToString("G", CultureInfo.InvariantCulture)} {fromUnit} = {resText} ({category})";
        if (!string.IsNullOrEmpty(note)) subtitle += $" • {note}";

        var candidate = new Candidate(
            Id: $"convert:{fromUnit}:{toUnit}:{val}",
            DisplayName: display,
            FullPathOrKey: subtitle,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: resText)
        {
            Icon = SymbolRegular.ArrowSwap24,
            KindLabel = "Converter",
            PrimaryAction = new CandidateAction(
                "copy_unit_result",
                "Copy result",
                "Enter",
                _ =>
                {
                    ClipboardHelper.SetClipboardText(resText);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                },
                Icon: SymbolRegular.Copy24),
            SecondaryActions = new[]
            {
                new CandidateAction(
                    "copy_unit_equation",
                    "Copy full equation",
                    "Ctrl+C",
                    _ =>
                    {
                        ClipboardHelper.SetClipboardText(subtitle);
                        return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                    },
                    Icon: SymbolRegular.ClipboardTextEdit24,
                    BeginGroup: true)
            }
        };

        return Task.FromResult<IReadOnlyList<Candidate>>(new[] { candidate });
    }

    private static bool TryParseSourceValueAndUnit(ReadOnlySpan<char> source, out double value, out string unit)
    {
        value = 0;
        unit = string.Empty;

        // Check for compound units: e.g. "5 ft 10 in"
        int ftIdx = source.IndexOf("ft", StringComparison.OrdinalIgnoreCase);
        int inIdx = source.IndexOf("in", StringComparison.OrdinalIgnoreCase);
        if (ftIdx > 0 && inIdx > ftIdx)
        {
            if (double.TryParse(source[..ftIdx].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double ft) &&
                double.TryParse(source.Slice(ftIdx + 2, inIdx - (ftIdx + 2)).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double inch))
            {
                value = (ft * 12.0) + inch;
                unit = "in";
                return true;
            }
        }

        int split = 0;
        while (split < source.Length && (char.IsAsciiDigit(source[split]) || source[split] is '.' or ',' or '-' or '+'))
        {
            split++;
        }

        if (split == 0) return false;

        ReadOnlySpan<char> numSpan = source[..split].Trim();
        ReadOnlySpan<char> unitSpan = source[split..].Trim();

        if (unitSpan.Length == 0) return false;
        if (!double.TryParse(numSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;

        unit = unitSpan.ToString().ToLowerInvariant();
        return true;
    }

    private static (double Value, string FormattedText, string Category, string? Note)? TryConvert(double val, string from, string to)
    {
        // 1. Temperature
        if (IsTemperature(from) && IsTemperature(to))
        {
            double c = from switch
            {
                "c" or "celsius" or "°c" => val,
                "f" or "fahrenheit" or "°f" => (val - 32.0) * 5.0 / 9.0,
                "k" or "kelvin" => val - 273.15,
                _ => double.NaN
            };

            double res = to switch
            {
                "c" or "celsius" or "°c" => c,
                "f" or "fahrenheit" or "°f" => (c * 9.0 / 5.0) + 32.0,
                "k" or "kelvin" => c + 273.15,
                _ => double.NaN
            };

            if (!double.IsNaN(res))
            {
                string label = to switch { "c" or "celsius" or "°c" => "°C", "f" or "fahrenheit" or "°f" => "°F", "k" or "kelvin" => "K", _ => to };
                string fmt = Math.Abs(res - Math.Round(res)) < 0.0001 ? $"{Math.Round(res)} {label}" : $"{res:F2} {label}";
                return (res, fmt, "Temperature", null);
            }
        }

        // 2. Length (Base: Meter)
        var lengthFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m"] = 1.0, ["meter"] = 1.0, ["meters"] = 1.0,
            ["km"] = 1000.0, ["kilometer"] = 1000.0, ["kilometers"] = 1000.0,
            ["cm"] = 0.01, ["centimeter"] = 0.01, ["centimeters"] = 0.01,
            ["mm"] = 0.001, ["millimeter"] = 0.001, ["millimeters"] = 0.001,
            ["mi"] = 1609.344, ["mile"] = 1609.344, ["miles"] = 1609.344,
            ["yd"] = 0.9144, ["yard"] = 0.9144, ["yards"] = 0.9144,
            ["ft"] = 0.3048, ["foot"] = 0.3048, ["feet"] = 0.3048,
            ["in"] = 0.0254, ["inch"] = 0.0254, ["inches"] = 0.0254,
            ["nm"] = 1852.0, ["nauticalmile"] = 1852.0
        };

        if (lengthFactors.TryGetValue(from, out double fromLen) && lengthFactors.TryGetValue(to, out double toLen))
        {
            double res = (val * fromLen) / toLen;
            return (res, FormatNumber(res, to), "Length", null);
        }

        // 3. Mass / Weight (Base: Kilogram)
        var massFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["kg"] = 1.0, ["kilogram"] = 1.0, ["kilograms"] = 1.0,
            ["g"] = 0.001, ["gram"] = 0.001, ["grams"] = 0.001,
            ["mg"] = 0.000001, ["milligram"] = 0.000001,
            ["lb"] = 0.45359237, ["lbs"] = 0.45359237, ["pound"] = 0.45359237, ["pounds"] = 0.45359237,
            ["oz"] = 0.028349523125, ["ounce"] = 0.028349523125, ["ounces"] = 0.028349523125,
            ["ton"] = 907.18474, ["tons"] = 907.18474,
            ["tonne"] = 1000.0, ["tonnes"] = 1000.0
        };

        if (massFactors.TryGetValue(from, out double fromMass) && massFactors.TryGetValue(to, out double toMass))
        {
            double res = (val * fromMass) / toMass;
            return (res, FormatNumber(res, to), "Mass", null);
        }

        // 4. Data Storage (Base: Byte)
        var dataFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["b"] = 1.0, ["byte"] = 1.0, ["bytes"] = 1.0,
            ["kb"] = 1e3, ["mb"] = 1e6, ["gb"] = 1e9, ["tb"] = 1e12, ["pb"] = 1e15,
            ["kib"] = 1024.0, ["mib"] = 1024.0 * 1024.0, ["gib"] = 1024.0 * 1024.0 * 1024.0, ["tib"] = 1024.0 * 1024.0 * 1024.0 * 1024.0
        };

        if (dataFactors.TryGetValue(from, out double fromData) && dataFactors.TryGetValue(to, out double toData))
        {
            double res = (val * fromData) / toData;
            return (res, FormatNumber(res, to), "Digital Storage", null);
        }

        // 5. Speed (Base: m/s)
        var speedFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["m/s"] = 1.0, ["km/h"] = 1.0 / 3.6, ["kph"] = 1.0 / 3.6, ["kmh"] = 1.0 / 3.6,
            ["mph"] = 0.44704, ["knot"] = 0.514444, ["knots"] = 0.514444
        };

        if (speedFactors.TryGetValue(from, out double fromSpeed) && speedFactors.TryGetValue(to, out double toSpeed))
        {
            double res = (val * fromSpeed) / toSpeed;
            return (res, FormatNumber(res, to), "Speed", null);
        }

        // 6. Volume (Base: Liter)
        var volFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["l"] = 1.0, ["liter"] = 1.0, ["liters"] = 1.0,
            ["ml"] = 0.001, ["milliliter"] = 0.001, ["milliliters"] = 0.001,
            ["cl"] = 0.01, ["centiliter"] = 0.01,
            ["gal"] = 3.785411784, ["gallon"] = 3.785411784, ["gallons"] = 3.785411784,
            ["qt"] = 0.946352946, ["quart"] = 0.946352946,
            ["pt"] = 0.473176473, ["pint"] = 0.473176473,
            ["cup"] = 0.2365882365, ["cups"] = 0.2365882365,
            ["floz"] = 0.0295735295625, ["fl oz"] = 0.0295735295625,
            ["tbsp"] = 0.01478676478125, ["tsp"] = 0.00492892159375,
            ["m3"] = 1000.0
        };

        if (volFactors.TryGetValue(from, out double fromVol) && volFactors.TryGetValue(to, out double toVol))
        {
            double res = (val * fromVol) / toVol;
            return (res, FormatNumber(res, to), "Volume", null);
        }

        // 7. Time (Base: Second)
        var timeFactors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["ms"] = 0.001, ["millisecond"] = 0.001, ["milliseconds"] = 0.001,
            ["s"] = 1.0, ["sec"] = 1.0, ["second"] = 1.0, ["seconds"] = 1.0,
            ["min"] = 60.0, ["minute"] = 60.0, ["minutes"] = 60.0,
            ["h"] = 3600.0, ["hr"] = 3600.0, ["hrs"] = 3600.0, ["hour"] = 3600.0, ["hours"] = 3600.0,
            ["d"] = 86400.0, ["day"] = 86400.0, ["days"] = 86400.0,
            ["wk"] = 604800.0, ["week"] = 604800.0, ["weeks"] = 604800.0,
            ["yr"] = 31557600.0, ["year"] = 31557600.0, ["years"] = 31557600.0
        };

        if (timeFactors.TryGetValue(from, out double fromTime) && timeFactors.TryGetValue(to, out double toTime))
        {
            double res = (val * fromTime) / toTime;
            return (res, FormatNumber(res, to), "Time", null);
        }

        // 8. Currency (Base: EUR)
        if (DefaultEurRates.TryGetValue(from, out double fromRate) && DefaultEurRates.TryGetValue(to, out double toRate))
        {
            double eur = val / fromRate;
            double res = eur * toRate;
            string fmt = $"{res:F2} {to.ToUpperInvariant()}";
            return (res, fmt, "Currency", "reference rate");
        }

        return null;
    }

    private static bool IsTemperature(string u) =>
        u is "c" or "celsius" or "°c" or "f" or "fahrenheit" or "°f" or "k" or "kelvin";

    private static string FormatNumber(double val, string unit)
    {
        if (Math.Abs(val - Math.Round(val)) < 0.0001)
        {
            return $"{Math.Round(val)} {unit}";
        }
        return $"{val:0.###} {unit}";
    }
}

/// <summary>
/// Instant developer utilities: UUID/GUID generation, Base64 encode/decode,
/// URL escaping/unescaping, cryptographic hashing (MD5, SHA-1, SHA-256),
/// and Unix epoch conversions.
/// </summary>
public sealed class DevUtilitySource : ISearchSource
{
    public string SourceId => "dev_utility";
    public SourceState State => SourceState.Ready;

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string raw = query.RawText.Trim();
        var results = new List<Candidate>();

        // 1. UUID / GUID generation (e.g. "uuid", "guid")
        if (string.Equals(raw, "uuid", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "guid", StringComparison.OrdinalIgnoreCase))
        {
            var g = Guid.NewGuid();
            string lowerHyphen = g.ToString("D");
            string upperHyphen = g.ToString("D").ToUpperInvariant();
            string noHyphens = g.ToString("N");
            string csharp = $"new Guid(\"{lowerHyphen}\")";

            results.Add(CreateCopyCandidate("guid:lower", lowerHyphen, "UUID (lowercase with hyphens)", lowerHyphen));
            results.Add(CreateCopyCandidate("guid:upper", upperHyphen, "UUID (uppercase with hyphens)", upperHyphen));
            results.Add(CreateCopyCandidate("guid:nohyphen", noHyphens, "UUID (no hyphens)", noHyphens));
            results.Add(CreateCopyCandidate("guid:csharp", csharp, "UUID (C# Guid syntax)", csharp));
            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        // 2. Base64 encode / decode (e.g. "b64 encode hello", "b64 decode aGVsbG8=", "base64 ...")
        if (raw.StartsWith("b64 encode ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("base64 encode ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("b64 ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("base64 ", StringComparison.OrdinalIgnoreCase))
        {
            bool isExplicitDecode = raw.Contains("decode ", StringComparison.OrdinalIgnoreCase);
            string text = raw;
            if (raw.StartsWith("b64 encode ", StringComparison.OrdinalIgnoreCase)) text = raw[11..];
            else if (raw.StartsWith("base64 encode ", StringComparison.OrdinalIgnoreCase)) text = raw[14..];
            else if (raw.StartsWith("b64 decode ", StringComparison.OrdinalIgnoreCase)) text = raw[11..];
            else if (raw.StartsWith("base64 decode ", StringComparison.OrdinalIgnoreCase)) text = raw[14..];
            else if (raw.StartsWith("b64 ", StringComparison.OrdinalIgnoreCase)) text = raw[4..];
            else if (raw.StartsWith("base64 ", StringComparison.OrdinalIgnoreCase)) text = raw[7..];

            text = text.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                if (isExplicitDecode)
                {
                    try
                    {
                        byte[] bytes = Convert.FromBase64String(text);
                        string decoded = Encoding.UTF8.GetString(bytes);
                        results.Add(CreateCopyCandidate("b64:dec", decoded, $"Decoded from Base64: {text}", decoded));
                    }
                    catch { }
                }
                else
                {
                    string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                    results.Add(CreateCopyCandidate("b64:enc", encoded, $"Base64 encoded: {text}", encoded));

                    // If it could also be valid base64, offer decoded candidate too
                    if (text.Length >= 4 && (text.Length % 4 == 0) && !text.Contains(' '))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(text);
                            string decoded = Encoding.UTF8.GetString(bytes);
                            if (decoded.All(c => !char.IsControl(c) || char.IsWhiteSpace(c)))
                            {
                                results.Add(CreateCopyCandidate("b64:dec_opt", decoded, $"Base64 decoded: {text}", decoded));
                            }
                        }
                        catch { }
                    }
                }

                if (results.Count > 0) return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 3. URL encode / decode (e.g. "url encode hello world", "url decode hello%20world")
        if (raw.StartsWith("url encode ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("url decode ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("url ", StringComparison.OrdinalIgnoreCase))
        {
            bool isDecode = raw.StartsWith("url decode ", StringComparison.OrdinalIgnoreCase);
            string text = isDecode ? raw[11..].Trim() : (raw.StartsWith("url encode ", StringComparison.OrdinalIgnoreCase) ? raw[11..].Trim() : raw[4..].Trim());
            if (!string.IsNullOrEmpty(text))
            {
                if (isDecode)
                {
                    string unescaped = Uri.UnescapeDataString(text);
                    results.Add(CreateCopyCandidate("url:dec", unescaped, $"URL unescaped: {text}", unescaped));
                }
                else
                {
                    string escaped = Uri.EscapeDataString(text);
                    results.Add(CreateCopyCandidate("url:enc", escaped, $"URL escaped: {text}", escaped));
                }
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 4. Hashes: MD5, SHA-256, SHA-1 (e.g. "md5 hello", "sha256 hello", "hash hello")
        if (raw.StartsWith("md5 ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("sha256 ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("sha1 ", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("hash ", StringComparison.OrdinalIgnoreCase))
        {
            string payload;
            bool onlyMd5 = raw.StartsWith("md5 ", StringComparison.OrdinalIgnoreCase);
            bool onlySha256 = raw.StartsWith("sha256 ", StringComparison.OrdinalIgnoreCase);
            bool onlySha1 = raw.StartsWith("sha1 ", StringComparison.OrdinalIgnoreCase);

            if (onlyMd5) payload = raw[4..].Trim();
            else if (onlySha256) payload = raw[7..].Trim();
            else if (onlySha1) payload = raw[5..].Trim();
            else payload = raw[5..].Trim();

            if (!string.IsNullOrEmpty(payload))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                if (onlySha256 || (!onlyMd5 && !onlySha1))
                {
                    byte[] hash = SHA256.HashData(bytes);
                    string hex = Convert.ToHexStringLower(hash);
                    results.Add(CreateCopyCandidate("hash:sha256", hex, $"SHA-256 hash of '{payload}'", hex));
                }
                if (onlyMd5 || (!onlySha256 && !onlySha1))
                {
                    byte[] hash = MD5.HashData(bytes);
                    string hex = Convert.ToHexStringLower(hash);
                    results.Add(CreateCopyCandidate("hash:md5", hex, $"MD5 hash of '{payload}'", hex));
                }
                if (onlySha1 || (!onlySha256 && !onlyMd5))
                {
                    byte[] hash = SHA1.HashData(bytes);
                    string hex = Convert.ToHexStringLower(hash);
                    results.Add(CreateCopyCandidate("hash:sha1", hex, $"SHA-1 hash of '{payload}'", hex));
                }
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 5. Unix Epoch Timestamps (e.g. "unix", "epoch", "now", "unix 1700000000")
        if (string.Equals(raw, "unix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "epoch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "now", StringComparison.OrdinalIgnoreCase))
        {
            var now = DateTimeOffset.UtcNow;
            string sec = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            string ms = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            string utcStr = now.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

            results.Add(CreateCopyCandidate("unix:sec", sec, $"Current Unix Timestamp (seconds) • {utcStr}", sec));
            results.Add(CreateCopyCandidate("unix:ms", ms, $"Current Unix Timestamp (milliseconds) • {utcStr}", ms));
            return Task.FromResult<IReadOnlyList<Candidate>>(results);
        }

        if (raw.StartsWith("unix ", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("epoch ", StringComparison.OrdinalIgnoreCase))
        {
            string arg = raw.StartsWith("unix ", StringComparison.OrdinalIgnoreCase) ? raw[5..].Trim() : raw[6..].Trim();
            if (long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts))
            {
                DateTimeOffset dto = ts < 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeSeconds(ts)
                    : DateTimeOffset.FromUnixTimeMilliseconds(ts);

                string utc = dto.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
                string local = dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss (zzz)", CultureInfo.InvariantCulture);

                results.Add(CreateCopyCandidate("unix:val", utc, $"Local: {local} • Epoch: {ts}", utc));
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
    }

    private Candidate CreateCopyCandidate(string id, string text, string desc, string toCopy)
    {
        return new Candidate(
            Id: id,
            DisplayName: text,
            FullPathOrKey: desc,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: toCopy)
        {
            Icon = SymbolRegular.Code24,
            KindLabel = "Developer",
            PrimaryAction = new CandidateAction(
                Id: "copy_" + id,
                Title: "Copy to clipboard",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(toCopy);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                },
                Icon: SymbolRegular.Copy24)
        };
    }
}

/// <summary>
/// Instant color format converter supporting HEX (#RGB, #RRGGBB, #AARRGGBB),
/// rgb(r, g, b), rgba(r, g, b, a), and hsl(h, s, l).
/// </summary>
public sealed class ColorConverterSource : ISearchSource
{
    public string SourceId => "color_converter";
    public SourceState State => SourceState.Ready;

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        ReadOnlySpan<char> span = query.RawText.AsSpan().Trim();
        if (span.StartsWith("color ", StringComparison.OrdinalIgnoreCase))
        {
            span = span[6..].Trim();
        }

        if (!TryParseColor(span, out byte r, out byte g, out byte b, out byte a))
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string hexRgb = $"#{r:X2}{g:X2}{b:X2}";
        string hexArgb = $"#{a:X2}{r:X2}{g:X2}{b:X2}";
        string rgbStr = a == 255 ? $"rgb({r}, {g}, {b})" : $"rgba({r}, {g}, {b}, {Math.Round(a / 255.0, 2):0.##})";
        string xamlStr = $"<SolidColorBrush Color=\"{hexArgb}\" />";
        string csharpStr = $"Color.FromArgb({a}, {r}, {g}, {b})";

        RgbToHsl(r, g, b, out int h, out int s, out int l);
        string hslStr = $"hsl({h}, {s}%, {l}%)";

        var allFormats = new (string Key, string Label, string Value)[]
        {
            ("hex", "Copy HEX", hexRgb),
            ("hex_argb", "Copy HEX + Alpha", hexArgb),
            ("rgb", "Copy CSS RGB", rgbStr),
            ("hsl", "Copy HSL", hslStr),
            ("xaml", "Copy XAML Brush", xamlStr),
            ("csharp", "Copy C# Color", csharpStr)
        };

        var list = new List<Candidate>
        {
            CreateColorCandidate("hex", hexRgb, $"HEX Color • RGB({r}, {g}, {b})", hexRgb, allFormats),
            CreateColorCandidate("hex_argb", hexArgb, $"HEX with Alpha • {hexArgb}", hexArgb, allFormats),
            CreateColorCandidate("rgb", rgbStr, $"CSS RGB • {rgbStr}", rgbStr, allFormats),
            CreateColorCandidate("hsl", hslStr, $"HSL Color • {hslStr}", hslStr, allFormats),
            CreateColorCandidate("xaml", xamlStr, $"XAML SolidColorBrush • {hexArgb}", xamlStr, allFormats),
            CreateColorCandidate("csharp", csharpStr, $"C# Color.FromArgb • {csharpStr}", csharpStr, allFormats)
        };

        return Task.FromResult<IReadOnlyList<Candidate>>(list);
    }

    private static bool TryParseColor(ReadOnlySpan<char> span, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0;
        a = 255;

        // HEX format: #RGB, #RGBA, #RRGGBB, #AARRGGBB
        if (span.StartsWith("#"))
        {
            ReadOnlySpan<char> hex = span[1..];
            if (hex.Length == 3) // RGB
            {
                if (char.IsAsciiHexDigit(hex[0]) && char.IsAsciiHexDigit(hex[1]) && char.IsAsciiHexDigit(hex[2]))
                {
                    r = (byte)(Convert.ToByte(new string(hex[0], 2), 16));
                    g = (byte)(Convert.ToByte(new string(hex[1], 2), 16));
                    b = (byte)(Convert.ToByte(new string(hex[2], 2), 16));
                    return true;
                }
            }
            if (hex.Length == 6) // RRGGBB
            {
                if (byte.TryParse(hex.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                    byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                    byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                {
                    return true;
                }
            }
            if (hex.Length == 8) // AARRGGBB
            {
                if (byte.TryParse(hex.Slice(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a) &&
                    byte.TryParse(hex.Slice(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                    byte.TryParse(hex.Slice(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                    byte.TryParse(hex.Slice(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                {
                    return true;
                }
            }
        }

        // rgb(r, g, b) or rgba(r, g, b, a)
        if (span.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) || span.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase))
        {
            int start = span.IndexOf('(');
            int end = span.IndexOf(')');
            if (start > 0 && end > start)
            {
                string[] parts = span.Slice(start + 1, end - start - 1).ToString().Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length >= 3 &&
                    byte.TryParse(parts[0], out r) &&
                    byte.TryParse(parts[1], out g) &&
                    byte.TryParse(parts[2], out b))
                {
                    if (parts.Length >= 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha))
                    {
                        a = (byte)Math.Clamp((int)Math.Round(alpha > 1.0 ? alpha : alpha * 255.0), 0, 255);
                    }
                    return true;
                }
            }
        }

        return false;
    }

    private static void RgbToHsl(byte r, byte g, byte b, out int h, out int s, out int l)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;

        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        double lum = (max + min) / 2.0;
        double sat = 0;
        double hue = 0;

        if (delta > 1e-6)
        {
            sat = lum <= 0.5 ? (delta / (max + min)) : (delta / (2.0 - max - min));
            if (Math.Abs(max - rd) < 1e-6)
                hue = ((gd - bd) / delta) + (gd < bd ? 6.0 : 0.0);
            else if (Math.Abs(max - gd) < 1e-6)
                hue = ((bd - rd) / delta) + 2.0;
            else
                hue = ((rd - gd) / delta) + 4.0;
            hue *= 60.0;
        }

        h = (int)Math.Round(hue);
        s = (int)Math.Round(sat * 100.0);
        l = (int)Math.Round(lum * 100.0);
    }

    private Candidate CreateColorCandidate(
        string id,
        string text,
        string desc,
        string toCopy,
        (string Key, string Label, string Value)[] allFormats)
    {
        var secondary = new List<CandidateAction>(allFormats.Length - 1);
        bool first = true;
        foreach (var (key, label, val) in allFormats)
        {
            if (string.Equals(key, id, StringComparison.Ordinal)) continue;
            secondary.Add(new CandidateAction(
                Id: "copy_color_" + key,
                Title: label,
                KeyGestureHint: null,
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(val);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                },
                Icon: SymbolRegular.Copy24,
                BeginGroup: first));
            first = false;
        }

        return new Candidate(
            Id: "color:" + id + ":" + toCopy,
            DisplayName: text,
            FullPathOrKey: desc,
            Category: SearchCategory.Actions,
            SourceId: SourceId,
            Tag: toCopy)
        {
            Icon = SymbolRegular.Color24,
            KindLabel = "Color",
            PrimaryAction = new CandidateAction(
                Id: "copy_color_" + id,
                Title: "Copy color to clipboard",
                KeyGestureHint: "Enter",
                ExecuteAsync: _ =>
                {
                    ClipboardHelper.SetClipboardText(toCopy);
                    return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                },
                Icon: SymbolRegular.Copy24),
            SecondaryActions = secondary
        };
    }
}

/// <summary>
/// Fast-path source for raw URLs (https://, localhost:3000), Windows file paths,
/// and environment variables (%appdata%, %temp%).
/// </summary>
public sealed class QuickAccessSource : ISearchSource
{
    public string SourceId => "quick_access";
    public SourceState State => SourceState.Ready;

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        string raw = query.RawText.Trim();
        var results = new List<Candidate>();

        // 1. Environment Variable fast path: e.g. "%appdata%", "%temp%", "%localappdata%", "%systemroot%"
        if (raw.StartsWith('%') && raw.EndsWith('%') && raw.Length > 2)
        {
            string expanded = Environment.ExpandEnvironmentVariables(raw);
            if (!string.Equals(expanded, raw, StringComparison.OrdinalIgnoreCase))
            {
                bool exists = Directory.Exists(expanded) || File.Exists(expanded);
                string title = $"Open {raw} in Explorer";
                string subtitle = expanded;

                results.Add(new Candidate(
                    Id: "env:" + raw,
                    DisplayName: title,
                    FullPathOrKey: subtitle,
                    Category: SearchCategory.Actions,
                    SourceId: SourceId,
                    Tag: raw)
                {
                    Icon = SymbolRegular.FolderOpen24,
                    KindLabel = "Quick Access",
                    PrimaryAction = new CandidateAction(
                        Id: "open_env",
                        Title: "Open in File Explorer",
                        KeyGestureHint: "Enter",
                        ExecuteAsync: _ =>
                        {
                            ProcessLauncherService.LaunchTarget(expanded);
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                        },
                        Icon: SymbolRegular.FolderOpen24),
                    SecondaryActions = new[]
                    {
                        new CandidateAction(
                            Id: "copy_env_path",
                            Title: "Copy path",
                            KeyGestureHint: "Ctrl+C",
                            ExecuteAsync: _ =>
                            {
                                ClipboardHelper.SetClipboardText(expanded);
                                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                            },
                            Icon: SymbolRegular.Copy24,
                            BeginGroup: true)
                    }
                });
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 2. Direct Web URLs & localhost (e.g. "localhost:3000", "127.0.0.1:8080", "https://github.com", "www.google.com")
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            string targetUrl = raw;
            if (raw.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                targetUrl = "http://" + raw;
            }
            else if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                targetUrl = "https://" + raw;
            }

            if (Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                results.Add(new Candidate(
                    Id: "url:" + targetUrl,
                    DisplayName: $"Open {raw} in Browser",
                    FullPathOrKey: targetUrl,
                    Category: SearchCategory.Actions,
                    SourceId: SourceId,
                    Tag: raw)
                {
                    Icon = SymbolRegular.Globe24,
                    KindLabel = "Quick Access",
                    PrimaryAction = new CandidateAction(
                        Id: "open_url",
                        Title: "Open in Browser",
                        KeyGestureHint: "Enter",
                        ExecuteAsync: _ =>
                        {
                            ProcessLauncherService.LaunchTarget(targetUrl);
                            return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                        },
                        Icon: SymbolRegular.Globe24),
                    SecondaryActions = new[]
                    {
                        new CandidateAction(
                            Id: "copy_url",
                            Title: "Copy URL",
                            KeyGestureHint: "Ctrl+C",
                            ExecuteAsync: _ =>
                            {
                                ClipboardHelper.SetClipboardText(targetUrl);
                                return Task.FromResult(new ActionResult(ActionOutcome.DismissDrawer));
                            },
                            Icon: SymbolRegular.Copy24,
                            BeginGroup: true)
                    }
                });
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        // 3. Direct Windows Paths (e.g. "C:\Windows", "\\server\share")
        if ((raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':') ||
            raw.StartsWith(@"\\", StringComparison.Ordinal) ||
            raw.StartsWith("//", StringComparison.Ordinal))
        {
            bool exists = Directory.Exists(raw) || File.Exists(raw);
            if (exists)
            {
                bool isDir = Directory.Exists(raw);
                results.Add(new Candidate(
                    Id: "path:" + raw,
                    DisplayName: $"Open {raw}",
                    FullPathOrKey: raw,
                    Category: SearchCategory.Actions,
                    SourceId: SourceId,
                    IsFolder: isDir,
                    Tag: raw)
                {
                    Icon = isDir ? SymbolRegular.FolderOpen24 : SymbolRegular.Document24,
                    KindLabel = isDir ? "Folder" : "File",
                    PrimaryAction = FileActionProvider.CreateOpenAction(raw),
                    ActionProvider = FileActionProvider.GetActions
                });
                return Task.FromResult<IReadOnlyList<Candidate>>(results);
            }
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
    }
}
