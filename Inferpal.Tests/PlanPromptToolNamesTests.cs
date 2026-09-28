using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  The agent's plan request names the tools the run can call.
//
//  Its JSON asks for a "tool" per step, and the planning call carries no tools: a model then plans
//  tools it cannot see (search_code, list_directory) and the ACT phase, reading its own plan, calls
//  them. Given the names, the plans name the real tools.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PlanPromptToolNamesTests
{
    private sealed class RecordingClient : IOllamaChatClient
    {
        public List<List<ChatMessageDto>> Requests { get; } = [];
        private int _n;

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Requests.Add([.. messages]);
            return Task.FromResult(_n++ == 0
                ? new ChatTurnResult("{\"goal\":\"g\",\"steps\":[{\"i\":1,\"desc\":\"answer\"}]}", null, 0, 0)
                : new ChatTurnResult("the answer", null, 0, 0));
        }
    }

    private sealed class Registry(params string[] names) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            names.Select(n => new ToolDefinition("function", new ToolFunction(n, "test tool", new { }))).ToList();
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("result");
    }

    private static async Task<RecordingClient> RunAsync(IToolRegistry tools)
    {
        var client = new RecordingClient();
        await new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 3 })
            .RunAsync(model: "m", history: [new ChatMessageDto("user", "Rename Cart.ComputeTotal.")], tools: tools,
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);
        return client;
    }

    [Fact]
    public async Task ThePlanRequest_NamesTheToolsTheRunCanCall_AndTheActPhaseSeesTheSamePrompt()
    {
        var client = await RunAsync(new Registry("read_file", "search_in_files", "rename_symbol"));

        var plan = client.Requests[0][^1].Content!;
        Assert.StartsWith(ModelPrompts.AgentPlanPrompt, plan, StringComparison.Ordinal);             // witness: the plan request
        Assert.EndsWith("Tools you can call (use these names for \"tool\"): read_file, search_in_files, rename_symbol.",
                        plan, StringComparison.Ordinal);
        Assert.True(client.Requests.Count > 1);
        Assert.Contains(client.Requests[1], m => m.Role == "user" && m.Content == plan);         // the anchored copy
    }

    [Fact]
    public async Task WithoutTools_ThePlanRequestIsThePromptAlone()
    {
        // Reference arm: no "Tools you can call:" line naming nothing.
        var client = await RunAsync(new Registry());

        Assert.Equal(ModelPrompts.AgentPlanPrompt, client.Requests[0][^1].Content);
    }
}
