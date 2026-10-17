using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Services;

namespace MetroHub.Core.Search;

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
                // Yield top/common settings
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
                // Check System Commands (e.g. "lock", "sleep", "restart", "shutdown", "recycle")
                for (int i = 0; i < PureMemoryData.SystemCommands.Length; i++)
                {
                    var cmd = PureMemoryData.SystemCommands[i];
                    if (MatchesCommand(cmd, normQuery))
                    {
                        results.Add(CreateCommandCandidate(cmd));
                    }
                }

                // Check Windows Settings in universal mode if length >= 3
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
        if (titleNorm.Contains(normQuery, StringComparison.Ordinal))
        {
            return true;
        }

        for (int k = 0; k < setting.Keywords.Length; k++)
        {
            if (setting.Keywords[k].Contains(normQuery, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesCommand(SystemCommandItem cmd, string normQuery)
    {
        string titleNorm = SearchRanker.NormalizeText(cmd.Title);
        if (titleNorm.StartsWith(normQuery, StringComparison.Ordinal))
        {
            return true;
        }

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

