using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A result count the model asked for and did not get is said, like every other cut that shapes a
/// conclusion.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>ClampedArgument</c> was written for the call-graph depth and wired into the two tools that
/// carried the defect that produced it — and left three other tools clamping a number the model had
/// written, in silence: <c>search_codebase</c> and <c>search_docs</c> on <c>top_k</c>,
/// <c>web_search</c> on <c>max_results</c>, all three to <c>1-10</c>. ⭐ <i>A fix that closes the
/// instance one saw leaves the class alive.</i>
/// </para>
/// <para>
/// ⚠ The cost is a CONCLUSION, not a smaller answer: asked for 25 and served 10, the report's own
/// header still reads <c>top 10</c>, and nothing distinguishes a list the tool cut from one the
/// index exhausted. The model concludes "this symbol appears in ten places" and stops looking — the
/// same failure the scan caps in this folder declare through <c>ScanCoverage</c>, made worse by the
/// fact that a clamp does not report a limit of the world but overrides an instruction.
/// </para>
/// <para>
/// ⚠ <c>fetch_url</c> clamps <c>max_chars</c> the same way and is deliberately NOT in here: its own
/// truncation line names both numbers (<i>"truncated to 50000 characters out of 183000 total"</i>),
/// so the fragment is already declared. That single exemption is also why this is an assertion by
/// name and not a scanned rule: one subject and one exemption measure nothing.
/// </para>
/// </remarks>
public sealed class ClampedSearchArgumentTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public ClampedSearchArgumentTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"clamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        // Comfortably more chunks than the clamp, so a served list of exactly ten is a CHOICE of
        // the tool and not the index running out — which is the whole point being measured.
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(_root, "src", $"Widget{i}.cs"), SampleClass($"Widget{i}"));
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        "    public int Widget()  => 1;",
        "    public int Two()     => 2;",
        "    public int Three()   => 3;",
        "    public int Four()    => 4;",
        "    public int Five()    => 5;",
        "    public int Six()     => 6;",
        "}");

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

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement;

    // ── search_codebase ───────────────────────────────────────────────────────

    private async Task<SemanticSearchTool> IndexedToolAsync()
    {
        var config = new InferpalConfig { RagEnabled = true, RagTopK = 5 };
        // One constant vector for every chunk: the cosine half then answers for all of them, so the
        // number served is decided by top_k alone.
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };

        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);
        index.StartIndexing(_root);
        await WaitUntilAsync(() => !index.IsIndexing && index.ChunkCount > 10,
                             "the pass indexes more chunks than the clamp allows", () => index.Status);

        return new SemanticSearchTool(index, client, config);
    }

    [Fact]
    public async Task SearchCodebase_AskedForMoreResultsThanItServes_SaysWhatItUsedInstead()
    {
        var tool = await IndexedToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"Widget","top_k":25}"""), CancellationToken.None);

        // WITNESS: the call really produced a report, not an early return — a "no results" branch
        // would satisfy a DoesNotContain and prove nothing.
        Assert.Contains("## Codebase search", report, StringComparison.Ordinal);

        Assert.Contains("'top_k' was 25", report, StringComparison.Ordinal);
        Assert.Contains("top_k=10", report, StringComparison.Ordinal);

        // ⚠ ABOVE the report, not under it: a caveat below a result is read after the result has
        // been believed.
        Assert.True(report.IndexOf("'top_k' was 25", StringComparison.Ordinal)
                  < report.IndexOf("## Codebase search", StringComparison.Ordinal),
                    "the notice must qualify the report it sits above, not trail it.");
    }

    [Fact]
    public async Task SearchCodebase_AskedForACountItAccepts_SaysNothing()
    {
        // REFERENCE ARM: without it, a build that printed the note on every call would pass the
        // test above while measuring nothing.
        var tool = await IndexedToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"Widget","top_k":4}"""), CancellationToken.None);

        Assert.Contains("## Codebase search", report, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k' was", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchCodebase_WithNoCountAtAll_SaysNothing()
    {
        // The second reference arm, and the one that could regress on its own: with `top_k` absent
        // the tool falls back to the user's `ragTopK` setting. NOTHING was asked for, so there is
        // nothing to report — a note there would blame the model for the user's configuration.
        var tool = await IndexedToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"Widget"}"""), CancellationToken.None);

        Assert.Contains("## Codebase search", report, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k' was", report, StringComparison.Ordinal);
    }

    // ── search_docs ───────────────────────────────────────────────────────────

    /// <summary>Enough text for the chunker to produce more chunks than the clamp allows.</summary>
    private static string Page(string marker) => string.Join(" ",
        Enumerable.Range(0, 20_000).Select(i => i % 30 == 0 ? marker : $"word{i % 17}"));

    private static async Task<SearchDocsTool> DocsToolAsync()
    {
        var config = new InferpalConfig { RagTopK = 5 };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };

        var docs = new DocsIndexService(client, config)
        {
            CrawlForTests = (_, _) => Task.FromResult(new List<DocCrawler.Page>
            {
                new("https://docs.example.com/a", "Retries",  Page("retries")),
                new("https://docs.example.com/b", "Timeouts", Page("retries")),
            }),
        };
        await docs.AddOrReindexAsync(DocSite.Create("https://docs.example.com/", "Example"),
                                     progress: null, CancellationToken.None);
        Assert.True(docs.ChunkCount > 10,
                    $"only {docs.ChunkCount} chunk(s) indexed: the clamp would not bite and this measures nothing.");

        return new SearchDocsTool(docs, client, config);
    }

    [Fact]
    public async Task SearchDocs_AskedForMoreResultsThanItServes_SaysWhatItUsedInstead()
    {
        var tool = await DocsToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"retries","top_k":25}"""), CancellationToken.None);

        Assert.Contains("## Documentation search", report, StringComparison.Ordinal);
        Assert.Contains("'top_k' was 25", report, StringComparison.Ordinal);
        Assert.Contains("top_k=10", report, StringComparison.Ordinal);
        Assert.True(report.IndexOf("'top_k' was 25", StringComparison.Ordinal)
                  < report.IndexOf("## Documentation search", StringComparison.Ordinal),
                    "the notice must qualify the report it sits above, not trail it.");
    }

    [Fact]
    public async Task SearchDocs_AskedForACountItAccepts_SaysNothing()
    {
        var tool = await DocsToolAsync();

        var report = await tool.ExecuteAsync(Raw("""{"query":"retries","top_k":4}"""), CancellationToken.None);

        Assert.Contains("## Documentation search", report, StringComparison.Ordinal);
        Assert.DoesNotContain("top_k' was", report, StringComparison.Ordinal);
    }

    // ── web_search ────────────────────────────────────────────────────────────

    /// <summary>
    /// The third site, asserted on its source: running it needs DuckDuckGo.
    /// </summary>
    /// <remarks>
    /// ⚠ The assertion reads the file <b>through the shared code-only reader</b>: the remark above
    /// the call names <c>ClampedArgument</c>, so a raw text scan would be green on the prose while
    /// the call itself had gone.
    /// </remarks>
    [Fact]
    public void WebSearch_ReportsTheResultCountItClamped()
    {
        var source = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Tools", "WebSearchTool.cs"));

        // WITNESS: the file really is the one that serves the results.
        Assert.Contains("max_results", source, StringComparison.Ordinal);

        Assert.Contains("ClampedArgument.Read(args, \"max_results\"", source, StringComparison.Ordinal);
        Assert.Contains("ClampedArgument.Above(maxNotice", source, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Inferpal.Core")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
