using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The final synthesis of a run fits the window: its tool digest takes what the window leaves after the head sent with
/// it (system prompt and conversation), not a fixed share of the window.
/// </summary>
/// <remarks>
/// The digest was bounded to 60 % of the window whatever the head weighed. A long conversation plus a full digest went
/// past the window — refused by LM Studio, cut at its head by Ollama — and the synthesis fell back without a word, on
/// exactly the runs that had gathered the most.
/// </remarks>
public sealed class SynthesisFitsTheWindowTests
{
    private const int Window = 8192;   // tokens, four characters each

    private sealed class RecordingClient(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    private sealed class BulkyResults : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("web_search", "search the web", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            Task.FromResult(string.Join(' ', Enumerable.Range(0, 400).Select(i => $"leek{i}")));   // ~2 700 characters
    }

    private static ChatTurnResult Call(int i) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction("web_search",
            JsonDocument.Parse($$"""{"query":"leek pie {{i}}"}""").RootElement.Clone()))], 0, 0);

    private static async Task<List<ChatMessageDto>> SynthesisRequestAsync(int headChars)
    {
        var script = new List<ChatTurnResult> { new("""{"goal":"find it","steps":[{"i":1,"desc":"search"}]}""", null, 0, 0) };
        script.AddRange(Enumerable.Range(0, 4).Select(Call));
        script.Add(new ChatTurnResult("<think>done</think>", null, 0, 0));    // no printable answer → synthesis
        script.Add(new ChatTurnResult("The recipe.", null, 0, 0));
        var client = new RecordingClient(script);
        var orch = new AgentOrchestrator(client, new InferpalConfig
        {
            ContextWindowSize = Window, CompactionEnabled = false, AgentMaxIterations = 6, AgentModeEnabled = true,
        });

        var history = new List<ChatMessageDto>
        {
            new("system", "you are an assistant " + new string('x', headChars / 2)),
            new("user", "earlier question " + new string('y', headChars / 2)),
            new("assistant", "earlier answer"),
            new("user", "Give me the leek pie recipe"),
        };
        var result = await orch.RunAsync(model: "m", history: history, tools: new BulkyResults(),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
            onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.Equal("The recipe.", result.FinalResponse);                 // witness: the synthesis ran and answered
        return client.Seen.Last();
    }

    [Fact]
    public async Task AHeavyConversation_LeavesTheDigestOnlyWhatTheWindowHas()
    {
        var request = await SynthesisRequestAsync(headChars: 24_000);

        var sent = AgentOrchestrator.EstimateChars(request);
        Assert.True(sent <= Window * 4, $"the synthesis request is {sent} characters, the window {Window * 4}");
        Assert.Contains("earlier tool result(s) of this run did not fit", request[^1].Content);   // the cut is said
    }

    [Fact]
    public async Task ALightConversation_KeepsTheWholeDigest()
    {
        // Reference arm: with room left, nothing of the run is dropped.
        var request = await SynthesisRequestAsync(headChars: 200);

        Assert.DoesNotContain("did not fit", request[^1].Content);
        Assert.Equal(4, request[^1].Content!.Split("### web_search").Length - 1);
    }

    [Fact]
    public void TheBudget_IsWhatTheWindowLeaves_CappedAt60PerCent()
    {
        Assert.Equal(Window * 4 * 6 / 10, AgentOrchestrator.SynthesisDigestBudget(Window, 100, 100));
        Assert.Equal(Window * 4 - Window - 18_000 - 300, AgentOrchestrator.SynthesisDigestBudget(Window, 18_000, 300));
        Assert.Equal(0, AgentOrchestrator.SynthesisDigestBudget(Window, 40_000, 300));
    }
}
