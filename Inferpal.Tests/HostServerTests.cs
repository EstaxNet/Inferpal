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
public class HostServerTests
{
    private const int TimeoutMs = 15_000;

    // ── In-test editor adapter (client side of the connection) ────────────────

    private sealed record TokenNote(string Text);
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

        [JsonRpcMethod("chat/token", UseSingleObjectParameterDeserialization = true)]
        public void ChatToken(TokenNote note)
        {
            Tokens.Add(note.Text);
            FirstToken.TrySetResult(note.Text);
        }

        [JsonRpcMethod("approval/request", UseSingleObjectParameterDeserialization = true)]
        public int ApprovalRequest(ApprovalNote note)
        {
            ApprovalPrompts++;
            LastApprovalMessage = note.Message;
            return ApprovalAnswer;
        }
    }

    private sealed class Harness : IDisposable
    {
        public required JsonRpc               Client    { get; init; }
        public required JsonRpc               ServerRpc { get; init; }
        public required HostServer            Server    { get; init; }
        public required FakeInferenceProvider Fake      { get; init; }
        public required ClientTarget          Target    { get; init; }

        public Task<InitializeResult> InitializeAsync(string? locale = null, string? rootDir = null) =>
            Client.InvokeWithParameterObjectAsync<InitializeResult>(
                "initialize", new { rootDir = rootDir ?? Path.GetTempPath(), locale });

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

        var fake   = new FakeInferenceProvider();
        var server = new HostServer(_ => fake, () =>
        {
            var cfg = new InferpalConfig();
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
            () => h.Client.InvokeAsync<string[]>("models/list").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
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

        var models = await h.Client.InvokeAsync<string[]>("models/list")
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

            // Host history rebuilt: fresh system prompt + the 3 conversational turns.
            var history = h.Server.CurrentSession!.History;
            Assert.Equal(4, history.Count);
            Assert.Equal("system", history[0].Role);
            Assert.Equal("hello", history[1].Content);
            Assert.Equal("hi there", history[3].Content);
        }
        finally
        {
            var deleted = await h.Client.InvokeWithParameterObjectAsync<bool>(
                "session/delete", new { name }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.True(deleted);
        }
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
        await h.Client.InvokeAsync<string[]>("models/list").WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var overlay = h.Server.CurrentSession!.Overlay;
        Assert.True(overlay.TryGet(@"C:\proj\b.cs", out var text));
        Assert.Equal("class B;", text);
        Assert.False(overlay.TryGet(@"C:\proj\a.cs", out _));
        Assert.Single(overlay.Paths);
    }

    // ── command/slash ──────────────────────────────────────────────────────────

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
        Assert.Contains("No agent step", resume.Markdown, StringComparison.Ordinal);
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
}
