using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        // Default Tier 2: Everything file search
        _tier2Sources.Add(new EverythingSearchSource());
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

            // Blueprint Section 5: Progressive display - Publish Tier 1 snapshot (IsFinal = false) if Tier 2 sources present
            if (_tier2Sources.Count > 0)
            {
                var t1Scored = ScoreAndSort(candidates, query, ct);
                var t1Groups = BuildSearchGroups(t1Scored, query.MaxPerCategory);
                var intermediateSnapshot = new SearchSnapshot(
                    SessionId: sessionId,
                    Groups: t1Groups,
                    IsFinal: false,
                    ElapsedMs: sw.ElapsedMilliseconds,
                    SourceStates: new Dictionary<string, SourceState>(sourceStates));

                PublishIfCurrent(intermediateSnapshot, sessionId, ct);
            }

            // 2. Tier 2: Everything File Search
            for (int i = 0; i < _tier2Sources.Count; i++)
            {
                if (ct.IsCancellationRequested) return;
                var source = _tier2Sources[i];
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
                sourceStates[source.SourceId] = source.State;
            }

            if (ct.IsCancellationRequested || sessionId != Interlocked.Read(ref _currentSessionId)) return;

            // 3. Deduplicate, Score & Rank Merged Candidates
            var finalScored = ScoreAndSort(candidates, query, ct);
            var finalGroups = BuildSearchGroups(finalScored, query.MaxPerCategory);

            sw.Stop();

            var finalSnapshot = new SearchSnapshot(
                SessionId: sessionId,
                Groups: finalGroups,
                IsFinal: true,
                ElapsedMs: sw.ElapsedMilliseconds,
                SourceStates: sourceStates);

            PublishIfCurrent(finalSnapshot, sessionId, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // Fail fast, publish empty degraded snapshot
        }
    }

    private static List<ScoredResult> ScoreAndSort(List<Candidate> candidates, SearchQuery query, CancellationToken ct)
    {
        var deduped = SearchRanker.Deduplicate(candidates);
        var usageCounts = StorageService.GetAllEffectiveOpenCounts();
        var scoredResults = new List<ScoredResult>(deduped.Count);

        for (int i = 0; i < deduped.Count; i++)
        {
            if (ct.IsCancellationRequested) break;
            var c = deduped[i];
            usageCounts.TryGetValue(c.Id, out int openCount);
            var scored = SearchRanker.ScoreCandidate(c, query, openCount: openCount);
            if (scored.Score > 0)
            {
                scoredResults.Add(scored);
            }
        }

        SearchRanker.SortResults(scoredResults);
        return scoredResults;
    }

    private void PublishIfCurrent(SearchSnapshot snapshot, long sessionId, CancellationToken ct)
    {
        lock (_sessionLock)
        {
            if (sessionId == Interlocked.Read(ref _currentSessionId) && !ct.IsCancellationRequested)
            {
                CurrentSnapshot = snapshot;
                SnapshotUpdated?.Invoke(snapshot);
            }
        }
    }

    internal static IReadOnlyList<SearchGroup> BuildSearchGroups(List<ScoredResult> results, int maxPerGroup)
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

        // Apps always comes first if present. Other categories ordered by TopScore descending.
        groups.Sort((a, b) =>
        {
            if (a.Category == SearchCategory.Apps && b.Category != SearchCategory.Apps) return -1;
            if (b.Category == SearchCategory.Apps && a.Category != SearchCategory.Apps) return 1;

            int scoreComp = b.TopScore.CompareTo(a.TopScore);
            if (scoreComp != 0) return scoreComp;

            return a.Category.CompareTo(b.Category);
        });
        return groups;
    }

    /// <summary>
    /// Resolves up to maxCount zero-state suggestions (recent apps and recently launched files/folders)
    /// based on frequency and recency from StorageService.
    /// </summary>
    public static IReadOnlyList<Candidate> GetZeroStateSuggestions(
        IEnumerable<CatalogItemModel>? apps = null,
        int maxCount = 15,
        DateTimeOffset? now = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        bool fallbackToInstalledApps = false)
    {
        if (maxCount <= 0) return Array.Empty<Candidate>();

        var topLaunches = StorageService.GetTopRecentLaunches(maxCount, now);
        var appList = apps as IList<CatalogItemModel> ?? apps?.ToList() ?? (IList<CatalogItemModel>)Array.Empty<CatalogItemModel>();
        var candidates = new List<Candidate>(maxCount);

        if (topLaunches != null && topLaunches.Count > 0)
        {
            var isDir = directoryExists ?? Directory.Exists;
            var isFile = fileExists ?? File.Exists;

            for (int t = 0; t < topLaunches.Count; t++)
            {
                if (candidates.Count >= maxCount) break;
                var (key, _, _) = topLaunches[t];
                if (string.IsNullOrWhiteSpace(key)) continue;

                // 1. Resolve against installed apps
                CatalogItemModel? matchingApp = null;
                for (int i = 0; i < appList.Count; i++)
                {
                    var a = appList[i];
                    if (string.Equals(a.TargetPath, key, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase))
                    {
                        matchingApp = a;
                        break;
                    }
                }

                if (matchingApp != null)
                {
                    candidates.Add(new Candidate(
                        Id: matchingApp.TargetPath ?? matchingApp.Name,
                        DisplayName: matchingApp.Name,
                        FullPathOrKey: matchingApp.TargetPath ?? string.Empty,
                        Category: SearchCategory.Apps,
                        SourceId: "recent_apps",
                        Modified: null,
                        Size: null,
                        IsFolder: false,
                        Tag: matchingApp));
                    continue;
                }

                // 2. Resolve against file system (or custom predicate)
                try
                {
                    bool folder = isDir(key);
                    bool file = !folder && isFile(key);

                    if (folder || file)
                    {
                        string name = Path.GetFileName(key);
                        if (string.IsNullOrEmpty(name)) name = key;

                        var cat = EverythingSearchSource.DetermineCategory(folder, Path.GetExtension(key), name);

                        candidates.Add(new Candidate(
                            Id: key,
                            DisplayName: name,
                            FullPathOrKey: key,
                            Category: cat,
                            SourceId: "recent_files",
                            Modified: null,
                            Size: null,
                            IsFolder: folder,
                            Tag: null));
                    }
                }
                catch
                {
                    // Silently skip invalid paths
                }
            }
        }

        // 3. Optional backfill with installed apps if requested
        if (fallbackToInstalledApps && candidates.Count < maxCount && appList.Count > 0)
        {
            var existingIds = new HashSet<string>(candidates.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < appList.Count && candidates.Count < maxCount; i++)
            {
                var app = appList[i];
                string id = app.TargetPath ?? app.Name;
                if (!existingIds.Contains(id))
                {
                    existingIds.Add(id);
                    candidates.Add(new Candidate(
                        Id: id,
                        DisplayName: app.Name,
                        FullPathOrKey: app.TargetPath ?? string.Empty,
                        Category: SearchCategory.Apps,
                        SourceId: "installed_apps",
                        Modified: null,
                        Size: null,
                        IsFolder: false,
                        Tag: app));
                }
            }
        }

        return candidates;
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

        foreach (var src in _tier1Sources.Concat(_tier2Sources))
        {
            if (src is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }
}
