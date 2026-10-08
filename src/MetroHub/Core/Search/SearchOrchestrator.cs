using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Models;
using MetroHub.Core.Services;

namespace MetroHub.Core.Search;

/// <summary>
/// Source adapter querying the in-memory installed applications cache.
/// Zero disk I/O during search.
/// </summary>
public sealed class AppSearchSource : ISearchSource
{
    public string SourceId => "installed_apps";
    public SourceState State => SourceState.Ready;

    private readonly Func<List<CatalogItemModel>>? _appsProvider;

    public AppSearchSource(Func<List<CatalogItemModel>>? appsProvider = null)
    {
        _appsProvider = appsProvider;
    }

    public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || query.IsEmpty)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        // If query mode is explicit Files or Folders, skip apps
        if (query.Mode == SearchMode.Files || query.Mode == SearchMode.Folders)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var apps = _appsProvider != null
            ? _appsProvider()
            : InstalledAppsService.GetInstalledApps(forceRefresh: false);

        if (apps == null || apps.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        var candidates = new List<Candidate>(apps.Count);
        for (int i = 0; i < apps.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var app = apps[i];
            candidates.Add(new Candidate(
                Id: app.TargetPath ?? app.Name,
                DisplayName: app.Name,
                FullPathOrKey: app.TargetPath ?? string.Empty,
                Category: SearchCategory.Apps,
                SourceId: SourceId,
                Modified: null,
                Size: null,
                IsFolder: false,
                Tag: app));
        }

        return Task.FromResult<IReadOnlyList<Candidate>>(candidates);
    }
}

/// <summary>
/// Application-layer search orchestrator.
/// Manages input debounce (150ms), session cancellation (latest-wins),
/// multi-source execution across tiers, result deduplication, and snapshot publishing.
/// </summary>
public sealed class SearchOrchestrator : IDisposable
{
    private readonly List<ISearchSource> _tier1Sources = new();
    private readonly List<ISearchSource> _tier2Sources = new();
    private readonly object _sessionLock = new();
    private readonly int _debounceMs;

    private Timer? _debounceTimer;
    private CancellationTokenSource? _activeCts;
    private long _currentSessionId = 0;
    private bool _isDisposed = false;

    public event Action<SearchSnapshot>? SnapshotUpdated;

    public SearchSnapshot CurrentSnapshot { get; private set; } = SearchSnapshot.Empty;
    public long CurrentSessionId => Interlocked.Read(ref _currentSessionId);

    public SearchOrchestrator(int debounceMs = 150)
    {
        _debounceMs = debounceMs;
        // Default Tier 1: Installed Apps
        _tier1Sources.Add(new AppSearchSource());
    }

    public SearchOrchestrator(IEnumerable<ISearchSource> tier1Sources, IEnumerable<ISearchSource>? tier2Sources = null, int debounceMs = 150)
    {
        _debounceMs = debounceMs;
        if (tier1Sources != null) _tier1Sources.AddRange(tier1Sources);
        if (tier2Sources != null) _tier2Sources.AddRange(tier2Sources);
    }

    /// <summary>
    /// Enqueues new search input. Debounces typing by 150ms.
    /// If empty, clears immediately without waiting for debounce.
    /// </summary>
    public void SetQuery(string? text)
    {
        if (_isDisposed) return;

        lock (_sessionLock)
        {
            // Cancel any in-flight background query
            CancelActiveSession();

            if (string.IsNullOrWhiteSpace(text))
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;

                CurrentSnapshot = SearchSnapshot.Empty;
                SnapshotUpdated?.Invoke(CurrentSnapshot);
                return;
            }

            string capturedText = text;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                TriggerSearchSession(capturedText);
            }, null, _debounceMs, Timeout.Infinite);
        }
    }

    private void TriggerSearchSession(string text)
    {
        if (_isDisposed) return;

        long sessionId;
        CancellationToken ct;

        lock (_sessionLock)
        {
            sessionId = Interlocked.Increment(ref _currentSessionId);
            _activeCts?.Dispose();
            _activeCts = new CancellationTokenSource();
            ct = _activeCts.Token;
        }

        _ = Task.Run(() => ExecutePipelineAsync(text, sessionId, ct), ct);
    }

    private async Task ExecutePipelineAsync(string rawText, long sessionId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            if (ct.IsCancellationRequested || sessionId != Interlocked.Read(ref _currentSessionId)) return;

            var query = SearchRanker.ParseQuery(rawText, sessionId);
            if (query.IsEmpty) return;

            // 1. Tier 1: Fast In-Memory Sources (Apps)
            var candidates = new List<Candidate>();
            var sourceStates = new Dictionary<string, SourceState>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < _tier1Sources.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var source = _tier1Sources[i];
                sourceStates[source.SourceId] = source.State;

                try
                {
                    var batch = await source.SearchAsync(query, ct).ConfigureAwait(false);
                    if (batch != null && batch.Count > 0)
                    {
                        candidates.AddRange(batch);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    sourceStates[source.SourceId] = new SourceState(SourceStateKind.Degraded, ex.Message);
                }
            }

            if (ct.IsCancellationRequested || sessionId != Interlocked.Read(ref _currentSessionId)) return;

            // 2. Deduplicate, Score & Rank Candidates
            var deduped = SearchRanker.Deduplicate(candidates);
            var scoredResults = new List<ScoredResult>(deduped.Count);

            for (int i = 0; i < deduped.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var scored = SearchRanker.ScoreCandidate(deduped[i], query);
                if (scored.Score > 0)
                {
                    scoredResults.Add(scored);
                }
            }

            SearchRanker.SortResults(scoredResults);

            if (ct.IsCancellationRequested || sessionId != Interlocked.Read(ref _currentSessionId)) return;

            // 3. Category Grouping (Top N per group, ordered by group's top score)
            var groups = BuildSearchGroups(scoredResults, query.MaxPerCategory);

            sw.Stop();

            var snapshot = new SearchSnapshot(
                SessionId: sessionId,
                Groups: groups,
                IsFinal: true,
                ElapsedMs: sw.ElapsedMilliseconds,
                SourceStates: sourceStates);

            // 4. Publish only if still current session
            lock (_sessionLock)
            {
                if (sessionId == Interlocked.Read(ref _currentSessionId) && !ct.IsCancellationRequested)
                {
                    CurrentSnapshot = snapshot;
                    SnapshotUpdated?.Invoke(snapshot);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Fail fast, publish empty degraded snapshot
        }
    }

    private static IReadOnlyList<SearchGroup> BuildSearchGroups(List<ScoredResult> results, int maxPerGroup)
    {
        if (results.Count == 0) return Array.Empty<SearchGroup>();

        var groupMap = new Dictionary<SearchCategory, List<ScoredResult>>();

        for (int i = 0; i < results.Count; i++)
        {
            var item = results[i];
            if (!groupMap.TryGetValue(item.Candidate.Category, out var list))
            {
                list = new List<ScoredResult>();
                groupMap[item.Candidate.Category] = list;
            }
            list.Add(item);
        }

        var groups = new List<SearchGroup>(groupMap.Count);
        foreach (var (cat, items) in groupMap)
        {
            int topScore = items[0].Score;
            var topItems = items.Take(maxPerGroup).ToList();
            string title = cat switch
            {
                SearchCategory.Apps => "Apps",
                SearchCategory.Folders => "Folders",
                SearchCategory.Documents => "Documents",
                SearchCategory.Images => "Images",
                SearchCategory.Media => "Media",
                SearchCategory.Code => "Code",
                _ => "Other"
            };

            groups.Add(new SearchGroup(cat, title, topItems, items.Count, topScore));
        }

        // Blueprint 7.3: Order groups by their top score descending
        groups.Sort((a, b) => b.TopScore.CompareTo(a.TopScore));
        return groups;
    }

    private void CancelActiveSession()
    {
        try
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        }
        catch { }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        lock (_sessionLock)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            CancelActiveSession();
        }
    }
}
