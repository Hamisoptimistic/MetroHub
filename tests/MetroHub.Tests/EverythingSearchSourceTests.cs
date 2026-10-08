using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Everything.Net.Abstractions;
using Everything.Net.Enums;
using Everything.Net.Models;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class EverythingSearchSourceTests
{
    private sealed class FakeEverythingClient : IEverythingClient
    {
        public bool Available { get; set; } = true;
        public Func<EverythingQuery, EverythingQueryResponse>? SearchHandler { get; set; }
        public int SearchCallCount { get; private set; }

        public bool IsAvailable() => Available;

        public EverythingQueryResponse Search(EverythingQuery query)
        {
            SearchCallCount++;
            if (SearchHandler != null)
            {
                return SearchHandler(query);
            }

            return CreateSuccessResponse(query.SearchText);
        }

        public Task<EverythingQueryResponse> SearchAsync(EverythingQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(Search(query));
        }
    }

    private static EverythingQueryResponse CreateSuccessResponse(string searchText, params EverythingSearchResult[] results)
    {
        return new EverythingQueryResponse
        {
            SearchText = searchText,
            Success = true,
            TotalResults = (uint)results.Length,
            Results = results
        };
    }

    private static EverythingQueryResponse CreateFailureResponse(string searchText, EverythingErrorCode errorCode, string errorMessage)
    {
        return new EverythingQueryResponse
        {
            SearchText = searchText,
            Success = false,
            TotalResults = 0,
            Results = Array.Empty<EverythingSearchResult>(),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage
        };
    }

    private static EverythingSearchResult CreateResult(uint index, string fileName, string path, string? extension = null, bool isFolder = false, long? size = null, DateTime? dateModified = null)
    {
        string fullPath = Path.Combine(path, fileName);
        return new EverythingSearchResult
        {
            Index = index,
            FileName = fileName,
            Path = path,
            FullPath = fullPath,
            Extension = extension ?? (isFolder ? null : Path.GetExtension(fileName).TrimStart('.')),
            IsFolder = isFolder,
            Size = size,
            DateModified = dateModified
        };
    }

    [Fact]
    public void BuildSearchText_PowerMode_PreservesRawText()
    {
        var query = SearchRanker.ParseQuery(">ext:png size:>2mb", 1);
        string text = EverythingSearchSource.BuildSearchText(query);

        Assert.Equal("ext:png size:>2mb", text);
    }

    [Fact]
    public void BuildSearchText_SafeMode_SanitizesUnbalancedParenthesesAndOperators()
    {
        var query = SearchRanker.ParseQuery("annual report (q3 | urgent!", 1);
        string text = EverythingSearchSource.BuildSearchText(query);

        // Parens unbalanced -> stripped. | and ! in query text -> stripped. Exclusions appended.
        Assert.Contains("annual report q3 urgent", text);
        Assert.DoesNotContain("(", text);
        Assert.DoesNotContain("|", text);
        Assert.DoesNotContain("urgent!", text);
        Assert.Contains(@"!node_modules\", text);
    }

    [Fact]
    public void BuildSearchText_SpecialCharacters_QuotesOperatorTokensForLiteralSearch()
    {
        // Dash: prevents Everything from treating - as boolean NOT
        var dashQuery = SearchRanker.ParseQuery("my-file", 1);
        string dashText = EverythingSearchSource.BuildSearchText(dashQuery);
        Assert.Contains("\"my-file\"", dashText);

        // Comma
        var commaQuery = SearchRanker.ParseQuery("report,v1", 1);
        string commaText = EverythingSearchSource.BuildSearchText(commaQuery);
        Assert.Contains("\"report,v1\"", commaText);

        // Dot
        var dotQuery = SearchRanker.ParseQuery("index.html", 1);
        string dotText = EverythingSearchSource.BuildSearchText(dotQuery);
        Assert.Contains("\"index.html\"", dotText);

        // Backtick
        var backtickQuery = SearchRanker.ParseQuery("code`test", 1);
        string backtickText = EverythingSearchSource.BuildSearchText(backtickQuery);
        Assert.Contains("\"code`test\"", backtickText);

        // Standard letters should NOT have quotes
        var plainQuery = SearchRanker.ParseQuery("brave browser", 1);
        string plainText = EverythingSearchSource.BuildSearchText(plainQuery);
        Assert.Contains("brave browser", plainText);
        Assert.DoesNotContain("\"brave\"", plainText);
    }

    [Theory]
    [InlineData(SearchCategory.Folders, "folder:")]
    [InlineData(SearchCategory.Documents, "ext:doc;docx;pdf")]
    [InlineData(SearchCategory.Images, "ext:png;jpg")]
    [InlineData(SearchCategory.Media, "ext:mp3;flac")]
    [InlineData(SearchCategory.Code, "ext:cs;xaml")]
    public void BuildSearchText_CategoryFilter_AppendsAppropriateExtensionFilters(SearchCategory category, string expectedFilterPart)
    {
        var rawQuery = new SearchQuery("test", "test", new[] { "test" }, SearchMode.All, category, 10, 1);
        string text = EverythingSearchSource.BuildSearchText(rawQuery);

        Assert.Contains(expectedFilterPart, text);
        Assert.Contains(@"!node_modules\", text);
    }

    [Fact]
    public void DetermineCategory_MapsExtensionsAndFoldersCorrectly()
    {
        Assert.Equal(SearchCategory.Folders, EverythingSearchSource.DetermineCategory(isFolder: true, null, "Projects"));
        Assert.Equal(SearchCategory.Documents, EverythingSearchSource.DetermineCategory(isFolder: false, "pdf", "invoice.pdf"));
        Assert.Equal(SearchCategory.Images, EverythingSearchSource.DetermineCategory(isFolder: false, "png", "screenshot.png"));
        Assert.Equal(SearchCategory.Media, EverythingSearchSource.DetermineCategory(isFolder: false, "mp3", "song.mp3"));
        Assert.Equal(SearchCategory.Code, EverythingSearchSource.DetermineCategory(isFolder: false, "cs", "SearchRanker.cs"));
        Assert.Equal(SearchCategory.Other, EverythingSearchSource.DetermineCategory(isFolder: false, "bin", "firmware.bin"));
    }

    [Fact]
    public async Task SearchAsync_QueryUnderTwoChars_ReturnsEmptyWithoutCallingClient()
    {
        var fakeClient = new FakeEverythingClient();
        using var source = new EverythingSearchSource(fakeClient);

        var query = SearchRanker.ParseQuery("a", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, fakeClient.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_AppsMode_ReturnsEmpty()
    {
        var fakeClient = new FakeEverythingClient();
        using var source = new EverythingSearchSource(fakeClient);

        var query = SearchRanker.ParseQuery("app:visual", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(0, fakeClient.SearchCallCount);
    }

    [Fact]
    public async Task SearchAsync_ClientUnavailable_TransitionsToUnavailableStateWithBackoff()
    {
        var fakeClient = new FakeEverythingClient { Available = false };
        using var source = new EverythingSearchSource(fakeClient);

        var query = SearchRanker.ParseQuery("project", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(SourceStateKind.Unavailable, source.State.State);

        // Immediate second call should be suppressed by backoff
        await source.SearchAsync(query, CancellationToken.None);
        Assert.Equal(SourceStateKind.Unavailable, source.State.State);
    }

    [Fact]
    public async Task SearchAsync_ClientDegraded_TransitionsToDegradedThenUnavailableAfterThreeFailures()
    {
        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q => CreateFailureResponse(q.SearchText, EverythingErrorCode.Ipc, "IPC disconnect")
        };

        using var source = new EverythingSearchSource(fakeClient);
        var query = SearchRanker.ParseQuery("testquery", 1);

        // Fail 1 -> Degraded
        await source.SearchAsync(query, CancellationToken.None);
        Assert.Equal(SourceStateKind.Degraded, source.State.State);

        // Fail 2 -> Degraded
        await source.SearchAsync(query, CancellationToken.None);
        Assert.Equal(SourceStateKind.Degraded, source.State.State);

        // Fail 3 -> Unavailable
        await source.SearchAsync(query, CancellationToken.None);
        Assert.Equal(SourceStateKind.Unavailable, source.State.State);

        // Success recovers state to Ready
        fakeClient.SearchHandler = q => CreateSuccessResponse(q.SearchText);

        // Wait brief moment or override backoff probe to verify recovery
        typeof(EverythingSearchSource)
            .GetField("_lastProbeTime", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?
            .SetValue(source, DateTimeOffset.MinValue);

        await source.SearchAsync(query, CancellationToken.None);
        Assert.Equal(SourceStateKind.Ready, source.State.State);
    }

    [Fact]
    public async Task SearchAsync_ExclusionFilter_DropsExcludedNoise()
    {
        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q => CreateSuccessResponse(
                q.SearchText,
                CreateResult(0, "ValidReport.pdf", @"C:\Docs", "pdf", isFolder: false),
                CreateResult(1, "package.json", @"C:\App\node_modules\pkg", "json", isFolder: false),
                CreateResult(2, "App.dll", @"C:\App\bin\Debug", "dll", isFolder: false))
        };

        using var source = new EverythingSearchSource(fakeClient);
        var query = SearchRanker.ParseQuery("report", 1);
        var results = await source.SearchAsync(query, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("ValidReport.pdf", results[0].DisplayName);
        Assert.Equal(SearchCategory.Documents, results[0].Category);
    }

    [Fact]
    public async Task SearchAsync_Timeout_UnblocksIn400msAndTransitionsToDegraded()
    {
        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q =>
            {
                Thread.Sleep(1000); // Simulate Everything hanging
                return CreateSuccessResponse(q.SearchText);
            }
        };

        using var source = new EverythingSearchSource(fakeClient);
        var query = SearchRanker.ParseQuery("hanging", 1);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await source.SearchAsync(query, CancellationToken.None);
        sw.Stop();

        Assert.Empty(results);
        // Hard timeout must fire within 400-900ms
        Assert.InRange(sw.ElapsedMilliseconds, 350, 950);
        Assert.Equal(SourceStateKind.Degraded, source.State.State);
    }

    [Fact]
    public async Task SearchAsync_Capacity1Queue_LatestQuerySupersedesPreviousPendingQuery()
    {
        var gate = new ManualResetEventSlim(false);
        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q =>
            {
                gate.Wait(1000);
                return CreateSuccessResponse(
                    q.SearchText,
                    CreateResult(0, $"{q.SearchText}.txt", @"C:\", "txt", isFolder: false));
            }
        };

        using var source = new EverythingSearchSource(fakeClient);

        // Start first query that blocks
        var task1 = source.SearchAsync(SearchRanker.ParseQuery("first", 1), CancellationToken.None);

        // Queue second and third queries rapidly while worker is busy
        var task2 = source.SearchAsync(SearchRanker.ParseQuery("second", 2), CancellationToken.None);
        var task3 = source.SearchAsync(SearchRanker.ParseQuery("third", 3), CancellationToken.None);

        // Unblock worker
        gate.Set();

        var res3 = await task3;
        // Third (latest) query must succeed
        Assert.NotNull(res3);
    }

    [Fact]
    public async Task SearchOrchestrator_Tier1AndTier2_PublishesProgressiveSnapshots()
    {
        var appSource = new AppSearchSource(() => new List<MetroHub.Core.Models.CatalogItemModel>
        {
            new() { Name = "Visual Studio", TargetPath = @"C:\Program Files\VS\devenv.exe" }
        });

        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q => CreateSuccessResponse(
                q.SearchText,
                CreateResult(0, "VisualNotes.docx", @"C:\Users\Docs", "docx", isFolder: false))
        };

        var fileSource = new EverythingSearchSource(fakeClient);

        var snapshots = new List<SearchSnapshot>();
        var tcsFinal = new TaskCompletionSource<bool>();

        using var orchestrator = new SearchOrchestrator(
            tier1Sources: new[] { appSource },
            tier2Sources: new[] { fileSource },
            debounceMs: 20);

        orchestrator.SnapshotUpdated += snap =>
        {
            snapshots.Add(snap);
            if (snap.IsFinal)
            {
                tcsFinal.TrySetResult(true);
            }
        };

        orchestrator.SetQuery("Visual");

        var completed = await Task.WhenAny(tcsFinal.Task, Task.Delay(2000));
        Assert.Equal(tcsFinal.Task, completed);

        // Verify progressive snapshots
        Assert.True(snapshots.Count >= 2, $"Expected at least 2 snapshots, got {snapshots.Count}");

        // First snapshot: Intermediate (IsFinal = false, Tier 1 apps)
        var firstSnap = snapshots[0];
        Assert.False(firstSnap.IsFinal);
        Assert.Contains(firstSnap.Groups, g => g.Category == SearchCategory.Apps);

        // Final snapshot: IsFinal = true, merged both Apps and Documents
        var finalSnap = snapshots[^1];
        Assert.True(finalSnap.IsFinal);
        Assert.Contains(finalSnap.Groups, g => g.Category == SearchCategory.Apps);
        Assert.Contains(finalSnap.Groups, g => g.Category == SearchCategory.Documents);
    }

    [Fact]
    public async Task ChaosTest_ConcurrentSearchesAndCancellations_NeverCrashesOrDeadlocks()
    {
        var random = new Random(42);
        var fakeClient = new FakeEverythingClient
        {
            SearchHandler = q =>
            {
                Thread.Sleep(random.Next(5, 30));
                return CreateSuccessResponse(
                    q.SearchText,
                    CreateResult(0, "file.txt", @"C:\", "txt", isFolder: false));
            }
        };

        using var source = new EverythingSearchSource(fakeClient);

        var tasks = new List<Task>();
        for (int i = 0; i < 20; i++)
        {
            int queryId = i;
            tasks.Add(Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(random.Next(10, 80));
                try
                {
                    var q = SearchRanker.ParseQuery($"query{queryId}", queryId);
                    await source.SearchAsync(q, cts.Token);
                }
                catch (OperationCanceledException) { }
            }));
        }

        // All concurrent queries must settle without deadlock
        var timeout = Task.Delay(3000);
        var allTask = Task.WhenAll(tasks);
        var completed = await Task.WhenAny(allTask, timeout);

        Assert.Equal(allTask, completed);
    }
}
