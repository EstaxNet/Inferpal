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

        var parsed = SlashCommandRouter.ParseUserTemplates("/good=some text\nbad without slash=x");

        Assert.Single(parsed);
        Assert.Contains(Notes("UserTemplates"), d => d.Contains("bad without slash"));
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

        _ = Registry("my_tool=echo ok\nline without equals").Definitions.ToList();

        Assert.Contains(Notes("CustomTools"), d => d.Contains("line without equals"));
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

    /// <summary>The most likely one: a misspelt key. The server then appeared nowhere - not even
    /// among the failures /mcp lists, which only cover the ones that tried to start.</summary>
    [Fact]
    public void AnMcpServerWithoutTransport_IsRecorded()
    {
        Diagnostics.Clear();

        var servers = McpServerConfig.Parse("{ \"mcpServers\": { \"myserver\": { \"cmd\": \"node\" } } }");

        Assert.Empty(servers);
        Assert.Contains(Notes("Mcp"), d => d.Contains("myserver"));
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
        McpServerConfig.Parse("{ \"mcpServers\": { \"myserver\": { \"cmd\": \"node\" } } }", out var rejected);

        var one = Assert.Single(rejected);
        Assert.Equal("myserver", one.Name);
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
}
