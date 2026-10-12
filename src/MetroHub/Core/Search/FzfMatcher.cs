using System;
using System.Runtime.CompilerServices;

namespace MetroHub.Core.Search;

/// <summary>
/// FZF-style subsequence matcher. Allocation-free: stackalloc only, query &lt;= 64, target &lt;= 256 chars.
/// Query must already be lowercased/normalized. Pass the RAW DisplayName as target
/// (camelCase bonuses need original casing); matching itself is case-insensitive.
/// </summary>
public static class FzfMatcher
{
    public const int MaxQueryLength = 64;
    public const int MaxTargetLength = 256;
    public const int MinScore = 20;
    public const int MaxScore = 65;

    // FZF constants
    private const int ScoreMatch = 16;
    private const int GapStart = 3;
    private const int GapExtend = 1;
    private const int BonusWhite = 10;       // after whitespace / string start
    private const int BonusDelimiter = 9;    // after - _ . / \
    private const int BonusNonWord = 8;      // after other punctuation
    private const int BonusCamel = 7;        // aB boundary, letter->digit
    private const int BonusConsecutive = 4;
    private const int FirstCharMultiplier = 2;
    private const int Neg = -(1 << 28);

    /// <summary>Greedy forward scan: true if all query chars appear in order in target.</summary>
    public static bool CanMatchSubsequence(ReadOnlySpan<char> query, ReadOnlySpan<char> target)
    {
        if (query.Length == 0 || query.Length > target.Length) return false;
        int qi = 0;
        char qc = Lower(query[0]);
        for (int t = 0; t < target.Length; t++)
        {
            if (Lower(target[t]) != qc) continue;
            if (++qi == query.Length) return true;
            qc = Lower(query[qi]);
        }
        return false;
    }

    /// <summary>Returns 0 if no subsequence match, otherwise a score in [20, 65].</summary>
    public static int Score(ReadOnlySpan<char> query, ReadOnlySpan<char> target)
    {
        int n = query.Length;
        if (n == 0 || n > MaxQueryLength) return 0;
        if (target.Length > MaxTargetLength) target = target.Slice(0, MaxTargetLength);
        if (n > target.Length) return 0;

        // 1) Greedy forward pre-filter; also yields the first possible match index.
        int start = -1, qi = 0;
        char qc = Lower(query[0]);
        for (int t = 0; t < target.Length; t++)
        {
            if (Lower(target[t]) != qc) continue;
            if (qi == 0) start = t;
            if (++qi == n) break;
            qc = Lower(query[qi]);
        }
        if (qi < n) return 0;

        // 2) Window end = last occurrence of final query char (always >= start, guaranteed by step 1).
        char lastCh = Lower(query[n - 1]);
        int end = target.Length - 1;
        while (Lower(target[end]) != lastCh) end--;

        int w = end - start + 1;

        // 3) Per-window buffers on the stack.
        Span<int> rows = stackalloc int[w * 2];
        Span<char> lt = stackalloc char[w];
        Span<byte> bonus = stackalloc byte[w];
        Span<int> prev = rows.Slice(0, w);
        Span<int> cur = rows.Slice(w, w);

        for (int j = 0; j < w; j++)
        {
            int t = start + j;
            char c = target[t];
            lt[j] = Lower(c);
            bonus[j] = (byte)Bonus(t == 0 ? ' ' : target[t - 1], c);
        }

        // 4) DP, one row per query char. prev[j] = best score with query[i] matched at window pos j.
        qc = Lower(query[0]);
        for (int j = 0; j < w; j++)
            prev[j] = lt[j] == qc ? ScoreMatch + bonus[j] * FirstCharMultiplier : Neg;

        for (int i = 1; i < n; i++)
        {
            qc = Lower(query[i]);
            int run = Neg; // best prev[k] (k <= j-2) minus gap penalty up to j
            for (int j = 0; j < w; j++)
            {
                if (j >= 2) run = Math.Max(run - GapExtend, prev[j - 2] - GapStart);

                int v = Neg;
                if (j >= i && lt[j] == qc)
                {
                    int b = bonus[j];
                    int cons = j >= 1 ? prev[j - 1] + ScoreMatch + Math.Max(b, BonusConsecutive) : Neg;
                    int gap = run + ScoreMatch + b;
                    v = Math.Max(cons, gap);
                    if (v < Neg / 2) v = Neg;
                }
                cur[j] = v;
            }
            Span<int> tmp = prev; prev = cur; cur = tmp;
        }

        int best = Neg;
        for (int j = 0; j < w; j++)
            if (prev[j] > best) best = prev[j];
        if (best < Neg / 2) return 0;

        // 5) Normalize raw score into [MinScore, MaxScore].
        int lo = n * ScoreMatch;
        int span = BonusWhite * FirstCharMultiplier + (n - 1) * BonusConsecutive; // perfect prefix run
        int d = Math.Clamp(best - lo, 0, span);
        return MinScore + d * (MaxScore - MinScore) / span;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static char Lower(char c) =>
        (uint)(c - 'A') <= 'Z' - 'A' ? (char)(c | 0x20) : c < 128 ? c : char.ToLowerInvariant(c);

    private static int Bonus(char prev, char cur)
    {
        if (prev == ' ' || char.IsWhiteSpace(prev)) return BonusWhite;
        if (prev is '-' or '_' or '.' or '/' or '\\') return BonusDelimiter;
        if (!char.IsLetterOrDigit(prev)) return BonusNonWord;
        if (char.IsLower(prev) && char.IsUpper(cur)) return BonusCamel;
        if (!char.IsDigit(prev) && char.IsDigit(cur)) return BonusCamel;
        return 0;
    }
}
