using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When the stream is stopped inside a tool call — its arguments broken, or repeating — only THAT call is refused: the
/// calls the model had finished before it run.
/// </summary>
/// <remarks>
/// The mark was put on every call of the turn: a <c>read_file</c> complete before a broken <c>apply_diff</c> was refused
/// as "stopped being something the tool can read", and the model rewrote a sound call along with the broken one.
/// </remarks>
public sealed class StoppedCallMarkTests
{
    private sealed class Tools : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction("read_file", "reads a file",
                JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement.Clone())),
            new("function", new ToolFunction("apply_diff", "edits a file",
                new ApplyDiffTool(new AutoApprove(), new FileHistoryService(), () => string.Empty).Parameters)),
        ];
        public List<string> Ran { get; } = [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Ran.Add(name);
            return Task.FromResult("ran");
        }
    }

    private sealed class AutoApprove : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    private static string Chunk(object toolCall) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { tool_calls = new[] { toolCall } } } } }) + "\n\n";

    /// <summary>Two calls streamed one after the other: a complete read_file, then an apply_diff.</summary>
    private static string TwoCalls(string secondArguments)
    {
        var sb = new StringBuilder();
        sb.Append(Chunk(new { index = 0, id = "1", type = "function", function = new { name = "read_file", arguments = "" } }));
        sb.Append(Chunk(new { index = 0, function = new { arguments = """{"path": "src/Inventory.cs"}""" } }));
        sb.Append(Chunk(new { index = 1, id = "2", type = "function", function = new { name = "apply_diff", arguments = "" } }));
        for (var i = 0; i < secondArguments.Length; i += 7)
            sb.Append(Chunk(new { index = 1, function = new { arguments = secondArguments.Substring(i, Math.Min(7, secondArguments.Length - i)) } }));
        return sb.Append("data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n").ToString();
    }

    private static async Task<(ChatTurnResult Turn, Tools Tools)> SendAsync(string stream)
    {
        using var server = new LoopbackHttpServer(path => path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? stream : null);
        var client = new OpenAiCompatibleClient(new InferpalConfig
            { Provider = "openai-compatible", BaseUrl = server.BaseUrl, ContextWindowSize = 262_144 });
        var tools = new Tools();
        return (await client.SendChatAsync("m", [new ChatMessageDto("user", "change Reserve")], tools,
                                           onToken: null, CancellationToken.None), tools);
    }

    [Fact]
    public async Task ABrokenSecondCall_IsRefused_AndTheFirstRuns()
    {
        // apply_diff cut by an unescaped quote: its `}` closes the whole arguments object, and the rest follows it.
        var (turn, tools) = await SendAsync(TwoCalls(
            """{"path": "src/Inventory.cs", "old_content": "throw new InvalidOperationException($"} ,"Not enough stock" ,"  ," ,"  ,"}"""));

        var calls = turn.ToolCalls!;
        Assert.Equal(["read_file", "apply_diff"], calls.Select(c => c.Function.Name));
        Assert.Null(calls[0].Function.BrokenShape);
        Assert.NotNull(calls[1].Function.BrokenShape);                     // witness: the stream WAS stopped in a call

        var first  = await AgentOrchestrator.ExecuteToolSafeAsync(tools, calls[0].Function, turn.CutAtLimit, CancellationToken.None);
        var second = await AgentOrchestrator.ExecuteToolSafeAsync(tools, calls[1].Function, turn.CutAtLimit, CancellationToken.None);
        Assert.Equal("ran", first);
        Assert.Contains("NOT executed", second);
        Assert.Equal(["read_file"], tools.Ran);
    }

    [Fact]
    public async Task TwoSoundCalls_AreNeitherMarked()
    {
        // Reference arm.
        var (turn, _) = await SendAsync(TwoCalls("""{"path": "src/Inventory.cs", "old_content": "x", "new_content": "y"}"""));

        Assert.All(turn.ToolCalls!, c => Assert.Null(c.Function.BrokenShape));
        Assert.All(turn.ToolCalls!, c => Assert.False(c.Function.StoppedRepeating));
    }
}
