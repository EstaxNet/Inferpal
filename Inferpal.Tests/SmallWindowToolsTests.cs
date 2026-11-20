using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A window too small for every tool definition sends the tools used most: at 8 192 tokens the built-in tools alone fill
/// the window, and every request would be refused before the question is read.
/// </summary>
public class SmallWindowToolsTests
{
    private sealed class Named(int descriptionChars, params string[] names) : IToolRegistry
    {
        public List<string> Ran { get; } = [];
        public HashSet<string> TurnedOff { get; } = [];
        public string? TurnedOffRefusal(string name) => TurnedOff.Contains(name) ? $"Error: '{name}' is turned off." : null;
        public IReadOnlyList<ToolDefinition> Definitions { get; } =
            [.. names.Select(n => new ToolDefinition("function", new ToolFunction(n, new string('d', descriptionChars), new { })))];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            Ran.Add(name);
            return Task.FromResult("ran " + name);
        }
    }

    [Theory]
    [InlineData(8_192, 7_900, true)]    // the factory window and the built-in tools
    [InlineData(8_192, 4_096, false)]   // half the window exactly: not cut
    [InlineData(32_768, 7_900, false)]
    [InlineData(0, 7_900, false)]       // a window not known yet cuts nothing
    public void TheCut_IsDecidedOnTheShareOfTheWindow(int window, int toolTokens, bool cuts) =>
        Assert.Equal(cuts, SmallWindowToolRegistry.Cuts(window, toolTokens));

    [Theory]
    [InlineData("ollama", false, 8_192)]              // Ollama is sent its setting: that IS the window
    [InlineData("openai-compatible", true, 8_192)]    // reported by the server
    [InlineData("openai-compatible", false, 0)]       // only the setting standing in: never cut on it
    [InlineData("lmstudio", false, 0)]
    [InlineData("lmstudio", true, 8_192)]             // a model LM Studio says it loaded small
    public void TheCutWindow_IsOneTheServerHas(string provider, bool read, int expected) =>
        Assert.Equal(expected, ContextManager.CutWindow(8_192, read, provider));

    [Fact]
    public async Task ASmallWindow_SendsTheToolsUsedMost_AndWhatThePersonAdded_AndRefusesTheRest()
    {
        var inner = new Named(1, "read_file", "interact_page", "query_database", "github__list_issues", "tool_search",
                              "read_skill_file", "ask_question");
        var tools = new SmallWindowToolRegistry(inner, 8_192);

        Assert.Equal(["read_file", "github__list_issues", "tool_search", "read_skill_file", "ask_question"],
                     tools.Definitions.Select(d => d.Function.Name));
        Assert.Equal("ran read_file", await tools.ExecuteAsync("read_file", default, CancellationToken.None));

        var refused = await tools.ExecuteAsync("interact_page", default, CancellationToken.None);
        Assert.StartsWith("Error: tool 'interact_page' is not offered at this context window (8192 tokens)", refused, StringComparison.Ordinal);
        Assert.Contains("read_file, github__list_issues, tool_search, read_skill_file, ask_question", refused, StringComparison.Ordinal);
        Assert.Equal(["read_file"], inner.Ran);   // the refused call never reached the registry
        // A call written as text to a tool left out is still read as a call, so it reaches that refusal.
        Assert.Contains("interact_page", tools.CallableNames);
    }

    [Fact]
    public void TheCutNotice_NamesWhatIsLeftOut_OncePerMode()
    {
        var turn = new Named(1, "read_file", "interact_page", "query_database", "github__list_issues");
        var said = new HashSet<string>();

        var notice = ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, [], "", "ollama", 8_192, said);

        // Every tool left out is named — the person keeps one by writing its name — with where a larger window is made.
        Assert.Equal(Localization.Strings.MsgToolsCutSmallWindow(8_192, 2, "interact_page, query_database",
                                                                 Localization.Strings.HeadShareRemedyOllama), notice);
        Assert.Null(ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, [], "", "ollama", 8_192, said));
        // A question that names a tool leaves one fewer out: not said again for that.
        Assert.Null(ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, ["interact_page"], "", "ollama", 8_192, said));
        Assert.NotNull(ContextManager.ToolsCutNotice("C:/repo", "granite", 6_144, turn, [], "", "ollama", 8_192, said));   // another window
        Assert.NotNull(ContextManager.ToolsCutNotice("C:/repo", "qwen", 8_192, turn, [], "", "ollama", 8_192, said));      // another model
        // A new conversation is told again.
        var told = new HashSet<string>();
        Assert.NotNull(ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, [], "", "ollama", 8_192, told));
        ContextManager.ForgetCutNotices(told);
        Assert.NotNull(ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, [], "", "ollama", 8_192, told));
        Assert.NotNull(ContextManager.ToolsCutNotice("C:/repo", "granite", 8_192, turn, [],
                                                     ContextManager.ToolsCutModeKey(planMode: true, null), "ollama", 8_192, said));   // another mode
    }

    [Fact]
    public void AToolTheQuestionNames_Stays()
    {
        // /debug's loop names its two tools.
        var inner = new Named(1, "read_file", "debug_control", "debug_inspect", "interact_page");

        var named = SmallWindowToolRegistry.NamedIn("Set a breakpoint with debug_control, then read the stop with debug_inspect.",
                                                    inner.Definitions.Select(d => d.Function.Name)).ToList();
        var sent = new SmallWindowToolRegistry(inner, 8_192, named).Definitions.Select(d => d.Function.Name);

        Assert.Equal(["read_file", "debug_control", "debug_inspect"], sent);
        Assert.Equal(["debug_control"], SmallWindowToolRegistry.NamedIn("use Debug_Control", ["debug_control"]));   // in any case
        // A name inside a longer identifier is not one.
        Assert.Empty(SmallWindowToolRegistry.NamedIn("use my_interact_page_helper", ["interact_page"]));
    }

    [Fact]
    public async Task ANameTheWindowDidNotTakeOut_IsTheWrappedRegistrysToAnswer()
    {
        // Plan mode never sends delete_file: its own refusal ("describe it as a step") answers, not the window's.
        var inner = new Named(1, "read_file", "interact_page");
        var tools = new SmallWindowToolRegistry(inner, 8_192);

        Assert.Equal("ran delete_file", await tools.ExecuteAsync("delete_file", default, CancellationToken.None));
        Assert.StartsWith("Error: tool 'interact_page' is not offered at this context window",
                          await tools.ExecuteAsync("interact_page", default, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AToolThePersonTurnedOff_KeepsItsOwnRefusal()
    {
        // The cause they control: "write its name in a question" would not bring it back.
        var inner = new Named(1, "read_file", "interact_page");
        inner.TurnedOff.Add("query_database");
        var tools = new SmallWindowToolRegistry(inner, 8_192);

        Assert.Equal("Error: 'query_database' is turned off.", tools.TurnedOffRefusal("query_database"));
        Assert.Equal("ran query_database", await tools.ExecuteAsync("query_database", default, CancellationToken.None));
        // Reference arm: a tool the window took out gets the window's refusal.
        Assert.Null(tools.TurnedOffRefusal("interact_page"));
        Assert.StartsWith("Error: tool 'interact_page' is not offered at this context window",
                          await tools.ExecuteAsync("interact_page", default, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AQuestionsRuns_DoNotDecideTheCutAgain()
    {
        // The question kept what it named; deciding again with no question to read would take it out.
        var config = new InferpalConfig { ContextWindowSize = 8_192 };
        var tools = new Named(4_000, "read_file", "interact_page", "query_database", "http_request", "view_page");   // ≈ 5 000 tokens
        Assert.NotSame(tools, await ContextManager.ToolsForWindowAsync(config, null, "m", tools, null, CancellationToken.None));   // witness

        var marked = SmallWindowToolRegistry.MarkDecided(tools);
        Assert.Same(marked, await ContextManager.ToolsForWindowAsync(config, null, "m", marked, null, CancellationToken.None));
        Assert.False(SmallWindowToolRegistry.IsDecided(tools));   // the wrapper is marked, never what it wraps
        // The mark is on that registry, never on the flow: another registry run from here decides its own.
        var queued = new Named(4_000, "read_file", "interact_page", "query_database", "http_request", "view_page");
        Assert.NotSame(queued, await ContextManager.ToolsForWindowAsync(config, null, "m", queued, null, CancellationToken.None));
    }

    [Fact]
    public void ARegistryAlreadyCut_IsNotWrappedAgain()
    {
        // A registry already cut comes back as it is: a command's run is never cut twice.
        var inner = new Named(2_400, "read_file", "interact_page", "query_database", "http_request");
        var cut = SmallWindowToolRegistry.For(inner, 4_096, null);
        Assert.NotSame(inner, cut);   // witness: the first pass cuts

        Assert.Same(cut, SmallWindowToolRegistry.For(cut, 1_024, null));   // still over half: nothing more to cut
    }

    private static List<ChatMessageDto> FourTurns()
    {
        var history = new List<ChatMessageDto> { new("system", "s") };
        for (var i = 0; i < 4; i++)
            history.AddRange([new("user", $"question {i} " + new string('w', 4 * 350)), new("assistant", new string('a', 4 * 350))]);
        history.Add(new("user", "last question"));
        return history;
    }

    [Fact]
    public async Task ThePlan_MakesRoomForTheToolsTheTurnSends_NotForEveryDefinition()
    {
        // A conversation that fits beside the kept tools and not beside every definition: cut, nothing compacted.
        var config = new InferpalConfig { ContextWindowSize = 8_192, CompactionEnabled = false };
        var fake = new FakeInferenceProvider();

        var cut = await ContextManager.PrepareAsync(FourTurns(), config, fake, lastPromptTokens: 3_100, onStep: _ => { },
                                                    ct: CancellationToken.None, model: "m", toolTokens: 7_900, keptToolTokens: () => 2_000);
        Assert.True(cut.ToolsCut);
        Assert.Equal(ContextOutcome.None, cut.Outcome);

        // Reference arm: the same conversation and every definition, no kept count — the history no longer fits.
        var whole = await ContextManager.PrepareAsync(FourTurns(), config, fake, lastPromptTokens: 3_100, onStep: _ => { },
                                                      ct: CancellationToken.None, model: "m", toolTokens: 7_900);
        Assert.False(whole.ToolsCut);
        Assert.NotEqual(ContextOutcome.None, whole.Outcome);
    }

    [Fact]
    public async Task ASettingThatOnlyStandsInForTheWindow_CutsNothing()
    {
        // An OpenAI-compatible server that does not say its window: the 8 192 of the setting is a guess.
        var config = new InferpalConfig { Provider = "openai-compatible", ContextWindowSize = 8_192, CompactionEnabled = false };
        var fake = new FakeInferenceProvider { LoadedContextWindow = null };
        var unread = await ContextManager.PrepareAsync(FourTurns(), config, fake, 3_100, _ => { }, CancellationToken.None,
                                                       model: "m", toolTokens: 7_900, keptToolTokens: () => 2_000);
        Assert.False(unread.ToolsCut);

        fake.LoadedContextWindow = 8_192;   // reference arm: the server says it
        var read = await ContextManager.PrepareAsync(FourTurns(), config, fake, 3_100, _ => { }, CancellationToken.None,
                                                     model: "m", toolTokens: 7_900, keptToolTokens: () => 2_000);
        Assert.True(read.ToolsCut);
    }

    private sealed class Seen(InferpalConfig config, int? loaded) : OpenAiCompatibleClient(config)
    {
        public List<string> Sent { get; } = [];

        public override Task<ChatTurnResult> SendChatAsync(
            string model, List<ChatMessageDto> messages, IToolRegistry tools, Action<string>? onToken,
            ReasoningEffort effort, CancellationToken ct, TaskComplexity complexity = TaskComplexity.Normal,
            string? toolChoice = null, Action<string>? onThinking = null, Action<ModelWait?>? onWaiting = null)
        {
            Sent.AddRange(tools.Definitions.Select(d => d.Function.Name));
            return Task.FromResult(new ChatTurnResult("done", null, 0, 0));
        }

        public override Task<int?> GetLoadedContextWindowAsync(string model, CancellationToken ct) => Task.FromResult(loaded);
    }

    [Theory]
    [InlineData(8_192, true)]
    [InlineData(32_768, false)]   // reference arm: room for every definition
    public async Task ACommandsRun_IsCutLikeAQuestion(int loaded, bool cut)
    {
        // The run of /task, /tdd or /fix-build plans nothing: the loop itself sends what fits.
        var config = new InferpalConfig
        {
            Provider = "openai-compatible", BaseUrl = "http://127.0.0.1:9", ContextWindowSize = loaded, AgentModeEnabled = true,
        };
        var client = new Seen(config, loaded);
        var tools = new Named(2_400, "read_file", "interact_page", "query_database", "http_request", "view_page",
                              "edit_notebook", "ocr", "query_data");   // ≈ 4 800 tokens of definitions
        var steps = new List<string>();

        await client.RunAgentAsync("m", [new ChatMessageDto("system", "s"), new ChatMessageDto("user", "go")], tools,
                                   onStep: steps.Add, onToken: null, CancellationToken.None);

        Assert.Contains("read_file", client.Sent);
        Assert.Equal(!cut, client.Sent.Contains("interact_page"));
        // The run says what it leaves out, on its status line (a /task journal keeps it).
        Assert.Equal(cut, steps.Contains(Localization.Strings.MsgToolsCutSmallWindowRun(
            loaded, 7, "interact_page, query_database, http_request, view_page, edit_notebook, ocr, …",
            Localization.Strings.HeadShareRemedyServer)));
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task ToolsList_SaysWhatASmallWindowLeavesOut_AndWeighsWhatIsSent()
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 8_192);
        await h.InitializeAsync();
        var tools = h.Server.CurrentSession!.Tools;

        var cut = Services.Presentation.ToolListPresenter.Build(tools, tools, 8_192, ["view_page"]);
        var rows = cut.Groups.SelectMany(g => g.Rows).ToList();

        Assert.Equal("cutByWindow", rows.Single(r => r.Name == "interact_page").State);
        Assert.Equal("sent", rows.Single(r => r.Name == "view_page").State);   // named: kept
        Assert.Equal("sent", rows.Single(r => r.Name == "read_file").State);
        var sent = new SmallWindowToolRegistry(tools, 8_192, ["view_page"]).Definitions;
        Assert.Contains(Localization.Strings.ToolListSent(sent.Count, Services.Presentation.SettingsWidgets.Amount(RequestSize.ToolTokens(sent))),
                        cut.Summary);
        Assert.Contains(Localization.Strings.ToolListCutByWindow(rows.Count(r => r.State == "cutByWindow"), 8_192), cut.Summary);

        // Reference arm: a window that holds every definition lists nothing as left out.
        Assert.DoesNotContain(Services.Presentation.ToolListPresenter.Build(tools, tools, 32_768).Groups.SelectMany(g => g.Rows),
                              r => r.State == "cutByWindow");
        // Plan mode sends fewer tools than half the window: decided on that registry, as the turn decides, nothing is cut.
        Assert.True(RequestSize.ToolTokens(new PlanModeToolRegistry(tools).Definitions) * 2 <= 8_192);   // witness of that
        Assert.DoesNotContain(Services.Presentation.ToolListPresenter.Build(tools, new PlanModeToolRegistry(tools), 8_192)
                                  .Groups.SelectMany(g => g.Rows), r => r.State == "cutByWindow");

        // The Tools page of the host decides it on the window the next question is cut for (here the factory 8 192, Ollama).
        var page = await Call<SettingsToolsDto>(h, "settings/tools", new { });
        Assert.Contains(page.List.Groups.SelectMany(g => g.Rows), r => r.Name == "interact_page" && r.State == "cutByWindow");
    }

    [Fact]
    public async Task AToolNamedBeforeItIsOffered_IsKeptForWhenItIs()
    {
        // interact_window is offered once the app it acts on runs, mid-run: named before, it must still be there then.
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 8_192);
        h.Fake.RunAgentThroughChat = false;
        h.Fake.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("done", [], history));
        await h.InitializeAsync();
        var s = h.Server.CurrentSession!;
        Assert.DoesNotContain(s.Tools.Definitions, d => d.Function.Name == "interact_window");   // witness: not offered now

        Assert.Null((await Call<ChatSendResult>(h, "chat/send", new { prompt = "start the app, then click Save with interact_window", agentMode = false })).Error);

        Assert.Contains("interact_window", s.CutNamedTools);
    }

    [Fact]
    public async Task ANoteTheQuestionCarries_NamesToolsToo()
    {
        // Regenerate sends the notes the first run read: the person's words, like the question.
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = 8_192);
        h.Fake.RunAgentThroughChat = false;
        h.Fake.OnAgentRun = (history, _, _, _) => Task.FromResult(new AgentResult("done", [], history));
        await h.InitializeAsync();

        Assert.Null((await Call<ChatSendResult>(h, "chat/send", new { prompt = "check the page", agentMode = false, notes = new[] { "use view_page" } })).Error);

        Assert.Contains("view_page", h.Server.CurrentSession!.CutNamedTools);
    }

    [Fact]
    public async Task TheToolsAConversationNamed_AreKeptWithIt()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        var s = h.Server.CurrentSession!;
        s.CutNamedTools = ["view_page"];

        var saved = s.CaptureState();
        s.CutNamedTools = [];
        s.ApplyState(saved);
        Assert.Equal(["view_page"], s.CutNamedTools);   // a reloaded conversation keeps them

        s.ApplyState(Services.Persistence.SessionState.New);
        Assert.Empty(s.CutNamedTools);                  // a new one has none
    }

    private static async Task<(IToolRegistry Offered, string[] Notices, bool SharedMarked, bool SharedOffersWebSearch)> AskAtWindowAsync(
        int window, bool agentMode, params string[] prompts)
    {
        using var h = CreateHarness(cfg => cfg.ContextWindowSize = window);
        h.Fake.LoadedContextWindow = window;
        h.Fake.RunAgentThroughChat = false;
        IToolRegistry? offered = null;
        // The chat loop runs through the client; the agent mode's orchestrator asks the model step by step.
        h.Fake.OnAgentRun = (history, tools, _, _) =>
        {
            offered = tools;
            return Task.FromResult(new AgentResult("done", [], history));
        };
        h.Fake.OnChatRequest = (_, _, tools, _) =>
        {
            if (tools.Definitions.Count > 0) offered ??= tools;
            return Task.FromResult(new ChatTurnResult("done", null, 0, 0));
        };
        await h.InitializeAsync();

        foreach (var prompt in prompts is [] ? ["first", "second"] : prompts)
        {
            offered = null;   // the last question's request
            Assert.Null((await Call<ChatSendResult>(h, "chat/send", new { prompt, agentMode })).Error);
        }
        // Notifications are one-way: a round-trip request guarantees they were dispatched.
        await Call<string[]>(h, "models/list", new { });
        Assert.NotNull(offered);   // witness: a request with tools was seen
        var shared = h.Server.CurrentSession!.Tools;
        return (offered!, [.. h.Target.HostNotices], SmallWindowToolRegistry.IsDecided(shared),
                shared.Definitions.Any(d => d.Function.Name == "web_search"));
    }

    private static string CutNoticeStart() =>
        Localization.Strings.MsgToolsCutSmallWindow(8_192, 1, "§", "").Split('8')[0];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtTheFactoryWindow_ATurnSendsTheToolsUsedMost_AndSaysWhatItLeavesOutOnce(bool agentMode)
    {
        var (offered, notices, _, _) = await AskAtWindowAsync(8_192, agentMode);

        Assert.Contains(offered.Definitions, d => d.Function.Name == "read_file");
        Assert.All(offered.Definitions, d => Assert.True(SmallWindowToolRegistry.IsKept(d.Function.Name), d.Function.Name));
        Assert.DoesNotContain(offered.Definitions, d => d.Function.Name == "interact_page");
        Assert.True(RequestSize.ToolTokens(offered.Definitions) * 2 <= 8_192);   // the question has room
        Assert.Single(notices, n => n.StartsWith(CutNoticeStart(), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtTheFactoryWindow_TheToolsAQuestionNamed_StayForTheConversation(bool agentMode)
    {
        // Named over half the window with the kept ones: the agent loop does not cut them again; Continue keeps them.
        var (offered, _, _, sharedOffersWebSearch) = await AskAtWindowAsync(8_192, agentMode,
            "use interact_page, query_database, http_request, view_page, edit_notebook and ocr", "continue");

        Assert.Contains(offered.Definitions, d => d.Function.Name == "interact_page");
        Assert.Contains(offered.Definitions, d => d.Function.Name == "ocr");
        // Witnesses: still over half the window (a second decision would cut again), and still a cut (web_search offered, not sent).
        Assert.True(RequestSize.ToolTokens(offered.Definitions) * 2 > 8_192);
        Assert.DoesNotContain(offered.Definitions, d => d.Function.Name == "web_search");
        Assert.True(sharedOffersWebSearch);
        Assert.True(SmallWindowToolRegistry.IsDecided(offered));   // the registry the turn decided reached the loop
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtALargerWindow_ATurnSendsEveryTool_AndSaysNothing(bool agentMode)
    {
        var (offered, notices, sharedMarked, _) = await AskAtWindowAsync(32_768, agentMode);

        Assert.Contains(offered.Definitions, d => d.Function.Name == "interact_page");
        Assert.DoesNotContain(notices, n => n.StartsWith(CutNoticeStart(), StringComparison.Ordinal));
        // An uncut turn may pass the session's own registry (no tool events, no mode, no plan here): never marked, or no
        // command run would ever be cut again.
        Assert.False(sharedMarked);
    }
}
