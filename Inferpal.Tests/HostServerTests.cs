using System.IO;
using System.Net.Http;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Models;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Headless protocol tests: a real StreamJsonRpc connection over in-memory duplex streams,
/// with the same wire conventions as Program.cs (<see cref="HostRpc"/>), a scripted
/// <see cref="FakeInferenceProvider"/> and an in-test editor adapter (<see cref="ClientTarget"/>).
/// This is exactly how the VS Code extension will drive the host — minus the process spawn.
/// </summary>
// In the serialised signal collection because constructing a HostServer declares, process-wide and
// one-way, that this process has no in-process Visual Studio peer (§22). Production has one role
// per process; a test process plays both, so this suite must not run alongside one that needs the
// VS-peer side of that switch — SignalScratchDir resets it, and this keeps the reset meaningful.
[Collection(SignalCollection.Name)]
public class HostServerTests
{
    private const int TimeoutMs = 15_000;

    // ── In-test editor adapter (client side of the connection) ────────────────

    private sealed record TokenNote(string Text);
    private sealed record ToolNote(string Name, string Input, string Output, bool HasErrors);
    private sealed record ApprovalNote(string Message);

    private sealed class ClientTarget
    {
        public readonly TaskCompletionSource<string> FirstToken =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Tokens = [];

        public int     ApprovalAnswer  = 0;
        public int     ApprovalPrompts;
        public string? LastApprovalMessage;
        public string? Diagnostics;

        [JsonRpcMethod("editor/diagnostics")]
        public string? EditorDiagnostics() => Diagnostics;

        /// <summary>The active document the adapter serves (<c>{ path, text }</c>), null = none.</summary>
        public object? ActiveDocument;

        [JsonRpcMethod("editor/activeDocument")]
        public object? EditorActiveDocument() => ActiveDocument;

        [JsonRpcMethod("chat/token", UseSingleObjectParameterDeserialization = true)]
        public void ChatToken(TokenNote note)
        {
            Tokens.Add(note.Text);
            FirstToken.TrySetResult(note.Text);
        }

        /// <summary>Every <c>chat/tool</c> notice received, in order (filled by the RPC dispatch thread).</summary>
        public readonly System.Collections.Concurrent.ConcurrentQueue<ToolNote> ToolNotes = new();

        [JsonRpcMethod("chat/tool", UseSingleObjectParameterDeserialization = true)]
        public void ChatTool(ToolNote note) => ToolNotes.Enqueue(note);

        // §27.5 — opt-in: stand in for a card the user never answers, so only the host's
        // $/cancelRequest can end the wait. Off by default; the other approval tests answer
        // immediately as before.
        public bool ApprovalHangs;
        public readonly TaskCompletionSource<bool> ApprovalEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> ApprovalCancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        [JsonRpcMethod("approval/request", UseSingleObjectParameterDeserialization = true)]
        public async Task<int> ApprovalRequest(ApprovalNote note, CancellationToken ct)
        {
            ApprovalPrompts++;
            LastApprovalMessage = note.Message;
            ApprovalEntered.TrySetResult(true);

            if (ApprovalHangs)
            {
                // The token StreamJsonRpc fires on $/cancelRequest — the very one the VS Code
                // adapter hands to the chat card so a cancelled turn retires it.
                using (ct.Register(() => ApprovalCancelled.TrySetResult(true)))
                    await Task.Delay(Timeout.Infinite, ct);
            }
            return ApprovalAnswer;
        }

        // ── Debugger adapter (roadmap §21) ────────────────────────────────────
        // Stands in for the TypeScript DebugBridge: the point of these is that the host's
        // RpcDebugSession really crosses the wire, not that VS Code's debugger works.

        public readonly List<string> DebugCalls = [];
        public object? StartAnswer = new { state = (object?)null, failure = (string?)null };
        public object? PausedState;

        [JsonRpcMethod("debug/listBreakpoints")]
        public object[] ListBreakpoints()
        {
            DebugCalls.Add("listBreakpoints");
            return [new { file = @"C:\ws\Program.cs", line = 14, enabled = true }];
        }

        [JsonRpcMethod("debug/state")]
        public object? DebugState()
        {
            DebugCalls.Add("state");
            return PausedState;
        }

        [JsonRpcMethod("debug/start")]
        public object? DebugStart()
        {
            DebugCalls.Add("start");
            return StartAnswer;
        }

        [JsonRpcMethod("debug/stop")]
        public void DebugStop() => DebugCalls.Add("stop");
    }

    private sealed class Harness : IDisposable
    {
        public required JsonRpc               Client    { get; init; }
        public required JsonRpc               ServerRpc { get; init; }
        public required HostServer            Server    { get; init; }
        public required FakeInferenceProvider Fake      { get; init; }
        public required ClientTarget          Target    { get; init; }

        public Task<InitializeResult> InitializeAsync(string? locale = null, string? rootDir = null,
                                                      bool debug = false) =>
            Client.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize", new { rootDir = rootDir ?? Path.GetTempPath(), locale, debug });

        public void Dispose()
        {
            try { Client.Dispose(); }    catch { }
            try { ServerRpc.Dispose(); } catch { }
            Server.Dispose();
        }
    }

    private static Harness CreateHarness(Action<InferpalConfig>? configure = null)
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        // The host's chat mode runs the basic tool loop: the fake answers it through OnChat, like the real loop.
        var fake   = new FakeInferenceProvider { RunAgentThroughChat = true };
        var server = new HostServer(_ => fake, () =>
        {
            // RAG off unless a test asks: initialize indexes the workspace when it is on, and the
            // default root below is the whole temp directory.
            var cfg = new InferpalConfig { RagEnabled = false };
            configure?.Invoke(cfg);
            return cfg;
        });
        var serverRpc = HostRpc.Create(serverStream, serverStream, server);
        server.Attach(serverRpc);
        serverRpc.StartListening();

        var target = new ClientTarget();
        var client = HostRpc.Create(clientStream, clientStream, target);
        client.StartListening();

        return new Harness { Client = client, ServerRpc = serverRpc, Server = server, Fake = fake, Target = target };
    }

    // ── fim/complete ───────────────────────────────────────────────────────────

    /// <summary>
    /// VS Code's inline completion follows Inferpal's settings, like Visual Studio's: the FIM model, and the
    /// tokens and temperature of the chosen mode.
    /// </summary>
    /// <remarks>
    /// The VS Code panel shows these settings, and the inline provider sent none of them: the host completed
    /// with 128 tokens, a temperature of 0.2 and the CHAT model, whatever the settings.
    /// </remarks>
    [Fact]
    public async Task FimComplete_FollowsTheInlineCompletionSettings()
    {
        using var h = CreateHarness(cfg =>
        {
            cfg.InlineCompletionModel = "qwen2.5-coder:1.5b";
            cfg.InlineCompletionMode  = "HighAccuracy";
        });
        h.Fake.OnFim = (_, _) => "completed";
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var text = await h.Client.InvokeWithParameterObjectAsync<string>("fim/complete", new { prefix = "a", suffix = "b" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("completed", text);
        var preset = FimContextBuilder.GetSettings("HighAccuracy");
        Assert.Equal(("qwen2.5-coder:1.5b", preset.MaxTokens, preset.Temperature), h.Fake.LastFim);
    }

    /// <summary>Inline completion unchecked: the backend is not called, as in Visual Studio.</summary>
    [Fact]
    public async Task FimComplete_WhenInlineCompletionIsOff_DoesNotCallTheBackend()
    {
        using var h = CreateHarness(cfg => cfg.InlineCompletionEnabled = false);
        h.Fake.OnFim = (_, _) => "should not appear";
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var text = await h.Client.InvokeWithParameterObjectAsync<string>("fim/complete", new { prefix = "a", suffix = "b" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(string.Empty, text);
        Assert.Null(h.Fake.LastFim);
    }

    // Reference arm: with no FIM model configured, the choice is left to the client (the chat model), as for the
    // Visual Studio leg.
    [Fact]
    public async Task FimComplete_WithoutAnInlineModel_LeavesTheModelToTheClient()
    {
        using var h = CreateHarness(cfg => cfg.InlineCompletionModel = string.Empty);
        h.Fake.OnFim = (_, _) => "completed";
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await h.Client.InvokeWithParameterObjectAsync<string>("fim/complete", new { prefix = "a", suffix = "b" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.NotNull(h.Fake.LastFim);
        Assert.Null(h.Fake.LastFim!.Value.Model);
    }

    /// <summary>What VS Code's provider needs before it asks: the switch and the delay of the mode.</summary>
    [Fact]
    public async Task FimSettings_GiveWhetherInlineCompletionIsOn_AndTheDebounceOfItsMode()
    {
        using var h = CreateHarness(cfg =>
        {
            cfg.InlineCompletionEnabled = false;
            cfg.InlineCompletionMode    = "Fast";
        });
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var settings = await h.Client.InvokeAsync<FimSettingsResult>("fim/settings")
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.False(settings.Enabled);
        Assert.Equal(FimContextBuilder.GetSettings("Fast").DebounceMs, settings.DebounceMs);
        // Witness: the delay of the chosen mode is not the default mode's.
        Assert.NotEqual(FimContextBuilder.GetSettings("Default").DebounceMs, settings.DebounceMs);
    }

    // ── active file → system prompt ────────────────────────────────────────────

    /// <summary>Waits until the X-Ray panel shows (or no longer shows) a section: the active-document notification
    /// and the request after it are not dispatched in a guaranteed order.</summary>
    private static async Task WaitForXraySectionAsync(Harness h, Func<string, bool> matches, bool present = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var panel = await h.Client.InvokeAsync<XRayPanelDto>("xray/panel");
            if (panel.Sections.Any(sec => matches(sec.Id)) == present) return;
            await Task.Delay(20);
        }
    }

    private static string NewRootWithCSharpRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-active-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, ".inferpal", "rules"));
        File.WriteAllText(Path.Combine(root, ".inferpal", "rules", "csharp.md"),
            "---\nglobs: *.cs\n---\nRULE-FOR-CSHARP-FILES");
        return root;
    }

    /// <summary>
    /// Under VS Code, the system prompt follows the active file as in Visual Studio: the glob-scoped project rules
    /// and the persona of its language.
    /// </summary>
    /// <remarks>
    /// The host received the active document (<c>editor/didChangeActiveDocument</c>) and never passed it to the
    /// prompt: a <c>globs: *.cs</c> rule never applied, and the "persona auto-switch" box did nothing.
    /// </remarks>
    [Fact]
    public async Task TheSystemPrompt_FollowsTheActiveFile_ScopedRulesAndPersona()
    {
        var root = NewRootWithCSharpRule();
        try
        {
            using var h = CreateHarness();
            string? system = null;
            h.Fake.OnChatRequest = (_, history, _, _) =>
            {
                system = history.FirstOrDefault(m => m.Role == "system")?.Content;
                return Task.FromResult(new ChatTurnResult("ok", null, 1, 1));
            };
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            await h.Client.NotifyWithParameterObjectAsync("editor/didChangeActiveDocument",
                new { path = Path.Combine(root, "src", "Program.cs") });
            await WaitForXraySectionAsync(h, id => id.StartsWith("Rules|", StringComparison.Ordinal));
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.NotNull(system);
            Assert.Contains("RULE-FOR-CSHARP-FILES", system);
            Assert.Contains(SystemPromptBuilder.PersonaSnippetFor("csharp"), system);

            // Reference arm: a file the rule does not target removes it; the persona stays the one of the last code
            // file, as in Visual Studio (a Markdown file picks none).
            await h.Client.NotifyWithParameterObjectAsync("editor/didChangeActiveDocument",
                new { path = Path.Combine(root, "README.md") });
            await WaitForXraySectionAsync(h, id => id.StartsWith("Rules|", StringComparison.Ordinal), present: false);
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "again", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.DoesNotContain("RULE-FOR-CSHARP-FILES", system);
            Assert.Contains(SystemPromptBuilder.PersonaSnippetFor("csharp"), system);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Persona auto-switch unchecked: the active file adds no persona (the rule still applies).</summary>
    [Fact]
    public async Task WithPersonaAutoSwitchOff_TheActiveFileAddsNoPersona()
    {
        var root = NewRootWithCSharpRule();
        try
        {
            using var h = CreateHarness(cfg => cfg.PersonaAutoSwitch = false);
            string? system = null;
            h.Fake.OnChatRequest = (_, history, _, _) =>
            {
                system = history.FirstOrDefault(m => m.Role == "system")?.Content;
                return Task.FromResult(new ChatTurnResult("ok", null, 1, 1));
            };
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            await h.Client.NotifyWithParameterObjectAsync("editor/didChangeActiveDocument",
                new { path = Path.Combine(root, "Program.cs") });
            await WaitForXraySectionAsync(h, id => id.StartsWith("Rules|", StringComparison.Ordinal));
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hi", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.NotNull(system);
            Assert.Contains("RULE-FOR-CSHARP-FILES", system);   // witness: the active file did arrive
            Assert.DoesNotContain(SystemPromptBuilder.PersonaSnippetFor("csharp"), system);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── initialize ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_ReturnsProviderCapabilities()
    {
        using var h = CreateHarness();
        h.Fake.Capabilities = ProviderCapabilities.OpenAiCompatible;

        var result = await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.False(result.ModelManagement);
        Assert.False(result.Fim);
        Assert.NotEqual("0.0.0", result.HostVersion);
    }

    /// <summary>
    /// With RAG on, the workspace is indexed without anyone calling <c>index/start</c> — which no
    /// adapter does. Before, <c>search_codebase</c> and the per-turn auto-context stayed empty for the
    /// whole VS Code session unless the user typed <c>/index rebuild</c>.
    /// </summary>
    [Fact]
    public async Task Initialize_WithRagOn_IndexesTheWorkspaceWithoutBeingAsked()
    {
        TestRagStore.Redirect();
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-rag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fine.cs"), string.Join('\n',
            "public class Fine", "{",
            "    public int One()   => 1;", "    public int Two()   => 2;", "    public int Three() => 3;",
            "    public int Four()  => 4;", "    public int Five()  => 5;", "    public int Six()   => 6;", "}"));
        try
        {
            using var h = CreateHarness(cfg => cfg.RagEnabled = true);
            h.Fake.OnEmbedding = _ => [0.1f, 0.2f, 0.3f];
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            IndexStatusResult status;
            do
            {
                status = await h.Client.InvokeAsync<IndexStatusResult>("index/status");
                if (status.ChunkCount > 0) break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);

            Assert.True(status.ChunkCount > 0,
                $"The workspace was never indexed (indexing: {status.IsIndexing}, root: {status.RootDir}).");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Initialize_KeepsTheEditorLocale_WhenLoadingTheConfigWouldClearIt()
    {
        // The real config factory is InferpalConfig.Load, which calls Strings.ApplyLanguage(
        // cfg.Language) unconditionally — so "Auto" (empty) resets the override and the strings fall
        // back to the machine's UI culture, silently discarding the handshake. The harness's own
        // factory never did that, which is exactly why the test below passed while the running host
        // answered in French with `locale: "en"`. Found by driving the host, not by reading it.
        using var h = CreateHarness(cfg =>
        {
            cfg.Language = string.Empty;
            Strings.ApplyLanguage(cfg.Language);   // what Config.Load does, reproduced
        });
        try
        {
            await h.InitializeAsync(locale: "en").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Equal("en", Strings.OverrideCulture?.Name);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task Initialize_LetsAnExplicitConfigLanguageBeatTheEditorLocale()
    {
        // A language pinned in the settings is the more deliberate signal of the two.
        using var h = CreateHarness(cfg =>
        {
            cfg.Language = "de";
            Strings.ApplyLanguage(cfg.Language);
        });
        try
        {
            await h.InitializeAsync(locale: "en").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Equal("de", Strings.OverrideCulture?.Name);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task Initialize_AppliesLocaleHandshake_NormalizingVsCodeCasing()
    {
        using var h = CreateHarness();
        try
        {
            // VS Code reports lowercase ids ("zh-cn"); .NET wants "zh-CN".
            await h.InitializeAsync(locale: "zh-cn").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Equal("zh-CN", Strings.OverrideCulture?.Name);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public async Task MethodsBeforeInitialize_FailWithRemoteError()
    {
        using var h = CreateHarness();

        await Assert.ThrowsAsync<RemoteInvocationException>(
            () => h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
    }

    // ── chat ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatSend_PlainChat_StreamsTokensAndReturnsFinalText()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = (onToken, _) =>
        {
            onToken?.Invoke("Hel");
            onToken?.Invoke("lo");
            return Task.FromResult(new ChatTurnResult("Hello", null, 5, 7));
        };

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("Hello", result.Text);
        Assert.False(result.Cancelled);
        Assert.Equal(5, result.TokensUsed);
        Assert.Equal(7, result.PromptTokens);

        // Token notifications reached the adapter.
        var first = await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal("Hel", first);
    }

    /// <summary>
    /// A model can end a turn with its reasoning and a separator only (<c>&lt;think&gt;…&lt;/think&gt;---</c>). Visual Studio
    /// drops that visually empty bubble and falls back to the final answer, the tool summary or the empty-response notice.
    /// The host judged the stream on its printable characters — the reasoning counts — and handed VS Code the separator:
    /// a bubble the user cannot see, saved to the conversation.
    /// </summary>
    [Fact]
    public async Task ChatSend_AVisuallyEmptyStream_FallsBackLikeVisualStudio()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = (onToken, _) =>
        {
            onToken?.Invoke("<think>reasoning</think>");
            onToken?.Invoke("---");
            return Task.FromResult(new ChatTurnResult("<think>reasoning</think>---", null, 5, 7));
        };

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.False(Inferpal.Services.Agent.ChatTurnPolicy.IsVisiblyEmpty(result.Text),
            $"The turn ended on a bubble the user cannot see: \"{result.Text}\".");
    }

    /// <summary>
    /// With tools off, the answer went into the model's history as the backend returned it — its reasoning included.
    /// The other two paths, and Visual Studio, keep the answer the user saw (ChoosePersistedAnswer).
    /// </summary>
    [Fact]
    public async Task ChatSend_ToolsOff_KeepsTheAnswerTheUserSaw_InTheHistory()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Server.CurrentSession!.ToolsEnabled = false;

        h.Fake.OnChat = (onToken, _) =>
        {
            onToken?.Invoke("<think>private chain of thought</think>the answer");
            return Task.FromResult(new ChatTurnResult("<think>private chain of thought</think>the answer", null, 3, 5));
        };

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var last = h.Server.CurrentSession!.History[^1];
        Assert.Equal("assistant", last.Role);
        Assert.Equal("the answer", last.Content);
    }

    /// <summary>
    /// The agent loop runs on the configured agent model, as in Visual Studio. The adapter always sends
    /// the model picked in the chat (its setting, or the default model), and the host let that explicit
    /// model win over the router: <c>agentModel</c> was never used under VS Code.
    /// </summary>
    [Fact]
    public async Task ChatSend_TheAgentLoop_RunsOnTheAgentModel_EvenWithAModelPickedInTheChat()
    {
        using var h = CreateHarness(cfg => { cfg.DefaultModel = "chat-default"; cfg.AgentModel = "agent-model"; });
        await h.InitializeAsync();

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "fix it", model = "picked-in-chat", agentMode = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.NotEmpty(h.Fake.ChatModels);
        Assert.All(h.Fake.ChatModels, m => Assert.Equal("agent-model", m));

        var agentCalls = h.Fake.ChatModels.Count;
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hello", model = "picked-in-chat", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal("picked-in-chat", h.Fake.ChatModels[agentCalls]);   // plain chat keeps the picked model
    }

    /// <summary>
    /// An agent turn leaves the question and one answer in the history, as in Visual Studio — not the
    /// run's internal transcript (plan prompts, plan JSON, tool calls and results), which bloats every
    /// following prompt and teaches the model to replay its previous answer.
    /// </summary>
    [Fact]
    public async Task ChatSend_AnAgentTurn_KeepsOnlyTheQuestionAndTheAnswer()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.OnChat = (_, _) => Task.FromResult(new ChatTurnResult("done", null, 3, 5));
        var before = h.Server.CurrentSession!.History.Count;

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "first question", agentMode = true })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var added = h.Server.CurrentSession!.History.Skip(before).ToList();
        Assert.Equal(new[] { "user", "assistant" }, added.Select(m => m.Role).ToArray());
        Assert.Equal("first question", added[0].Content);
    }

    private sealed record PinsNote(List<string> Pins, string? Notice);

    /// <summary>
    /// A file pinned from the chat while the settings panel is open survives the panel's save of its own pin
    /// edit: pinned files merge line by line, as in the Visual Studio window. Field by field, the panel's list
    /// replaced the whole setting and the chat's pin was gone.
    /// </summary>
    [Fact]
    public async Task SavingThePanel_KeepsAFilePinnedFromTheChatMeanwhile()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"pinmerge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (a, b, c) = (Path.Combine(root, "a.cs"), Path.Combine(root, "b.cs"), Path.Combine(root, "c.cs"));
            foreach (var f in new[] { a, b, c }) File.WriteAllText(f, "// pinned");
            using var h = CreateHarness(cfg => cfg.PinnedContextFiles = a);
            await h.InitializeAsync(rootDir: root);

            var opened = await h.Client.InvokeAsync<string>("config/get").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await h.Client.InvokeWithParameterObjectAsync<PinsNote>("pins/add", new { path = b })   // the chat pins B
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var edited = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
            edited["pinnedContextFiles"] = a + "\n" + c;                                           // the panel adds C
            await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = edited.ToJsonString(), @base = opened })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var pins = h.Server.CurrentSession!.Config.PinnedContextFiles.Split('\n');
            Assert.Contains(c, pins);   // witness: the panel's own edit applied
            Assert.Contains(b, pins);   // the chat's pin survived
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Pins from the chat go through the host, which owns the setting and the system prompt, as the Visual Studio
    /// window does: a pinned file is in the very next prompt, a fourth one is refused with the reason, and
    /// unpinning takes it out again.
    /// </summary>
    [Fact]
    public async Task Pins_AddListRemove_FollowTheSettingAndTheSystemPrompt()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"pins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var files = new[] { "a.cs", "b.cs", "c.cs", "d.cs" }.Select(n => Path.Combine(root, n)).ToArray();
            foreach (var f in files) File.WriteAllText(f, $"// marker-{Path.GetFileNameWithoutExtension(f)}");
            using var h = CreateHarness(cfg => cfg.PinnedContextFiles = "");
            await h.InitializeAsync(rootDir: root);

            Task<PinsNote> Call(string method, object args) =>
                h.Client.InvokeWithParameterObjectAsync<PinsNote>(method, args).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            string SystemPrompt() => h.Server.CurrentSession!.History[0].Content;

            var added = await Call("pins/add", new { path = files[0] });
            Assert.Equal([files[0]], added.Pins);
            Assert.Null(added.Notice);
            Assert.Contains("marker-a", SystemPrompt(), StringComparison.Ordinal);

            await Call("pins/add", new { path = files[1] });
            await Call("pins/add", new { path = files[2] });
            var refused = await Call("pins/add", new { path = files[3] });
            Assert.Equal(3, refused.Pins.Count);
            Assert.False(string.IsNullOrEmpty(refused.Notice));   // the cap is said, not silent

            Assert.Equal(3, (await Call("pins/list", new { })).Pins.Count);

            var removed = await Call("pins/remove", new { path = files[0] });
            Assert.DoesNotContain(files[0], removed.Pins);
            Assert.DoesNotContain("marker-a", SystemPrompt(), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The first question of a conversation carries the workspace context, as in Visual Studio: the solution and
    /// the open editors, once per conversation. VS Code sent none — the model knew neither the solution nor what
    /// was open. Here the solution is the one in the workspace, never the machine-wide state of a running Visual
    /// Studio.
    /// </summary>
    [Fact]
    public async Task ChatSend_TheFirstTurn_CarriesTheWorkspaceContext_OncePerConversation()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "App.sln"), "Microsoft Visual Studio Solution File, Format Version 12.00\n");
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root);
            h.Fake.OnChat = (_, _) => Task.FromResult(new ChatTurnResult("done", null, 3, 5));
            await h.Client.NotifyWithParameterObjectAsync(
                "textDocument/didOpen", new { path = Path.Combine(root, "Program.cs"), text = "class P;" });
            // Notifications are one-way: a round-trip request guarantees they were dispatched.
            await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            async Task<string> Ask(string prompt)
            {
                await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt, agentMode = false })
                    .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                return h.Server.CurrentSession!.History.Last(m => m.Role == "user").Content;
            }

            var first = await Ask("first question");
            Assert.Contains("## Workspace context", first, StringComparison.Ordinal);
            Assert.Contains("App.sln", first, StringComparison.Ordinal);
            Assert.Contains("Program.cs", first, StringComparison.Ordinal);

            Assert.DoesNotContain("## Workspace context", await Ask("second question"), StringComparison.Ordinal);

            await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Contains("## Workspace context", await Ask("a new conversation"), StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>Witness: a workspace with no solution and nothing open adds no empty context block.</summary>
    [Fact]
    public async Task ChatSend_AWorkspaceWithNothingToSay_AddsNoContextBlock()
    {
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"ws-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root);
            h.Fake.OnChat = (_, _) => Task.FromResult(new ChatTurnResult("done", null, 3, 5));

            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>("chat/send", new { prompt = "hello", agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var question = h.Server.CurrentSession!.History.Last(m => m.Role == "user").Content;
            Assert.Equal("hello", question);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Chat mode keeps its tools, as in Visual Studio: the basic tool loop, without the orchestrator's plan.
    /// Only <c>/tools off</c> is chat without tools. The VS Code Chat switch took every tool away — the model
    /// could not even read a file.
    /// </summary>
    [Fact]
    public async Task ChatSend_ChatMode_KeepsTheTools_UnlessToolsAreOff()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.OnChat = (_, _) => Task.FromResult(new ChatTurnResult("done", null, 3, 5));

        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "read Foo.cs", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Single(h.Fake.AgentRuns);   // the tool loop ran, not a bare chat call

        h.Server.CurrentSession!.ToolsEnabled = false;   // witness: /tools off is chat without tools
        await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hello", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Single(h.Fake.AgentRuns);
    }

    /// <summary>
    /// Regenerate takes the last exchange out of the history before the question is resent, as in Visual
    /// Studio. Resent on top, the model read its own previous answer and the question twice. The system
    /// prompt is never taken back: with no question left, nothing is removed.
    /// </summary>
    [Fact]
    public async Task ChatRollbackLastTurn_TakesTheLastExchangeBack_AndNeverTheSystemPrompt()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.OnChat = (_, _) => Task.FromResult(new ChatTurnResult("done", null, 3, 5));
        var before = h.Server.CurrentSession!.History.Count;

        foreach (var prompt in new[] { "first question", "second question" })
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt, agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Task<bool> Rollback() =>
            h.Client.InvokeAsync<bool>("chat/rollbackLastTurn").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(await Rollback());
        var kept = h.Server.CurrentSession!.History.Skip(before).ToList();
        Assert.Equal(new[] { "user", "assistant" }, kept.Select(m => m.Role).ToArray());
        Assert.Equal("first question", kept[0].Content);

        Assert.True(await Rollback());
        Assert.False(await Rollback());
        Assert.Equal(before, h.Server.CurrentSession!.History.Count);
    }

    /// <summary>
    /// Every <c>oodaTurnThreshold</c> turns the host folds a session summary into the system prompt and
    /// shows it, as in Visual Studio. The setting was offered in the VS Code panel and did nothing there.
    /// A new conversation leaves the summary behind.
    /// </summary>
    [Fact]
    public async Task ChatSend_AtTheOodaThreshold_FoldsASessionSummaryIntoTheSystemPrompt()
    {
        using var h = CreateHarness(cfg => cfg.OodaTurnThreshold = 2);
        await h.InitializeAsync();
        // Turns answer "done"; the utility model, asked for the session summary, answers with the summary.
        h.Fake.OnChatRequest = (_, messages, _, _) => Task.FromResult(new ChatTurnResult(
            messages[^1].Content == Inferpal.Localization.Strings.OodaSummarizePrompt ? "the session so far" : "done",
            null, 3, 5));

        async Task Send(string prompt) =>
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt, agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        string SystemPrompt() => h.Server.CurrentSession!.History[0].Content;

        await Send("first question");
        Assert.DoesNotContain("the session so far", SystemPrompt(), StringComparison.Ordinal);

        await Send("second question");
        Assert.Contains("## Session Summary\n\nthe session so far", SystemPrompt(), StringComparison.Ordinal);
        // Notifications are one-way: a round-trip request guarantees they were dispatched.
        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Contains(h.Target.ToolNotes,
            n => n.Name == "ooda_recap" && n.Output.Contains("the session so far", StringComparison.Ordinal));

        await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.DoesNotContain("the session so far", SystemPrompt(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary comes from the utility model through the provider's basic loop, which returns the reply whole. A
    /// reasoning model's chain of thought was folded into the system prompt of every following turn, and shown.
    /// </summary>
    [Fact]
    public async Task ChatSend_TheSessionSummary_LeavesTheUtilityModelsReasoningOut()
    {
        using var h = CreateHarness(cfg => cfg.OodaTurnThreshold = 2);
        await h.InitializeAsync();
        h.Fake.OnChatRequest = (_, messages, _, _) => Task.FromResult(new ChatTurnResult(
            messages[^1].Content == Inferpal.Localization.Strings.OodaSummarizePrompt
                ? "<think>private reasoning</think>the session so far"
                : "done",
            null, 3, 5));

        async Task Send(string prompt) =>
            await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
                "chat/send", new { prompt, agentMode = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await Send("first question");
        await Send("second question");
        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var systemPrompt = h.Server.CurrentSession!.History[0].Content;
        Assert.Contains("## Session Summary\n\nthe session so far", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("private reasoning", systemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Target.ToolNotes,
            n => n.Name == "ooda_recap" && n.Output.Contains("private reasoning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatCancel_ReturnsPartialTextWithCancelledFlag()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("par");
            await Task.Delay(Timeout.Infinite, ct);   // hangs until chat/cancel
            return new ChatTurnResult(string.Empty, null, 0, 0);
        };

        var sendTask = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false });

        await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeAsync("chat/cancel");

        var result = await sendTask.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.True(result.Cancelled);
        Assert.Equal("par", result.Text);
    }

    /// <summary>
    /// Stopped while the model was still reasoning, the partial answer is an unclosed <c>&lt;think&gt;</c> and nothing
    /// else. Visual Studio drops that bubble; the host handed it over as the answer, and VS Code saved a turn of pure
    /// reasoning. The witness above keeps a visible partial answer.
    /// </summary>
    [Fact]
    public async Task ChatCancel_DuringTheReasoning_LeavesNoPartialAnswer()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("<think>still thinking");
            await Task.Delay(Timeout.Infinite, ct);   // hangs until chat/cancel
            return new ChatTurnResult(string.Empty, null, 0, 0);
        };

        var sendTask = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false });

        await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeAsync("chat/cancel");

        var result = await sendTask.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.True(result.Cancelled);
        Assert.Equal(string.Empty, result.Text);
    }

    [Theory]
    [InlineData("chat/reset")]
    [InlineData("session/load")]
    [InlineData("session/branch")]
    public async Task HistoryMutations_AreRefusedWhileATurnIsInFlight(string method)
    {
        // These three replace HostSession.History wholesale; doing that under a running agent
        // loop is a data race on the list the loop is appending to. Nothing in the protocol
        // orders the calls, so the host refuses rather than corrupts.
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("par");
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatTurnResult(string.Empty, null, 0, 0);
        };

        var sendTask = h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false });
        await h.Target.FirstToken.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // Each method has its own argument shape; chat/reset takes none.
        Task Invoke() => method switch
        {
            "chat/reset"   => h.Client.InvokeAsync("chat/reset"),
            "session/load" => h.Client.InvokeWithParameterObjectAsync<object?>("session/load", new { name = "last_session" }),
            _              => h.Client.InvokeWithParameterObjectAsync<object?>(
                                  "session/branch", new { turn = 1, messages = Array.Empty<object>() }),
        };

        // VS Code shows this refusal verbatim (the "load session" palette command, a command's error
        // bubble): it follows the language and does not name the RPC method. Literal expectation.
        RemoteInvocationException refused;
        try
        {
            Strings.ApplyLanguage("fr");
            refused = await Assert.ThrowsAsync<RemoteInvocationException>(
                () => Invoke().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
        }
        finally { Strings.ApplyLanguage(null); }
        Assert.Contains("Une réponse est encore en cours — arrêtez-la, puis réessayez.", refused.Message);
        Assert.DoesNotContain(method, refused.Message);

        await h.Client.InvokeAsync("chat/cancel");
        await sendTask.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // …and the same call goes through once the turn is over.
        await Invoke().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
    }

    [Fact]
    public async Task ChatSend_ProviderFailure_ReturnsStructuredErrorNotRpcFault()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        h.Fake.OnChat = (_, _) => throw new HttpRequestException("backend unreachable");

        var result = await h.Client.InvokeWithParameterObjectAsync<ChatSendResult>(
            "chat/send", new { prompt = "hi", agentMode = false })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.False(result.Cancelled);
        Assert.Contains("backend unreachable", result.Error);
    }

    // ── backend surface ────────────────────────────────────────────────────────

    [Fact]
    public async Task ModelsList_ReturnsProviderModels()
    {
        using var h = CreateHarness();
        h.Fake.ModelNames = ["llama3.1", "qwen3"];
        await h.InitializeAsync();

        var models = await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal(["llama3.1", "qwen3"], models);
    }

    [Fact]
    public async Task Shutdown_CompletesShutdownRequested()
    {
        using var h = CreateHarness();

        await h.Client.InvokeAsync("shutdown");
        await h.Server.ShutdownRequested.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
    }

    // ── reverse approval flow ──────────────────────────────────────────────────

    [Fact]
    public async Task Approval_AdapterAnswersOnce_Approves()
    {
        using var h = CreateHarness();
        h.Target.ApprovalAnswer = 1;   // Once
        var approval = new RpcApprovalService(new InferpalConfig(), () => null, h.ServerRpc);

        var ok = await approval.RequestApprovalAsync("write_file", @"C:\x.txt", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(ok);
        Assert.NotNull(h.Target.LastApprovalMessage);
    }

    [Fact]
    public async Task Approval_TurnCancelled_CancelsTheAdapterRequest()
    {
        // §27.5, the falsifiable half of the live validation: the ghost-card fix assumes a
        // cancelled turn reaches the card. This test proves the wire - the turn's ct (what
        // `chat/cancel` cancels) -> StreamJsonRpc emits $/cancelRequest -> the adapter handler's
        // token lights up. That token is the one chatViewProvider wires to `approvalDismiss`.
        // What remains visual: the card freezing in the webview.
        using var h = CreateHarness();
        h.Target.ApprovalHangs = true;   // the user never answers
        var approval = new RpcApprovalService(new InferpalConfig(), () => null, h.ServerRpc);

        using var turnCts = new CancellationTokenSource();
        var pending = approval.RequestApprovalAsync("write_file", @"C:\x.txt", turnCts.Token);

        await h.Target.ApprovalEntered.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        turnCts.Cancel();                // what ChatCancel() does to the turn's CTS

        await h.Target.ApprovalCancelled.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
    }

    [Fact]
    public async Task Approval_AdapterDenies_Blocks()
    {
        using var h = CreateHarness();
        h.Target.ApprovalAnswer = 0;   // Deny
        var approval = new RpcApprovalService(new InferpalConfig(), () => null, h.ServerRpc);

        var ok = await approval.RequestApprovalAsync("write_file", @"C:\x.txt", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.False(ok);
    }

    [Fact]
    public async Task Approval_AlwaysGrant_SkipsSubsequentPrompts()
    {
        using var h = CreateHarness();
        h.Target.ApprovalAnswer = 2;   // Always
        var approval = new RpcApprovalService(new InferpalConfig(), () => null, h.ServerRpc);

        Assert.True(await approval.RequestApprovalAsync("run_command", "echo 1", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
        Assert.True(await approval.RequestApprovalAsync("run_command", "echo 2", CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));

        Assert.Equal(1, h.Target.ApprovalPrompts);   // second call rode the session grant
    }

    // ── sessions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The auto-save is one file for every project: VS Code reloads it at start-up, and it must come
    /// back only in the workspace that wrote it.
    /// </summary>
    [Fact]
    public async Task TheAutoSave_IsNotRestoredIntoAnotherWorkspace()
    {
        var rootA = Directory.CreateTempSubdirectory("inferpal-autosave-a-").FullName;
        var rootB = Directory.CreateTempSubdirectory("inferpal-autosave-b-").FullName;
        try
        {
            using (var a = CreateHarness())
            {
                await a.InitializeAsync(rootDir: rootA).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                await a.Client.InvokeWithParameterObjectAsync<object?>("session/save", new
                {
                    name     = "last_session",
                    messages = new object[] { new { role = "user", content = "about project A" } },
                }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            }

            using (var b = CreateHarness())
            {
                await b.InitializeAsync(rootDir: rootB).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                var elsewhere = await b.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
                    "session/load", new { name = "last_session" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                Assert.Null(elsewhere);
            }

            // Witness: the same workspace gets its conversation back.
            using var again = CreateHarness();
            await again.InitializeAsync(rootDir: rootA).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var home = await again.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
                "session/load", new { name = "last_session" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.NotNull(home);
            Assert.Equal("about project A", home!.Messages[0].Content);
        }
        finally
        {
            new Inferpal.Services.Persistence.ConversationStore().Delete("last_session");
            try { Directory.Delete(rootA, true); } catch { }
            try { Directory.Delete(rootB, true); } catch { }
        }
    }

    [Fact]
    public async Task Session_SaveLoadDelete_RoundTripsAndRebuildsHistory()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        // Unique name so parallel/repeated runs never collide in the shared real store.
        var name = $"test-host-{Guid.NewGuid():N}";
        try
        {
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new
            {
                name,
                messages = new object[]
                {
                    new { role = "user",      content = "hello" },
                    new { role = "tool",      content = "tool output", toolName = "read_file" },
                    new { role = "assistant", content = "hi there" },
                },
            }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var loaded = await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
                "session/load", new { name }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.NotNull(loaded);
            Assert.Equal(3, loaded!.Messages.Count);
            Assert.Equal("read_file", loaded.Messages[1].ToolName);

            // Host history rebuilt: fresh system prompt + the conversational turns. The tool result
            // comes back AS PLAIN TEXT in the turn that produced it — a saved transcript has no
            // tool_calls, and a `tool` message without its call is dropped by every
            // OpenAI-compatible backend (2026-09-11). It does not open a turn of its own either:
            // that is the unit compaction and /branch count.
            var history = h.Server.CurrentSession!.History;
            Assert.Equal(3, history.Count);
            Assert.Equal("system", history[0].Role);
            Assert.Equal("user", history[1].Role);
            Assert.StartsWith("hello", history[1].Content);
            Assert.Contains("read_file",   history[1].Content);
            Assert.Contains("tool output", history[1].Content);
            Assert.Equal("hi there", history[2].Content);
        }
        finally
        {
            var deleted = await h.Client.InvokeWithParameterObjectAsync<bool>(
                "session/delete", new { name }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.True(deleted);
        }
    }

    [Fact]
    public async Task SessionBranch_ForksAtTurn_LinksTheParentAndBecomesTheCurrentSession()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var name = $"test-branch-{Guid.NewGuid():N}";
        SessionBranchResult? branch = null;
        try
        {
            object[] messages =
            [
                new { role = "user",      content = "first" },
                new { role = "assistant", content = "answer one" },
                new { role = "user",      content = "second" },
                new { role = "assistant", content = "answer two" },
            ];

            // Saving makes it the current session — the branch must record it as its parent.
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name, messages })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            branch = await h.Client.InvokeWithParameterObjectAsync<SessionBranchResult>(
                "session/branch", new { turn = 1, messages }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Equal($"{name}__b2", branch!.Name);
            Assert.Equal(name, branch.Parent);
            Assert.Equal(2, branch.Messages.Count);              // turn 1 only, answer included
            Assert.Contains(branch.Name, branch.Message);        // localized confirmation bubble

            // The conversation continues in the branch: truncated history + new current session.
            var history = h.Server.CurrentSession!.History;
            Assert.Equal(3, history.Count);                      // system + the 2 kept messages
            Assert.Equal("answer one", history[^1].Content);
            Assert.Equal(branch.Name, h.Server.CurrentSession!.CurrentSessionName);

            // Reloading the branch shows the parent link the store persisted.
            var listed = await h.Client.InvokeAsync<List<SessionSummaryDto>>("session/list")
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var summary = listed.Single(x => x.Name == branch.Name);
            Assert.Equal(name, summary.Parent);
            Assert.Equal(1, summary.ForkTurn);
        }
        finally
        {
            await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name });
            if (branch is not null)
                await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name = branch.Name });
        }
    }

    [Fact]
    public async Task SessionBranch_UnknownTurn_ReturnsNull()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var branch = await h.Client.InvokeWithParameterObjectAsync<SessionBranchResult?>(
            "session/branch", new { turn = 7, messages = new object[] { new { role = "user", content = "only one" } } })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(branch);
    }

    /// <summary>
    /// <c>/branch</c> decides on the conversation the user sees. The host listed and checked turns
    /// against its own history — where a user message carries the RAG auto-context in front of the
    /// question, and compaction renumbers turns — while the fork ran on the adapter's transcript: the
    /// listing previewed context blocks, and <c>/branch 3</c> could fork a different turn than the one
    /// it listed. The command now asks the adapter for its transcript.
    /// </summary>
    [Fact]
    public async Task SlashBranch_AsksTheAdapterToDecideOnItsTranscript()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var slash = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/branch 2" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(slash.Handled);
        var effect = Assert.Single(slash.Effects!);
        Assert.Equal("branchCommand", effect.Kind);
        Assert.Equal("2", effect.Value);
    }

    private sealed record BranchCommandAnswer(string? Message, int? ForkTurn, string? SwitchTo);

    [Fact]
    public async Task BranchCommand_ListsAndChecksTheDisplayedTranscript_NotTheModelHistory()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        // The model's history: a compaction summary, and auto-context in front of the question.
        h.Server.CurrentSession!.History.AddRange(
        [
            new ChatMessageDto("user", "[Context Summary] earlier turns"),
            new ChatMessageDto("user", "### src/Foo.cs:12-40\n```\nclass Foo {}\n```\n\nhow do I parse this?"),
            new ChatMessageDto("assistant", "like so"),
        ]);
        var shown = new object[]
        {
            new { role = "user",      content = "first question" },
            new { role = "assistant", content = "first answer" },
            new { role = "user",      content = "how do I parse this?" },
            new { role = "assistant", content = "like so" },
        };

        var listing = await h.Client.InvokeWithParameterObjectAsync<BranchCommandAnswer>(
            "session/branchCommand", new { args = "", messages = shown }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Contains("**2.** how do I parse this?", listing.Message);
        Assert.DoesNotContain("src/Foo.cs", listing.Message);

        var fork = await h.Client.InvokeWithParameterObjectAsync<BranchCommandAnswer>(
            "session/branchCommand", new { args = "2", messages = shown }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal(2, fork.ForkTurn);
        Assert.Null(fork.Message);
    }

    [Fact]
    public async Task SlashTask_SubmitsInTheBackgroundAndListsWithoutBlockingTheTurn()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var submit = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/task audit the RAG layer" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // The command returns immediately: the run is detached, not awaited by this turn.
        Assert.True(submit.Handled);
        Assert.Contains("t1", submit.Markdown);

        var listing = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/task" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(listing.Handled);
        Assert.Contains("`t1`", listing.Markdown);
        Assert.Contains("audit the RAG layer", listing.Markdown);
    }

    [Fact]
    public async Task SlashTask_IsOfferedByTheAdapterAutocomplete()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var commands = await h.Client.InvokeAsync<List<SlashCommandInfoDto>>("command/list")
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains(commands, c => c.Command == "/task");
    }

    [Fact]
    public async Task SessionTitle_SanitizesModelAnswerAndReturnsTimestampedFileName()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.ChatResult = new ChatTurnResult("\"Fix the parser bug\"", null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<SessionTitleResult>(
            "session/title", new { text = "the parser crashes on empty input" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("Fix_the_parser_bug", result.Title);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{4}_Fix_the_parser_bug$", result.FileName);
        // Named by the utility role, not the chat model (Model Router).
        Assert.Equal(ModelRouter.Resolve(h.Server.CurrentSession!.Config, ModelRole.Utility),
                     h.Fake.AgentRuns[^1].Model);
    }

    [Fact]
    public async Task SessionTitle_EmptyText_FallsBackToSnippetWithoutCallingTheModel()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var result = await h.Client.InvokeWithParameterObjectAsync<SessionTitleResult>(
            "session/title", new { text = "" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Empty(h.Fake.AgentRuns);                       // nothing to summarise
        Assert.EndsWith(SessionManager.MakeSnippet(string.Empty), result.FileName);
    }

    [Fact]
    public async Task SessionLoad_UnknownName_ReturnsNull()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        var loaded = await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
            "session/load", new { name = $"test-missing-{Guid.NewGuid():N}" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(loaded);
    }

    // ── reverse editor diagnostics ─────────────────────────────────────────────

    [Fact]
    public async Task EditorDiagnostics_RoundTripFromAdapter()
    {
        using var h = CreateHarness();
        h.Target.Diagnostics = "x.cs(1,1): error CS0001: kaboom";
        var surface = new Inferpal.Host.RpcEditorSurface(h.ServerRpc, new Inferpal.Services.Editor.OpenDocumentOverlay());

        var result = await surface.GetEditorDiagnosticsAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("x.cs(1,1): error CS0001: kaboom", result);
    }

    [Fact]
    public async Task EditorDiagnostics_CleanPanel_ReturnsNull()
    {
        using var h = CreateHarness();
        h.Target.Diagnostics = null;
        var surface = new Inferpal.Host.RpcEditorSurface(h.ServerRpc, new Inferpal.Services.Editor.OpenDocumentOverlay());

        var result = await surface.GetEditorDiagnosticsAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Null(result);
    }

    // ── open-document overlay via RPC notifications ────────────────────────────

    [Fact]
    public async Task DidOpenDidClose_MaintainOpenEditorsList()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didOpen", new { path = @"C:\proj\a.cs", text = "class A;" });
        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didOpen", new { path = @"C:\proj\b.cs", text = "class B;" });
        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didClose", new { path = @"C:\proj\a.cs" });

        // Notifications are one-way: a round-trip request guarantees they were dispatched.
        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var overlay = h.Server.CurrentSession!.Overlay;
        Assert.True(overlay.TryGet(@"C:\proj\b.cs", out var text));
        Assert.Equal("class B;", text);
        Assert.False(overlay.TryGet(@"C:\proj\a.cs", out _));
        Assert.Single(overlay.Paths);
    }

    [Fact]
    public async Task DidOpenDidChange_CarryWhetherTheBufferIsUnsaved()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();

        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didOpen", new { path = @"C:\proj\a.cs", text = "class A;", dirty = true });
        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didOpen", new { path = @"C:\proj\b.cs", text = "class B;" });
        await h.Client.NotifyWithParameterObjectAsync(
            "textDocument/didChange", new { path = @"C:\proj\a.cs", text = "class A2;", dirty = false });

        await h.Client.InvokeWithParameterObjectAsync<string[]>("models/list", new { }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var overlay = h.Server.CurrentSession!.Overlay;
        Assert.False(overlay.TryGetUnsaved(@"C:\proj\a.cs", out _));   // saved: read from disk
        Assert.True(overlay.TryGet(@"C:\proj\a.cs", out var text));    // still open and mirrored
        Assert.Equal("class A2;", text);
        Assert.True(overlay.TryGetUnsaved(@"C:\proj\b.cs", out _));    // an adapter that does not say keeps the buffer
    }

    // ── command/slash ──────────────────────────────────────────────────────────

    /// <summary>
    /// The VS Code support bundle names the backend state, as the Visual Studio one does (its status
    /// line). Without it, a bundle sent for "nothing answers" said nothing about the connection.
    /// </summary>
    [Fact]
    public async Task DiagnosticsExport_NamesTheBackendState()
    {
        using var h = CreateHarness();
        await h.InitializeAsync();
        h.Fake.ConnectionOk = false;

        var export = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/diagnostics export" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var bundle = Assert.Single(export.Effects!, e => e.Kind == "copyToClipboard").Value;
        Assert.Contains("**Backend**", bundle);
        Assert.Contains("unreachable", bundle);
    }

    [Fact]
    public async Task CommandSlash_UnknownCommand_ReturnsHelpBubble()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        // Since slash V2 the unknown-command help is served headlessly (VS parity) —
        // the raw "/…" text is never forwarded to the model.
        var unknown = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/definitely-not-a-command" });

        Assert.True(unknown.Handled);
        Assert.Contains("/definitely-not-a-command", unknown.Markdown, StringComparison.Ordinal);
    }

    // The bug this locks: /test used to fall through to `Handled = false`, and since the VS Code
    // adapter only intercepts /fix /refactor /doc, the literal string "/test" reached the model,
    // which improvised an answer about a command it knows nothing about. Anything but
    // `Handled = false` is the fix; here there is no active document, so it says so.
    [Fact]
    public async Task CommandSlash_Test_IsServedHeadlessly_NotForwardedToTheModel()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/test" });

        Assert.True(result.Handled);
        Assert.Equal(Strings.SlashNoActiveDocument, result.Markdown);
    }

    // /test on an EXISTING test file: the model returns the WHOLE file, and the host writes it to
    // disk. A test dropped along the way was gone with no backup — VS Code's local history does not
    // cover a file written behind its back.
    [Fact]
    public async Task CommandSlash_Test_OnAnExistingTestFile_BacksItUpBeforeRewritingIt()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-host-test-").FullName;
        try
        {
            var source = Path.Combine(dir, "Calculator.cs");
            File.WriteAllText(source, "public class Calculator { public int Add(int a, int b) => a + b; }");
            var testPath = Inferpal.Services.CodeActions.TestFilePathResolver.Resolve(source);
            Directory.CreateDirectory(Path.GetDirectoryName(testPath)!);
            File.WriteAllText(testPath, "// existing tests the model may drop");

            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: dir).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            h.Target.ActiveDocument = new { path = source, text = File.ReadAllText(source) };
            h.Fake.ChatResult = new Inferpal.Models.ChatTurnResult("// rewritten test file", null, 0, 0);

            var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
                "command/slash", new { text = "/test" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.True(result.Handled);
            Assert.Equal("// rewritten test file", File.ReadAllText(testPath));
            var historyDir = Inferpal.Services.Execution.FileHistoryService.GetHistoryDir(testPath);
            Assert.True(Directory.Exists(historyDir), "no backup was taken before the rewrite");
            Assert.Contains(Directory.EnumerateFiles(historyDir),
                f => File.ReadAllText(f) == "// existing tests the model may drop");
            Assert.Contains("/restore", result.Markdown, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* cleanup */ } }
    }

    [Fact]
    public async Task CommandSlash_Replay_WithoutRuns_ReturnsEmptyRunMessage()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/replay" });

        Assert.True(result.Handled);
        Assert.Equal(Strings.ReplayNone, result.Markdown);
    }

    // Roadmap §19: the headless side must serve /onboard from the same Core handler as VS —
    // a command that only exists in the tool window is the drift this repository already paid for.
    [Fact]
    public async Task CommandSlash_Onboard_IsServedHeadlessly()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/onboard" });

        Assert.True(result.Handled);
        Assert.Contains(Strings.OnboardHeading, result.Markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandSlash_Xray_ReturnsTokenBreakdown()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/xray" });

        Assert.True(result.Handled);
        Assert.Contains("🩻", result.Markdown);
        Assert.Contains(Strings.XrayLabelBase, result.Markdown);
    }

    [Fact]
    public async Task XrayPanel_ReturnsSections_WithBaseNotToggleable()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var panel = await h.Client.InvokeAsync<Host.XRayPanelDto>("xray/panel");

        var baseRow = Assert.Single(panel.Sections, s => s.Id == "Base");
        Assert.False(baseRow.CanToggle);
        Assert.True(baseRow.Enabled);
        Assert.True(panel.TotalTokens > 0);
        Assert.Equal(panel.RawPrompt.Length > 0, panel.TotalTokens > 0);
    }

    [Fact]
    public async Task XrayToggle_DisablesSection_AndRefreshesSystemPrompt()
    {
        using var h = CreateHarness(cfg => cfg.CustomSystemPrompt = "Always answer in haiku.");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var before = await h.Client.InvokeAsync<Host.XRayPanelDto>("xray/panel");
        Assert.Contains("haiku", before.RawPrompt);

        var after = await h.Client.InvokeWithParameterObjectAsync<Host.XRayPanelDto>(
            "xray/toggle", new { id = "Custom", enabled = false });

        Assert.DoesNotContain("haiku", after.RawPrompt);
        Assert.False(after.Sections.Single(s => s.Id == "Custom").Enabled);
        Assert.True(after.TotalTokens < before.TotalTokens);

        // Re-enable → back to the full prompt.
        var restored = await h.Client.InvokeWithParameterObjectAsync<Host.XRayPanelDto>(
            "xray/toggle", new { id = "Custom", enabled = true });
        Assert.Contains("haiku", restored.RawPrompt);
    }

    /// <summary>
    /// A <c>/template</c> mode is a prompt layer like the others, under VS Code as in Visual Studio: it shows in the
    /// X-Ray panel and can be switched off there. The host appended its suffix after the built prompt, outside the
    /// builder: the section did not exist in the panel, and nothing could switch it off.
    /// </summary>
    [Fact]
    public async Task XRay_ShowsTheTemplateLayer_AndItsToggleRemovesIt()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var panel = await h.Client.InvokeAsync<Host.XRayPanelDto>("xray/panel");
        Assert.Contains(panel.Sections, s => s.Id == "Template");
        Assert.Contains("## Mode: Code Review", panel.RawPrompt, StringComparison.Ordinal);

        var off = await h.Client.InvokeWithParameterObjectAsync<Host.XRayPanelDto>(
            "xray/toggle", new { id = "Template", enabled = false });
        Assert.DoesNotContain("## Mode: Code Review", off.RawPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
    }

    // ── codeAction/run ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CodeActionRun_Rewrite_ReturnsPerHunkOffsetEdits()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("int y = 2;\nint z = 3;", null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "fix", text = "int x = 1;\nint z = 3;", selStart = 0, selEnd = 0 });

        Assert.Equal("edited", result.Outcome);
        Assert.Equal("int y = 2;\nint z = 3;", result.NewText);
        var edit = Assert.Single(result.Edits);
        Assert.Equal(0, edit.Start);
        Assert.Equal(11, edit.End);                 // "int x = 1;\n" replaced
        Assert.Equal("int y = 2;\n", edit.NewText);
    }

    [Fact]
    public async Task CodeActionRun_SentinelReply_ReportsNoChange()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult(CodeActionSentinel.Token, null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "refactor", text = "int x = 1;", selStart = 0, selEnd = 0 });

        Assert.Equal("noChange", result.Outcome);
        Assert.Empty(result.Edits);
    }

    [Fact]
    public async Task CodeActionRun_IdenticalRewrite_ReportsNoChange()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("int x = 1;", null, 0, 0);

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "doc", text = "int x = 1;", selStart = 0, selEnd = 0 });

        Assert.Equal("noChange", result.Outcome);
    }

    [Fact]
    public async Task CodeActionRun_ProviderFailure_ReportsFailedNotFault()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.OnChat = (_, _) => throw new HttpRequestException("backend down");

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "fix", text = "int x = 1;", selStart = 0, selEnd = 0 });

        Assert.Equal("failed", result.Outcome);
        Assert.Equal("backend down", result.FailureDetail);
    }

    [Fact]
    public async Task CodeActionRun_Selection_RewritesOnlyTheRange()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ChatResult = new ChatTurnResult("BB", null, 0, 0);

        // doc "aa\nbb\ncc", selection over "bb" ([3,5)).
        var result = await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
            "codeAction/run", new { kind = "fix", text = "aa\nbb\ncc", selStart = 3, selEnd = 5 });

        Assert.Equal("edited", result.Outcome);
        Assert.Equal("aa\nBB\ncc", result.NewText);
    }

    [Fact]
    public async Task CodeActionRun_UnknownKind_Faults()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        await Assert.ThrowsAsync<RemoteInvocationException>(() =>
            h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "explain", text = "int x = 1;", selStart = 0, selEnd = 0 }));
    }

    // ── backend/status ─────────────────────────────────────────────────────────

    [Fact]
    public async Task BackendStatus_Connected_ReportsVramBadge()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ConnectionOk = true;   // Capabilities default to Ollama (VramMonitoring = true)
        h.Fake.Running = [new RunningModelInfo("llama3.1:8b", 4_800_000_000, "2026-01-01T00:00:00Z")];

        var status = await h.Client.InvokeAsync<Host.BackendStatusResult>("backend/status");

        Assert.True(status.Connected);
        Assert.StartsWith("llama3.1 · ", status.VramBadge, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendStatus_Unreachable_NoBadge()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.ConnectionOk = false;
        h.Fake.Running = [new RunningModelInfo("llama3.1:8b", 4_800_000_000, "2026-01-01T00:00:00Z")];

        var status = await h.Client.InvokeAsync<Host.BackendStatusResult>("backend/status");

        Assert.False(status.Connected);
        Assert.Equal(string.Empty, status.VramBadge);
    }

    [Fact]
    public async Task BackendStatus_NoVramMonitoring_NoBadge()
    {
        using var h = CreateHarness();
        h.Fake.Capabilities = ProviderCapabilities.OpenAiCompatible;
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Fake.Running = [new RunningModelInfo("llama3.1:8b", 4_800_000_000, "2026-01-01T00:00:00Z")];

        var status = await h.Client.InvokeAsync<Host.BackendStatusResult>("backend/status");

        Assert.True(status.Connected);
        Assert.Equal(string.Empty, status.VramBadge);
    }

    // ── command/slash V2 (headless commands + typed effects) ──────────────────

    [Fact]
    public async Task Slash_Help_ReturnsHandledMarkdown()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/help" });

        Assert.True(result.Handled);
        Assert.False(string.IsNullOrWhiteSpace(result.Markdown));
    }

    [Fact]
    public async Task Slash_UserTemplate_ReturnsSendAsPromptEffect()
    {
        using var h = CreateHarness(cfg => cfg.PromptTemplates = "/standup=Summarize {args} for me");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/standup today" });

        Assert.True(result.Handled);
        var effect = Assert.Single(result.Effects!);
        Assert.Equal("sendAsPrompt", effect.Kind);
        Assert.Equal("Summarize today for me", effect.Value);
    }

    [Fact]
    public async Task Slash_Model_ChangesDefaultAndEmitsStateChange()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/model llama3.2:3b" });

        Assert.True(result.Handled);
        Assert.Equal("llama3.2:3b", h.Server.CurrentSession!.Config.DefaultModel);
        var effect = Assert.Single(result.Effects!);
        Assert.Equal("stateChange", effect.Kind);
        Assert.Equal("model", effect.Name);
        Assert.Equal("llama3.2:3b", effect.Value);
    }

    [Fact]
    public async Task Slash_Clear_ResetsHistoryAndEmitsClearTranscript()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        h.Server.CurrentSession!.History.Add(new ChatMessageDto("user", "hello"));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/clear" });

        Assert.True(result.Handled);
        Assert.Equal("clearTranscript", Assert.Single(result.Effects!).Kind);
        var history = h.Server.CurrentSession!.History;
        Assert.Single(history);
        Assert.Equal("system", history[0].Role);
    }

    [Theory]
    [InlineData("/clear")]
    [InlineData("chat/reset")]
    [InlineData("session/load")]
    [InlineData("session/branch")]
    public async Task ANewConversation_LeavesTheTemplateModeBehind(string how)
    {
        // VS leaves the /template mode on /clear, session load and /branch; the host kept it in the system prompt, with nothing on screen.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/template code-review" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Contains("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);

        var name = $"test-template-{Guid.NewGuid():N}";
        object[] messages =
        [
            new { role = "user",      content = "first" },
            new { role = "assistant", content = "answer one" },
            new { role = "user",      content = "second" },
            new { role = "assistant", content = "answer two" },
        ];
        SessionBranchResult? branch = null;
        try
        {
            switch (how)
            {
                case "/clear":
                    await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
                        "command/slash", new { text = "/clear" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    break;
                case "chat/reset":
                    await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    break;
                case "session/load":
                    await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name, messages })
                        .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    await h.Client.InvokeWithParameterObjectAsync<SessionLoadResult?>(
                        "session/load", new { name }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    break;
                default:
                    await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name, messages })
                        .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    branch = await h.Client.InvokeWithParameterObjectAsync<SessionBranchResult>(
                        "session/branch", new { turn = 1, messages }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
                    break;
            }

            Assert.DoesNotContain("## Mode: Code Review", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
        }
        finally
        {
            await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name });
            if (branch is not null)
                await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name = branch.Name });
        }
    }

    [Fact]
    public async Task SavingTheSettings_KeepsTheConversation_AndRefreshesTheSystemPrompt()
    {
        // Picking a model in the VS Code chat goes through config/update: the host reset the conversation while the transcript stayed on screen.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var s = h.Server.CurrentSession!;
        s.History.Add(new ChatMessageDto("user", "remember the vulkan crash"));
        s.CurrentSessionName = "ongoing";

        var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        cfg["customSystemPrompt"] = "refreshed-by-save";
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var history = h.Server.CurrentSession!.History;
        Assert.Contains("refreshed-by-save", history[0].Content, StringComparison.Ordinal);
        Assert.Contains(history, m => m.Role == "user" && m.Content == "remember the vulkan crash");
        Assert.Equal("ongoing", h.Server.CurrentSession!.CurrentSessionName);
    }

    [Fact]
    public async Task ArchivingTheConversationJustLeft_DoesNotBindTheNewOne()
    {
        // The new-conversation button archives AFTER chat/reset: binding the new conversation to the archive file made the next /branch rewrite the archive.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var named   = $"test-named-{Guid.NewGuid():N}";
        var archive = $"test-archive-{Guid.NewGuid():N}";
        object[] messages = [new { role = "user", content = "first" }, new { role = "assistant", content = "answer" }];
        try
        {
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name = named, messages })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(named, h.Server.CurrentSession!.CurrentSessionName);

            await h.Client.InvokeAsync("chat/reset").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            await h.Client.InvokeWithParameterObjectAsync<object?>("session/save", new { name = archive, messages, archive = true })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.Null(h.Server.CurrentSession!.CurrentSessionName);
        }
        finally
        {
            await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name = named });
            await h.Client.InvokeWithParameterObjectAsync<bool>("session/delete", new { name = archive });
        }
    }

    [Fact]
    public async Task SavingTheSettings_AppliesTheMcpServers_WithoutARestart()
    {
        // The VS window reconnects MCP servers on save; the host never did: an added server did not start, and disabling MCP left the servers running.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var mcp = h.Server.CurrentSession!.Mcp;
        Assert.Empty(mcp.Status);

        var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        cfg["mcpEnabled"]     = true;
        cfg["mcpServersJson"] = """{ "ghost": { "command": "inferpal-no-such-mcp-server" } }""";
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains(mcp.Status, st => st.Name == "ghost" && !st.Connected);

        cfg["mcpEnabled"] = false;
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Empty(mcp.Status);
    }

    [Fact]
    public async Task EnablingRag_InTheSettings_IndexesTheWorkspaceWithoutARestart()
    {
        // The host indexed only at initialize: RAG ticked in the settings stayed without an index until restart.
        var root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"host-rag-on-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fine.cs"), string.Join('\n',
            "public class Fine", "{",
            "    public int One()   => 1;", "    public int Two()   => 2;", "    public int Three() => 3;",
            "    public int Four()  => 4;", "    public int Five()  => 5;", "    public int Six()   => 6;", "}"));
        try
        {
            using var h = CreateHarness();
            h.Fake.OnEmbedding = _ => [0.1f, 0.2f, 0.3f];
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(string.Empty, h.Server.CurrentSession!.Index.IndexedRoot);

            var cfg = System.Text.Json.Nodes.JsonNode.Parse(await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
            cfg["ragEnabled"] = true;
            await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = cfg.ToJsonString() })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            IndexStatusResult status;
            do
            {
                status = await h.Client.InvokeAsync<IndexStatusResult>("index/status");
                if (status.ChunkCount > 0) break;
                await Task.Delay(50);
            } while (DateTime.UtcNow < deadline);

            Assert.True(status.ChunkCount > 0,
                $"Turning RAG on did not index the workspace (indexing: {status.IsIndexing}, root: {status.RootDir}).");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Slash_ToolsOff_ForcesPlainChatOnNextTurn()
    {
        using var h = CreateHarness(cfg => cfg.AgentModeEnabled = true);
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/tools off" });
        Assert.True(result.Handled);

        await h.Client.InvokeWithParameterObjectAsync<Host.ChatSendResult>(
            "chat/send", new { prompt = "hi" });

        // Plain chat path records into ChatModels; the agent path would record into AgentRuns.
        Assert.Empty(h.Fake.AgentRuns);
        Assert.Single(h.Fake.ChatModels);
    }

    [Fact]
    public async Task Slash_PHistory_FillsPromptFromAdapterHistory()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/phistory use 1", promptHistory = new[] { "first prompt", "second prompt" } });

        Assert.True(result.Handled);
        var effect = Assert.Single(result.Effects!);
        Assert.Equal("setPrompt", effect.Kind);
    }

    [Fact]
    public async Task Slash_ReadTool_AttachesAsChip()
    {
        using var h = CreateHarness();
        var root = Directory.CreateTempSubdirectory("inferpal-slash-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "hello.txt"), "file content");
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
                "command/slash", new { text = $"/read {Path.Combine(root, "hello.txt")}" });

            Assert.True(result.Handled);
            var effect = Assert.Single(result.Effects!);
            Assert.Equal("attachChip", effect.Kind);
            Assert.Equal("hello.txt", effect.Name);
            Assert.Contains("file content", effect.Value, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Slash_Export_EmitsExportRequestEffect()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/export" });

        Assert.True(result.Handled);
        Assert.Equal("exportRequest", Assert.Single(result.Effects!).Kind);
    }

    [Fact]
    public async Task Slash_Setup_ReturnsHeadlessUnavailableNotFallthrough()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/setup" });

        // Handled with a deterministic message — never sent to the model as a raw "/setup".
        Assert.True(result.Handled);
        Assert.False(string.IsNullOrWhiteSpace(result.Markdown));
    }

    [Fact]
    public async Task Slash_CodeActions_FallThroughToTheAdapter()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/explain" });

        Assert.False(result.Handled);
    }

    // ── plan / step mode ───────────────────────────────────────────────────────

    [Fact]
    public async Task Slash_Plan_TogglesModeAndInjectsPromptSuffix()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var on = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/plan" });

        Assert.True(on.Handled);
        Assert.True(h.Server.CurrentSession!.PlanMode);
        Assert.Contains("Plan mode (read-only)", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);

        var off = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/plan" });

        Assert.True(off.Handled);
        Assert.False(h.Server.CurrentSession!.PlanMode);
        Assert.DoesNotContain("Plan mode (read-only)", h.Server.CurrentSession!.History[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Slash_AgentStep_TogglesAndResumeIdleAnswers()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var on = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/agent-step" });
        Assert.True(h.Server.CurrentSession!.StepMode);
        Assert.Contains(on.Effects!, e => e.Kind == "stateChange" && e.Name == "stepMode" && e.Value == "on");

        // /resume outside a pause answers deterministically (and must not need the turn gate).
        var resume = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/resume" });
        Assert.True(resume.Handled);
        // ⚠ Against the RESOURCE, not against English: this message is localized now, and the
        // literal assertion failed on a French-locale machine — a test that freezes a string is
        // precisely what forbids translating it.
        Assert.Equal(Inferpal.Localization.Strings.NoAgentStepPaused, resume.Markdown);
    }

    // ── settings/strings (localized labels served to the VS Code settings panel) ──

    [Fact]
    public async Task SettingsStrings_ReturnsLocalizedResxEntries()
    {
        using var h = CreateHarness();
        await h.InitializeAsync(locale: "fr").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var strings = await h.Client.InvokeAsync<Dictionary<string, string>>("settings/strings");

        Assert.True(strings.Count > 50);
        Assert.Contains("LabelProvider", strings.Keys);
        Assert.Contains("HintProvider", strings.Keys);
        Assert.Contains("SectionRag", strings.Keys);
        Assert.All(strings.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
    }

    /// <summary>
    /// `settings/strings` is a hand-written list: a name the schema references but the list does not serve is
    /// shown as a raw key in the VS Code panel ("UnitTurns"). No test held the inclusion.
    /// </summary>
    [Fact]
    public async Task SettingsStrings_ServeEveryNameTheSchemaReferences()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var strings = await h.Client.InvokeAsync<Dictionary<string, string>>("settings/strings");

        var local = new[] { Services.Presentation.SettingsSchema.LocalLabelInlineDiff,
                            Services.Presentation.SettingsSchema.LocalLabelTabTools };
        var names = Services.Presentation.SettingsSchema.AllFields
            .SelectMany(f => new[] { f.Label, f.Hint, f.Unit })
            .Concat(Services.Presentation.SettingsSchema.Tabs.SelectMany(t => t.Sections)
                .SelectMany(s => new[] { s.Title, s.ToggleLabel, s.ToggleHint }))
            .Where(n => !string.IsNullOrEmpty(n) && !local.Contains(n))
            .Distinct()
            .ToList();

        // Witness: the schema does reference dozens of names.
        Assert.True(names.Count > 50, $"only {names.Count} name(s) read from the schema");

        var missing = names.Where(n => !strings.ContainsKey(n!)).Order().ToList();
        Assert.True(missing.Count == 0,
            "Names the settings schema references but settings/strings does not serve (rendered as raw keys):\n  "
            + string.Join("\n  ", missing));
    }

    // ── config round trip (settings panel contract) ────────────────────────────

    /// <summary>The VS Code settings webview round-trips the FULL config JSON through
    /// `config/update`; every property must survive unchanged (absent fields reset).</summary>
    [Fact]
    public async Task ConfigUpdate_FullRoundTrip_PreservesEveryProperty()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var node = System.Text.Json.Nodes.JsonNode.Parse(
            await h.Client.InvokeAsync<string>("config/get"))!.AsObject();

        // Mutate every primitive to a non-default value (reflexive: new config properties
        // are covered automatically).
        foreach (var key in node.Select(kv => kv.Key).ToList())
        {
            var value = node[key];
            node[key] = value?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => key + "-mutated",
                System.Text.Json.JsonValueKind.True   => false,
                System.Text.Json.JsonValueKind.False  => true,
                System.Text.Json.JsonValueKind.Number => value.GetValue<double>() + 7,
                _ => value,
            };
        }

        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = node.ToJsonString() });
        var after = System.Text.Json.Nodes.JsonNode.Parse(
            await h.Client.InvokeAsync<string>("config/get"))!.AsObject();

        foreach (var kv in node)
            Assert.Equal(kv.Value?.ToJsonString(), after[kv.Key]?.ToJsonString());
    }

    /// <summary>The settings panel sends back the object it opened with. A change made elsewhere since —
    /// here the default model picked from the chat — must survive a save that did not touch it.</summary>
    [Fact]
    public async Task ConfigUpdate_FromTheOpeningSnapshot_KeepsChangesMadeSince()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        var opened = await h.Client.InvokeAsync<string>("config/get");

        var elsewhere = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
        elsewhere["defaultModel"] = "picked-from-chat";
        await h.Client.InvokeWithParameterObjectAsync("config/update", new { json = elsewhere.ToJsonString() });

        var panel = System.Text.Json.Nodes.JsonNode.Parse(opened)!.AsObject();
        panel["customSystemPrompt"] = "edited-in-panel";
        await h.Client.InvokeWithParameterObjectAsync("config/update",
            new { json = panel.ToJsonString(), @base = opened });

        var after = System.Text.Json.Nodes.JsonNode.Parse(
            await h.Client.InvokeAsync<string>("config/get"))!.AsObject();
        Assert.Equal("edited-in-panel", after["customSystemPrompt"]!.GetValue<string>());
        Assert.Equal("picked-from-chat", after["defaultModel"]!.GetValue<string>());
    }

    // ── command/list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CommandList_ReturnsBuiltInsAndUserTemplates()
    {
        using var h = CreateHarness(cfg => cfg.PromptTemplates = "/standup=Summarize my day");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var list = await h.Client.InvokeAsync<List<Host.SlashCommandInfoDto>>("command/list");

        Assert.Contains(list, c => c.Command == "/help" && c.Hint.Length > 0);
        Assert.Contains(list, c => c.Command == "/xray");
        Assert.Contains(list, c => c.Command == "/standup" && c.Hint == "Summarize my day");
    }

    // ── Debugger (roadmap §21, tranche 3) ──────────────────────────────────────

    [Fact]
    public async Task WithoutADeclaredDebugger_TheDebugToolsAreNotOffered()
    {
        // An adapter that does not serve `debug/*` answers "method not found" to every call, so a
        // registered tool could only ever fail — while still costing tokens in the definitions sent
        // on every turn. The declaration in the handshake is what decides.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var names = h.Server.CurrentSession!.Tools.Definitions.Select(d => d.Function.Name).ToList();

        Assert.DoesNotContain("debug_control", names);
        Assert.DoesNotContain("debug_inspect", names);
    }

    [Fact]
    public async Task WithADeclaredDebugger_TheToolsExist_AndReallyReachTheAdapter()
    {
        using var h = CreateHarness();
        h.Target.PausedState = new
        {
            reason   = "breakpoint",
            threadId = 0,
            frames   = new[] { new { id = 3, function = "Program.Compute", file = @"C:\ws\Program.cs", line = 14 } },
            locals   = new[] { new { name = "total", type = "int", value = "106" } },
        };
        await h.InitializeAsync(debug: true).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var names = h.Server.CurrentSession!.Tools.Definitions.Select(d => d.Function.Name).ToList();
        Assert.Contains("debug_control", names);
        Assert.Contains("debug_inspect", names);

        // `/debug` with no argument reports; the answers must come from the adapter, over the wire.
        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/debug" });

        Assert.True(result.Handled);
        Assert.Contains(@"C:\ws\Program.cs:14", result.Markdown);
        Assert.Contains("Program.Compute", result.Markdown);   // came back over the wire, not invented
        Assert.Contains("state", h.Target.DebugCalls);
        Assert.Contains("listBreakpoints", h.Target.DebugCalls);
    }

    [Fact]
    public async Task DebuggerMention_AttachesTheBreakState_FromTheAdaptersOwnDebugger()
    {
        // The VS Code adapter used to answer this mention itself, from
        // `vscode.debug.activeDebugSession`, and attached the session's NAME AND TYPE — where
        // Visual Studio attaches the stop reason, the call stack and the locals, and where
        // docs/mentions.md promises the break state for both editors. It is served here now, from
        // the same reader as get_debugger_state.
        using var h = CreateHarness();
        h.Target.PausedState = new
        {
            reason   = "breakpoint",
            threadId = 0,
            frames   = new[] { new { id = 3, function = "Program.Compute", file = @"C:\ws\Program.cs", line = 14 } },
            locals   = new[] { new { name = "total", type = "int", value = "106" } },
        };
        await h.InitializeAsync(debug: true, rootDir: @"C:\ws").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.MentionResolveResult>(
            "mention/resolve", new { category = "debugger" });

        Assert.Equal("🐞 @debugger", result.Name);
        Assert.Contains("Program.Compute", result.Content);
        Assert.Contains("total", result.Content);
        Assert.Null(result.Notice);
        Assert.Contains("state", h.Target.DebugCalls);   // it really crossed the wire
    }

    [Fact]
    public async Task DebuggerMention_WithNothingPaused_SaysSo_InsteadOfSilence()
    {
        // Nothing to attach is not nothing to say. `mention/resolve` had no way to tell an empty
        // answer from a failure, so both reached the user as an @mention that did nothing at all.
        using var h = CreateHarness();
        h.Target.PausedState = null;
        await h.InitializeAsync(debug: true).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.MentionResolveResult>(
            "mention/resolve", new { category = "debugger" });

        Assert.Null(result.Name);
        Assert.Null(result.Content);
        Assert.Equal(Strings.MentionDebuggerNone, result.Notice);
    }

    [Fact]
    public async Task DebuggerMention_WithNoDebuggerDeclared_SaysSoToo()
    {
        // An adapter that never declared `debug/*` has no port to ask. That is still an answer the
        // user must see, not a mention that silently does nothing.
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.MentionResolveResult>(
            "mention/resolve", new { category = "debugger" });

        Assert.Equal(Strings.MentionDebuggerNone, result.Notice);
        Assert.Empty(h.Target.DebugCalls);               // nothing was asked of an absent adapter
    }

    [Fact]
    public async Task DebugStart_ThatTheAdapterRefuses_IsNotReportedAsACompletedRun()
    {
        // The VS Code case this exists for: a workspace with no launch configuration. "It ran and
        // never hit your breakpoint" and "it never started" lead to opposite next moves, and only
        // one of them is true.
        using var h = CreateHarness();
        h.Target.ApprovalAnswer = 1;                       // starting executes: it is approved here
        h.Target.StartAnswer = new { state = (object?)null, failure = "No launch configuration in this workspace." };
        await h.InitializeAsync(debug: true).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var output = await h.Server.CurrentSession!.Tools.ExecuteAsync(
            "debug_control", System.Text.Json.JsonDocument.Parse("""{"action":"start"}""").RootElement,
            CancellationToken.None);

        Assert.Contains("did not start", output);
        Assert.Contains("No launch configuration", output);
        Assert.DoesNotContain("ran to completion", output);
    }

    [Fact]
    public async Task AHypothesisComesBackAsAPromptToRun_NotAsADebuggerTheCommandDrivesItself()
    {
        // The command must not reach the debugger on its own: the loop goes through the tools, and
        // therefore through the approval on the one action that runs the user's program.
        using var h = CreateHarness();
        await h.InitializeAsync(debug: true).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var result = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/debug why is total 106 instead of 105" });

        var effect = Assert.Single(result.Effects!);
        Assert.Equal("sendAsPrompt", effect.Kind);
        Assert.Contains("why is total 106 instead of 105", effect.Value);
        Assert.Empty(h.Target.DebugCalls);
    }
}
