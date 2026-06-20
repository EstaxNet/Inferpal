using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// Pure building blocks of hybrid RAG search: the code-aware tokenizer, the in-memory BM25 ranker,
// and Reciprocal Rank Fusion.
public class HybridSearchTests
{
    // ── Tokenizer ──────────────────────────────────────────────────────────────

    [Fact]
    public void Tokenize_SplitsCamelCase_AndKeepsWholeIdentifier()
    {
        var t = CodeTokenizer.Tokenize("getUserName");
        Assert.Contains("get", t);
        Assert.Contains("user", t);
        Assert.Contains("name", t);
        Assert.Contains("getusername", t);   // whole identifier kept for exact matches
    }

    [Fact]
    public void Tokenize_SplitsSnakeCaseAndPunctuation()
    {
        var t = CodeTokenizer.Tokenize("user_id = foo.Bar(42)");
        Assert.Contains("user", t);
        Assert.Contains("id", t);
        Assert.Contains("foo", t);
        Assert.Contains("bar", t);
    }

    [Fact]
    public void Tokenize_SplitsAcronymBoundary()
    {
        var t = CodeTokenizer.Tokenize("HTTPServer");
        Assert.Contains("http", t);
        Assert.Contains("server", t);
    }

    [Fact]
    public void Tokenize_Empty_ReturnsEmpty()
    {
        Assert.Empty(CodeTokenizer.Tokenize(""));
        Assert.Empty(CodeTokenizer.Tokenize(null));
    }

    // ── BM25 ─────────────────────────────────────────────────────────────────────

    private static Bm25Index Index(params string[][] docs) =>
        new(docs.Select(d => (IReadOnlyList<string>)d).ToList());

    [Fact]
    public void Bm25_RanksOnlyDocsContainingQueryTerm()
    {
        var idx = Index(
            ["auth", "login", "user"],
            ["database", "connection"],
            ["auth", "token"]);

        var ranked = idx.Rank(["auth"], top: 10);
        var hit = ranked.Select(r => r.Index).ToHashSet();

        Assert.Contains(0, hit);
        Assert.Contains(2, hit);
        Assert.DoesNotContain(1, hit);   // no "auth" → score 0 → excluded
    }

    [Fact]
    public void Bm25_HigherTermFrequency_RanksHigher()
    {
        var idx = Index(
            ["auth", "x", "y"],          // tf(auth) = 1
            ["auth", "auth", "auth"]);   // tf(auth) = 3, same length

        var ranked = idx.Rank(["auth"], top: 10);
        Assert.Equal(1, ranked[0].Index);   // the doc with more occurrences wins
    }

    [Fact]
    public void Bm25_EmptyQueryOrCorpus_ReturnsEmpty()
    {
        Assert.Empty(Index().Rank(["auth"], 10));
        Assert.Empty(Index(["a", "b"]).Rank([], 10));
    }

    // ── Reciprocal Rank Fusion ────────────────────────────────────────────────────

    [Fact]
    public void Rrf_ItemRankedHighInBothLists_WinsOverItemInOnlyOne()
    {
        var a = new List<int> { 10, 20, 30 };
        var b = new List<int> { 20, 40 };

        var fused = ReciprocalRankFusion.Fuse(new[] { a, b });

        Assert.Equal(20, fused[0]);    // appears near top of both
        Assert.Contains(40, fused);    // present although only in one list
        Assert.Contains(10, fused);
    }

    [Fact]
    public void Rrf_SingleList_PreservesOrder()
    {
        var fused = ReciprocalRankFusion.Fuse(new[] { new List<int> { 5, 6, 7 } });
        Assert.Equal([5, 6, 7], fused);
    }
}
