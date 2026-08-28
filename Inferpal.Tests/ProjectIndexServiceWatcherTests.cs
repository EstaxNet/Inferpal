using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

// §27.1 - the blind window of the RAG watcher. The watcher is armed BEFORE the initial pass and
// the backlog accumulated during the pass is drained after the final SaveAsync: a file modified
// while the pass runs no longer stays stale until the next boot. The drain (and the normal watcher
// path) reuses the embeddings of chunks whose hash is unchanged instead of re-embedding
// le fichier entier.
public sealed class ProjectIndexServiceWatcherTests : IDisposable
{
    // Redirects the SQLite database out of the real %AppData% for the whole test process (same
    // precedent as TestReproScaffold.BaseDir). Never restored: no test may write the user's real
    // index.
    private static readonly string DbBase = InitDbBase();

    private static string InitDbBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragdb-{Guid.NewGuid():N}");
        RagDatabase.BaseDir = () => dir;
        return dir;
    }

    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public ProjectIndexServiceWatcherTests()
    {
        _ = DbBase; // forces static init before the first RagDatabase
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"rag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* nettoyage best-effort */ }
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

    private ProjectIndexService NewService(FakeInferenceProvider provider)
    {
        var config = new InferpalConfig { RagEnabled = true };
        var svc = new ProjectIndexService(provider, config, new LspSemanticProvider())
        {
            DebounceMs = 100, // the real 5 s debounce would make the tests far too slow
        };
        _services.Add(svc);
        return svc;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        var suffix = state is null ? string.Empty : $" (state: {state()})";
        Assert.Fail($"timeout en attendant : {what}{suffix}");
    }

    [Fact]
    public async Task FileRewrittenDuringInitialPass_IsReindexedAfterFinalSave()
    {
        var file = Path.Combine(_root, "Alpha.cs");
        // Enough lines to pass MinChunkLines (tiny fragments are ignored).
        await File.WriteAllTextAsync(file, SampleClass("AlphaOriginal"));

        var rewritten = false;
        var provider = new FakeInferenceProvider();
        provider.OnEmbedding = _ =>
        {
            // First embedding requested = the pass has already READ Alpha.cs (old content).
            // Rewrite the file now, in the middle of the pass, and give the FileSystemWatcher time
            // to deliver the event before the pass ends.
            if (!rewritten)
            {
                rewritten = true;
                File.WriteAllText(file, SampleClass("AlphaRewritten"));
                Thread.Sleep(500);
            }
            return [0.1f, 0.2f, 0.3f];
        };

        var svc = NewService(provider);
        svc.StartIndexing(_root);

        // Before the fix, the watcher did not exist during the pass: the rewrite was
        // invisible et l'index gardait AlphaOriginal jusqu'au prochain boot (timeout ici).
        await WaitUntilAsync(async () =>
        {
            var chunks = await svc.GetFileChunksAsync(file, _root, CancellationToken.None);
            return chunks.Any(c => c.Content.Contains("AlphaRewritten"));
        }, "re-indexing of the file rewritten during the pass (post-SaveAsync drain)", () => svc.Status);
    }

    [Fact]
    public async Task WatcherReindex_ReusesEmbeddings_ForUnchangedChunks()
    {
        var file   = Path.Combine(_root, "Beta.cs");
        var source = SampleClass("Beta");
        await File.WriteAllTextAsync(file, source);

        int embedCalls = 0;
        var provider = new FakeInferenceProvider();
        provider.OnEmbedding = _ =>
        {
            Interlocked.Increment(ref embedCalls);
            return [0.5f, 0.5f];
        };

        var svc = NewService(provider);
        svc.StartIndexing(_root);

        await WaitUntilAsync(() => Task.FromResult(svc.Status.Contains('✅')), "fin de la passe initiale", () => svc.Status);
        var afterPass = embedCalls;
        Assert.True(afterPass > 0, "the initial pass should have embedded at least one chunk");
        var before = (await svc.GetFileChunksAsync(file, _root, CancellationToken.None)).First();

        // Touch: identical content, fresh LastWrite -> the watcher re-indexes the file.
        await File.WriteAllTextAsync(file, source);

        // Re-indexing replaces the chunk instances: new reference + same hash = the watcher path
        // did run (a deterministic observation, with no blind sleeping).
        await WaitUntilAsync(async () =>
        {
            var now = (await svc.GetFileChunksAsync(file, _root, CancellationToken.None)).FirstOrDefault();
            return now is not null
                && !ReferenceEquals(now, before)
                && now.ContentHash == before.ContentHash;
        }, "re-indexing by the watcher after a save with no change");

        // Unchanged hash -> embedding reused, zero extra calls to the backend.
        Assert.Equal(afterPass, embedCalls);
        var after = await svc.GetFileChunksAsync(file, _root, CancellationToken.None);
        Assert.All(after, c => Assert.NotNull(c.Embedding));
    }
}
