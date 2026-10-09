using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A pass saves the vectors it computes as it goes. Saved only at the end, a first pass interrupted by the editor
/// closing lost every vector — and a large repository on a machine that embeds slowly never finished its first index.
/// </summary>
public sealed class IndexProgressSavedTests : IDisposable
{
    private const int Files = 60;
    private const int HoldAt = 46;   // the embedding that never answers: the editor closes during it
    private readonly string _root;

    public IndexProgressSavedTests()
    {
        TestRagStore.Redirect();
        _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragprogress-{Guid.NewGuid():N}")).FullName;
        for (var i = 0; i < Files; i++)
            File.WriteAllText(Path.Combine(_root, $"C{i:D2}.cs"), SampleClass($"Probe{i:D2}"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A C# class large enough to pass the chunkers' MinChunkLines threshold.</summary>
    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        "    public int One()   => 1;",
        "    public int Two()   => 2;",
        "    public int Three() => 3;",
        "    public int Four()  => 4;",
        "    public int Five()  => 5;",
        "    public int Six()   => 6;",
        "}");

    private static InferpalConfig Config(string model) => new() { RagEnabled = true, RagEmbeddingModel = model };

    /// <summary>A provider whose <see cref="HoldAt"/>-th embedding waits until the pass is stopped.</summary>
    private static (FakeInferenceProvider Provider, Func<int> Calls) Holding(float[] vector)
    {
        var calls = 0;
        var provider = new FakeInferenceProvider
        {
            OnEmbeddingAsync = async (_, ct) =>
            {
                if (Interlocked.Increment(ref calls) >= HoldAt) await Task.Delay(Timeout.Infinite, ct);
                return vector;
            },
        };
        return (provider, () => Volatile.Read(ref calls));
    }

    /// <summary>Starts a pass, waits for the held embedding, then closes the service as the editor closing does.</summary>
    private async Task InterruptedPassAsync(FakeInferenceProvider provider, Func<int> calls, string model)
    {
        var svc = new ProjectIndexService(provider, Config(model), new LspSemanticProvider());
        svc.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (calls() < HoldAt)
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass never reached embedding {HoldAt} (status: {svc.Status})");
            await Task.Delay(20);
        }
        svc.Dispose();
        while (svc.IsIndexing)
        {
            Assert.True(DateTime.UtcNow < deadline, "the pass did not stop");
            await Task.Delay(20);
        }
    }

    private async Task<int> FullPassAsync(FakeInferenceProvider provider, string model)
    {
        using var svc = new ProjectIndexService(provider, Config(model), new LspSemanticProvider());
        svc.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
        return svc.ChunkCount;
    }

    private Task<List<RagChunk>> StoredAsync() => new RagDatabase(_root).LoadAsync(CancellationToken.None);

    [Fact]
    public async Task AnInterruptedFirstPass_KeepsTheVectorsItComputed_AndTheNextPassOnlyEmbedsTheRest()
    {
        var (held, heldCalls) = Holding([0.1f, 0.2f, 0.3f]);
        await InterruptedPassAsync(held, heldCalls, "test-embed");

        var stored = await StoredAsync();
        var kept = stored.Count(c => c.Embedding is { Length: > 0 });
        // Two full batches were embedded before the hold: at least their files are in the store.
        Assert.True(kept >= 2 * ProjectIndexService.CheckpointFiles,
                    $"{kept} vector(s) kept after the interruption, {2 * ProjectIndexService.CheckpointFiles} expected at least");

        var calls = 0;
        var next = new FakeInferenceProvider
        {
            OnEmbedding = _ => { Interlocked.Increment(ref calls); return [0.1f, 0.2f, 0.3f]; },
        };
        var total = await FullPassAsync(next, "test-embed");
        Assert.Equal(total - kept, calls);   // what was kept is reused, never embedded twice

        // Witness: the finished pass stores every chunk with its vector.
        var final = await StoredAsync();
        Assert.Equal(total, final.Count);
        Assert.All(final, c => Assert.True(c.Embedding is { Length: > 0 }, $"{c.RelPath} has no vector"));
    }

    [Fact]
    public async Task AModelChangedThenInterrupted_LeavesNoVectorOfTheOldModelUnderTheNewName()
    {
        float[] oldModel = [1f, 0f, 0f], newModel = [0f, 1f, 0f];
        await FullPassAsync(new FakeInferenceProvider { OnEmbedding = _ => oldModel }, "embed-a");
        Assert.All(await StoredAsync(), c => Assert.Equal(oldModel, c.Embedding));   // witness: model A is stored

        var (held, heldCalls) = Holding(newModel);
        await InterruptedPassAsync(held, heldCalls, "embed-b");

        var stored = await StoredAsync();
        Assert.DoesNotContain(stored, c => c.Embedding is { } v && v.SequenceEqual(oldModel));
        Assert.Contains(stored, c => c.Embedding is { } v && v.SequenceEqual(newModel));
        Assert.Equal(EmbeddingModels.CodeIndexIdentity("embed-b"),
                     await new RagDatabase(_root).GetMetaAsync("embedding_model", CancellationToken.None));
    }
}
