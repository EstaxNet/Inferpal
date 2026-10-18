using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A note typed while a run works is handed to the run at its next boundary — after the tool calls in progress, before
/// the next request — as the last text the model reads; a note no boundary took comes back, never lost; and the
/// conversation keeps the notes the model read folded into their question, live and restored alike.
/// </summary>
public class RunSteeringTests
{
    // ── RunSteering itself ──────────────────────────────────────────────────

    [Fact]
    public void ANote_IsTheLastMessage_UnderTheRunsMarker_AndRecordedWithItsStep()
    {
        var applied = new List<AppliedNote>();
        var steering = new RunSteering { OnApplied = applied.Add };
        Assert.True(steering.TryPush(new UserNote("n1", "Use ILogger, not Console")));
        var messages = new List<ChatMessageDto> { new("system", "s"), new("user", "go") };

        Assert.Equal(1, steering.AddTo(messages, afterCalls: 3));

        var note = messages[^1];
        Assert.Equal("user", note.Role);
        Assert.True(note.IsUserNote);
        Assert.Contains("Use ILogger, not Console", note.Content);
        Assert.Contains($"[USER NOTE {steering.Token}]", note.Content);
        Assert.Equal(new AppliedNote("n1", "Use ILogger, not Console", 3), Assert.Single(applied));
        Assert.Equal(applied, steering.Applied);
        // Taken once: the next boundary adds nothing.
        Assert.Equal(0, steering.AddTo(messages, afterCalls: 4));
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public void TwoNotesBeforeOneBoundary_GoInOneMessage_InTheirOrder()
    {
        var steering = new RunSteering();
        steering.TryPush(new UserNote("a", "first"));
        steering.TryPush(new UserNote("b", "second"));
        var messages = new List<ChatMessageDto>();

        Assert.Equal(2, steering.AddTo(messages, 1));

        var text = Assert.Single(messages).Content!;
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Close_GivesBackWhatNoBoundaryTook_AndRefusesLaterNotes()
    {
        var steering = new RunSteering();
        steering.TryPush(new UserNote("a", "late"));

        Assert.Equal("a", Assert.Single(steering.Close()).Id);
        Assert.False(steering.TryPush(new UserNote("b", "after the end")));
        Assert.Empty(steering.Close());
        Assert.Empty(steering.Applied);
    }

    [Fact]
    public async Task NotesPushedWhileTheRunEnds_AreNeverLost()
    {
        // Pushes race the boundaries and the close: every note is read, given back, or refused — exactly one of them.
        for (var round = 0; round < 50; round++)
        {
            var steering = new RunSteering();
            var refused  = new ConcurrentBag<string>();
            var messages = new List<ChatMessageDto>();
            using var go = new ManualResetEventSlim();
            var pushers = Enumerable.Range(0, 4).Select(t => Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < 25; i++)
                    if (!steering.TryPush(new UserNote($"{t}-{i}", "x"))) refused.Add($"{t}-{i}");
            })).ToArray();
            var run = Task.Run(() =>
            {
                go.Wait();
                for (var i = 0; i < 10; i++) steering.AddTo(messages, i);
                return steering.Close();
            });
            go.Set();
            await Task.WhenAll(pushers);
            var unapplied = await run;

            var all = steering.Applied.Select(n => n.Id).Concat(unapplied.Select(n => n.Id)).Concat(refused).ToList();
            Assert.Equal(100, all.Count);
            Assert.Equal(100, all.Distinct().Count());
        }
    }

    // ── The orchestrator ────────────────────────────────────────────────────

    private sealed class ScriptedChatClient(IEnumerable<ChatTurnResult> script) : IChatTurnClient
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> SeenMessages { get; } = new();

        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            SeenMessages.Add(new List<ChatMessageDto>(messages));
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    /// <summary>A registry whose calls run <paramref name="onCall"/> first: the user types while the tool works.</summary>
    private sealed class TypingToolRegistry(Action<int> onCall) : IToolRegistry
    {
        private int _calls;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
        [
            new("function", new ToolFunction("read_file", "read", new { })),
            new("function", new ToolFunction("write_file", "write", new { })),
        ];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            onCall(++_calls);
            return Task.FromResult(name == "write_file" ? "written" : "file content");
        }
    }

    private static ChatTurnResult Call(string tool, string args) =>
        new(string.Empty, [new ToolCallDto(new ToolCallFunction(tool, JsonDocument.Parse(args).RootElement.Clone()))], 0, 0);

    private static ChatTurnResult Text(string text) => new(text, null, 0, 0);

    private const string TwoStepPlan = """{"goal":"g","steps":[{"i":1,"desc":"read","tool":"read_file"},{"i":2,"desc":"write","tool":"write_file"}]}""";

    private static InferpalConfig Config() =>
        new() { ContextWindowSize = 32768, CompactionEnabled = false, AgentMaxIterations = 8, AgentModeEnabled = true };

    private static Task<OrchestratorResult> RunOrchestratorAsync(ScriptedChatClient client, IToolRegistry tools, RunSteering? steering) =>
        new AgentOrchestrator(client, Config()).RunAsync(
            model: "m", history: [new("system", "sys"), new("user", "Add the Invoice class")], tools: tools,
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null,
            onStreamReset: null, ct: CancellationToken.None, steering: steering);

    [Fact]
    public async Task Orchestrator_ANoteTypedDuringATool_IsTheLastTextOfTheNextRequest()
    {
        var steering = new RunSteering();
        var client = new ScriptedChatClient([
            Text(TwoStepPlan),
            Call("read_file", """{"path":"Cart.cs"}"""),
            Call("write_file", """{"path":"Billing/Invoice.cs","content":"x"}"""),
            Text("Done."),
        ]);
        var tools = new TypingToolRegistry(call => { if (call == 1) steering.TryPush(new UserNote("n", "Put it in Billing/")); });

        var result = await RunOrchestratorAsync(client, tools, steering);

        Assert.Equal("Done.", result.FinalResponse);
        // Request 0 is the plan, 1 the first action, 2 the action after the read: the note is its last message.
        var afterRead = client.SeenMessages[2];
        Assert.True(afterRead[^1].IsUserNote);
        Assert.Contains("Put it in Billing/", afterRead[^1].Content);
        Assert.DoesNotContain(client.SeenMessages[1], m => m.IsUserNote);
        Assert.Equal(1, Assert.Single(steering.Applied).AfterCalls);
        Assert.Empty(steering.Close());
    }

    [Fact]
    public async Task Orchestrator_TheWriteANoteCorrects_SentAgain_IsNotALoop()
    {
        // Told "not that way", the model sends the refused write again — with the note in between, the second write is
        // the correction, not a stall.
        var write = Call("write_file", """{"path":"Invoice.cs","content":"x"}""");
        Task<OrchestratorResult> RunAsync(bool withNote)
        {
            var steering = new RunSteering();
            var client = new ScriptedChatClient([Text(TwoStepPlan), write, write, Text("Done.")]);
            return RunOrchestratorAsync(client,
                new TypingToolRegistry(call => { if (withNote && call == 1) steering.TryPush(new UserNote("n", "again")); }), steering);
        }

        var steered = await RunAsync(withNote: true);
        Assert.False(steered.WasLoopDetected);
        Assert.Equal("Done.", steered.FinalResponse);

        var reference = await RunAsync(withNote: false);   // reference arm: the same repeat without a note IS a loop
        Assert.True(reference.WasLoopDetected);
    }

    [Fact]
    public async Task Orchestrator_ANoteWaitingWhenThePlanIsDone_KeepsTheToolsOpen()
    {
        // One step: after it the observe prompt would ask for the answer "WITHOUT calling any more tools" — a note that
        // asks for one more change must not be read under that order.
        var steering = new RunSteering();
        var client = new ScriptedChatClient([
            Text("""{"goal":"g","steps":[{"i":1,"desc":"read","tool":"read_file"}]}"""),
            Call("read_file", """{"path":"Cart.cs"}"""),
            Text("Done."),
        ]);
        var tools = new TypingToolRegistry(_ => steering.TryPush(new UserNote("n", "Also add a test")));

        var result = await RunOrchestratorAsync(client, tools, steering);

        var afterRead = client.SeenMessages[2];
        Assert.DoesNotContain(afterRead, m => (m.Content ?? "").Contains("WITHOUT calling any more tools", StringComparison.Ordinal));
        Assert.True(afterRead[^1].IsUserNote);
        // And a prose reply to the note IS the answer: never sent back as a narration ("call the first tool").
        Assert.Equal("Done.", result.FinalResponse);
        Assert.Equal(3, client.SeenMessages.Count);
        Assert.DoesNotContain(client.SeenMessages.SelectMany(m => m),
                              m => m.Content == Inferpal.Services.Prompting.ModelPrompts.AgentNudgeToolCall);
    }

    [Fact]
    public void LongNotes_NeverPushTheRequestOutOfWhatThePromptsQuote()
    {
        // The answer-now prompt and the synthesis quote the request through TaskSnippet — its last 400 characters. Cut as
        // ONE text "request + notes", two long notes left only "…" and the notes: the answer addressed the corrections,
        // not the request. Each part is cut on its own.
        var question = "Add an Invoice class to the Shop project";
        var notes = new[] { new string('a', 300), new string('b', 300) };

        var quoted = ChatTurnPolicy.WithUserNotes(AgentOrchestrator.TaskSnippet(question), notes.Select(AgentOrchestrator.TaskSnippet));

        Assert.StartsWith(question, quoted, StringComparison.Ordinal);
        Assert.Contains(new string('b', 300), quoted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orchestrator_TheAnswerNowPrompt_QuotesTheRequestWithItsNotes()
    {
        // Read, then the plan is done: the answer-now prompt quotes the request — which by then carries the note. A long
        // note never pushes the request itself out of the quote (each part is cut on its own).
        var steering = new RunSteering();
        var client = new ScriptedChatClient([
            Text(TwoStepPlan),
            Call("read_file", """{"path":"Cart.cs"}"""),
            Call("write_file", """{"path":"Billing/Invoice.cs","content":"x"}"""),
            Text("Done."),
        ]);
        var longNote = "Put it in Billing/. " + string.Join(" ", Enumerable.Repeat("keep the namespace Shop.Billing", 20));
        var tools = new TypingToolRegistry(call => { if (call == 1) steering.TryPush(new UserNote("n", longNote)); });

        await RunOrchestratorAsync(client, tools, steering);

        var answerNow = client.SeenMessages[3].Last(m => (m.Content ?? "").Contains("All plan steps are complete", StringComparison.Ordinal));
        Assert.Contains("Shop.Billing", answerNow.Content);
        Assert.Contains("Add the Invoice class", answerNow.Content);
    }

    [Fact]
    public async Task Orchestrator_ANoteTypedDuringTheFinalAnswer_ComesBackUnread()
    {
        var steering = new RunSteering();
        var client = new FinalAnswerTypingClient(steering);

        await new AgentOrchestrator(client, Config()).RunAsync(
            "m", [new("system", "sys"), new("user", "go")], new TypingToolRegistry(_ => { }),
            _ => { }, null, null, null, null, null, CancellationToken.None, steering: steering);

        Assert.Empty(steering.Applied);
        Assert.Equal("late", Assert.Single(steering.Close()).Text);
    }

    /// <summary>Plans one read, reads, then answers — and the user types while that answer is written.</summary>
    private sealed class FinalAnswerTypingClient(RunSteering steering) : IChatTurnClient
    {
        private int _turn;
        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            switch (++_turn)
            {
                case 1: return Task.FromResult(Text("""{"goal":"g","steps":[{"i":1,"desc":"read","tool":"read_file"}]}"""));
                case 2: return Task.FromResult(Call("read_file", """{"path":"a"}"""));
                default:
                    steering.TryPush(new UserNote("n", "late"));
                    return Task.FromResult(Text("Here is the answer."));
            }
        }
    }

    [Fact]
    public async Task Orchestrator_ANotePushedDuringThePlan_IsReadBeforeTheFirstAction()
    {
        var steering = new RunSteering();
        var client = new PlanTypingClient(steering);

        await new AgentOrchestrator(client, Config()).RunAsync(
            "m", [new("system", "sys"), new("user", "go")], new TypingToolRegistry(_ => { }),
            _ => { }, null, null, null, null, null, CancellationToken.None, steering: steering);

        Assert.True(client.FirstAction![^1].IsUserNote);
        Assert.Equal(0, Assert.Single(steering.Applied).AfterCalls);
    }

    private sealed class PlanTypingClient(RunSteering steering) : IChatTurnClient
    {
        private int _turn;
        public List<ChatMessageDto>? FirstAction { get; private set; }
        public Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal, string? toolChoice = null,
            Action<string>? onThinking = null)
        {
            switch (++_turn)
            {
                case 1:
                    steering.TryPush(new UserNote("n", "Billing folder"));
                    return Task.FromResult(Text("""{"goal":"g","steps":[{"i":1,"desc":"write","tool":"write_file"}]}"""));
                case 2:
                    FirstAction = new List<ChatMessageDto>(messages);
                    return Task.FromResult(Text("Done."));
                default:
                    return Task.FromResult(Text("Done."));
            }
        }
    }

    [Fact]
    public async Task ASummaryOfTheRunsEarlierContext_KeepsTheNotesWordForWord()
    {
        // The run's middle is summarised when it nears the window: a correction the summary would paraphrase — or
        // leave out — is no longer the one the user gave.
        var summarizer = new ScriptedChatClient([Text("SUMMARY OF OLD TURNS")]);
        var orch = new AgentOrchestrator(summarizer, new InferpalConfig { ContextWindowSize = 8192, CompactionEnabled = true });
        var note = new ChatMessageDto("user", "[USER NOTE ab12] … Use ILogger, not Console …") { IsUserNote = true };
        var messages = new List<ChatMessageDto> { new("system", new string('s', 4000)), new("user", "go") };
        messages.AddRange(Enumerable.Range(0, 4).Select(_ => new ChatMessageDto("tool", new string('x', 8000))));
        messages.Add(note);
        messages.AddRange(Enumerable.Range(0, 6).Select(_ => new ChatMessageDto("tool", new string('x', 8000))));

        await orch.CompactRunContextAsync(messages, anchorCount: 2, model: "m", alreadySummarized: false,
                                          onStep: _ => { }, ct: CancellationToken.None);

        Assert.Contains(messages, m => m.Role == "assistant" && (m.Content ?? "").StartsWith("SUMMARY OF OLD TURNS", StringComparison.Ordinal));   // witness: summarised
        Assert.Same(note, Assert.Single(messages, m => m.IsUserNote));
    }

    // ── The basic loop ──────────────────────────────────────────────────────

    private sealed class ScriptedBasicClient(InferpalConfig config, IEnumerable<ChatTurnResult> script) : OllamaClient(config)
    {
        private readonly Queue<ChatTurnResult> _script = new(script);
        public List<List<ChatMessageDto>> SeenMessages { get; } = [];

        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null, Action<string?>? onWaiting = null)
        {
            SeenMessages.Add(new List<ChatMessageDto>(messages));
            return Task.FromResult(_script.Count > 1 ? _script.Dequeue() : _script.Peek());
        }
    }

    [Fact]
    public async Task BasicLoop_ANoteTypedDuringATool_IsTheLastTextOfTheNextRequest_AndRestartsTheLoopCount()
    {
        var steering = new RunSteering();
        var write = Call("write_file", """{"path":"Invoice.cs","content":"x"}""");
        var client = new ScriptedBasicClient(new InferpalConfig { AgentMaxIterations = 5, ContextWindowSize = 32768 },
            [write, write, Text("Done.")]);
        var tools = new TypingToolRegistry(call => { if (call == 1) steering.TryPush(new UserNote("n", "Billing/ please")); });

        var result = await client.RunAgentAsync("m", [new("system", "sys"), new("user", "go")], tools,
            _ => { }, null, CancellationToken.None, steering: steering);

        Assert.False(result.WasLoopDetected);
        Assert.Equal("Done.", result.FinalResponse);
        Assert.True(client.SeenMessages[1][^1].IsUserNote);
        Assert.Contains("Billing/ please", client.SeenMessages[1][^1].Content);
        Assert.Equal(1, Assert.Single(steering.Applied).AfterCalls);
    }

    [Fact]
    public void ANoteFollowingAToolResult_IsFoldedIntoIt_ForStrictTemplates()
    {
        // What the clients send: the note joins the tool result (a user turn after a tool result is refused by strict
        // templates) — its marker is what says it is not tool output.
        var steering = new RunSteering();
        steering.TryPush(new UserNote("n", "Billing/"));
        var messages = new List<ChatMessageDto> { new("system", "s"), new("user", "go"), new("tool", "file content") };
        steering.AddTo(messages, 1);

        var sent = Inferpal.Services.Inference.InferenceProviderBase.CoalesceConsecutiveRoles(messages);

        var last = sent[^1];
        Assert.Equal("tool", last.Role);
        Assert.StartsWith("file content", last.Content);
        Assert.EndsWith($"[/USER NOTE {steering.Token}]", last.Content);
    }

    // ── The conversation ────────────────────────────────────────────────────

    [Fact]
    public void TheRestoredHistory_FoldsTheNotes_AsTheLiveHistoryDid()
    {
        var live = ChatTurnPolicy.WithUserNotes("Add the Invoice class", ["Put it in Billing/", "Name it Bill"]);
        var saved = new List<SavedMessage>
        {
            new("user", "Add the Invoice class"),
            new("user", "Put it in Billing/", SessionManager.NoteMarker),
            new("tool", "written", "write_file"),
            new("user", "Name it Bill", SessionManager.NoteMarker),
            new("assistant", "Done."),
            new("user", "Thanks"),
            new("user", "and a test", SessionManager.NoteMarker),
            new("assistant", "Added."),
        };

        var restored = SessionManager.BuildRestoredHistory("sys", saved);

        Assert.Equal(["system", "user", "assistant", "user", "assistant"], restored.Select(m => m.Role));
        Assert.Equal(live, restored[1].Content);
        // The last question's notes too: no question follows them.
        Assert.Equal(ChatTurnPolicy.WithUserNotes("Thanks", ["and a test"]), restored[3].Content);
    }

    [Fact]
    public void NotesAfterAQuestionTheModelNeverGot_AreFoldedIntoNoQuestion()
    {
        // A turn whose host was lost: its question is a notice, but a note was read before the loss. Folded into the
        // question before, it rewrote a question asked and answered long ago.
        var saved = new List<SavedMessage>
        {
            new("user", "First question"),
            new("assistant", "First answer"),
            new("user", "Second question", SessionManager.NoticeMarker),
            new("user", "a note read before the host was lost", SessionManager.NoteMarker),
        };

        var restored = SessionManager.BuildRestoredHistory("sys", saved);

        Assert.Equal("First question", restored[1].Content);
        Assert.DoesNotContain(restored, m => (m.Content ?? "").Contains("host was lost", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Orchestrator_ANoteAfterTheAnswerNowPromptWasChosen_ReopensTheTools()
    {
        // The note arrives after the observe decision (here during the stream reset that follows it): the answer-now prompt
        // already sits in the run, and the note was read under "WITHOUT calling any more tools".
        var steering = new RunSteering();
        var pushed = false;
        var client = new ScriptedChatClient([
            Text("""{"goal":"g","steps":[{"i":1,"desc":"read","tool":"read_file"}]}"""),
            Call("read_file", """{"path":"Cart.cs"}"""),
            Text("Done."),
        ]);

        await new AgentOrchestrator(client, Config()).RunAsync(
            model: "m", history: [new("system", "sys"), new("user", "Add the Invoice class")], tools: new TypingToolRegistry(_ => { }),
            onStep: _ => { }, onToken: null, onPlanReady: null, onStepUpdate: null, onToolExecuted: null,
            onStreamReset: () => { if (!pushed) { pushed = true; steering.TryPush(new UserNote("n", "Also add a test")); } },
            ct: CancellationToken.None, steering: steering);

        var afterRead = client.SeenMessages[2];
        Assert.True(afterRead[^1].IsUserNote);   // witness: the note was read at this request
        Assert.DoesNotContain(afterRead, m => (m.Content ?? "").Contains("WITHOUT calling any more tools", StringComparison.Ordinal));
    }

    [Fact]
    public void ANote_IsNeverAQuestion()
    {
        var note = new SavedMessage("user", "Billing/", SessionManager.NoteMarker);
        Assert.False(SessionManager.IsQuestion(note));
        Assert.True(SessionManager.IsNote(note));
        Assert.True(SessionManager.IsQuestion(new SavedMessage("user", "go")));   // reference arm
        Assert.Equal(2, SessionManager.ConversationMessageCount(
            [new SavedMessage("user", "go"), note, new SavedMessage("assistant", "done")]));
    }

    [Fact]
    public void AQuestionAskedAgainWithItsNotes_GetsTheNewOnesUnderTheSameHeading()
    {
        // Regenerate asks the question with its notes, and the user adds one: the live history must read as the restored
        // one, which folds every note of the question under one heading.
        var live = ChatTurnPolicy.WithUserNotes(ChatTurnPolicy.WithUserNotes("go", ["a"]), ["b"]);
        Assert.Equal(ChatTurnPolicy.WithUserNotes("go", ["a", "b"]), live);
        Assert.Single(Regex.Matches(live, Regex.Escape(ChatTurnPolicy.UserNotesHeader)));
    }

    [Fact]
    public void WithUserNotes_NoNote_KeepsTheQuestion()
    {
        Assert.Equal("go", ChatTurnPolicy.WithUserNotes("go", []));
        Assert.Equal("go", ChatTurnPolicy.WithUserNotes("go", ["  "]));
        Assert.Equal($"go\n\n{ChatTurnPolicy.UserNotesHeader}\n- a\n- b\n  c",
                     ChatTurnPolicy.WithUserNotes("go\n", ["a", "b\nc"]));
    }
}
