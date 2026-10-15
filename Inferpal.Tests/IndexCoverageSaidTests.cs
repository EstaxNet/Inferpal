using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the index does not hold reaches the model that searches it — not only <c>/index</c> — and a file the pass could
/// not read keeps what the index held for it.
/// </summary>
/// <remarks>
/// <para>A file over the size cap (or a folder the pass could not list) was said by <c>/index</c> alone:
/// <c>search_codebase</c> served the persisted index as complete, and "nothing found" about a class in that file was
/// read as "it does not exist", across restarts.</para>
/// <para>A file the pass could not read — locked for a moment by a build writing it — was dropped by the pass's final
/// replacement: every chunk it had, in memory and on disk, gone until its next change.</para>
/// </remarks>
public sealed class IndexCoverageSaidTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public IndexCoverageSaidTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"idxcov-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Cart.cs"), SampleClass("Cart"));
        File.WriteAllText(Path.Combine(_root, "src", "Invoice.cs"), SampleClass("Invoice"));
    }

    public void Dispose()
    {
        foreach (var svc in _services) svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        $"    public int {name}One()   => 1;",
        $"    public int {name}Two()   => 2;",
        $"    public int {name}Three() => 3;",
        $"    public int {name}Four()  => 4;",
        $"    public int {name}Five()  => 5;",
        $"    public int {name}Six()   => 6;",
        "}");

    private ProjectIndexService NewService()
    {
        var svc = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true },
                                          new LspSemanticProvider());
        _services.Add(svc);
        return svc;
    }

    /// <summary>⚠ 30 s: the runner plays two test series in parallel. This test waits; it measures no overrun.</summary>
    private static async Task<ProjectIndexService> IndexedAsync(ProjectIndexService svc, string root)
    {
        svc.StartIndexing(root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
        return svc;
    }

    private static Task<string> SearchAsync(ProjectIndexService svc, string query) =>
        new SemanticSearchTool(svc, new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true })
            .ExecuteAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { query })).RootElement, CancellationToken.None);

    [Fact]
    public async Task ASearch_NamesTheFilesTooLargeToBeIndexed()
    {
        var huge = Path.Combine(_root, "src", "Huge.cs");
        File.WriteAllText(huge, SampleClass("Huge") + "\n" + string.Join('\n', Enumerable.Range(0, 6_000)
            .Select(i => $"// padding line {i:D5} — this file exists to cross the indexing size cap")));
        Assert.True(new FileInfo(huge).Length > CodeChunker.MaxFileSizeBytes);   // WITNESS

        var report = await SearchAsync(await IndexedAsync(NewService(), _root), "HugeOne");

        Assert.Contains($"1 file(s) over the {CodeChunker.MaxFileSizeKilobytes} KB indexing cap are not in the index: "
                      + Path.Combine("src", "Huge.cs"), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASearch_OfAnIndexThatHoldsEverything_SaysNothingAboutCoverage()
    {
        // Reference arm: a complete index carries no note — a note on every search would be the noise nobody reads.
        var report = await SearchAsync(await IndexedAsync(NewService(), _root), "CartOne");

        Assert.Contains("Cart.cs", report, StringComparison.Ordinal);   // WITNESS: the search ran and found
        Assert.DoesNotContain("indexing cap", report, StringComparison.Ordinal);
        Assert.DoesNotContain("the index is incomplete", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileLockedDuringAPass_KeepsItsChunks_AndIsSaidNotCaughtUp()
    {
        // FileShare.None is a mandatory lock on Windows only (an advisory flock elsewhere).
        if (!OperatingSystem.IsWindows()) return;

        var invoice = Path.Combine(_root, "src", "Invoice.cs");
        var first   = await IndexedAsync(NewService(), _root);
        var held    = first.ChunkCount;
        Assert.True(held > 0);
        first.Dispose();

        ProjectIndexService second;
        using (new FileStream(invoice, FileMode.Open, FileAccess.Read, FileShare.None))
            second = await IndexedAsync(NewService(), _root);

        Assert.Equal(held, second.ChunkCount);
        Assert.Contains(invoice, second.NotYetReindexed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Invoice.cs", await SearchAsync(second, "InvoiceOne"), StringComparison.Ordinal);
    }
}
