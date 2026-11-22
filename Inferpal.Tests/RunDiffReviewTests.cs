using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run that wrote files reads its own diff back once before it answers (pari 159): the answer is held, the diff sent
/// with the request, and the answer after it is the one kept. A run that wrote nothing answers at once.
/// </summary>
public class RunDiffReviewTests
{
    private sealed class ScriptedChatClient(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null, Action<string>? onThinking = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    /// <summary>A registry whose one tool "writes" when <paramref name="writes"/>, and gives the run's diff.</summary>
    private sealed class Writing(bool writes) : IToolRegistry
    {
        private int _written;
        public int DiffAsked { get; private set; }
        public IReadOnlyList<ToolDefinition> Definitions { get; } = [new("function", new ToolFunction("apply_diff", "edit", new { }))];
        public DiffInfo? ConsumeDiff() => null;
        public int? WritesInRun => _written;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            if (writes) _written++;
            return Task.FromResult("applied");
        }

        public Task<string?> RunDiffAsync(int budget, CancellationToken ct)
        {
            DiffAsked++;
            return Task.FromResult<string?>(_written > 0 ? "### pricing.py\n-TAX_RATE = 0.20\n+TAX_RATE = 0.25\n" : null);
        }
    }

    private static ChatTurnResult Call() =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction("apply_diff", JsonDocument.Parse("""{"path":"pricing.py"}""").RootElement.Clone()))], 0, 0);

    private static ChatTurnResult[] Script() =>
    [
        new("""{"goal":"change the rate","steps":[{"i":1,"desc":"edit pricing.py"}]}""", null, 0, 0),
        Call(),
        new("The rate is now 25%.", null, 0, 0),
        new("The rate is now 25%, and the test that expected 120 now expects 125.", null, 0, 0),
    ];

    private static Task<OrchestratorResult> Run(ScriptedChatClient client, IToolRegistry tools) =>
        new AgentOrchestrator(client, new InferpalConfig { ContextWindowSize = 32768, CompactionEnabled = false, AgentMaxIterations = 8, AgentModeEnabled = true })
            .RunAsync(model: "m", history: [new("system", "s"), new("user", "Change the tax rate in pricing.py to 25%.")], tools: tools,
                      onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null, onStreamReset: null,
                      ct: CancellationToken.None);

    [Fact]
    public async Task ARunThatWrote_ReadsItsDiffOnce_AndKeepsTheAnswerGivenAfter()
    {
        var client = new ScriptedChatClient(Script());
        var tools = new Writing(writes: true);

        var result = await Run(client, tools);

        var reviews = client.Seen.SelectMany(m => m).Where(m => m.Role == "user" && m.Content.StartsWith("Before you answer", StringComparison.Ordinal))
                            .Select(m => m.Content).Distinct().ToList();
        var review = Assert.Single(reviews);
        Assert.Contains("+TAX_RATE = 0.25", review, StringComparison.Ordinal);
        Assert.Contains("\"Change the tax rate in pricing.py to 25%.\"", review, StringComparison.Ordinal);
        Assert.Contains("the test that expected 120 now expects 125", result.FinalResponse, StringComparison.Ordinal);
        Assert.Equal(1, tools.DiffAsked);
    }

    private sealed class ScriptedOllama(IEnumerable<ChatTurnResult> script) : OllamaClient(new InferpalConfig { ContextWindowSize = 32768 })
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> Seen { get; } = [];

        public override Task<ChatTurnResult> SendChatAsync(string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    private static ChatTurnResult[] ChatScript() =>
    [
        Call(),
        new("The rate is now 25%.", null, 0, 0),
        new("The rate is now 25%, and the test that expected 120 now expects 125.", null, 0, 0),
    ];

    /// <summary>A chat turn asks for the review (RunDiffReview): the basic loop reads its diff back once, the streamed
    /// answer it held emptied through the turn's reset.</summary>
    [Fact]
    public async Task AChatRun_UnderItsTurnsReview_ReadsItsDiff_AndEmptiesTheAnswerItHeld()
    {
        var client = new ScriptedOllama(ChatScript());
        var resets = 0;
        AgentResult result;
        using (Inferpal.Services.Agent.RunDiffReview.Enter(() => resets++))
            result = await client.RunAgentAsync("m", [new("user", "Change the tax rate in pricing.py to 25%.")], new Writing(writes: true),
                                                _ => { }, null, CancellationToken.None);

        Assert.Single(client.Seen.SelectMany(m => m).Where(m => m.Content.StartsWith("Before you answer", StringComparison.Ordinal))
                                 .Select(m => m.Content).Distinct());
        Assert.Equal(1, resets);
        Assert.Contains("now expects 125", result.FinalResponse, StringComparison.Ordinal);
    }

    /// <summary>After the review, a fix narrated and then an empty turn: the answer is the one held, and the narration streamed
    /// since is emptied, so no front-end keeps it as the answer.</summary>
    [Fact]
    public async Task AReviewThatEndsWithoutAnAnswer_GivesBackTheHeldOne_AndEmptiesWhatStreamedSince()
    {
        var client = new ScriptedOllama(
        [
            Call(),
            new("The rate is now 25%.", null, 0, 0),
            new("Removing the debug print.", [.. Call().ToolCalls!], 0, 0),
            new("", null, 0, 0),
        ]);
        var resets = 0;
        AgentResult result;
        using (Inferpal.Services.Agent.RunDiffReview.Enter(() => resets++))
            result = await client.RunAgentAsync("m", [new("user", "Change the tax rate in pricing.py to 25%.")], new Writing(writes: true),
                                                _ => { }, null, CancellationToken.None);

        Assert.Equal("The rate is now 25%.", result.FinalResponse);
        Assert.Equal(2, resets);   // once for the review, once before the held answer is given back
    }

    /// <summary>Reference arm: outside a chat turn's scope (/tdd's fixes, /commit), the basic loop answers at once.</summary>
    [Fact]
    public async Task TheBasicLoop_OutsideAChatTurn_DoesNotReview()
    {
        var client = new ScriptedOllama(ChatScript());

        var result = await client.RunAgentAsync("m", [new("user", "Change the tax rate in pricing.py to 25%.")], new Writing(writes: true),
                                                _ => { }, null, CancellationToken.None);

        Assert.Equal("The rate is now 25%.", result.FinalResponse);
    }

    /// <summary>Reference arm: a run that wrote nothing is never shown a diff, nor is one asked for.</summary>
    [Fact]
    public async Task ARunThatWroteNothing_IsNotHeldForAReview()
    {
        var client = new ScriptedChatClient(Script());
        var tools = new Writing(writes: false);

        await Run(client, tools);

        Assert.DoesNotContain(client.Seen.SelectMany(m => m), m => m.Content.StartsWith("Before you answer", StringComparison.Ordinal));
        Assert.Equal(0, tools.DiffAsked);
    }
}
