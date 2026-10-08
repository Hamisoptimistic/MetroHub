using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Everything.Net.Abstractions;
using Everything.Net.Configuration;
using Everything.Net.Enums;
using Everything.Net.Models;
using Everything.Net.Services;
using Microsoft.Extensions.Options;

namespace MetroHub.Core.Search;

/// <summary>
/// Source adapter integrating the voidtools Everything search engine.
/// Serializes all native calls on a dedicated background worker thread with a capacity-1 queue (latest-wins).
/// Encapsulates all Voidtools types strictly within this file.
/// </summary>
public sealed class EverythingSearchSource : ISearchSource, IDisposable
{
    public string SourceId => "everything";

    public SourceState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
        private set
        {
            lock (_stateLock)
            {
                _state = value;
            }
        }
    }

    private readonly IEverythingClient? _client;
    private readonly int _maxResults;
    private readonly object _stateLock = new();
    private readonly object _queueLock = new();
    private readonly AutoResetEvent _workSignal = new(false);
    private readonly Thread _workerThread;

    private SourceState _state;
    private SearchWorkItem? _pendingItem;
    private int _consecutiveFailures = 0;
    private DateTimeOffset _lastProbeTime = DateTimeOffset.MinValue;
    private double _probeBackoffSeconds = 1.0;
    private const double MaxProbeBackoffSeconds = 30.0;
    private bool _isDisposed = false;

    public EverythingSearchSource(int maxResults = 100)
        : this(CreateDefaultClient(), maxResults)
    {
    }

    internal EverythingSearchSource(IEverythingClient? client, int maxResults = 100)
    {
        _maxResults = maxResults;
        _client = client;

        if (_client == null)
        {
            _state = new SourceState(SourceStateKind.Unavailable, "Everything native client could not be loaded.");
        }
        else
        {
            _state = SourceState.Starting;
        }

        _workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "MetroHub-EverythingWorker"
        };
        _workerThread.Start();
    }

    private static IEverythingClient? CreateDefaultClient()
    {
        try
        {
            var options = Options.Create(new EverythingClientOptions
            {
                ThrowOnUnavailableClient = false,
                DefaultMaxResults = 100,
                RequestPathAndFileNameByDefault = true
            });
            return new EverythingClient(options);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        if (_isDisposed || cancellationToken.IsCancellationRequested)
        {
            return Array.Empty<Candidate>();
        }

        // Blueprint 6.3: Do not run a file query for fewer than 2 characters unless prefix set
        if (query.IsEmpty || (query.NormalizedText.Length < 2 && query.Mode == SearchMode.All && !query.RawText.StartsWith('>')))
        {
            return Array.Empty<Candidate>();
        }

        // Apps-only mode skips Everything file searches
        if (query.Mode == SearchMode.Apps)
        {
            return Array.Empty<Candidate>();
        }

        if (_client == null)
        {
            return Array.Empty<Candidate>();
        }

        // Health backoff check if Unavailable
        var currentState = State;
        if (currentState.State == SourceStateKind.Unavailable)
        {
            var now = DateTimeOffset.UtcNow;
            if ((now - _lastProbeTime).TotalSeconds < _probeBackoffSeconds)
            {
                return Array.Empty<Candidate>();
            }
        }

        var workItem = new SearchWorkItem(query, cancellationToken);
        SearchWorkItem? oldItem;

        // Bounded queue with capacity 1 (latest-wins)
        lock (_queueLock)
        {
            oldItem = _pendingItem;
            _pendingItem = workItem;
            _workSignal.Set();
        }

        // Cancel superseded pending work item immediately
        oldItem?.Cancel();

        try
        {
            // Blueprint Section 5: Hard 400ms timeout for Everything queries
            return await workItem.Task.WaitAsync(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RecordFailure("Everything query timed out (400ms).");
            return Array.Empty<Candidate>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<Candidate>();
        }
        catch (Exception ex)
        {
            RecordFailure(ex.Message);
            return Array.Empty<Candidate>();
        }
    }

    private void WorkerLoop()
    {
        while (!_isDisposed)
        {
            try
            {
                _workSignal.WaitOne();
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (_isDisposed) break;

            SearchWorkItem? item;
            lock (_queueLock)
            {
                item = _pendingItem;
                _pendingItem = null;
            }

            if (item == null) continue;

            if (item.CancellationToken.IsCancellationRequested)
            {
                item.Cancel();
                continue;
            }

            try
            {
                var candidates = ExecuteSearchInternal(item.Query, item.CancellationToken);
                item.TrySetResult(candidates);
            }
            catch (OperationCanceledException)
            {
                item.Cancel();
            }
            catch (Exception ex)
            {
                RecordFailure(ex.Message);
                item.TrySetResult(Array.Empty<Candidate>());
            }
        }
    }

    private IReadOnlyList<Candidate> ExecuteSearchInternal(SearchQuery query, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _client == null)
        {
            return Array.Empty<Candidate>();
        }

        // Check availability
        try
        {
            _lastProbeTime = DateTimeOffset.UtcNow;
            if (!_client.IsAvailable())
            {
                RecordUnavailable("Everything service is not running or IPC is unavailable.");
                return Array.Empty<Candidate>();
            }
        }
        catch (Exception ex)
        {
            RecordUnavailable($"Everything availability probe failed: {ex.Message}");
            return Array.Empty<Candidate>();
        }

        string searchText = BuildSearchText(query);
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return Array.Empty<Candidate>();
        }

        var everythingQuery = new EverythingQuery
        {
            SearchText = searchText,
            MaxResults = (uint)_maxResults,
            RequestFlags = EverythingRequestFlags.FileName |
                           EverythingRequestFlags.Path |
                           EverythingRequestFlags.FullPathAndFileName |
                           EverythingRequestFlags.Extension |
                           EverythingRequestFlags.Size |
                           EverythingRequestFlags.DateModified |
                           EverythingRequestFlags.Attributes,
            Sort = null,
            MatchCase = false,
            MatchPath = false,
            MatchWholeWord = false,
            Regex = false
        };

        EverythingQueryResponse response;
        try
        {
            response = _client.Search(everythingQuery);
        }
        catch (Exception ex)
        {
            RecordFailure(ex.Message);
            return Array.Empty<Candidate>();
        }

        if (!response.Success)
        {
            RecordFailure(response.ErrorMessage ?? $"Everything error: {response.ErrorCode}");
            return Array.Empty<Candidate>();
        }

        RecordSuccess();

        if (response.Results == null || response.Results.Count == 0)
        {
            return Array.Empty<Candidate>();
        }

        var candidates = new List<Candidate>(response.Results.Count);
        for (int i = 0; i < response.Results.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var r = response.Results[i];
            string name = r.FileName ?? string.Empty;
            string fullPath = r.FullPath ?? string.Empty;

            // Blueprint 6.3: Exclusion filter verification
            if (SearchRanker.IsExcluded(name, fullPath) || SearchRanker.IsNoisePath(fullPath))
            {
                continue;
            }

            var category = DetermineCategory(r.IsFolder, r.Extension, name);

            candidates.Add(new Candidate(
                Id: fullPath,
                DisplayName: name,
                FullPathOrKey: fullPath,
                Category: category,
                SourceId: SourceId,
                Modified: r.DateModified,
                Size: (long?)r.Size,
                IsFolder: r.IsFolder,
                Tag: r));
        }

        return candidates;
    }

    internal static string BuildSearchText(SearchQuery query)
    {
        string raw = query.RawText.Trim();

        // 1. Power mode
        if (raw.StartsWith('>'))
        {
            return raw[1..].Trim();
        }
        if (raw.StartsWith("raw:", StringComparison.OrdinalIgnoreCase))
        {
            return raw[4..].Trim();
        }

        // 2. Safe mode
        string baseText = SanitizeSafeQuery(query.NormalizedText);
        if (string.IsNullOrWhiteSpace(baseText))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(baseText.Length + 64);
        sb.Append(baseText);

        // 3. Category filters
        if (query.Mode == SearchMode.Folders || query.CategoryFilter == SearchCategory.Folders)
        {
            sb.Append(" folder:");
        }
        else if (query.CategoryFilter == SearchCategory.Documents)
        {
            sb.Append(" ext:doc;docx;pdf;txt;rtf;xls;xlsx;ppt;pptx;md;csv;odt;ods;odp");
        }
        else if (query.CategoryFilter == SearchCategory.Images)
        {
            sb.Append(" ext:png;jpg;jpeg;gif;bmp;webp;svg;ico;tif;tiff");
        }
        else if (query.CategoryFilter == SearchCategory.Media)
        {
            sb.Append(" ext:mp3;flac;wav;m4a;aac;ogg;mp4;mkv;avi;mov;wmv;webm");
        }
        else if (query.CategoryFilter == SearchCategory.Code)
        {
            sb.Append(" ext:cs;xaml;json;xml;js;ts;html;css;cpp;h;c;py;go;rs;java;sql;ps1;sh;bat;cmd");
        }

        // 4. Exclusion syntax (Blueprint 6.3)
        sb.Append(@" !node_modules\ !bin\ !obj\ !.git\");

        return sb.ToString();
    }

    internal static string SanitizeSafeQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Strip boolean operators that user didn't intentionally escape
        var cleaned = text.Replace('|', ' ').Replace('!', ' ');

        // Check parentheses balance
        int openCount = 0;
        int closeCount = 0;
        for (int i = 0; i < cleaned.Length; i++)
        {
            if (cleaned[i] == '(') openCount++;
            else if (cleaned[i] == ')') closeCount++;
        }

        if (openCount != closeCount)
        {
            cleaned = cleaned.Replace('(', ' ').Replace(')', ' ');
        }

        // Collapse whitespace
        var sb = new StringBuilder(cleaned.Length);
        bool inSpace = false;
        for (int i = 0; i < cleaned.Length; i++)
        {
            char c = cleaned[i];
            if (char.IsWhiteSpace(c))
            {
                if (!inSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    inSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                inSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    internal static SearchCategory DetermineCategory(bool isFolder, string? extension, string? fileName)
    {
        if (isFolder) return SearchCategory.Folders;

        string ext = extension ?? (fileName != null ? Path.GetExtension(fileName) : string.Empty);
        if (ext.StartsWith('.')) ext = ext[1..];

        return ext.ToLowerInvariant() switch
        {
            "doc" or "docx" or "pdf" or "txt" or "rtf" or "xls" or "xlsx" or "ppt" or "pptx" or "csv" or "odt" or "ods" or "odp" or "epub"
                => SearchCategory.Documents,

            "png" or "jpg" or "jpeg" or "gif" or "bmp" or "webp" or "svg" or "ico" or "tif" or "tiff"
                => SearchCategory.Images,

            "mp3" or "flac" or "wav" or "m4a" or "aac" or "ogg" or "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm"
                => SearchCategory.Media,

            "cs" or "xaml" or "json" or "xml" or "js" or "ts" or "html" or "css" or "cpp" or "h" or "c" or "py" or "go" or "rs" or "java" or "sql" or "ps1" or "sh" or "bat" or "cmd"
                => SearchCategory.Code,

            _ => SearchCategory.Other
        };
    }

    private void RecordUnavailable(string reason)
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 3;
            _state = new SourceState(SourceStateKind.Unavailable, reason);
            _probeBackoffSeconds = Math.Min(MaxProbeBackoffSeconds, _probeBackoffSeconds * 2);
        }
    }

    private void RecordFailure(string reason)
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                _state = new SourceState(SourceStateKind.Unavailable, reason);
                _probeBackoffSeconds = Math.Min(MaxProbeBackoffSeconds, _probeBackoffSeconds * 2);
            }
            else
            {
                _state = new SourceState(SourceStateKind.Degraded, reason);
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_stateLock)
        {
            _consecutiveFailures = 0;
            _probeBackoffSeconds = 1.0;
            _state = SourceState.Ready;
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        SearchWorkItem? itemToCancel;
        lock (_queueLock)
        {
            itemToCancel = _pendingItem;
            _pendingItem = null;
        }
        itemToCancel?.Cancel();

        try
        {
            _workSignal.Set();
            _workerThread.Join(500);
            _workSignal.Dispose();
        }
        catch { }
    }

    private sealed class SearchWorkItem
    {
        public SearchQuery Query { get; }
        public CancellationToken CancellationToken { get; }
        private readonly TaskCompletionSource<IReadOnlyList<Candidate>> _tcs;

        public Task<IReadOnlyList<Candidate>> Task => _tcs.Task;

        public SearchWorkItem(SearchQuery query, CancellationToken cancellationToken)
        {
            Query = query;
            CancellationToken = cancellationToken;
            _tcs = new TaskCompletionSource<IReadOnlyList<Candidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void Cancel() => _tcs.TrySetCanceled(CancellationToken);
        public void TrySetResult(IReadOnlyList<Candidate> result) => _tcs.TrySetResult(result);
    }
}
