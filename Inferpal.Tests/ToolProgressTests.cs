using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What a tool says while it runs (each action <c>interact_window</c> plays) reaches the status line of the run that called it,
/// in both agent loops — the orchestrated one and the basic one — and nothing outside a run.
/// </summary>
public sealed class ToolProgressTests
{
    /// <summary>A tool that says two things while it runs, then answers.</summary>
    private sealed class SayingRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("read_file", "test tool", new { type = "object", properties = new { } }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            ToolProgress.Say("step 1 of 2");
            ToolProgress.Say("step 2 of 2");
            return Task.FromResult("result");
        }
    }

    private sealed class Script(params ChatTurnResult[] turns) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _turns = new(turns);
        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
                                                  CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
                                                  string? toolChoice = null, Action<string>? onThinking = null) =>
            Task.FromResult(_turns.Count > 1 ? _turns.Dequeue() : _turns.Peek());
    }

    [Fact]
    public async Task TheOrchestratedRun_ShowsWhatItsToolSays_OnItsStatusLine()
    {
        var call = new ChatTurnResult(string.Empty,
            [new ToolCallDto(new ToolCallFunction("read_file", JsonDocument.Parse("{}").RootElement.Clone()))], 0, 0);
        var client = new Script(new ChatTurnResult("""{"goal":"g","steps":[{"i":1,"desc":"read"}]}""", null, 0, 0), call,
                                new ChatTurnResult("done", null, 0, 0));
        var steps = new List<string>();

        await new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 8192, AgentMaxIterations = 4, AgentModeEnabled = true })
            .RunAsync("m", [new ChatMessageDto("user", "read it")], new SayingRegistry(), steps.Add, null, null, null, null, null,
                      CancellationToken.None);

        Assert.Contains("step 1 of 2", steps);
        Assert.Contains("step 2 of 2", steps);
    }

    [Fact]
    public async Task ARoute_EndsWithItsScope_EvenForWorkStartedInIt()
    {
        var outer = new List<string>();
        var inner = new List<string>();
        Task late;
        var release = new TaskCompletionSource();
        using (ToolProgress.Route(outer.Add))
        {
            using (ToolProgress.Route(inner.Add))
            {
                ToolProgress.Say("inside");
                // Work a tool starts in the run (a background job, a browser's pump) carries the flow on after it.
                late = Task.Run(async () => { await release.Task; ToolProgress.Say("after its run"); });
            }
            ToolProgress.Say("back outside");
        }
        ToolProgress.Say("no run");
        release.SetResult();
        await late;

        Assert.Equal(["inside"], inner);
        Assert.Equal(["back outside"], outer);
    }

    [Fact]
    public async Task TheBasicLoop_ShowsWhatItsToolSays_OnItsStatusLine()
    {
        var requests = 0;
        using var server = new LoopbackHttpServer(path =>
            !path.StartsWith("/v1/chat/completions", StringComparison.Ordinal) ? null
            : Interlocked.Increment(ref requests) == 1 ? CallStream() : AnswerStream());
        var client = new OpenAiCompatibleClient(new InferpalConfig { Provider = "openai-compatible", BaseUrl = server.BaseUrl });
        var steps = new List<string>();

        await client.RunAgentAsync("m", [new ChatMessageDto("user", "read it")], new SayingRegistry(), onStep: steps.Add, onToken: null,
                                   CancellationToken.None);

        Assert.True(requests >= 2, $"{requests} request(s): the tool was never called");   // witness
        Assert.Contains("step 1 of 2", steps);
        Assert.Contains("step 2 of 2", steps);
    }

    private static string CallStream() =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[]
            {
                new
                {
                    index = 0,
                    delta = new { tool_calls = new[] { new { index = 0, id = "c1", type = "function", function = new { name = "read_file", arguments = "{}" } } } },
                },
            },
        }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n"
        + "data: [DONE]\n\n";

    private static string AnswerStream() =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta = new { content = "Read." } } } }) + "\n\n"
        + "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: [DONE]\n\n";
}
