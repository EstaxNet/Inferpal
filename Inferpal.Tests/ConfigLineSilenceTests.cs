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
