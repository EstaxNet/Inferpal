using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A source file the indexing pass dropped because it is <b>too large</b> is named by
/// <c>/index</c>, like the folder it could not list.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Same argument as <see cref="IndexSkippedFolderTests"/>, and it is what makes this worse than a
/// wrong tool report: <b>the index is PERSISTED</b>. A file over
/// <c>CodeChunker.MaxFileSizeBytes</c> is absent from <c>search_codebase</c> and from the per-turn
/// auto-context for as long as the database lives, restarts included. Measured before the fix, on a
/// tree of two C# files of which one is oversized, <c>/index</c> answered
/// <c>Statut : RAG: ✅ 1 chunks</c> — a green tick and a count that is arithmetically consistent,
/// because a file that was never taken is missing from every total rather than subtracted from one.
/// </para>
/// <para>
/// ⚠ The rule was already written, and held by <b>one</b> of the two sites that drop by size:
/// <c>rename_symbol</c> counts its own into its <c>ScanCoverage</c>, under a comment that says it in
/// as many words — <i>"what was NOT looked at travels with the result, like in every other scanning
/// tool"</i>. The pass was the other scanning tool.
/// </para>
/// <para>
/// The third site, <c>WorkspaceSymbolScanner</c>, drops by size in silence too and is deliberately
/// left alone: its only consumer builds benchmark questions, so it chooses material instead of
/// answering a question about the repository.
/// </para>
/// </remarks>
public sealed class IndexOversizeSilenceTests : IDisposable
{
    private readonly string _root;
    private readonly string _huge;
    private readonly List<ProjectIndexService> _services = [];

    public IndexOversizeSilenceTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"idxbig-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Small.cs"), SampleClass("Small"));

        // A .cs the pass would index if it were not for its size: same extension, same shape,
        // padded past the cap with comments so it stays valid C#.
        _huge = Path.Combine(_root, "src", "Huge.cs");
        var padding = string.Join('\n', Enumerable.Range(0, 6_000)
            .Select(i => $"// padding line {i:D5} — this file exists to cross the indexing size cap"));
        File.WriteAllText(_huge, SampleClass("Huge") + "\n" + padding);
    }

    public void Dispose()
    {
        foreach (var svc in _services) svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

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

    private ProjectIndexService NewService()
    {
        var svc = new ProjectIndexService(new FakeInferenceProvider(),
                                          new InferpalConfig { RagEnabled = true },
                                          new LspSemanticProvider());
        _services.Add(svc);
        return svc;
    }

    /// <summary>
    /// The witness, both halves: the file really is past the cap, and it really is one the pass
    /// would otherwise index. Without the second, this would be measuring an unsupported extension.
    /// </summary>
    private void AssertTheBigFileIsExcludedBySizeAlone()
    {
        Assert.True(new FileInfo(_huge).Length > CodeChunker.MaxFileSizeBytes);
        Assert.Contains(Path.GetExtension(_huge), CodeChunker.SupportedExtensions,
                        StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ 30 s, not a couple of seconds: the runner compiles and then plays two test series in
    /// parallel. This test WAITS; it does not measure an overrun.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string> state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}. State: {state()}");
    }

    // ── What the pass records ────────────────────────────────────────────────

    [Fact]
    public async Task TheIndexingPass_CountsTheFilesItDroppedForSize()
    {
        AssertTheBigFileIsExcludedBySizeAlone();

        var svc = NewService();
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => svc.ChunkCount > 0 && !svc.IsIndexing,
                             "the pass indexes the small file", () => svc.Status);

        Assert.Equal(1, svc.SkippedBySize);
    }

    /// <summary>
    /// NEGATIVE WITNESS: a tree whose files all fit records nothing. Without it, a pass that always
    /// reported a dropped file would pass the test above while measuring nothing.
    /// </summary>
    [Fact]
    public async Task TheIndexingPass_OnATreeThatFits_CountsNothing()
    {
        File.Delete(_huge);
        Assert.False(File.Exists(_huge));

        var svc = NewService();
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => svc.ChunkCount > 0 && !svc.IsIndexing,
                             "the pass indexes the small file", () => svc.Status);

        Assert.Equal(0, svc.SkippedBySize);
    }

    // ── What the user reads ──────────────────────────────────────────────────

    [Fact]
    public async Task SlashIndex_SaysHowManyFilesWereTooLarge()
    {
        AssertTheBigFileIsExcludedBySizeAlone();

        var svc = NewService();
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => svc.ChunkCount > 0 && !svc.IsIndexing,
                             "the pass ran", () => svc.Status);

        var report = Report(svc);

        // Positive assertion on the localized sentence, and on the cap converted ONCE: asserting
        // the absence of something else would also pass on an empty report.
        Assert.Contains(
            Inferpal.Localization.Strings.IndexFilesTooLarge(1, CodeChunker.MaxFileSizeKilobytes),
            report);
        // Witness: this really is the full report, not the "not started yet" path.
        Assert.Contains(svc.ChunkCount.ToString("N0"), report);
    }

    /// <summary>Reference arm: a tree that fits says nothing about size.</summary>
    [Fact]
    public async Task SlashIndex_OnATreeThatFits_SaysNothingAboutSize()
    {
        File.Delete(_huge);

        var svc = NewService();
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => svc.ChunkCount > 0 && !svc.IsIndexing,
                             "the pass ran", () => svc.Status);

        var report = Report(svc);

        Assert.DoesNotContain(
            Inferpal.Localization.Strings.IndexFilesTooLarge(1, CodeChunker.MaxFileSizeKilobytes),
            report);
        Assert.Contains(svc.ChunkCount.ToString("N0"), report);
    }

    private static string Report(ProjectIndexService svc) =>
        Inferpal.Services.Commands.IndexCommandHandler.Handle(
            svc, new InferpalConfig { RagEnabled = true }, ["/index"], svc.RootDir);
}
