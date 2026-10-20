using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wpf.Ui.Controls;

namespace MetroHub.Core.Search;

/// <summary>
/// Representation of an explicit icon payload carried by a search candidate.
/// </summary>
public enum CandidateIconKind : byte
{
    None = 0,
    Symbol,
    ShellPath
}

/// <summary>
/// Immutable descriptor for candidate icons.
/// </summary>
public readonly record struct CandidateIcon(
    CandidateIconKind Kind,
    SymbolRegular Symbol = SymbolRegular.Empty,
    string? PathOrUri = null)
{
    public static CandidateIcon FromSymbol(SymbolRegular s) => new(CandidateIconKind.Symbol, s);
    public static CandidateIcon FromShellPath(string p) => new(CandidateIconKind.ShellPath, PathOrUri: p);
    public static implicit operator CandidateIcon(SymbolRegular s) => FromSymbol(s);
}

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
    Actions,
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
/// Possible outcome after executing a candidate action.
/// </summary>
public enum ActionOutcome
{
    DismissDrawer,
    StayOpenAndRefresh,
    ShowMessage,
    RequireConfirmation,
    PopulateSearchBox
}

/// <summary>
/// Result returned by a candidate action execution.
/// </summary>
public readonly record struct ActionResult(
    ActionOutcome Outcome,
    string? Message = null);

/// <summary>
/// Context passed into a candidate action upon invocation.
/// </summary>
public sealed record ActionContext(CancellationToken CancellationToken)
{
    public bool Confirmed { get; init; }
}

/// <summary>
/// Action that can be executed on a candidate.
/// </summary>
public sealed record CandidateAction(
    string Id,
    string Title,
    string? KeyGestureHint,
    Func<ActionContext, Task<ActionResult>> ExecuteAsync,
    CandidateIcon Icon = default,
    bool IsDestructive = false,
    bool BeginGroup = false);

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
    object? Tag = null)
{
    private string? _normalizedDisplayName;
    private string? _normalizedPath;

    public string NormalizedDisplayName => _normalizedDisplayName ??= SearchRanker.NormalizeText(DisplayName);
    public string NormalizedPath => _normalizedPath ??= SearchRanker.NormalizeText(FullPathOrKey);

    public string? Subtitle { get; init; }
    public CandidateAction? PrimaryAction { get; init; }
    public IReadOnlyList<CandidateAction>? SecondaryActions { get; init; }
    public Func<Candidate, IReadOnlyList<CandidateAction>>? ActionProvider { get; init; }
    public CandidateIcon Icon { get; init; }
    public string? KindLabel { get; init; }

    /// <summary>
    /// Evaluates all available actions for this candidate.
    /// ActionProvider is evaluated lazily at execution/menu time to capture real-time system and pin state.
    /// </summary>
    public IReadOnlyList<CandidateAction> GetAllActions()
    {
        if (ActionProvider != null)
        {
            return ActionProvider(this);
        }

        if (PrimaryAction == null && (SecondaryActions == null || SecondaryActions.Count == 0))
        {
            return Array.Empty<CandidateAction>();
        }

        if (PrimaryAction != null && (SecondaryActions == null || SecondaryActions.Count == 0))
        {
            return [PrimaryAction];
        }

        var list = new List<CandidateAction>(1 + (SecondaryActions?.Count ?? 0));
        if (PrimaryAction != null) list.Add(PrimaryAction);
        if (SecondaryActions != null) list.AddRange(SecondaryActions);
        return list;
    }
}

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
    public bool IsEmpty => Groups == null || Groups.Count == 0;
    public bool IsIntermediate => !IsFinal;

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
