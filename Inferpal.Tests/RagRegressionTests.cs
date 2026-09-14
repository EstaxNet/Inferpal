using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the semantic index keeps, reuses and lets in — the states where it answers "✅" about
/// content that is no longer the right one.
/// </summary>
[Collection("Diagnostics")]
public sealed class RagRegressionTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];
    private readonly List<string> _extraDirs = [];

    public RagRegressionTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ragreg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        foreach (var d in _extraDirs.Append(_root))
            try { Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        "    public int One()   => 1;",
        "    public int Two()   => 2;",
        "}");

    private ProjectIndexService NewService(FakeInferenceProvider provider, InferpalConfig? config = null)
    {
        var svc = new ProjectIndexService(provider, config ?? new InferpalConfig { RagEnabled = true }, new LspSemanticProvider())
        {
            DebounceMs = 100,
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
        Assert.Fail($"timeout waiting for: {what}" + (state is null ? string.Empty : $" (state: {state()})"));
    }

    // ── R1 — vectors from another embedding model ──────────────────────────────

    /// <summary>
    /// Switching the embedding model and running <c>/index rebuild</c> took the OLD vectors back as
    /// soon as a chunk's hash matched: incompatible dimensions (zero cosine everywhere) or noise
    /// scores, under a "✅" status.
    /// </summary>
    [Fact]
    public async Task ChangingTheEmbeddingModel_ReEmbedsOnTheNextPass_InsteadOfReusingOldVectors()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "Alpha.cs"), SampleClass("Alpha"));
        var embeddings = 0;
        var provider = new FakeInferenceProvider
        {
            OnEmbedding = _ => { Interlocked.Increment(ref embeddings); return [0.1f, 0.2f]; },
        };
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "model-a" };
        var svc = NewService(provider, config);

        svc.StartIndexing(_root);
        await WaitUntilAsync(() => Task.FromResult(svc.Status.Contains('✅')), "first pass", () => svc.Status);
        var afterFirst = Volatile.Read(ref embeddings);
        Assert.True(afterFirst > 0, "witness: the first pass did embed");

        config.RagEmbeddingModel = "model-b";
        svc.StartIndexing(_root);

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref embeddings) > afterFirst),
                             "re-embedding with the new model", () => svc.Status);
    }

    // ── R7 — embedding circuit open during a re-index ──────────────────────────

    /// <summary>
    /// Embedding circuit open while a file is re-indexed: a <c>break</c> left the loop BEFORE the
    /// following chunks were reused, and the unchanged chunks lost their vector, in memory and on disk.
    /// </summary>
    [Fact]
    public async Task AFileSavedWhileTheEmbeddingCircuitIsOpen_KeepsTheVectorsOfItsUnchangedChunks()
    {
        var file = Path.Combine(_root, "Pair.cs");
        await File.WriteAllTextAsync(file, SampleClass("First") + "\n" + SampleClass("Second"));
        var provider = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var svc = NewService(provider);

        svc.StartIndexing(_root);
        await WaitUntilAsync(() => Task.FromResult(svc.Status.Contains('✅')), "first pass", () => svc.Status);

        provider.IsEmbeddingCircuitOpen = true;   // before the write: the watcher may re-index at once
        // Same line count: First's chunk changes, Second's keeps its hash and its start line.
        await File.WriteAllTextAsync(file, SampleClass("Fir1t") + "\n" + SampleClass("Second"));
        svc.OnFileChangedCore(file);

        await WaitUntilAsync(async () =>
            (await svc.GetFileChunksAsync(file, _root, CancellationToken.None)).Any(c => c.Content.Contains("Fir1t")),
            "re-index of the saved file", () => svc.Status);

        var second = (await svc.GetFileChunksAsync(file, _root, CancellationToken.None))
            .Where(c => c.Content.Contains("Second", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(second);
        Assert.All(second, c => Assert.NotNull(c.Embedding));
    }

    // ── R3 — what the watcher lets into the C# semantic index ──────────────────

    /// <summary>
    /// Every agent write creates <c>.inferpal/history/…_Foo.cs</c>; the watcher reported it to the C#
    /// index BEFORE testing the exclusions, and the stale copy entered the compilation — two
    /// <c>class Foo</c>, and <c>rename_symbol</c> / <c>analyze_impact</c> answered about the copy.
    /// </summary>
    [Fact]
    public void AHistorySnapshotOfACSharpFile_NeverEntersTheSemanticIndex()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "Foo.cs"), "public class Foo { public void M() { } }");
        var index = CSharpSemanticIndex.ForWorkspace(_root);
        index.Build();
        var before = index.FileCount;

        var history = Path.Combine(_root, ".inferpal", "history");
        Directory.CreateDirectory(history);
        var snapshot = Path.Combine(history, "2026-09-13_10-00-00-000_abcd1234_Foo.cs");
        File.WriteAllText(snapshot, "public class Foo { }");

        var svc = NewService(new FakeInferenceProvider());
        svc.SetRoot(_root);
        svc.OnFileChangedCore(snapshot);

        Assert.Equal(before, index.FileCount);
    }

    /// <summary><c>StartsWith(root)</c> without a separator routed <c>C:\dev\App2\x.cs</c> to the index
    /// of <c>C:\dev\App</c>.</summary>
    [Fact]
    public void AFileInASiblingFolderWhoseNameExtendsTheRoot_IsNotRoutedToThatIndex()
    {
        File.WriteAllText(Path.Combine(_root, "A.cs"), "public class A { }");
        var index = CSharpSemanticIndex.ForWorkspace(_root);
        index.Build();
        var before = index.FileCount;

        var sibling = _root + "2";
        Directory.CreateDirectory(sibling);
        _extraDirs.Add(sibling);
        var other = Path.Combine(sibling, "B.cs");
        File.WriteAllText(other, "public class B { }");

        CSharpSemanticIndex.NotifyFileChanged(other);

        Assert.Equal(before, index.FileCount);
    }
}
