using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MetroHub.Core.Search;

/// <summary>
/// Operational mode detected from search prefix or path syntax.
/// </summary>
public enum SearchMode
{
    All,
    Apps,
    Files,
    Folders,
    Path
}

/// <summary>
/// Categorical grouping for search candidates.
/// </summary>
public enum SearchCategory
{
    Apps,
    Folders,
    Documents,
    Images,
    Media,
    Code,
    Other
}

/// <summary>
/// Quality tier for string matching.
/// </summary>
public enum MatchKind
{
    None = 0,
    PathOnly = 10,
    Fuzzy = 20,
    Contains = 40,
    Acronym = 55,
    WordPrefix = 60,
    Prefix = 80,
    Exact = 100
}

/// <summary>
/// Health state of an ISearchSource provider.
/// </summary>
public enum SourceStateKind
{
    Ready,
    Starting,
    Loading,
    Degraded,
    Unavailable
}

/// <summary>
/// Snapshot of provider health with optional diagnostic reason.
/// </summary>
public sealed record SourceState(SourceStateKind State, string? Reason = null)
{
    public static readonly SourceState Ready = new(SourceStateKind.Ready);
    public static readonly SourceState Starting = new(SourceStateKind.Starting);
    public static readonly SourceState Loading = new(SourceStateKind.Loading);
    public static readonly SourceState Degraded = new(SourceStateKind.Degraded);
    public static readonly SourceState Unavailable = new(SourceStateKind.Unavailable);
}

/// <summary>
/// Normalized immutable search query produced by QueryParser.
/// </summary>
public sealed record SearchQuery(
    string RawText,
    string NormalizedText,
    IReadOnlyList<string> Tokens,
    SearchMode Mode,
    SearchCategory? CategoryFilter,
    int MaxPerCategory,
    long SessionId)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(NormalizedText);
}

/// <summary>
/// Lightweight candidate produced by an ISearchSource.
/// </summary>
public sealed record Candidate(
    string Id,
    string DisplayName,
    string FullPathOrKey,
    SearchCategory Category,
    string SourceId,
    DateTimeOffset? Modified = null,
    long? Size = null,
    bool IsFolder = false,
    object? Tag = null);

/// <summary>
/// Ranked candidate with score, match quality, and optional debug reasons.
/// </summary>
public sealed record ScoredResult(
    Candidate Candidate,
    int Score,
    MatchKind MatchKind,
    IReadOnlyList<string>? Reasons = null);

/// <summary>
/// Grouped results for UI rendering.
/// </summary>
public sealed record SearchGroup(
    SearchCategory Category,
    string Title,
    IReadOnlyList<ScoredResult> Items,
    int TotalCount,
    int TopScore);

/// <summary>
/// Immutable snapshot published to presentation layer.
/// </summary>
public sealed record SearchSnapshot(
    long SessionId,
    IReadOnlyList<SearchGroup> Groups,
    bool IsFinal,
    long ElapsedMs,
    IReadOnlyDictionary<string, SourceState> SourceStates)
{
    public static readonly SearchSnapshot Empty = new(
        0,
        Array.Empty<SearchGroup>(),
        true,
        0,
        new Dictionary<string, SourceState>());
}

/// <summary>
/// Pluggable source adapter contract.
/// </summary>
public interface ISearchSource
{
    string SourceId { get; }
    SourceState State { get; }
    Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);
}
