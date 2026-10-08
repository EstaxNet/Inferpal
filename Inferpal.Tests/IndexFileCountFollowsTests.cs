using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The index's "N files" — in <c>/index</c>'s status and on the settings card — is the files it holds chunks of NOW, not
/// the files the last full pass listed.
/// </summary>
/// <remarks>
/// ⚠ The count was set once per full pass, failed files included: deleted or moved files, saves that emptied a file,
/// left it unchanged while the chunk count and the "updated at" time beside it moved.
/// </remarks>
public sealed class IndexFileCountFollowsTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public IndexFileCountFollowsTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"rag-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        $"    public int One()   => 1; // {name}",
        $"    public int Two()   => 2; // {name}",
        $"    public int Three() => 3; // {name}",
        $"    public int Four()  => 4; // {name}",
        $"    public int Five()  => 5; // {name}",
        $"    public int Six()   => 6; // {name}",
        "}");

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, Func<string> state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"timed out waiting for: {what} (state: {state()})");
    }

    [Fact]
    public async Task ADeletedFile_LeavesTheCount()
    {
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
            await File.WriteAllTextAsync(Path.Combine(_root, name + ".cs"), SampleClass(name));
        var svc = new ProjectIndexService(new FakeInferenceProvider { Embedding = [0.1f, 0.2f] },
                                          new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed-model" },
                                          new LspSemanticProvider()) { DebounceMs = 100 };
        _services.Add(svc);
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => Task.FromResult(svc.Status.Contains('✅')), "the pass is finished", () => svc.Status);

        // Reference arm: after the pass, the pass and the index agree.
        Assert.Contains("from 3 files", svc.Status);
        Assert.Equal(3, (await svc.SnapshotAsync(CancellationToken.None)).Files);

        File.Delete(Path.Combine(_root, "Beta.cs"));

        await WaitUntilAsync(async () => (await svc.SnapshotAsync(CancellationToken.None)).Files == 2,
                             "the card counts the files left", () => svc.Status);
        await WaitUntilAsync(() => Task.FromResult(svc.Status.Contains("from 2 files")),
                             "the status counts the files left", () => svc.Status);
    }
}
