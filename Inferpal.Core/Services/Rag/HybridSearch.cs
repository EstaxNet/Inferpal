namespace Inferpal.Services.Rag;

/// <summary>
/// Code-aware tokenizer for the lexical (BM25) side of hybrid search. Splits on non-alphanumeric
/// boundaries, then sub-splits identifiers on camelCase / acronym / letter↔digit boundaries, and
/// lowercases. Multi-part identifiers also emit the whole joined token so an exact-identifier query
/// (<c>RagDatabase</c>) matches both the parts (<c>rag</c>, <c>database</c>) and the whole.
/// </summary>
internal static class CodeTokenizer
{
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;

        int i = 0, n = text.Length;
        while (i < n)
        {
            if (!char.IsLetterOrDigit(text[i])) { i++; continue; }
            int start = i;
            while (i < n && char.IsLetterOrDigit(text[i])) i++;
            AddSubTokens(text.AsSpan(start, i - start), tokens);
        }
        return tokens;
    }

    private static void AddSubTokens(ReadOnlySpan<char> run, List<string> outp)
    {
        int pieces = 0, s = 0;
        for (int k = 1; k <= run.Length; k++)
        {
            var boundary = k == run.Length;
            if (!boundary)
            {
                char prev = run[k - 1], cur = run[k];
                boundary =
                    (char.IsLower(prev) && char.IsUpper(cur)) ||                                   // get|User
                    (char.IsLetter(prev) && char.IsDigit(cur)) ||                                  // utf|8
                    (char.IsDigit(prev) && char.IsLetter(cur)) ||                                  // 8|bit
                    (char.IsUpper(prev) && char.IsUpper(cur) && k + 1 < run.Length && char.IsLower(run[k + 1])); // HTTP|Server
            }
            if (!boundary) continue;

            if (k - s >= 2)
            {
                outp.Add(run.Slice(s, k - s).ToString().ToLowerInvariant());
                pieces++;
            }
            s = k;
        }

        // Whole multi-part identifier (e.g. "ragdatabase") so exact-identifier queries match too.
        if (pieces > 1 && run.Length >= 3)
            outp.Add(run.ToString().ToLowerInvariant());
    }
}

/// <summary>
/// In-memory BM25 lexical ranker over a fixed corpus of pre-tokenized documents. Pure/testable.
/// Built once per search from the chunk corpus; <see cref="Rank"/> scores a tokenized query.
/// </summary>
internal sealed class Bm25Index
{
    private const double K1 = 1.5;   // term-frequency saturation
    private const double B  = 0.75;  // length normalisation

    private readonly Dictionary<string, int>[] _tf;   // per-doc term → frequency
    private readonly Dictionary<string, int>  _df = new(StringComparer.Ordinal);
    private readonly double _avgLen;
    private readonly int    _n;

    public Bm25Index(IReadOnlyList<IReadOnlyList<string>> docs)
    {
        _n  = docs.Count;
        _tf = new Dictionary<string, int>[_n];

        long totalLen = 0;
        for (int i = 0; i < _n; i++)
        {
            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in docs[i])
                map[t] = map.GetValueOrDefault(t) + 1;
            _tf[i]   = map;
            totalLen += docs[i].Count;
            foreach (var term in map.Keys)
                _df[term] = _df.GetValueOrDefault(term) + 1;
        }
        _avgLen = _n > 0 ? (double)totalLen / _n : 0;
    }

    /// <summary>Returns document indices with a positive BM25 score, best first, capped to <paramref name="top"/>.</summary>
    public List<(int Index, double Score)> Rank(IReadOnlyList<string> queryTerms, int top)
    {
        var scores = new double[_n];
        if (_n == 0 || _avgLen <= 0) return [];

        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            if (!_df.TryGetValue(term, out var df) || df == 0) continue;
            var idf = Math.Log(1 + (_n - df + 0.5) / (df + 0.5));

            for (int i = 0; i < _n; i++)
            {
                if (!_tf[i].TryGetValue(term, out var tf)) continue;
                var denom = tf + K1 * (1 - B + B * DocLength(i) / _avgLen);
                scores[i] += idf * (tf * (K1 + 1)) / denom;
            }
        }

        var ranked = new List<(int, double)>();
        for (int i = 0; i < _n; i++)
            if (scores[i] > 0) ranked.Add((i, scores[i]));

        ranked.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return ranked.Count > top ? ranked.GetRange(0, top) : ranked;
    }

    // Document length = total term occurrences (sum of term frequencies), cached lazily.
    private int[]? _lenCache;
    private int DocLength(int i)
    {
        _lenCache ??= _tf.Select(m => m.Values.Sum()).ToArray();
        return _lenCache[i];
    }
}

/// <summary>
/// Reciprocal Rank Fusion: merges several ranked lists into one by summing <c>1/(k + rank)</c> across
/// the lists in which each item appears. Rank-based (not score-based), so it fuses heterogeneous
/// rankings — cosine similarity and BM25 — without score calibration. Pure/testable.
/// </summary>
internal static class ReciprocalRankFusion
{
    public const int DefaultK = 60;

    /// <summary>Items ordered by descending fused score. <paramref name="rankings"/> are best-first lists.</summary>
    public static List<T> Fuse<T>(IEnumerable<IReadOnlyList<T>> rankings, int k = DefaultK) where T : notnull
    {
        var score = new Dictionary<T, double>();
        foreach (var ranking in rankings)
            for (int rank = 0; rank < ranking.Count; rank++)
                score[ranking[rank]] = score.GetValueOrDefault(ranking[rank]) + 1.0 / (k + rank + 1);

        return score.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }
}
