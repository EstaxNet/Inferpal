using System.IO;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A compacted conversation is kept with its summary (<see cref="HistorySummary"/>): restored, its history is the one the
/// model had — the anchors, the summary, then the turns after it. Restored whole, a long conversation comes back over the
/// compaction trigger, and its first question after a restart waits for a new summary of everything, written from a
/// request whose budget no longer reaches the conversation's start.
/// </summary>
public class HistorySummaryKeptTests
{
    private static (string Role, string Content, bool Scaffolding) Shape(ChatMessageDto m) => (m.Role, m.Content ?? "", m.IsScaffolding);

    /// <summary>A conversation as both lists hold it: the model's history (live) and the screen's transcript (saved),
    /// which also keeps the tool steps and the notices.</summary>
    private sealed class Conversation
    {
        public List<ChatMessageDto> History { get; } = [new("system", "sys")];
        public List<SavedMessage> Saved { get; } = [];
        public HistorySummary? Summary { get; private set; }
        private int _turn;

        public void Ask()
        {
            var q = $"question {_turn}";
            History.Add(new ChatMessageDto("user", q));
            Saved.Add(new SavedMessage("user", q));
            Saved.Add(new SavedMessage("tool", "read the file", "read_file"));
        }

        public void Answer()
        {
            var a = $"answer {_turn++} " + new string('a', 200);
            History.Add(new ChatMessageDto("assistant", a));
            Saved.Add(new SavedMessage("assistant", a));
            Saved.Add(new SavedMessage("assistant", "a notice", SessionManager.NoticeMarker));
        }

        /// <summary>The pre-send check, as both front-ends run it: after the question, before the answer.</summary>
        public void Compact(string? summary, int anchors)
        {
            var plan = HistoryCompaction.Decide(History, 1_000, 100_000, keepTurnsConfig: 2, kvAnchorMessages: anchors,
                                                compactionEnabled: true);
            Assert.NotEqual(CompactionAction.None, plan.Action);   // witness: it compacts
            Summary = HistoryCompaction.Apply(History, plan, summary, Summary);
        }

        public void Turns(int n) { for (var i = 0; i < n; i++) { Ask(); Answer(); } }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]   // the default: the anchors end on a question whose answer the summary holds
    public void ACompactedConversation_IsRestoredAsTheModelHadIt_ThriceCompacted(int anchors)
    {
        var c = new Conversation();
        c.Turns(6);
        c.Ask(); c.Compact("FIRST SUMMARY", anchors); c.Answer();
        c.Turns(4);
        c.Ask(); c.Compact(summary: null, anchors); c.Answer();   // a summary that fell back: the turns dropped
        c.Turns(4);
        c.Ask(); c.Compact("SECOND SUMMARY", anchors); c.Answer();
        c.Turns(2);

        var (restored, kept) = SessionManager.BuildRestoredHistory("sys", c.Saved, c.Summary);

        Assert.NotNull(c.Summary);
        Assert.Equal(c.Summary, kept);
        Assert.Equal(c.History.Select(Shape), restored.Select(Shape));
        Assert.Contains(restored, m => m.Content == "SECOND SUMMARY");
    }

    /// <summary>Turns dropped without a summary (compaction off, or its fallback) are kept as the model had them too — the
    /// marker in their place —, and a summary written after them still counts the questions they held.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void ATruncatedConversation_IsRestoredAsTheModelHadIt_AndSoIsASummaryAfterIt(int anchors)
    {
        var c = new Conversation();
        c.Turns(6);
        c.Ask(); c.Compact(summary: null, anchors); c.Answer();
        var (afterDrop, keptDrop) = SessionManager.BuildRestoredHistory("sys", c.Saved, c.Summary);
        Assert.True(keptDrop?.Dropped);
        Assert.Equal(c.History.Select(Shape), afterDrop.Select(Shape));

        c.Turns(3);
        c.Ask(); c.Compact("LATER SUMMARY", anchors); c.Answer();
        c.Turns(1);
        var (restored, kept) = SessionManager.BuildRestoredHistory("sys", c.Saved, c.Summary);

        Assert.False(kept?.Dropped ?? true);
        Assert.Equal(c.History.Select(Shape), restored.Select(Shape));
    }

    /// <summary>A summary that does not fit the conversation is not this conversation's (a branch cut before the turns it
    /// stands for, a file edited by hand): every turn is restored, and the summary is not held — or a later save would
    /// place it again.</summary>
    [Theory]
    [InlineData(0, 5)]     // more questions than the conversation holds
    [InlineData(10, 1)]    // more anchors than the turns it stands for
    [InlineData(-5, -1)]   // nonsense
    public void ASummaryThatDoesNotFitTheConversation_IsNotApplied(int anchors, int questions)
    {
        var c = new Conversation();
        c.Turns(3);
        var (restored, kept) = SessionManager.BuildRestoredHistory("sys", c.Saved, new HistorySummary("S", anchors, questions));

        Assert.Null(kept);
        Assert.Equal(SessionManager.BuildRestoredHistory("sys", c.Saved).Select(Shape), restored.Select(Shape));
    }

    /// <summary>The session file keeps it, text, anchors and count.</summary>
    [Fact]
    public async Task TheSummary_SurvivesTheSessionFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"summary-kept-{Guid.NewGuid():N}");
        try
        {
            var store = new ConversationStore(dir);
            var state = new SessionState(Summary: new HistorySummary("what was said", 3, 7), Recap: "recap", RecapInSystem: "older");
            await store.SaveAsync("kept", [new("user", "q")], CancellationToken.None, workspaceRoot: null, state: state);
            Assert.Equal(state, (await store.LoadAsync("kept", CancellationToken.None))!.State);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public partial class HostServerTests
{
    /// <summary>A window's model is the window's (VS Code): only a conversation's own pick — an ACP thread's — is kept with
    /// the conversation.</summary>
    [Fact]
    public async Task AWindowsModel_IsNotKeptWithTheConversation_AThreadsPickIs()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync("models/useForSession", new { model = "window-model" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal("window-model", h.Server.CurrentSession!.Config.DefaultModel);   // witness: it was taken
        Assert.Null(h.Server.CurrentSession!.CaptureState().Model);

        await h.Client.InvokeWithParameterObjectAsync("models/useForSession", new { model = "thread-model", conversation = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal("thread-model", h.Server.CurrentSession!.CaptureState().Model);

        // A new conversation has none of its own (an ACP thread gives its pick back).
        await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>("command/slash", new { text = "/clear" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Null(h.Server.CurrentSession!.CaptureState().Model);
    }

    /// <summary>
    /// ⚠ A restarted host takes from the slot only what belongs to the conversation on screen: one loaded since the slot was
    /// written (the slot is written at the end of a turn) is another conversation, and the slot's summary would be placed
    /// over its first turns, its name and mode carried with it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARestartedHost_TakesFromTheSlot_OnlyForTheConversationItHolds(bool screenContinuesTheSlot)
    {
        var root  = Directory.CreateTempSubdirectory("inferpal-slot-merge-").FullName;
        var store = new ConversationStore(slotOwner: ConversationStore.VsCodeSlot);   // the host's slot
        var held  = new SessionState(TemplateSuffix: "## Mode: X", Summary: new HistorySummary("X SUMMARY", 0, 1),
                                     Recap: "X RECAP", RecapInSystem: "X RECAP");
        try
        {
            await store.AutoSaveAsync([new("user", "x1"), new("assistant", "a1"), new("user", "x2"), new("assistant", "a2")],
                                      CancellationToken.None, root, held);
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            object[] screen = screenContinuesTheSlot
                ? [new { role = "user", content = "x1" }, new { role = "assistant", content = "a1" },
                   new { role = "user", content = "x2" }, new { role = "assistant", content = "a2" },
                   new { role = "user", content = "x3" }]
                : [new { role = "user", content = "z1" }, new { role = "assistant", content = "b1" },
                   new { role = "user", content = "z2" }, new { role = "assistant", content = "b2" }];
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name = "last_session", messages = screen })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var saved = (await store.LoadAutoSaveAsync(root, CancellationToken.None))!;
            Assert.Equal(screen.Length, saved.Messages.Count);   // witness: the host wrote the screen
            Assert.Equal(screenContinuesTheSlot ? held : SessionState.New, saved.State);
        }
        finally
        {
            try { await store.ForgetAutoSaveAsync(root, CancellationToken.None); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>The recap the system message carried comes back with the summary it went with; restored whole, the turns it
    /// tells of are all there again, and it is left out.</summary>
    [Fact]
    public async Task TheRecapInTheSystemMessage_ComesBackWithItsSummary_Only()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var store = new ConversationStore();
        List<SavedMessage> messages = [new("user", "q1"), new("assistant", "a1"), new("user", "q2"), new("assistant", "a2")];
        var compacted = $"recap-compacted-{Guid.NewGuid():N}";
        var whole     = $"recap-whole-{Guid.NewGuid():N}";
        try
        {
            await store.SaveAsync(compacted, messages, CancellationToken.None, workspaceRoot: null,
                                  state: new SessionState(Summary: new HistorySummary("S", 0, 1), RecapInSystem: "RECAP-OF-THE-SESSION"));
            await store.SaveAsync(whole, messages, CancellationToken.None, workspaceRoot: null,
                                  state: new SessionState(RecapInSystem: "RECAP-OF-THE-SESSION"));
            async Task<string> SystemAfterLoading(string name)
            {
                await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>("session/load", new { name })
                    .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                return h.Server.CurrentSession!.History[0].Content ?? "";
            }

            Assert.Contains("RECAP-OF-THE-SESSION", await SystemAfterLoading(compacted), StringComparison.Ordinal);
            Assert.DoesNotContain("RECAP-OF-THE-SESSION", await SystemAfterLoading(whole), StringComparison.Ordinal);
            Assert.Contains("q1", string.Concat(h.Server.CurrentSession!.History.Select(m => m.Content)), StringComparison.Ordinal);
        }
        finally { store.Delete(compacted); store.Delete(whole); }
    }

    /// <summary>
    /// ⚠ A conversation compacted before a restart comes back compacted: its first question after the restart asks for no
    /// summary. VS Code's host saves the screen to the slot before it loads it back — the summary is taken from the slot.
    /// </summary>
    [Fact]
    public async Task AConversationCompactedBeforeARestart_ComesBackCompacted_AndAsksForNoNewSummary()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-summary-kept-").FullName;
        static void Config(InferpalConfig cfg)
        {
            cfg.ContextWindowSize = 8_192; cfg.CompactionEnabled = true; cfg.ContextWindowKeepTurns = 2;
            cfg.KvCacheAnchorMessages = 2; cfg.OodaTurnThreshold = 0;
        }
        var screen = new List<object>();
        for (var turn = 0; turn < 10; turn++)
        {
            screen.Add(new { role = "user",      content = $"question {turn}" });
            screen.Add(new { role = "assistant", content = $"answer {turn} " + (turn == 0 ? "" : new string('a', 4_000)) });
        }
        var summaries = 0;
        var sent = new List<ChatMessageDto>();
        Task<ChatTurnResult> Answer(List<ChatMessageDto> history)
        {
            // A summarizing request carries no system prompt: one message, the transcript.
            if (history[0].Role != "system") { summaries++; return Task.FromResult(new ChatTurnResult("EARLIER TURNS, SUMMARISED", null, 1, 1)); }
            sent = [.. history];
            return Task.FromResult(new ChatTurnResult("ok", null, 1, 1));
        }
        async Task Save(Harness h) => await h.Client.InvokeWithParameterObjectAsync<object?>("session/save",
            new { name = "last_session", messages = screen }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        async Task Load(Harness h) => await h.Client.InvokeWithParameterObjectAsync<object?>("session/load",
            new { name = "last_session" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        async Task Send(Harness h, string q) => await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send",
            new { prompt = q, agentMode = false }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        try
        {
            List<ChatMessageDto> live;
            using (var first = CreateHarness(Config))
            {
                await first.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                await Save(first);
                await Load(first);
                first.Fake.OnChatRequest = (_, history, _, _) => Answer(history);
                await Send(first, "next");
                Assert.Equal(1, summaries);   // witness: the conversation was compacted
                live = [.. first.Server.CurrentSession!.History];
                screen.Add(new { role = "user", content = "next" });
                screen.Add(new { role = "assistant", content = "ok" });
                await Save(first);   // the end of the turn
            }

            using var second = CreateHarness(Config);
            await second.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await Save(second);   // as VS Code's restarted host: the screen first, then back
            await Load(second);
            var restored = second.Server.CurrentSession!.History;
            Assert.Equal(live.Skip(1).Select(m => (m.Role, m.Content)), restored.Skip(1).Select(m => (m.Role, m.Content)));

            second.Fake.OnChatRequest = (_, history, _, _) => Answer(history);
            await Send(second, "after the restart");
            Assert.Equal(1, summaries);
            Assert.Contains(sent, m => m.Content?.StartsWith("EARLIER TURNS, SUMMARISED", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(sent, m => m.Content == "question 5");
        }
        finally
        {
            try { await new ConversationStore(slotOwner: ConversationStore.VsCodeSlot).ForgetAutoSaveAsync(root, CancellationToken.None); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
