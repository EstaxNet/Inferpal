using System.Diagnostics;
using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The C# semantic index is a cache, and a cache is only safe because something invalidates it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Its own remark says so — and then names the hole:</b> <i>"The cache is only safe BECAUSE
/// something invalidates it — ProjectIndexService's file watcher calls Update […] A cache without
/// invalidation would answer about code that no longer exists, the exact silent wrongness this class
/// exists to remove"</i>, followed by <i>"⚠ With RAG disabled there is no watcher, so the index would
/// go stale"</i>. The watcher was armed inside the indexing pass, and the pass only starts when
/// <c>ragEnabled</c> is on — a setting the user can turn off. With it off the compilation was built
/// on the first C# analysis of the session and frozen for the life of the process.
/// </para>
/// <para>
/// ⚠ <b>And it is the section the tool tells the model to trust.</b> <c>analyze_impact</c> renders
/// the semantic hits under <i>"resolved by the C# compiler — unlike the sections above, which match
/// names"</i>: the heuristic sections are recomputed from disk on every call, and the authoritative
/// one was the stale one. <c>rename_symbol</c> is spared a wrong write only by its own
/// <c>Stale</c> guard, which then drops it to the syntactic path this class exists to replace.
/// </para>
/// <para>
/// The realistic shape is not an exotic one: the agent writes a file and then asks what it broke.
/// </para>
/// </remarks>
public sealed class SemanticIndexInvalidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"seminval-{Guid.NewGuid():N}");
    private readonly List<ProjectIndexService> _services = [];

    public SemanticIndexInvalidationTests()
    {
        TestRagStore.Redirect();
        Directory.CreateDirectory(_root);
        CSharpSemanticIndex.ResetCacheForTests();
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        CSharpSemanticIndex.ResetCacheForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A service with RAG <b>off</b> — the configuration the defect lives in.</summary>
    private ProjectIndexService NewServiceWithoutRag()
    {
        var svc = new ProjectIndexService(
            new FakeInferenceProvider(), new InferpalConfig { RagEnabled = false }, new LspSemanticProvider());
        _services.Add(svc);
        return svc;
    }

    private static string Caller(string name) =>
        $"namespace App; public class {name} {{ public void Go() {{ new Target().Handle(); }} }}";

    /// <summary>
    /// 30 s, not 5: the runner compiles and then runs two test series in parallel, so a few seconds
    /// here are not a few seconds of wall clock. Lengthening it hides nothing — the assertion is the
    /// state, not the delay.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string>? state = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail($"Timeout waiting for {what}. {state?.Invoke()}");
    }

    [Fact]
    public async Task WithRagOff_TheSemanticIndex_SeesAFileCreatedAfterItWasBuilt()
    {
        File.WriteAllText(Path.Combine(_root, "Target.cs"),
            "namespace App; public class Target { public void Handle() { } }");
        File.WriteAllText(Path.Combine(_root, "Caller.cs"), Caller("Caller"));

        var svc = NewServiceWithoutRag();
        svc.SetRoot(_root);

        var index = CSharpSemanticIndex.ForWorkspace(_root);
        // WITNESS: the index really answers, and it is now built and cached for this root.
        Assert.Single(index.FindReferences("Handle").References);

        File.WriteAllText(Path.Combine(_root, "Caller2.cs"), Caller("Caller2"));
        File.WriteAllText(Path.Combine(_root, "Caller3.cs"), Caller("Caller3"));

        await WaitUntilAsync(
            () => index.FindReferences("Handle").References.Count == 3,
            "the semantic index to see the two new callers",
            () => $"references now = {index.FindReferences("Handle").References.Count}, files = {index.FileCount}");
    }

    [Fact]
    public async Task WithRagOff_TheSemanticIndex_ForgetsAReferenceThatWasDeleted()
    {
        File.WriteAllText(Path.Combine(_root, "Target.cs"),
            "namespace App; public class Target { public void Handle() { } }");
        File.WriteAllText(Path.Combine(_root, "Caller.cs"), Caller("Caller"));

        var svc = NewServiceWithoutRag();
        svc.SetRoot(_root);

        var index = CSharpSemanticIndex.ForWorkspace(_root);
        Assert.Single(index.FindReferences("Handle").References);   // witness

        // The other half of staleness, and the one that makes `analyze_impact` say "safe to
        // refactor": a call the agent itself has just removed.
        File.WriteAllText(Path.Combine(_root, "Caller.cs"),
            "namespace App; public class Caller { public void Go() { } }");

        await WaitUntilAsync(
            () => index.FindReferences("Handle").References.Count == 0,
            "the semantic index to forget the removed call",
            () => $"references now = {index.FindReferences("Handle").References.Count}");
    }

    [Fact]
    public async Task ASnapshotUnderDotInferpal_NeverEntersTheIndex_EvenThroughTheWatcher()
    {
        // REFERENCE ARM: arming the watcher earlier must not start letting the excluded tree in.
        // Every agent write leaves a `.inferpal/history/…_Foo.cs` copy, and a second `class Target`
        // in the compilation is how `rename_symbol` and `analyze_impact` answered about the copy.
        File.WriteAllText(Path.Combine(_root, "Target.cs"),
            "namespace App; public class Target { public void Handle() { } }");

        var svc = NewServiceWithoutRag();
        svc.SetRoot(_root);

        var index = CSharpSemanticIndex.ForWorkspace(_root);
        // The query is what builds the compilation — FileCount alone reads the mutable field and
        // would be 0 here, turning every assertion below into a green that measures nothing.
        Assert.Single(index.FindDeclarations("Target"));
        var before = index.FileCount;
        Assert.True(before > 0, "the index did not parse anything: this test would measure nothing.");

        var history = Path.Combine(_root, ".inferpal", "history");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "2026-09-18_10-00-00-000_abcd1234_Target.cs"),
            "namespace App; public class Target { public void Handle() { } }");

        // A positive witness that the watcher is live at all, written after the snapshot: once the
        // ordinary file has landed, the snapshot has had its chance too.
        File.WriteAllText(Path.Combine(_root, "Caller.cs"), Caller("Caller"));
        await WaitUntilAsync(
            () => index.FindReferences("Handle").References.Count == 1,
            "the watcher to deliver the ordinary file");

        Assert.Equal(before + 1, index.FileCount);   // Caller.cs only — never the snapshot
    }
}
