using System;
using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Debugging;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The lines a user writes in their settings - custom tools, command templates, MCP servers - that
/// the product could not read used to disappear without a word.
/// </summary>
/// <remarks>
/// The same class as the permission rules, and the same arbitration: skipping a faulty line rather
/// than throwing the whole set away is right; staying silent is not.
///
/// The most misleading case is not the typo: a custom tool whose NAME collides with a built-in one
/// is dropped ("built-in tools take priority", the arbitration is right) and the user is left
/// watching a tool they declared never being called. That one announces itself nowhere:
/// <c>customTools</c> is read by the MODEL, not typed by the human.
/// </remarks>
[Collection("Diagnostics")]
public class ConfigLineSilenceTests
{
    private static string[] Notes(string context) =>
        [.. Diagnostics.Snapshot().Where(e => e.Context == context).Select(e => e.Detail)];

    [Fact]
    public void AMalformedTemplateLine_IsRecorded()
    {
        Diagnostics.Clear();

        var parsed = SlashCommandRouter.ParseUserTemplates("/good=some text\nbad, no slash=x");

        Assert.Single(parsed);
        Assert.Contains(Notes("UserTemplates"), d => d.Contains("bad, no slash"));
    }

    /// <summary>Reference arm: blank lines and comments are not errors.</summary>
    [Fact]
    public void BlankAndCommentedTemplateLines_AreNotReported()
    {
        Diagnostics.Clear();

        var parsed = SlashCommandRouter.ParseUserTemplates("# disabled=x\n\n   \n/good=some text");

        Assert.Single(parsed);
        Assert.Empty(Notes("UserTemplates"));
    }

    /// <summary>The same tool graph the host builds, with MCP and the index at their empty values:
    /// only the built-ins and the ones declared in <paramref name="customTools"/> come out.</summary>
    private static ToolRegistry Registry(string customTools)
    {
        var config   = new InferpalConfig { CustomTools = customTools };
        var client   = new FakeInferenceProvider();
        var editor   = new NullEditorSurface();
        var approval = new NoopApproval();
        return new ToolRegistry(editor, approval, config,
                                new ProjectIndexService(client, config, new LspSemanticProvider()), client,
                                new ProjectMapService(editor), new McpToolService(config, approval),
                                new DocsIndexService(client, config), new OpenDocumentOverlay(),
                                new NullDebugSession());
    }

    [Fact]
    public void ACustomToolWhoseNameIsABuiltIn_IsRecorded()
    {
        Diagnostics.Clear();

        var names = Registry("read_file=echo hello").Definitions.Select(d => d.Function.Name).ToList();

        Assert.Contains("read_file", names);   // the built-in keeps its name: the arbitration is right
        Assert.Contains(Notes("CustomTools"), d => d.Contains("built-in") && d.Contains("read_file"));
    }

    [Fact]
    public void AMalformedCustomToolLine_IsRecorded()
    {
        Diagnostics.Clear();

        _ = Registry("my_tool=echo ok\nline with no equals").Definitions.ToList();

        Assert.Contains(Notes("CustomTools"), d => d.Contains("line with no equals"));
    }

    /// <summary>Reference arm: a valid declaration says nothing.</summary>
    [Fact]
    public void AValidCustomTool_ReportsNothing()
    {
        Diagnostics.Clear();

        var names = Registry("my_tool=echo ok").Definitions.Select(d => d.Function.Name).ToList();

        Assert.Contains("my_tool", names);
        Assert.Empty(Notes("CustomTools"));
    }

    // ── Say it once, not on every pass ────────────────────────────────────────

    /// <summary>
    /// <c>CustomTools</c> is reparsed on EVERY read of <c>Definitions</c>, i.e. at least three times
    /// per request to the model: the client's <c>.Count</c> then its <c>.ToList()</c>, the set of
    /// known names, and two orchestrator guards.
    /// </summary>
    [Fact]
    public void ACustomToolRejection_IsReportedOnce_NotOnEveryReadOfTheToolList()
    {
        Diagnostics.Clear();
        var registry = Registry("read_file=echo hello");

        for (var i = 0; i < 5; i++) _ = registry.Definitions.ToList();

        Assert.Contains("read_file", Assert.Single(Notes("CustomTools")));
    }

    /// <summary>
    /// The consequence, and it is the one that costs: the ring keeps only
    /// <see cref="Diagnostics.Capacity"/> entries, so a faulty line repeated on every request pushes
    /// out the failure the user came to <c>/diagnostics</c> to find.
    /// </summary>
    /// <remarks>
    /// POSITIVE assertion: we require the witness to still be there. Two faulty lines × 120 passes =
    /// 240 entries without the guard, i.e. more than the ring's capacity.
    /// </remarks>
    [Fact]
    public void AnUnusableCustomToolLine_DoesNotPushTheRestOfTheRingOut()
    {
        Diagnostics.Clear();
        Diagnostics.Record("Witness", "the failure the user opened /diagnostics to find");
        var registry = Registry("read_file=echo hello\nline with no equals");

        for (var i = 0; i < 120; i++) _ = registry.Definitions.ToList();

        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Witness");
        Assert.Equal(2, Notes("CustomTools").Length);
    }

    /// <summary>
    /// <c>/diagnostics clear</c>: the user starts again from a clean ring, so what they have just
    /// erased must be able to be said again — otherwise "once per process" becomes "never again".
    /// </summary>
    [Fact]
    public void ClearingTheChannel_MakesTheNextPassSpeakAgain()
    {
        Diagnostics.Clear();
        var registry = Registry("line with no equals sign at all");

        _ = registry.Definitions.ToList();
        Assert.Single(Notes("CustomTools"));

        Diagnostics.Clear();
        _ = registry.Definitions.ToList();
        Assert.Single(Notes("CustomTools"));
    }

    /// <summary>
    /// The command-template loader is the <b>autocomplete</b>'s: it runs on every keystroke while a
    /// slash command is being typed — <c>IsBuiltIn</c>'s comment says so, and that is why that
    /// particular answer has been cached for a long time.
    /// </summary>
    [Fact]
    public void AShadowedTemplate_IsReportedOnce_NotOnEveryKeystroke()
    {
        Diagnostics.Clear();
        var cfg = new InferpalConfig { PromptTemplates = "/plan=mon modele" };

        for (var i = 0; i < 12; i++) Assert.Empty(SlashTemplates.Load(cfg, null));

        Assert.Contains("/plan", Assert.Single(Notes("UserTemplates")));
    }

    // ── A pathological pattern is said only once ──────────────────────────────

    /// <summary>
    /// A glob whose evaluation blows past its budget is reported <b>once per pattern</b>, not once
    /// per item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ This is the half of the class <c>DroppedLineOnce</c> had left open: it covered a rejected
    /// configuration <i>line</i>, and the ring drowns just as well under a note emitted <b>per
    /// item</b>. The multiplier is worse there — <c>IndexExclusions</c> evaluates its patterns on
    /// <b>every file</b> of the indexing pass, hence thousands of identical entries in a ring that
    /// keeps <see cref="Diagnostics.Capacity"/>, and <c>RulesService</c> re-evaluates its globs on
    /// every rebuild of the system prompt, that is, on every change of active file.
    /// </para>
    /// <para>
    /// Both patterns come from a file that arrives with a <b>cloned repository</b>
    /// (<c>.inferpal/project.json</c>, <c>.inferpal/rules/*.md</c>): that is untrusted input, and
    /// the glob used here is the one
    /// <c>RulesServiceTests.GlobMatch_APathologicalRepoAuthoredGlob_CannotFreezeThePromptBuild</c>
    /// has already measured as exceeding its budget.
    /// </para>
    /// <para>
    /// ⚠ These two tests live HERE and not in <c>IndexExclusionsTests</c>/<c>RulesServiceTests</c>:
    /// they read the ring, which is <b>process</b> state, and only this class is in the serialized
    /// collection. Written elsewhere, a concurrent <c>Clear()</c> erased their witness — measured,
    /// not assumed.
    /// </para>
    /// </remarks>
    [Fact]
    public void APathologicalIndexExclusion_IsReportedOncePerPattern_NotOncePerFile()
    {
        Diagnostics.Clear();
        Diagnostics.Record("Witness", "the failure the user opened /diagnostics to find");

        var root  = Path.Combine(Path.GetTempPath(), "inferpal-excl-" + Guid.NewGuid().ToString("N"));
        string[] extra = [string.Concat(Enumerable.Repeat("a*", 20)) + "b"];
        var file  = Path.Combine(root, "src", new string('a', 40) + "c");

        for (var i = 0; i < 20; i++)
            Assert.False(Inferpal.Services.Rag.IndexExclusions.IsExcluded(file, root, extra));

        var said = Notes("IndexExclusions");
        Assert.Single(said);
        Assert.Contains("timed out", said[0]);
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Witness");
    }

    /// <summary>The second reader of the same glob dialect, whose consequence differs: the rule this
    /// glob scopes does not apply.</summary>
    [Fact]
    public void APathologicalRuleGlob_IsReportedOncePerGlob_NotOnEveryPromptRebuild()
    {
        Diagnostics.Clear();
        Diagnostics.Record("Witness", "the failure the user opened /diagnostics to find");

        var glob = string.Concat(Enumerable.Repeat("b*", 20)) + "z";
        var path = new string('b', 40) + "c";

        for (var i = 0; i < 20; i++)
            Assert.False(Inferpal.Services.Governance.RulesService.GlobMatch(glob, path));

        var said = Notes("Rules");
        Assert.Single(said);
        Assert.Contains("is not applied", said[0]);
        Assert.Contains(Diagnostics.Snapshot(), e => e.Context == "Witness");
    }

    // ── Two tools cannot claim the same name ──────────────────────────────────

    /// <summary>
    /// ⚠ Two lines the user reads as <b>distinct</b>: the name is normalized (lowercase, spaces to
    /// underscores), so <c>My Tool</c> and <c>my_tool</c> are one and the same — the same trap as
    /// <c>my-server</c> / <c>my.server</c> on the MCP side, where it is documented and handled.
    /// Without a guard, the backend received two definitions of the same name and only the first
    /// command ran, in silence.
    /// </summary>
    [Fact]
    public void TwoCustomToolsClaimingTheSameName_AreNotBothOffered_AndTheSecondIsSaid()
    {
        Diagnostics.Clear();

        var names = Registry("My Tool=echo one\nmy_tool=echo two")
            .Definitions.Select(d => d.Function.Name).ToList();

        Assert.Single(names, n => n == "my_tool");   // exposed, and exactly once
        Assert.Contains(Notes("CustomTools"),
                        d => d.Contains("already declared") && d.Contains("echo two"));
    }

    // ── Serveurs MCP ──────────────────────────────────────────────────────────

    /// <summary>The likeliest one: a misspelled key. The server then appeared nowhere — not even in
    /// /mcp's "failed" list, which lists only those that tried to start.</summary>
    [Fact]
    public void AnMcpServerWithoutTransport_IsRecorded()
    {
        Diagnostics.Clear();

        var servers = McpServerConfig.Parse("{ \"mcpServers\": { \"monserveur\": { \"cmd\": \"node\" } } }");

        Assert.Empty(servers);
        Assert.Contains(Notes("Mcp"), d => d.Contains("monserveur"));
    }

    /// <summary>One comma too many and EVERY server disappears at once.</summary>
    [Fact]
    public void AnMcpListThatIsNotValidJson_IsRecorded()
    {
        Diagnostics.Clear();

        var servers = McpServerConfig.Parse("{ \"mcpServers\": { \"a\": { \"command\": \"node\" }, } }");

        Assert.Empty(servers);
        Assert.NotEmpty(Notes("Mcp"));
    }

    /// <summary>
    /// Rejected entries also reach the screen, not only /diagnostics: they join the status snapshot
    /// both panels already render, so "✗ myserver — …" shows where the user is already looking.
    /// </summary>
    [Fact]
    public void ARejectedMcpEntry_CarriesItsNameAndReasonSeparately()
    {
        McpServerConfig.Parse("{ \"mcpServers\": { \"monserveur\": { \"cmd\": \"node\" } } }", out var rejected);

        var one = Assert.Single(rejected);
        Assert.Equal("monserveur", one.Name);
        Assert.Contains("command", one.Reason);
    }

    /// <summary>
    /// The reason for invalid JSON carries the exception message, which itself contains colons
    /// (LineNumber, BytePositionInLine). A name/reason pair encoded in a single string and split
    /// again would have shown an absurd server name.
    /// </summary>
    [Fact]
    public void ARejectedListKeepsItsWholeReason_EvenWhenItContainsColons()
    {
        McpServerConfig.Parse("{ \"mcpServers\": { \"a\": { \"command\": \"node\" }, } }", out var rejected);

        var one = Assert.Single(rejected);
        Assert.Equal("mcpServers", one.Name);
        Assert.Contains("not valid JSON", one.Reason);
    }

    /// <summary>Reference arm: a valid configuration says nothing.</summary>
    [Fact]
    public void AValidMcpServer_ReportsNothing()
    {
        Diagnostics.Clear();

        var servers = McpServerConfig.Parse("{ \"mcpServers\": { \"a\": { \"command\": \"node\" } } }");

        Assert.Single(servers);
        Assert.Empty(Notes("Mcp"));
    }
    // ── Missing pinned file ───────────────────────────────────────────────────

    [Fact]
    public void AMissingPinnedFile_IsReportedOnce_NotOnEveryPromptRebuild()
    {
        Diagnostics.Clear();
        // Unique path: the report is remembered per PROCESS, and a shared path would make this
        // test depend on execution order.
        var missing = Path.Combine(Path.GetTempPath(), "inferpal-missing-pin-" + Guid.NewGuid().ToString("N") + ".md");
        var cfg     = new InferpalConfig { PinnedContextFiles = missing };

        var prompt = new SystemPromptBuilder(cfg).Build("BASE");

        Assert.DoesNotContain("Pinned:", prompt);   // witness: it really did not travel
        Assert.Single(Notes("PinnedFiles"));

        // The system prompt is rebuilt on EVERY active-file change: the same message two hundred
        // times would drown the ring, and a noisy channel stops being read.
        new SystemPromptBuilder(cfg).Build("BASE");
        Assert.Single(Notes("PinnedFiles"));
    }

    /// <summary>
    /// "Once" is not "only once in the process's life": a pinned file that comes back and then
    /// disappears again is said again.
    /// </summary>
    /// <remarks>
    /// This behaviour already existed; it is tested here because its mechanism moved from
    /// <c>SystemPromptBuilder</c>'s private set to <c>Diagnostics.DroppedLineOnce</c>, shared with
    /// the two other repeated parsers. A move without a witness is a bet.
    /// </remarks>
    [Fact]
    public void APinnedFileThatComesBackThenVanishesAgain_IsSaidAgain()
    {
        Diagnostics.Clear();
        var path = Path.Combine(Path.GetTempPath(), "inferpal-pin-back-" + Guid.NewGuid().ToString("N") + ".md");
        var cfg  = new InferpalConfig { PinnedContextFiles = path };

        new SystemPromptBuilder(cfg).Build("BASE");
        Assert.Single(Notes("PinnedFiles"));

        File.WriteAllText(path, "PINNED CONTENT");
        try { Assert.Contains("PINNED CONTENT", new SystemPromptBuilder(cfg).Build("BASE")); }
        finally { File.Delete(path); }

        new SystemPromptBuilder(cfg).Build("BASE");
        Assert.Equal(2, Notes("PinnedFiles").Length);
    }

    /// <summary>
    /// A pinned file dropped <b>by the cap</b> is said, like the one that is missing.
    /// </summary>
    /// <remarks>
    /// Same silence, another cause. The settings window sets no cap and
    /// <c>PinnedFilesPolicy.Serialize</c> deliberately keeps the entries beyond <c>MaxPinned</c>:
    /// the user writes five, sees all five in the settings, and two never reach the system prompt —
    /// nor the 📌 bullets, which read the same capped list. It is word for word what the missing
    /// file's comment complains about.
    /// </remarks>
    [Fact]
    public void PinnedFilesPastTheCap_AreReportedInsteadOfSilentlyDropped()
    {
        Diagnostics.Clear();
        var paths = Enumerable.Range(0, PinnedFilesPolicy.MaxPinned + 2)
            .Select(_ => Path.Combine(Path.GetTempPath(), "inferpal-pin-cap-" + Guid.NewGuid().ToString("N") + ".md"))
            .ToList();
        foreach (var p in paths) File.WriteAllText(p, "PINNED " + Path.GetFileNameWithoutExtension(p));

        try
        {
            var prompt = new SystemPromptBuilder(
                new InferpalConfig { PinnedContextFiles = string.Join("\n", paths) }).Build("BASE");

            // Witness: the first three really did go — the cap did not eat everything.
            foreach (var kept in paths.Take(PinnedFilesPolicy.MaxPinned))
                Assert.Contains("PINNED " + Path.GetFileNameWithoutExtension(kept), prompt);

            var said = Notes("PinnedFiles");
            Assert.Equal(2, said.Length);
            foreach (var dropped in paths.Skip(PinnedFilesPolicy.MaxPinned))
                Assert.Contains(said, d => d.Contains(Path.GetFileName(dropped)));
        }
        finally { foreach (var p in paths) File.Delete(p); }
    }

    /// <summary>Reference arm: a pinned file that EXISTS says nothing.</summary>
    [Fact]
    public void AnExistingPinnedFile_ReportsNothing()
    {
        Diagnostics.Clear();
        var path = Path.Combine(Path.GetTempPath(), "inferpal-pin-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "PINNED CONTENT");
        try
        {
            var prompt = new SystemPromptBuilder(new InferpalConfig { PinnedContextFiles = path }).Build("BASE");

            Assert.Contains("PINNED CONTENT", prompt);   // witness: the pin was read
            Assert.Empty(Notes("PinnedFiles"));
        }
        finally { File.Delete(path); }
    }

    // ── .inferpal/validators.json ─────────────────────────────────────────────

    // A stray comma or an entry without "marker": the command the user wrote for Smart Fix never
    // ran, and nothing — not even this ring — said why.
    [Theory]
    [InlineData("not json", "validators.json ignored")]
    [InlineData("[1, 2]", "JSON object")]
    [InlineData("""{ ".ts": "tsc" }""", "must be an object")]
    [InlineData("""{ ".ts": { "command": "tsc" } }""", "'.ts'")]
    [InlineData("""{ ".ts": { "marker": "tsconfig.json" } }""", "\"command\"")]
    public async Task AnUnusableValidatorsFile_IsRecordedOnce(string json, string expected)
    {
        Diagnostics.Clear();
        var root = WorkspaceWithValidators(json);
        try
        {
            var validator = new Inferpal.Services.CodeActions.SmartFixValidator(
                new InferpalConfig { SmartFixEnabled = true }, () => root);
            var written = Path.Combine(root, "notes.txt");   // no validated extension: nothing runs

            // The file is re-read after every write: two passes, one entry.
            await validator.ValidateAsync(written, CancellationToken.None);
            await validator.ValidateAsync(written, CancellationToken.None);

            Assert.Contains(expected, Assert.Single(Notes("ValidatorsOverlay")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Reference arm: a usable validators.json reports nothing.</summary>
    [Fact]
    public async Task AUsableValidatorsFile_ReportsNothing()
    {
        Diagnostics.Clear();
        const string json = """{ ".ts": { "marker": "tsconfig.json", "command": "npx tsc --noEmit" } }""";
        var root = WorkspaceWithValidators(json);
        try
        {
            await new Inferpal.Services.CodeActions.SmartFixValidator(
                    new InferpalConfig { SmartFixEnabled = true }, () => root)
                .ValidateAsync(Path.Combine(root, "notes.txt"), CancellationToken.None);

            Assert.Single(Inferpal.Services.CodeActions.BuildValidators.ParseConfig(json));   // witness: the entry is valid
            Assert.Empty(Notes("ValidatorsOverlay"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string WorkspaceWithValidators(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-validators-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".inferpal"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "validators.json"), json);
        return root;
    }
}
