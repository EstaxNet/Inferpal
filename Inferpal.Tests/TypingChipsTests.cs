using System.IO;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The chips of a draft — the code the search finds while the person types — come from one search in the Core, the
/// same for Visual Studio's 🔮 chips and VS Code's dashed ones: the file found, the lines its excerpts span, the excerpts
/// said to be excerpts; nothing for a draft too short, a command, or a search turned off.
/// </summary>
public sealed class TypingChipsTests : IDisposable
{
    private readonly string _root;
    private readonly List<ProjectIndexService> _services = [];

    public TypingChipsTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"typing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private async Task<(ProjectIndexService Index, InferpalConfig Config, string File)> IndexedAsync(int methods, int debounceMs = 100)
    {
        var file = Path.Combine(_root, "Cart.cs");
        await File.WriteAllTextAsync(file, string.Join('\n', new[] { "public class Cart", "{" }
            .Concat(Enumerable.Range(1, methods).SelectMany(i => new[] { $"    public decimal Total{i}(decimal x)", "    {", $"        return x * {i};", "    }", "" }))
            .Append("}")));
        var config = new InferpalConfig { RagEnabled = true, RagEmbeddingModel = "embed-model" };
        var index = new ProjectIndexService(new FakeInferenceProvider { Embedding = [0.1f, 0.2f, 0.3f] }, config,
                                            new LspSemanticProvider()) { DebounceMs = debounceMs };
        _services.Add(index);
        index.StartIndexing(_root);
        for (var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30); DateTime.UtcNow < deadline; await Task.Delay(50))
            if (!index.IsIndexing && index.ChunkCount > 0) return (index, config, file);
        Assert.Fail("timed out waiting for the index");
        return default;
    }

    [Fact]
    public async Task ADraft_GivesTheFileTheSearchFinds_WithItsExcerpts()
    {
        var (index, config, file) = await IndexedAsync(methods: 40);

        var chips = await RagAutoContext.WhileTypingAsync(index, config, "why does the cart total ignore the discount", CancellationToken.None);

        var chip = Assert.Single(chips);
        Assert.Equal(file, chip.Path);
        // A long file the excerpts do not cover: their lines are named, and the content says what it is.
        Assert.Matches(@"^\d+–\d+(, \d+–\d+)*$", chip.Lines);
        Assert.StartsWith("Excerpts of Cart.cs", chip.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cart total")]                                  // too short to be worth a search
    [InlineData("/explain why the cart total ignores it")]      // a command
    public async Task NoChip_ForADraftThatIsNotAQuestionYet(string draft)
    {
        var (index, config, _) = await IndexedAsync(methods: 3);
        Assert.NotEmpty(await RagAutoContext.WhileTypingAsync(index, config, "why does the cart total ignore the discount", CancellationToken.None)); // witness

        Assert.Empty(await RagAutoContext.WhileTypingAsync(index, config, draft, CancellationToken.None));
    }

    /// <summary>A file changed since it was indexed is not offered: its chunks are the code from before.</summary>
    [Fact]
    public async Task NoChip_ForAFileChangedSinceItWasIndexed()
    {
        // A re-index that waits long enough to be read pending.
        var (index, config, file) = await IndexedAsync(methods: 3, debounceMs: 60_000);
        const string draft = "why does the cart total ignore the discount";
        Assert.NotEmpty(await RagAutoContext.WhileTypingAsync(index, config, draft, CancellationToken.None));   // witness

        await File.AppendAllTextAsync(file, "\n// changed\n");
        index.OnFileChangedCore(file);
        Assert.Contains(file, index.NotYetReindexed);                                                            // witness

        Assert.Empty(await RagAutoContext.WhileTypingAsync(index, config, draft, CancellationToken.None));
    }

    [Fact]
    public async Task NoChip_WhenTheSearchIsOff()
    {
        var (index, config, _) = await IndexedAsync(methods: 3);
        config.RagEnabled = false;

        Assert.Empty(await RagAutoContext.WhileTypingAsync(index, config, "why does the cart total ignore the discount", CancellationToken.None));
    }

    // ── Both editors draw them from it ────────────────────────────────────────

    private static string Code(params string[] parts) =>
        ConventionCoverageTests.CodeOnly(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), .. parts]));

    private static string Ts(params string[] parts) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine([ConversationPersistenceSilenceTests.RepoRoot(), "vscode", "src", .. parts])));

    /// <summary>Visual Studio's chips and the host's come from the one search; Visual Studio schedules nothing for a
    /// keystroke that cannot have chips, keeps a chip the person removed away from the draft until it is sent, and never
    /// counts a 🔮 chip — excerpts — as its file attached (the turn's relevant code would leave the file's other chunks
    /// out).</summary>
    [Fact]
    public void BothEditors_AskTheCoresSearch()
    {
        var mentions = Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs");
        Assert.Contains("RagAutoContext.WhileTypingAsync(_indexService, _config, prompt, cts.Token)", mentions, StringComparison.Ordinal);
        Assert.Contains("if (!Services.Rag.RagAutoContext.MayChip(_indexService, _config, prompt)) return;", mentions, StringComparison.Ordinal);
        Assert.Contains("if (_removedAutoChips.Contains(path)) continue;", mentions, StringComparison.Ordinal);
        Assert.Contains("_removedAutoChips.Add(path);", mentions, StringComparison.Ordinal);
        Assert.Contains("_removedAutoChips.Clear();", Code("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs"), StringComparison.Ordinal);
        // A 🔮 chip counts as its file for the turn's relevant code: not counted, the same search sent its chunks again.
        Assert.Contains(".Where(a => !string.IsNullOrEmpty(a.SourcePath) && !a.IsCut)",
                        Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Attachments.cs"), StringComparison.Ordinal);
        Assert.Contains("RagAutoContext.WhileTypingAsync(s.Index, s.Config, p.Text ?? string.Empty, ct)",
                        Code("Inferpal.Host", "HostSlashCommands.cs"), StringComparison.Ordinal);
    }

    /// <summary>VS Code: the draft goes to the extension at each keystroke; its chips go at once, the search runs once
    /// typing pauses and is cancelled as the person types on or sends; a file attached by hand keeps its own chip; the
    /// chips are drawn apart, with what they are.</summary>
    [Fact]
    public void VsCode_DrawsTheDraftsChips()
    {
        var webview = Ts("webview", "main.ts");
        Assert.Contains("post({ type: 'draft', text: promptEl.value });", webview);
        // A text the panel sets itself is a new draft too — but as a message leaves (its chips leave with it).
        Assert.Matches(@"function setPrompt\(text: string\): void \{\s*promptEl\.value = text;\s*post\(\{ type: 'draft', text \}\);", webview);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(webview, @"promptEl\.value = '';").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(webview, @"promptEl\.value = ").Count);
        Assert.Contains("el.className = chip.auto ? 'chip auto' : 'chip';", webview);
        Assert.Contains("note.textContent = t('chipsFoundWhileTyping');", webview);

        var provider = Ts("chatViewProvider.ts");
        Assert.Matches(@"private draftChanged\(text: string\): void \{\s*this\.cancelDraftSearch\(\);\s*if \(this\.pendingAttachments\.some\(\(a\) => a\.auto\)\) \{", provider);
        Assert.Contains("this.draftTimer = setTimeout(() => void this.searchDraft(host, text), 800);", provider);
        Assert.Contains("if (search.token.isCancellationRequested || this.draftSearch !== search) {", provider);
        // "Same file?" as the file system answers it, against the chips attached by hand and the ones removed.
        Assert.Contains("const held = (p: string | undefined): boolean => p !== undefined && samePath(p, chip.path);", provider);
        Assert.Contains("if (this.pendingAttachments.some((a) => held(a.sourcePath) || held(a.origin)) || this.removedAutoChips.some(held)) {", provider);
        Assert.Contains("this.removedAutoChips.push(removed.origin);", provider);
        // Capped as every chip is.
        Assert.Contains("const capped = capAttachment(chip.content, 60_000);", provider);
        // The draft's chips leave with the draft; a gesture's message (Explain from the menu) does not take them.
        Assert.Matches(@"const before = this\.turnsEnded;[^{]*if \(typed\) \{\s*this\.cancelDraftSearch\(\);\s*this\.removedAutoChips = \[\];\s*\} else if \(this\.pendingAttachments\.some\(\(a\) => a\.auto\)\) \{\s*this\.pendingAttachments = this\.pendingAttachments\.filter\(\(a\) => !a\.auto\);", provider);
        // A redraw draws them as they are.
        Assert.Contains("chips: this.chipsShown(),", provider);
        // Its file counts as attached unless the chip is cut, as Visual Studio's 🔮 chip.
        Assert.Contains("sourcePath: capped.cut ? undefined : chip.path,", provider);
        Assert.Contains("origin: chip.path,", provider);
    }
}
