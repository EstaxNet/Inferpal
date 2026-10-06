using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A field name made of code is a value cut by an unescaped quote: the call is refused.
//
//  Captured on the real-condition battery (devstral, apply_edits): the C# `$"Not enough…"` was
//  written into old_content without escaping its quote. The JSON still parsed — old_content ended
//  at `…InvalidOperationException($` and the rest of the line became a field name — and the edit
//  replaced that prefix of the method: seven compilation errors.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class UnescapedQuoteArgumentTests
{
    // The arguments as the server streamed them, the two tabs included.
    private static readonly string Captured = """
        {"edits": [{"path": "src/Shop/Inventory.cs", "old_content": "    public void Reserve(string sku, int quantity)\n    {\n        if (Stock(sku) < quantity)\n            throw new InvalidOperationException($"<TAB>, "Not enough stock for '{sku}'."<TAB>:<TAB>");\n        _stock[sku] = Stock(sku) - quantity;\n    }", "new_content": "    public bool Reserve(string sku, int quantity) => true;"}]}
        """.Replace("<TAB>", "\t");

    private sealed class Recorder : IToolRegistry
    {
        public List<string> Ran { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Ran.Add(name);
            return Task.FromResult("ran");
        }
    }

    private static Task<string> Run(Recorder tools, string name, string json) =>
        AgentOrchestrator.ExecuteToolSafeAsync(
            tools, new ToolCallFunction(name, JsonDocument.Parse(json).RootElement.Clone()), replyCut: false,
            CancellationToken.None);

    [Fact]
    public async Task TheCapturedEdit_IsRefused_NamingTheCutAndTheInterpolatedString()
    {
        using var doc = JsonDocument.Parse(Captured);                         // WITNESS: it IS valid JSON
        var edit = doc.RootElement.GetProperty("edits")[0];
        Assert.EndsWith("InvalidOperationException($", edit.GetProperty("old_content").GetString());

        var tools = new Recorder();
        var reply = await Run(tools, "apply_edits", Captured);

        Assert.Empty(tools.Ran);
        Assert.Contains("NOT executed", reply);
        Assert.Contains("Not enough stock for '{sku}'.", reply);
        Assert.Contains("$\\\"", reply);
    }

    [Fact]
    public async Task OrdinaryFieldNames_AndEscapedQuotes_Run()
    {
        // Reference arms: escaped quotes, an interpolated string written right, names with $ and -.
        var tools = new Recorder();
        await Run(tools, "apply_diff", """{"path": "a.cs", "old_content": "x = $\"a {b}\";", "new_content": "y"}""");
        await Run(tools, "mcp_tool", """{"$schema": "s", "file-path": "p", "items": [{"max_results": 3}]}""");
        await Run(tools, "list_files", "{}");

        Assert.Equal(["apply_diff", "mcp_tool", "list_files"], tools.Ran);
    }

    [Fact]
    public async Task AnMcpTool_KeepsItsFreeFormObjects()
    {
        // Reference arm: a third party's schema may declare a map whose keys are text — never judged as a cut.
        var tools = new Recorder();
        await Run(tools, "mcp__i18n__translate", """{"strings": {"Hello, world (greeting)": "Bonjour"}}""");

        Assert.Equal(["mcp__i18n__translate"], tools.Ran);
    }

    [Fact]
    public async Task AQuoteCutWithoutADollar_IsRefused_WithoutTheInterpolationHint()
    {
        var tools = new Recorder();
        var reply = await Run(tools, "write_file", """{"path": "a.txt", "content": "He said ", "hello there": "."}""");

        Assert.Empty(tools.Ran);
        Assert.Contains("\"hello there\"", reply);
        Assert.DoesNotContain("interpolated", reply);
    }
}
