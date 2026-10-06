using System.Text.Json;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Agent;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  Two different unreadable calls are two calls, not a loop.
//
//  On the real-condition battery (devstral, hard-signature) a derailed apply_edits was refused, the model
//  wrote a DIFFERENT one — and the run was stopped as "repeating the same tool calls": an unreadable call
//  carries the empty object as its arguments, so the loop detector compared `apply_edits:{}` with
//  `apply_edits:{}`. Two runs of ten ended there; a third attempt is where another run succeeded.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class DerailedRetryIsNotALoopTests
{
    private const string Head =
        "{\"edits\": [{\"path\": \"/ws/src/Shop/Inventory.cs\", \"old_content\": \"    public void Reserve(string sku, int quantity)\\n"
        + "    {\\n        if (Stock(sku) < quantity)\\n            throw new InvalidOperationException($\"";

    // The two attempts devstral wrote one after the other, as far as the client read them.
    private const string FirstAttempt  = Head + "} ,\"Not enough stock";
    private const string SecondAttempt = Head + "\t, \"Not enough stock for '{sku}'.\"\t: \"";

    private static ToolCallDto Derailed(string raw) =>
        new(ToolCallArguments.Parse("apply_edits", raw)
            with { BrokenShape = "an element of 'edits' began as text where the tool reads an object" });

    [Fact]
    public void TwoDifferentDerailedCalls_AreNotALoop_TheSameOneTwiceIs()
    {
        var first  = Derailed(FirstAttempt);
        var second = Derailed(SecondAttempt);
        Assert.Equal(first.Function.Arguments.GetRawText(), second.Function.Arguments.GetRawText());   // WITNESS: both {}

        var counts = new Dictionary<string, int>();
        Assert.False(AgentLoopPolicy.IsLoop(counts, [first]));
        Assert.False(AgentLoopPolicy.IsLoop(counts, [second]));

        // Reference arm: the same unreadable call written twice IS a loop.
        var again = new Dictionary<string, int>();
        Assert.False(AgentLoopPolicy.IsLoop(again, [Derailed(FirstAttempt)]));
        Assert.True(AgentLoopPolicy.IsLoop(again, [Derailed(FirstAttempt)]));
    }

    private sealed class Scripted(IEnumerable<ChatTurnResult> script) : IOllamaChatClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);

        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools,
            Action<string>? onToken, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null) =>
            Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
    }

    private sealed class Registry : IToolRegistry
    {
        public List<string> Ran { get; } = [];
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [new("function", new ToolFunction("apply_edits", "edit", new { })), new("function", new ToolFunction("apply_diff", "edit", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Ran.Add(name);
            return Task.FromResult("Change applied.");
        }
    }

    [Fact]
    public async Task AfterTwoDifferentDerailedEdits_TheThirdAttemptRuns()
    {
        var diff = new ChatTurnResult(string.Empty,
            [new ToolCallDto(new ToolCallFunction("apply_diff", JsonSerializer.SerializeToElement(
                new { path = "/ws/src/Shop/Inventory.cs", old_content = "public void Reserve", new_content = "public bool Reserve" })))],
            0, 0);
        var fake = new Scripted(
        [
            new ChatTurnResult("""{"goal":"change Reserve","steps":[{"i":1,"desc":"edit","tool":"apply_edits"}]}""", null, 0, 0),
            new ChatTurnResult(string.Empty, [Derailed(FirstAttempt)], 0, 0),
            new ChatTurnResult(string.Empty, [Derailed(SecondAttempt)], 0, 0),
            diff,
            new ChatTurnResult("Reserve now returns a bool.", null, 0, 0),
        ]);
        var tools = new Registry();

        var result = await new AgentOrchestrator(fake, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = false, AgentMaxIterations = 8 })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Make Reserve return a bool.")],
                      tools: tools, onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null,
                      onToolExecuted: null, onStreamReset: null, ct: CancellationToken.None);

        Assert.False(result.WasLoopDetected);
        Assert.Equal(["apply_diff"], tools.Ran);                           // the derailed ones never ran; the third did
        Assert.Equal("Reserve now returns a bool.", result.FinalResponse);
    }
}
