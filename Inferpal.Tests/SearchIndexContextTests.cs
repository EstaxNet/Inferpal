using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The memory the agent appends to keeps its newest notes when the prompt has to cut it.
/// </summary>
/// <remarks>
/// <c>memory.md</c> and <c>notes.md</c> are written by APPENDING — the newest entry is the last. Cut like every other
/// prompt file, from the end, a memory past its share of the budget kept its oldest notes and dropped the ones just
/// written, while <c>update_memory</c> had answered "saved": the next session never saw them.
/// </remarks>
public sealed class AppendedMemoryKeepsItsEndTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "inferpal-tests", $"memtail-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string Log(string first, string last) =>
        $"- {first}\n" + string.Concat(Enumerable.Range(0, 2_000).Select(i => $"- note {i} about the build cache\n")) + $"- {last}\n";

    private string Section(string file, PromptSectionKind kind, string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", file), content);
        return new SystemPromptBuilder(new InferpalConfig { ContextWindowSize = 8_192 })
            .BuildSections("base", projectRoot: _root).Single(s => s.Kind == kind).Content;
    }

    [Theory]
    [InlineData("memory.md", nameof(PromptSectionKind.Memory))]
    [InlineData("notes.md",  nameof(PromptSectionKind.Notes))]
    public void AnAppendedFilePastItsShare_KeepsItsNewestEntries(string file, string kindName)
    {
        var section = Section(file, Enum.Parse<PromptSectionKind>(kindName), Log("OLDEST-ENTRY", "NEWEST-ENTRY"));

        Assert.Contains("omitted", section, StringComparison.Ordinal);   // WITNESS: the file was cut
        Assert.Contains("NEWEST-ENTRY", section, StringComparison.Ordinal);
        Assert.DoesNotContain("OLDEST-ENTRY", section, StringComparison.Ordinal);
        // Cut on a line: the first kept line is a whole entry.
        var firstKept = section[(section.IndexOf("follow]", StringComparison.Ordinal) + "follow]".Length)..].TrimStart('\n');
        Assert.StartsWith("- note ", firstKept, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProjectContext_StillKeepsItsHead()
    {
        // Reference arm: a document that starts with its structure keeps the start.
        var section = Section("context.md", PromptSectionKind.ProjectContext, Log("OLDEST-ENTRY", "NEWEST-ENTRY"));

        Assert.Contains("OLDEST-ENTRY", section, StringComparison.Ordinal);
        Assert.DoesNotContain("NEWEST-ENTRY", section, StringComparison.Ordinal);
    }

    [Fact]
    public void AZeroShare_CutsEverything_WithoutThrowing()
    {
        Assert.StartsWith("[... the earliest", SystemPromptBuilder.CapSection("- a\n- b\n", "memory", 0, keepEnd: true),
                          StringComparison.Ordinal);
    }
}

/// <summary>
/// A re-index the site cut short keeps the pages it could not fetch.
/// </summary>
/// <remarks>
/// The new crawl REPLACED the site's stored pages: a re-index during rate limiting (the site answers 429 to the crawler
/// after a few pages) shrank a complete index to the pages fetched before the refusals, and the status said ✅ with the
/// smaller count — the refusals were counted, the loss was not.
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsPartialRecrawlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docspartial-{Guid.NewGuid():N}");

    public DocsPartialRecrawlTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string Root = "https://docs.example.com/guide/";
    private static readonly DocSite Site = DocSite.Create(Root, "Guide");

    private sealed class SiteHandler(Func<Dictionary<string, (HttpStatusCode Status, string Html)>> site) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, html) = site().TryGetValue(request.RequestUri!.AbsolutePath, out var page)
                ? page
                : (HttpStatusCode.NotFound, "<html><body>not found</body></html>");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }

    private static string Page(string title, params string[] links) =>
        $"<html><head><title>{title}</title></head><body><p>{title} explained at length, with enough words to index.</p>"
        + string.Concat(links.Select(l => $"<a href=\"{l}\">{l}</a>")) + "</body></html>";

    private static Dictionary<string, (HttpStatusCode, string)> Complete() => new()
    {
        ["/guide/"]        = (HttpStatusCode.OK, Page("Home", "/guide/install", "/guide/config")),
        ["/guide/install"] = (HttpStatusCode.OK, Page("Install")),
        ["/guide/config"]  = (HttpStatusCode.OK, Page("Config")),
    };

    private static async Task<(int Pages, string Status)> TwoPasses(Dictionary<string, (HttpStatusCode, string)> second)
    {
        var current = Complete();
        var client  = new FakeInferenceProvider { OnEmbedding = _ => [1f, 0f] };
        var service = new DocsIndexService(client, new InferpalConfig { RagEmbeddingModel = "embed" })
        {
            CrawlerForTests = () => new DocCrawler(new SiteHandler(() => current), (_, _) => Task.FromResult(false)),
        };

        await service.AddOrReindexAsync(Site, null, CancellationToken.None);
        Assert.Equal(3, (await service.SitesAsync()).Single().PageCount);   // WITNESS: the first pass indexed the site
        current = second;
        await service.AddOrReindexAsync(Site, null, CancellationToken.None);
        return ((await service.SitesAsync()).Single().PageCount, service.Status);
    }

    [Fact]
    public async Task ARecrawlTheSiteRefusedPartly_KeepsThePagesItCouldNotFetch()
    {
        var second = Complete();
        second["/guide/config"] = (HttpStatusCode.TooManyRequests, "<html><body>slow down</body></html>");

        var (pages, status) = await TwoPasses(second);

        Assert.Equal(3, pages);
        Assert.Contains("kept 1 page(s) from the previous index", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARecrawlThatRanToItsEnd_DropsAPageTheSiteRemoved()
    {
        // Reference arm: a page that is gone (404, nothing refused) leaves the index.
        var second = Complete();
        second.Remove("/guide/config");

        var (pages, status) = await TwoPasses(second);

        Assert.Equal(2, pages);
        Assert.DoesNotContain("kept", status, StringComparison.Ordinal);
    }
}

/// <summary>
/// A file whose path holds a space is attached in VS Code.
/// </summary>
/// <remarks>
/// The picker wrote the path raw and the extension read a typed mention up to the first space: <c>@docs/My Notes.md</c>
/// named <c>docs/My</c>, no file, and the file the person picked was not attached — with nothing said (measured by running
/// both readers under Node: <c>docs/probes/domaine7-recherche-index-contexte.md</c>). The extension has no TypeScript
/// test runner: these rules read its source.
/// </remarks>
public sealed class MentionWithASpaceTests
{
    [Fact]
    public void OneReaderReadsBothForms_AndThePickerQuotesAPathWithASpace()
    {
        var paths = WebviewRebuildTests.TsCode("mentionPaths.ts");
        var reader = WebviewRebuildTests.Body(paths, "export function mentionTokens(");
        Assert.Contains("prompt.matchAll(/@(?:\"([^\"\\n]+)\"|([^\\s@\"]+))/g)", reader, StringComparison.Ordinal);
        Assert.Contains("/\\s/.test(path) ? `@\"${path}\"` : `@${path}`",
                        WebviewRebuildTests.Body(paths, "export function mentionFor("), StringComparison.Ordinal);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var expand = WebviewRebuildTests.Body(provider, "private async expandMentions(prompt: string): Promise<{ text: string; paths: string[]; labels: string[] }>");
        Assert.Contains("mentionTokens(prompt)", expand, StringComparison.Ordinal);
        Assert.DoesNotContain("matchAll(", expand, StringComparison.Ordinal);

        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.Contains("const written = mentionFor(path);", WebviewRebuildTests.Body(webview, "function insertMention("),
                        StringComparison.Ordinal);
        Assert.Contains(".replace(COMMITTED_RE, mentionFor(rel) + ' ')", webview, StringComparison.Ordinal);
        Assert.DoesNotContain("'@' + rel", webview, StringComparison.Ordinal);
        Assert.DoesNotContain("'@' + path", webview, StringComparison.Ordinal);
    }
}
