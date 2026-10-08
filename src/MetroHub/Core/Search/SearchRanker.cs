using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MetroHub.Core.Search;

/// <summary>
/// Configurable weights for relevance scoring. Kept in a single cohesive object.
/// </summary>
public sealed record RankingWeights
{
    public int Exact { get; init; } = 100;
    public int Prefix { get; init; } = 80;
    public int WordPrefix { get; init; } = 60;
    public int Acronym { get; init; } = 55;
    public int Contains { get; init; } = 40;
    public int PathOnly { get; init; } = 10;
    public int Fuzzy1 { get; init; } = 30;
    public int Fuzzy2 { get; init; } = 20;

    public int AppSingleTokenBoost { get; init; } = 10;
    public int FolderPathBoost { get; init; } = 5;
    public int UserLocationBoost { get; init; } = 15;
    public int MaxUsageBoost { get; init; } = 25;
    public int MaxRecencyBoost { get; init; } = 10;

    public int NoisePenalty { get; init; } = 30;
    public int DeepPathPenalty { get; init; } = 10;

    public static readonly RankingWeights Default = new();
}

/// <summary>
/// Pure, allocation-conscious domain algorithms for query parsing, scoring, tie-breaking, and deduplication.
/// Zero I/O, zero threading, 100% deterministic.
/// </summary>
public static class SearchRanker
{
    private static readonly string[] NoiseKeywords = ["\\bin\\", "\\obj\\", "\\node_modules\\", "\\.git\\", "\\temp\\", "\\appdata\\local\\temp\\"];
    private static readonly string[] ExcludedExtensions = [".tmp", ".pdb", ".log", ".bak", ".ilk", ".exp"];
    private static readonly string[] ExcludedKeywords = ["uninstall", "unins000", "crashreport", "diagnostics", "troubleshoot"];

    #region Query Parser

    /// <summary>
    /// Parses raw user input into a normalized immutable SearchQuery.
    /// Performs NFC normalization, accent stripping, space collapsing, tokenization, and mode detection.
    /// </summary>
    public static SearchQuery ParseQuery(string? rawText, long sessionId = 0, int maxPerCategory = 5)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SearchQuery(rawText ?? string.Empty, string.Empty, Array.Empty<string>(), SearchMode.All, null, maxPerCategory, sessionId);
        }

        string trimmed = rawText.Trim();
        if (trimmed.Length > 256)
        {
            trimmed = trimmed[..256];
        }

        var mode = SearchMode.All;
        SearchCategory? categoryFilter = null;
        string textToProcess = trimmed;

        // Prefix mode detection
        if (textToProcess.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            mode = SearchMode.Apps;
            categoryFilter = SearchCategory.Apps;
            textToProcess = textToProcess[4..].TrimStart();
        }
        else if (textToProcess.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            mode = SearchMode.Files;
            textToProcess = textToProcess[5..].TrimStart();
        }
        else if (textToProcess.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            mode = SearchMode.Folders;
            categoryFilter = SearchCategory.Folders;
            textToProcess = textToProcess[7..].TrimStart();
        }
        else if (IsPathQuery(textToProcess))
        {
            mode = SearchMode.Path;
        }

        // Strip diacritics while preserving case for camelCase tokenization
        string textNoAccents = RemoveDiacritics(textToProcess);
        var tokens = Tokenize(textNoAccents);
        string normalized = NormalizeText(textNoAccents);

        return new SearchQuery(trimmed, normalized, tokens, mode, categoryFilter, maxPerCategory, sessionId);
    }

    private static bool IsPathQuery(string text)
    {
        if (text.Length >= 2 && char.IsLetter(text[0]) && text[1] == ':') return true;
        if (text.StartsWith(@"\\", StringComparison.Ordinal) || text.StartsWith("//", StringComparison.Ordinal)) return true;
        return false;
    }

    public static string NormalizeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        // 1. Unicode NFC
        string nfc = text.Normalize(NormalizationForm.FormC);

        // 2. Remove diacritics / accents
        string noAccents = RemoveDiacritics(nfc);

        // 3. Lowercase & collapse whitespace
        var sb = new StringBuilder(noAccents.Length);
        bool inSpace = false;

        foreach (char c in noAccents)
        {
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
                sb.Append(char.ToLowerInvariant(c));
                inSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static string RemoveDiacritics(string text)
    {
        string normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(normalizedString.Length);

        foreach (char c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string[] Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();

        var tokens = new List<string>();
        var current = new StringBuilder();

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char prev = i > 0 ? text[i - 1] : '\0';

            // Split on spaces, dots, dashes, underscores, and camelCase
            bool isDelimiter = c == ' ' || c == '.' || c == '-' || c == '_' || c == '/' || c == '\\' || c == ':';
            bool isCamelBoundary = prev != '\0' && char.IsLower(prev) && char.IsUpper(c);

            if (isDelimiter || isCamelBoundary)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                if (!isDelimiter)
                {
                    current.Append(char.ToLowerInvariant(c));
                }
            }
            else
            {
                current.Append(char.ToLowerInvariant(c));
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }

    #endregion

    #region Ranker & Scoring

    /// <summary>
    /// Pure, high-performance score calculation for a single candidate against a normalized query.
    /// Returns a ScoredResult with deterministic score and MatchKind.
    /// Allocation-free in the hot path.
    /// </summary>
    public static ScoredResult ScoreCandidate(
        Candidate candidate,
        SearchQuery query,
        int openCount = 0,
        DateTimeOffset? now = null,
        RankingWeights? weights = null)
    {
        weights ??= RankingWeights.Default;

        if (query.IsEmpty)
        {
            return new ScoredResult(candidate, 0, MatchKind.None);
        }

        string q = query.NormalizedText;
        string name = candidate.DisplayName;
        string path = candidate.FullPathOrKey;

        MatchKind matchKind = MatchKind.None;
        int matchScore = 0;

        // 1. Exact Name Match (Fast case-insensitive check)
        if (name.Length == q.Length && string.Equals(name, q, StringComparison.OrdinalIgnoreCase))
        {
            matchKind = MatchKind.Exact;
            matchScore = weights.Exact;
        }
        // 2. Prefix Match
        else if (name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
        {
            matchKind = MatchKind.Prefix;
            matchScore = weights.Prefix;
        }
        else
        {
            // 3. Word Prefix / Contains check
            int matchIdx = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (matchIdx >= 0)
            {
                if (matchIdx == 0 || IsWordBoundary(name[matchIdx - 1]))
                {
                    matchKind = MatchKind.WordPrefix;
                    matchScore = weights.WordPrefix;
                }
                else
                {
                    matchKind = MatchKind.Contains;
                    matchScore = weights.Contains;
                }
            }
            // 4. Acronym Match (e.g. "vsc" for "Visual Studio Code")
            else if (q.Length >= 2 && q.Length <= 8 && MatchesAcronymFast(name, q))
            {
                matchKind = MatchKind.Acronym;
                matchScore = weights.Acronym;
            }
            // 5. Path Match
            else if (path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                matchKind = MatchKind.PathOnly;
                matchScore = weights.PathOnly;
            }
            // 6. Bounded Fuzzy / Typo Match (for Apps only, query length >= 3)
            else if (candidate.Category == SearchCategory.Apps && q.Length >= 3 && Math.Abs(name.Length - q.Length) <= 2)
            {
                int dist = ComputeLevenshteinDistance(q, name.ToLowerInvariant(), maxThreshold: 2);
                if (dist == 1)
                {
                    matchKind = MatchKind.Fuzzy;
                    matchScore = weights.Fuzzy1;
                }
                else if (dist == 2)
                {
                    matchKind = MatchKind.Fuzzy;
                    matchScore = weights.Fuzzy2;
                }
            }
        }

        if (matchKind == MatchKind.None)
        {
            return new ScoredResult(candidate, 0, MatchKind.None);
        }

        int totalScore = matchScore;

        // Boosts
        if (candidate.Category == SearchCategory.Apps && query.Tokens.Count == 1 && matchKind >= MatchKind.Prefix)
        {
            totalScore += weights.AppSingleTokenBoost;
        }

        if (query.Mode == SearchMode.Path && candidate.IsFolder)
        {
            totalScore += weights.FolderPathBoost;
        }

        if (openCount > 0)
        {
            totalScore += Math.Min(weights.MaxUsageBoost, openCount * 5);
        }

        if (candidate.Modified.HasValue)
        {
            var refTime = now ?? DateTimeOffset.UtcNow;
            double days = (refTime - candidate.Modified.Value).TotalDays;
            if (days >= 0 && days < 7)
            {
                totalScore += (int)Math.Max(0, weights.MaxRecencyBoost - (days * 1.4));
            }
        }

        if (IsUserLocation(path))
        {
            totalScore += weights.UserLocationBoost;
        }

        // Penalties
        if (IsNoisePath(path))
        {
            totalScore -= weights.NoisePenalty;
        }

        if (path.Length > 50 && CountPathDepth(path) > 8)
        {
            totalScore -= weights.DeepPathPenalty;
        }

        return new ScoredResult(candidate, Math.Max(1, totalScore), matchKind);
    }

    private static bool IsWordBoundary(char c)
    {
        return c == ' ' || c == '.' || c == '-' || c == '_' || c == '/' || c == '\\';
    }

    private static bool MatchesAcronymFast(string name, string query)
    {
        int qIdx = 0;
        bool isStartOfWord = true;

        for (int i = 0; i < name.Length && qIdx < query.Length; i++)
        {
            char c = name[i];
            if (IsWordBoundary(c))
            {
                isStartOfWord = true;
            }
            else if (isStartOfWord || (i > 0 && char.IsLower(name[i - 1]) && char.IsUpper(c)))
            {
                if (char.ToLowerInvariant(c) == query[qIdx])
                {
                    qIdx++;
                }
                isStartOfWord = false;
            }
            else
            {
                isStartOfWord = false;
            }
        }

        return qIdx == query.Length;
    }

    private static bool IsUserLocation(string path)
    {
        if (path.Length < 12) return false;
        return path.IndexOf("\\desktop\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("\\documents\\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("\\downloads\\", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsNoisePath(string path)
    {
        if (path.Length < 6) return false;
        for (int i = 0; i < NoiseKeywords.Length; i++)
        {
            if (path.IndexOf(NoiseKeywords[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    private static int CountPathDepth(string path)
    {
        int count = 0;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == '\\' || path[i] == '/') count++;
        }
        return count;
    }

    /// <summary>
    /// Bounded Levenshtein distance with early exit threshold.
    /// </summary>
    public static int ComputeLevenshteinDistance(string s, string t, int maxThreshold)
    {
        int n = s.Length;
        int m = t.Length;

        if (Math.Abs(n - m) > maxThreshold) return maxThreshold + 1;
        if (n == 0) return m <= maxThreshold ? m : maxThreshold + 1;
        if (m == 0) return n <= maxThreshold ? n : maxThreshold + 1;

        var prevRow = new int[m + 1];
        var currRow = new int[m + 1];

        for (int j = 0; j <= m; j++) prevRow[j] = j;

        for (int i = 1; i <= n; i++)
        {
            currRow[0] = i;
            int minInRow = currRow[0];

            for (int j = 1; j <= m; j++)
            {
                int cost = (s[i - 1] == t[j - 1]) ? 0 : 1;
                currRow[j] = Math.Min(
                    Math.Min(currRow[j - 1] + 1, prevRow[j] + 1),
                    prevRow[j - 1] + cost);

                if (currRow[j] < minInRow) minInRow = currRow[j];
            }

            if (minInRow > maxThreshold) return maxThreshold + 1;

            Array.Copy(currRow, prevRow, m + 1);
        }

        return prevRow[m] <= maxThreshold ? prevRow[m] : maxThreshold + 1;
    }

    /// <summary>
    /// Deterministic tie-breaking (Blueprint Section 7.2):
    /// 1. Score DESC
    /// 2. Shorter Path Length ASC
    /// 3. DisplayName Ordinal ASC
    /// 4. FullPathOrKey Ordinal ASC
    /// </summary>
    public static int CompareScoredResults(ScoredResult a, ScoredResult b)
    {
        int scoreCompare = b.Score.CompareTo(a.Score);
        if (scoreCompare != 0) return scoreCompare;

        int pathLenCompare = a.Candidate.FullPathOrKey.Length.CompareTo(b.Candidate.FullPathOrKey.Length);
        if (pathLenCompare != 0) return pathLenCompare;

        int nameCompare = string.Compare(a.Candidate.DisplayName, b.Candidate.DisplayName, StringComparison.Ordinal);
        if (nameCompare != 0) return nameCompare;

        return string.Compare(a.Candidate.FullPathOrKey, b.Candidate.FullPathOrKey, StringComparison.Ordinal);
    }

    public static void SortResults(List<ScoredResult> results)
    {
        results.Sort(CompareScoredResults);
    }

    #endregion

    #region Deduplication & Exclusion

    /// <summary>
    /// Deduplicates candidates across multiple sources.
    /// Prefers Start Menu shortcut (.lnk) over raw target if app names or paths collide.
    /// </summary>
    public static IReadOnlyList<Candidate> Deduplicate(IEnumerable<Candidate> candidates)
    {
        var map = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in candidates)
        {
            string key = c.Category == SearchCategory.Apps ? $"app:{c.DisplayName}" : c.FullPathOrKey;

            if (map.TryGetValue(key, out var existing))
            {
                if (!existing.FullPathOrKey.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    c.FullPathOrKey.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    map[key] = c;
                }
            }
            else
            {
                map[key] = c;
            }
        }

        return new List<Candidate>(map.Values);
    }

    /// <summary>
    /// Pure check against noisy or excluded files/keywords.
    /// </summary>
    public static bool IsExcluded(string name, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;

        foreach (var kw in ExcludedKeywords)
        {
            if (name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var ext in ExcludedExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) ||
                fullPath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    #endregion
}
