using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Streamed arguments that can no longer be what the tool reads stop the reading there.
//
//  Captured on LM Studio with Devstral Small 2 (Fixtures/devstral-unescaped-dollar.json, verbatim): an apply_edits
//  wrote the `$"` of a C# string without escaping it, then strings separated by `" ,"` for 67 to 150 seconds — 7 000
//  to 19 000 characters, lexically valid JSON to the end, no event reaching the front-ends. The first string where an
//  edit object goes arrives a few hundred characters in.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ArgumentsShapeTests
{
    private sealed record Captured(string Source, string Tool, string Arguments, int StreamedChars);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Captured Fixture(int index)
    {
        var all = JsonSerializer.Deserialize<Captured[]>(
            File.ReadAllText(Path.Combine(RepoRoot(), "Inferpal.Tests", "Fixtures", "devstral-unescaped-dollar.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(4, all.Length);   // witness: every captured stream is read
        return all[index];
    }

    /// <summary>The registry the real request carries: apply_edits and apply_diff with their real schemas.</summary>
    private sealed class Tools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } = Declare();
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("ran");

        private static List<ToolDefinition> Declare()
        {
            var history = new FileHistoryService();
            ITool[] tools =
            [
                new ApplyEditsTool(new AutoApprove(), history, () => string.Empty),
                new ApplyDiffTool(new AutoApprove(), history, () => string.Empty),
            ];
            return tools.Select(t => new ToolDefinition("function", new ToolFunction(t.Name, t.Description, t.Parameters)))
                        .ToList();
        }
    }

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    /// <summary>A structured call streamed the way LM Studio streams one: the name first, then the arguments in pieces.</summary>
    private static string CallStream(string name, string arguments, int piece = 7)
    {
        static string Chunk(object toolCall) =>
            "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { toolCall } } } } }) + "\n\n";

        var sb = new StringBuilder(Chunk(new { index = 0, id = "1", type = "function", function = new { name, arguments = "" } }));
        for (var i = 0; i < arguments.Length; i += piece)
            sb.Append(Chunk(new { index = 0, function = new { arguments = arguments.Substring(i, Math.Min(piece, arguments.Length - i)) } }));
        return sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    private static async Task<ChatTurnResult> SendAsync(string stream)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? stream : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 });
        return await client.SendChatAsync("m", [new ChatMessageDto("user", "change Reserve")], new Tools(),
                                          onToken: null, CancellationToken.None);
    }

    private static int ReadChars(ToolCallFunction call) =>
        (call.UnparsedArguments ?? call.Arguments.GetRawText()).Length;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ACapturedDerailment_IsStoppedAtTheFirstTextWhereAnEditObjectGoes(int index)
    {
        var captured = Fixture(index);
        Assert.Contains("InvalidOperationException($\"}", captured.Arguments);   // WITNESS: the unescaped $" is there

        var turn = await SendAsync(CallStream(captured.Tool, captured.Arguments));

        var call = Assert.Single(turn.ToolCalls!).Function;
        Assert.Equal("an element of 'edits' began as text where the tool reads an object", call.BrokenShape);
        Assert.InRange(ReadChars(call), 1, 600);                                // stopped near char 300, not at the end
        Assert.True(captured.Arguments.Length >= 2000);
    }

    [Fact]
    public async Task TheFunnel_RefusesIt_WithTheCauseAndTheEscape()
    {
        var call = new ToolCallFunction("apply_edits", JsonDocument.Parse("{}").RootElement.Clone())
            { BrokenShape = "an element of 'edits' began as text where the tool reads an object" };
        var tools = new Tools();

        var reply = await AgentOrchestrator.ExecuteToolSafeAsync(tools, call, replyCut: false, CancellationToken.None);

        Assert.StartsWith("Error: the arguments of this 'apply_edits' call stopped being something the tool can read", reply);
        Assert.Contains("NOT executed", reply);
        Assert.Contains("$\\\"", reply);
    }

    [Fact]
    public async Task TextAfterTheClosedObject_StopsAFlatTool()
    {
        // apply_diff cut the same way: its `}` closes the WHOLE arguments object, and the rest follows it.
        var args = """{"path": "src/Inventory.cs", "old_content": "throw new InvalidOperationException($"} ,"Not enough stock" ,"  ," ,"  ," ,"  ,"}""";

        var call = Assert.Single((await SendAsync(CallStream("apply_diff", args))).ToolCalls!).Function;

        Assert.Equal("text went on after the arguments object had closed", call.BrokenShape);
    }

    [Theory]
    // Reference arms: an interpolated string written right, several edits, a double-encoded root, keys in any order.
    [InlineData("apply_edits", """{"edits": [{"path": "a.cs", "old_content": "throw new X($\"Not {sku}\");", "new_content": "return false;"}, {"path": "b.cs", "old_content": "var n = 1;", "new_content": "var n = 2;", "occurrence": "all"}]}""")]
    [InlineData("apply_diff", """{"path": "a.cs", "old_content": "x", "new_content": "y", "occurrence": "first"}""")]
    [InlineData("apply_diff", "\"{\\\"path\\\": \\\"a.cs\\\", \\\"old_content\\\": \\\"x\\\", \\\"new_content\\\": \\\"y\\\"}\"")]
    [InlineData("apply_edits", """{"edits": []}""")]
    public async Task AWellFormedCall_IsNeverStopped(string tool, string args)
    {
        var turn = await SendAsync(CallStream(tool, args, piece: 3));

        var call = Assert.Single(turn.ToolCalls!).Function;
        Assert.Null(call.BrokenShape);
        Assert.Equal(tool, call.Name);
    }

    [Fact]
    public void AnMcpTool_IsNotWatched()
    {
        // A third party's schema may declare a free-form object: nothing about its arguments is judged here.
        var watcher = new ArgumentsShapeWatcher(_ => JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        Assert.Null(watcher.Breaks(0, "mcp__i18n__translate", """{"strings": {"Hello, world": "Bonjour"}} trailing"""));
        Assert.Equal("text went on after the arguments object had closed",
                     new ArgumentsShapeWatcher(_ => null).Breaks(0, "write_file", """{"path": "a"} trailing"""));
    }
}
