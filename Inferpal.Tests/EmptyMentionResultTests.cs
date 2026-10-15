using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Lsp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@code</c> and <c>@tree</c> attach what their tool found, or say why there is nothing — never a chip whose content
/// is "no results" or "could not locate a root".
/// </summary>
/// <remarks>
/// Both editors attached whatever the tool's text was: a search that found nothing made a 🔮 chip holding "No results",
/// and a workspace with nothing to map a 🌲 chip holding "❌ Could not locate solution or project root." — the question
/// went to the model as if it carried the code or the tree it was about. The decision is read on the tool's state (the
/// hit count, the root), never on its sentence.
/// </remarks>
public sealed class EmptyMentionResultTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public EmptyMentionResultTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"empty-mention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Cart.cs"), string.Join('\n',
            "public class Cart",
            "{",
            "    public int CartTotal()    => 1;",
            "    public int CartCount()    => 2;",
            "    public int CartDiscount() => 3;",
            "    public int CartTax()      => 4;",
            "    public int CartShipping() => 5;",
            "}"));
    }

    public void Dispose()
    {
        foreach (var svc in _services) svc.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>⚠ 30 s: the runner plays two test series in parallel. This test waits; it measures no overrun.</summary>
    private async Task<SemanticSearchTool> IndexedSearchAsync()
    {
        var svc = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true },
                                          new LspSemanticProvider());
        _services.Add(svc);
        svc.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!(svc.Status.StartsWith("RAG: ✅", StringComparison.Ordinal) && !svc.IsIndexing))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the pass did not finish (status: {svc.Status})");
            await Task.Delay(50);
        }
        Assert.True(svc.ChunkCount > 0, "witness: the workspace is indexed");
        return new SemanticSearchTool(svc, new FakeInferenceProvider(), new InferpalConfig { RagEnabled = true });
    }

    private static Task<SemanticSearchTool.Answer> AnswerAsync(SemanticSearchTool search, string query) =>
        search.AnswerAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { query })).RootElement, CancellationToken.None);

    [Fact]
    public async Task ACodeSearchThatFindsNothing_IsANotice_NotAChip()
    {
        var search = await IndexedSearchAsync();

        var answer = await AnswerAsync(search, "zebra");

        Assert.Equal(0, answer.Hits);
        var mention = MentionController.CodeMention(answer, "🔮 zebra");
        Assert.Null(mention.Label);
        Assert.Null(mention.Content);
        Assert.NotNull(mention.Notice);
        Assert.StartsWith(Strings.MentionCodeNothingFound(string.Empty), mention.Notice);
        Assert.Contains(Strings.RagNoResults("zebra"), mention.Notice);
    }

    [Fact]
    public async Task ACodeSearchThatFindsCode_IsAttached_UnderItsLabel()
    {
        // Reference arm: hits make the chip, holding the report the model reads.
        var search = await IndexedSearchAsync();

        var answer = await AnswerAsync(search, "CartTotal");

        Assert.True(answer.Hits > 0, answer.Text);
        var mention = MentionController.CodeMention(answer, "🔮 CartTotal");
        Assert.Null(mention.Notice);
        Assert.Equal("🔮 CartTotal", mention.Label);
        Assert.Contains("CartTotal", mention.Content);
        Assert.Equal(answer.Text, await search.ExecuteAsync(
            JsonDocument.Parse("""{"query":"CartTotal"}""").RootElement, CancellationToken.None));   // the tool serves the same text
    }

    [Fact]
    public void AMapWithNoRoot_IsANotice_AndAMap_IsAttached()
    {
        var none = MentionController.TreeMention(null, "🌲 @tree");
        Assert.Null(none.Label);
        Assert.Null(none.Content);
        Assert.Equal(Strings.MentionTreeNoRoot, none.Notice);

        // Reference arm.
        var map = MentionController.TreeMention("# Project map\n- src/", "🌲 @tree");
        Assert.Null(map.Notice);
        Assert.Equal("🌲 @tree", map.Label);
        Assert.Equal("# Project map\n- src/", map.Content);
    }

    [Fact]
    public void BothEditors_DecideCodeAndTreeThroughThePresenters()
    {
        var root = ConversationPersistenceSilenceTests.RepoRoot();
        var vm   = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"));
        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSlashCommands.cs"));

        foreach (var (name, code) in new[] { ("Visual Studio", vm), ("VS Code host", host) })
        {
            Assert.True(code.Contains("MentionController.CodeMention(", StringComparison.Ordinal), $"{name}: @code is not decided");
            Assert.True(code.Contains("CodeSearch.AnswerAsync(", StringComparison.Ordinal), $"{name}: @code no longer reads the answer");
            Assert.True(code.Contains("MentionController.TreeMention(", StringComparison.Ordinal), $"{name}: @tree is not decided");
            Assert.True(code.Contains("ProjectMap.TryGenerateMapAsync(", StringComparison.Ordinal), $"{name}: @tree no longer reads the map");
            // The tools' TEXT is no longer what these mentions attach.
            Assert.DoesNotContain("\"search_codebase\"", code);
            Assert.DoesNotContain("\"generate_project_map\"", code);
        }
    }
}
