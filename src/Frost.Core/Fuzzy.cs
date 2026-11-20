namespace Frost.Core;

public sealed record FuzzyMatch(int Score, int[] Indices);

/// <summary>Case-insensitive subsequence matcher with bonuses for prefixes, word starts and consecutive runs.</summary>
public static class Fuzzy
{
    private const int Base = 10, Consecutive = 15, WordStart = 12, Prefix = 8;

    private static bool IsSep(char c) => c is ' ' or '\\' or '/' or '-' or '_' or '.' or ':';

    /// <summary>Returns null when <paramref name="query"/> is not a subsequence of <paramref name="candidate"/>. Spaces in the query are ignored.</summary>
    public static FuzzyMatch? Score(string query, string candidate)
    {
        var q = query.Replace(" ", "");
        int n = q.Length, m = candidate.Length;
        if (n == 0) return new FuzzyMatch(0, Array.Empty<int>());
        if (n > m) return null;

        // dp[i,j]: best score with q[i] matched at candidate[j]; from[i,j]: where q[i-1] matched.
        var dp = new int[n, m];
        var from = new int[n, m];
        const int None = int.MinValue / 2;
        for (int i = 0; i < n; i++)
        {
            int run = None, runAt = -1; // best of dp[i-1,k] + k over k <= j-2, for the gap-penalised path
            for (int j = 0; j < m; j++)
            {
                dp[i, j] = None;
                if (i > 0 && j >= 2 && dp[i - 1, j - 2] > None && dp[i - 1, j - 2] + (j - 2) > run) { run = dp[i - 1, j - 2] + (j - 2); runAt = j - 2; }
                if (char.ToLowerInvariant(q[i]) != char.ToLowerInvariant(candidate[j])) continue;

                int gain = Base + (j == 0 ? Prefix : 0) + (j == 0 || IsSep(candidate[j - 1]) ? WordStart : 0);
                if (i == 0) { dp[i, j] = gain - Math.Min(j, 3); continue; } // small penalty for starting late
                int best = None, bk = -1;
                if (j >= 1 && dp[i - 1, j - 1] > None) { best = dp[i - 1, j - 1] + Consecutive; bk = j - 1; }
                if (runAt >= 0 && run - (j - 1) > best) { best = run - (j - 1); bk = runAt; }
                if (bk < 0) continue;
                dp[i, j] = best + gain; from[i, j] = bk;
            }
        }

        int end = -1, top = None;
        for (int j = 0; j < m; j++) if (dp[n - 1, j] > top) { top = dp[n - 1, j]; end = j; }
        if (end < 0) return null;
        var idx = new int[n];
        for (int i = n - 1, j = end; i >= 0; j = from[i, j], i--) idx[i] = j;
        return new FuzzyMatch(top, idx);
    }

    /// <summary>Matches and sorts best first (ties: shorter text, then original order). An empty query keeps every item in its order.</summary>
    public static List<(T Item, FuzzyMatch Match)> Rank<T>(IEnumerable<T> items, string query, Func<T, string> text)
    {
        var res = new List<(T Item, FuzzyMatch Match, int Order)>();
        foreach (var it in items)
        {
            var m = Score(query, text(it));
            if (m != null) res.Add((it, m, res.Count));
        }
        if (query.Trim().Length > 0)
            res = res.OrderByDescending(r => r.Match.Score).ThenBy(r => text(r.Item).Length).ThenBy(r => r.Order).ToList();
        return res.Select(r => (r.Item, r.Match)).ToList();
    }
}

/// <summary>Most-recent-first folder list: deduped (case-insensitive, trailing separator ignored) and capped.</summary>
public static class RecentList
{
    public const int Max = 50;

    private static string Key(string p) => p.Length > 3 ? p.TrimEnd('\\', '/') : p;

    public static void Add(List<string> list, string path, int max = Max)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        list.RemoveAll(x => string.Equals(Key(x), Key(path), StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }
}
