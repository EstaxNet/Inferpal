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

    private static Harness CreateHarness()
    {
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        var fake   = new FakeInferenceProvider();
        var server = new HostServer(_ => fake, () => new InferpalConfig());
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
    public async Task CommandSlash_UnknownOrChatOnlyCommand_IsNotHandled()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        var unknown = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/definitely-not-a-command" });
        var vsOnly = await h.Client.InvokeWithParameterObjectAsync<Host.SlashCommandResult>(
            "command/slash", new { text = "/models" });   // delegated id the host doesn't serve

        Assert.False(unknown.Handled);
        Assert.False(vsOnly.Handled);
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
}
