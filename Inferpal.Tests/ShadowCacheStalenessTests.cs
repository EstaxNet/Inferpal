using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ The search pre-computed while the user types (the "shadow") is read by the per-turn auto-context, the @code
/// mention and <c>search_codebase</c> — and it was cleared only when the WHOLE index was replaced. A file re-indexed
/// since (the agent edited it, the user saved it) kept its old chunks in the cached results: a Regenerate, which sends
/// the same text again, put the code from before the edit under "Relevant code" as current, and the "not yet
/// re-indexed" filter could not catch it — the file HAD been re-indexed.
/// </summary>
public sealed class ShadowCacheStalenessTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public ShadowCacheStalenessTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}", "{",
        "    public int One()   => 1;", "    public int Two()   => 2;", "    public int Three() => 3;",
        "    public int Four()  => 4;", "    public int Five()  => 5;", "    public int Six()   => 6;", "}");

    private async Task<(ProjectIndexService Index, string File)> IndexedAsync()
    {
        var file = Path.Combine(_root, "Alpha.cs");
        await File.WriteAllTextAsync(file, SampleClass("AlphaOriginal"));
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed-model" };
        var index = new ProjectIndexService(new FakeInferenceProvider { Embedding = [0.1f, 0.2f, 0.3f] }, config,
                                            new LspSemanticProvider()) { DebounceMs = 100 };
        _services.Add(index);
        index.StartIndexing(_root);
        await WaitUntilAsync(() => !index.IsIndexing && index.ChunkCount > 0, "the initial pass");
        return (index, file);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); DateTime.UtcNow < deadline; await Task.Delay(50))
            if (condition()) return;
        Assert.Fail($"timed out waiting for {what}");
    }

    private const string Query = "what does the AlphaOriginal class return";

    private static bool Holds(List<RagHit>? results, string text) =>
        results?.Any(h => h.Chunk.Content.Contains(text, StringComparison.Ordinal)) == true;

    [Fact]
    public async Task AFileReindexedSinceThePreWarm_IsNotServedFromTheCache()
    {
        var (index, file) = await IndexedAsync();
        await index.ShadowPreWarmAsync(Query, CancellationToken.None);
        Assert.True(Holds(index.TryGetShadow(Query).Results, "AlphaOriginal"));   // WITNESS: the cache was filled

        await File.WriteAllTextAsync(file, SampleClass("AlphaRewritten"));
        index.OnFileChangedCore(file);
        await WaitUntilAsync(() => index.NotYetReindexed.Count == 0
                                   && index.SearchAsync([0.1f, 0.2f, 0.3f], "AlphaRewritten", 5, CancellationToken.None)
                                           .GetAwaiter().GetResult().Any(h => h.Chunk.Content.Contains("AlphaRewritten")),
                             "the re-index of Alpha.cs");

        var (embedding, results) = index.TryGetShadow(Query);
        Assert.False(Holds(results, "AlphaOriginal"), "the cache served the code from before the change");
        Assert.NotNull(embedding);   // the query's vector does not depend on the code: still worth reusing
    }

    [Fact]
    public async Task WithNothingChanged_TheCacheServes()
    {
        // REFERENCE ARM: the fix invalidates on a change, not on every read.
        var (index, _) = await IndexedAsync();
        await index.ShadowPreWarmAsync(Query, CancellationToken.None);

        Assert.True(Holds(index.TryGetShadow(Query).Results, "AlphaOriginal"));
    }
}
