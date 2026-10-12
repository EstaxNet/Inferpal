using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Host;
using Inferpal.Host.Acp;
using Inferpal.Localization;
using Inferpal.Models;
using Nerdbank.Streams;
using StreamJsonRpc;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inferpal as an Agent Client Protocol agent (<c>Inferpal.Host --acp</c>), driven in memory by a client that speaks the
/// wire as written — one JSON message per line — over a host whose model is the fake provider.
/// </summary>
/// <remarks>The conformance kit and the official client measure the real binary (docs/probes/acp/); these hold the
/// properties those runs showed, on every build.</remarks>
[Collection(SignalCollection.Name)]
public sealed class AcpAgentTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"acp-{Guid.NewGuid():N}");

    public AcpAgentTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── The client side of the wire ────────────────────────────────────────────

    /// <summary>A client that writes and reads raw lines: what reaches it is exactly what an ACP client receives.</summary>
    private sealed class LineClient : IAsyncDisposable
    {
        private readonly Stream _stream;
        private readonly StreamReader _reader;
        private readonly Task _loop;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private int _next;

        public readonly ConcurrentQueue<string> Lines = new();
        public readonly ConcurrentQueue<JsonElement> Notifications = new();
        public readonly ConcurrentQueue<JsonElement> Requests = new();

        /// <summary>What the client answers to the agent's requests (method, params) → result.</summary>
        public Func<string, JsonElement, object?> Answer = (_, _) => null;

        public LineClient(Stream stream)
        {
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _loop   = Task.Run(ReadAsync);
        }

        private async Task ReadAsync()
        {
            while (await _reader.ReadLineAsync() is { } line)
            {
                Lines.Enqueue(line);
                JsonElement m;
                try { m = JsonDocument.Parse(line).RootElement.Clone(); }
                catch (JsonException) { continue; }
                var hasMethod = m.TryGetProperty("method", out var method);
                var hasId     = m.TryGetProperty("id", out var id);
                if (hasMethod && hasId)
                {
                    Requests.Enqueue(m);
                    var result = Answer(method.GetString()!, m.TryGetProperty("params", out var p) ? p : default);
                    await WriteAsync(new { jsonrpc = "2.0", id = JsonSerializer.Deserialize<object>(id.GetRawText()), result = result ?? new { } });
                }
                else if (hasMethod) Notifications.Enqueue(m);
                else if (hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt32(), out var tcs)) tcs.TrySetResult(m);
            }
        }

        private readonly SemaphoreSlim _write = new(1, 1);

        private async Task WriteAsync(object message)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
            await _write.WaitAsync();
            try { await _stream.WriteAsync(bytes); await _stream.FlushAsync(); }
            finally { _write.Release(); }
        }

        /// <summary>The whole response message (with <c>result</c> or <c>error</c>).</summary>
        public async Task<JsonElement> RequestAsync(string method, object @params)
        {
            var id = Interlocked.Increment(ref _next);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params });
            return await tcs.Task.WaitAsync(Wait);
        }

        public Task NotifyAsync(string method, object @params) => WriteAsync(new { jsonrpc = "2.0", method, @params });

        public IEnumerable<JsonElement> Updates(string sessionId, string kind) =>
            Notifications.Where(n => n.GetProperty("method").GetString() == "session/update"
                                     && n.GetProperty("params").GetProperty("sessionId").GetString() == sessionId
                                     && n.GetProperty("params").GetProperty("update").GetProperty("sessionUpdate").GetString() == kind)
                         .Select(n => n.GetProperty("params").GetProperty("update"));

        public string Said(string sessionId) =>
            string.Concat(Updates(sessionId, "agent_message_chunk").Select(u => u.GetProperty("content").GetProperty("text").GetString()));

        public async ValueTask DisposeAsync()
        {
            _stream.Dispose();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required AcpAgent Agent { get; init; }
        public required JsonRpc Rpc { get; init; }
        public required LineClient Client { get; init; }
        public required FakeInferenceProvider Fake { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            try { Rpc.Dispose(); } catch { }
            await Agent.DisposeAsync();
        }
    }

    private async Task<Harness> StartAsync(Func<CancellationToken, Task<bool>>? ready = null,
                                          Action<FakeInferenceProvider>? fake = null,
                                          Func<FakeInferenceProvider, HostServer>? host = null,
                                          string? webDir = null, Func<string, bool>? openBrowser = null)
    {
        var provider = new FakeInferenceProvider { RunAgentThroughChat = true, ModelNames = ["test-model"] };
        provider.ChatResult = new ChatTurnResult("ok", null, 0, 0);
        fake?.Invoke(provider);
        var agent = new AcpAgent(
            () => host?.Invoke(provider)
                  ?? new HostServer(_ => provider, () => new InferpalConfig { RagEnabled = false, DefaultModel = "test-model" }),
            ready ?? (_ => Task.FromResult(true)), webDir, openBrowser ?? (_ => true));
        var (agentSide, clientSide) = FullDuplexStream.CreatePair();
        var rpc = AcpRpc.Create(agentSide, agentSide, agent);
        agent.Attach(rpc);
        rpc.StartListening();
        return new Harness { Agent = agent, Rpc = rpc, Client = new LineClient(clientSide), Fake = provider };
    }

    private static object Init(bool fs = false, bool terminalAuth = false, string? title = null) => new
    {
        protocolVersion = 1,
        clientCapabilities = new { fs = new { readTextFile = fs, writeTextFile = false }, auth = new { terminal = terminalAuth } },
        clientInfo = new { name = "test-client", title, version = "1.0" },
    };

    private async Task<string> NewSessionAsync(Harness h)
    {
        var r = await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() });
        return r.GetProperty("result").GetProperty("sessionId").GetString()!;
    }

    private static Task<JsonElement> PromptAsync(Harness h, string sessionId, string text) =>
        h.Client.RequestAsync("session/prompt", new { sessionId, prompt = new[] { new { type = "text", text } } });

    private static string StopReason(JsonElement response) =>
        response.GetProperty("result").GetProperty("stopReason").GetString()!;

    // ── Handshake ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_AnswersVersion1_AndOffersTheSetupOnlyToAClientThatRunsTerminals()
    {
        await using var h = await StartAsync();
        var plain = (await h.Client.RequestAsync("initialize", Init())).GetProperty("result");
        Assert.Equal(1, plain.GetProperty("protocolVersion").GetInt32());
        Assert.True(plain.GetProperty("agentCapabilities").GetProperty("loadSession").GetBoolean());
        Assert.Equal(0, plain.GetProperty("authMethods").GetArrayLength());

        // Two terminal methods: the settings window first (every setting, in a browser), the console setup second.
        var withTerminal = (await h.Client.RequestAsync("initialize", Init(terminalAuth: true))).GetProperty("result");
        var methods = withTerminal.GetProperty("authMethods").EnumerateArray().ToList();
        Assert.Equal(2, methods.Count);
        Assert.All(methods, m => Assert.Equal("terminal", m.GetProperty("type").GetString()));
        Assert.Equal(["settings-window", "setup"], methods.Select(m => m.GetProperty("id").GetString()!));
        Assert.Equal(["--settings", "--setup"], methods.Select(m => Assert.Single(m.GetProperty("args").EnumerateArray()).GetString()!));
    }

    [Fact]
    public async Task TheSettingsWindow_IsAnAuthenticationMethod_LikeTheSetup()
    {
        await using var h = await StartAsync(ready: _ => Task.FromResult(true));
        await h.Client.RequestAsync("initialize", Init(terminalAuth: true));
        foreach (var id in new[] { "settings-window", "setup" })
            Assert.True((await h.Client.RequestAsync("authenticate", new { methodId = id })).TryGetProperty("result", out _), id);
        Assert.True((await h.Client.RequestAsync("authenticate", new { methodId = "nope" })).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task AnUnsupportedVersion_IsAnsweredWithVersion1_NeverEchoed()
    {
        await using var h = await StartAsync();
        var r = await h.Client.RequestAsync("initialize", new { protocolVersion = 65535, clientCapabilities = new { } });
        Assert.Equal(1, r.GetProperty("result").GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    public async Task NotSetUp_SessionNewAsksForTheSetup_OnlyWhenTheClientCanRunIt()
    {
        await using var h = await StartAsync(ready: _ => Task.FromResult(false));
        await h.Client.RequestAsync("initialize", Init(terminalAuth: true));
        var refused = await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.Equal(AcpRpc.AuthRequired, refused.GetProperty("error").GetProperty("code").GetInt32());

        // Without a way to run the setup, refusing would leave no way forward: the session opens.
        await h.Client.RequestAsync("initialize", Init());
        var opened = await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(opened.TryGetProperty("result", out _), opened.GetRawText());
    }

    [Fact]
    public async Task AFailure_IsNeverReportedAsAuthenticationRequired()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var r = await PromptAsync(h, "no-such-session", "hello");
        var error = r.GetProperty("error");
        Assert.Equal(AcpRpc.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.DoesNotContain('\n', error.GetProperty("message").GetString()!);
    }

    // ── A turn ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APrompt_StreamsTheAnswer_AsOneJsonMessagePerLine()
    {
        await using var h = await StartAsync(fake: f => f.OnChat = (onToken, _) =>
        {
            onToken?.Invoke("First line\n");
            onToken?.Invoke("second line");
            return Task.FromResult(new ChatTurnResult("First line\nsecond line", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var r = await PromptAsync(h, sid, "hello");

        Assert.Equal("end_turn", StopReason(r));
        Assert.StartsWith("First line\nsecond line", h.Client.Said(sid));
        // Every line the agent wrote is one JSON-RPC message: an answer's line breaks travel escaped.
        Assert.All(h.Client.Lines, line => Assert.Equal("2.0", JsonDocument.Parse(line).RootElement.GetProperty("jsonrpc").GetString()));
    }

    [Fact]
    public async Task ReasoningTags_AreNotSentAsTheAnswer()
    {
        await using var h = await StartAsync(fake: f => f.OnChat = (onToken, _) =>
        {
            foreach (var t in new[] { "<thi", "nk>private reasoning</th", "ink>", "The answer." }) onToken?.Invoke(t);
            return Task.FromResult(new ChatTurnResult("<think>private reasoning</think>The answer.", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        await PromptAsync(h, sid, "hello");

        var said = h.Client.Said(sid);
        Assert.StartsWith("The answer.", said);
        Assert.DoesNotContain("private reasoning", said);
        Assert.DoesNotContain("<thi", said);
    }

    [Fact]
    public async Task Cancel_EndsTheTurnCancelled_AndNothingFollowsTheResponse()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("partial");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatTurnResult(string.Empty, null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var prompt = PromptAsync(h, sid, "write forever");
        await entered.Task.WaitAsync(Wait);
        await h.Client.NotifyAsync("session/cancel", new { sessionId = sid });
        var r = await prompt;
        var count = h.Client.Notifications.Count;
        await Task.Delay(500);

        Assert.Equal("cancelled", StopReason(r));
        Assert.Equal(count, h.Client.Notifications.Count);
    }

    /// <summary>
    /// ⚠ A Stop that comes while the prompt is being built — here, while the host sizes an attached file's excerpt —
    /// finds no turn on the host to stop. The turn was then sent anyway and ran whole, tools included, under a response
    /// that said "cancelled".
    /// </summary>
    [Fact]
    public async Task AStopWhileThePromptIsBuilt_SendsNothingToTheModel()
    {
        var excerpting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        await using var h = await StartAsync(fake: f =>
            f.OnChat = (_, _) => { Interlocked.Increment(ref asked); return Task.FromResult(new ChatTurnResult("ran", null, 0, 0)); });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        // Held from now on: the excerpt of the attached file asks the server for the window its model loaded.
        h.Fake.OnLoadedContextWindow = async _ => { excerpting.TrySetResult(); await release.Task; };

        var prompt = h.Client.RequestAsync("session/prompt", new
        {
            sessionId = sid,
            prompt = new object[]
            {
                new { type = "text", text = "rewrite this file" },
                new { type = "resource", resource = new { uri = "file:///" + _root.Replace('\\', '/') + "/a.txt", text = "content" } },
            },
        });
        await excerpting.Task.WaitAsync(Wait);
        await h.Client.NotifyAsync("session/cancel", new { sessionId = sid });
        await Task.Delay(200);   // the cancel is handled before the excerpt answers
        release.TrySetResult();
        var r = await prompt;

        Assert.Equal("cancelled", StopReason(r));
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task AStoppedTurn_IsSavedStopped_NotAsAFinishedAnswer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnChat = async (onToken, ct) =>
        {
            onToken?.Invoke("partial answer");
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ChatTurnResult(string.Empty, null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var prompt = PromptAsync(h, sid, "write forever");
        await entered.Task.WaitAsync(Wait);
        await h.Client.NotifyAsync("session/cancel", new { sessionId = sid });
        Assert.Equal("cancelled", StopReason(await prompt));

        var saved = await new Services.Persistence.ConversationStore().LoadAsync(sid, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal("partial answer", saved!.Messages[^2].Content);
        Assert.Equal(Strings.MsgCancelled, saved.Messages[^1].Content);
        Assert.Equal(Services.Persistence.SessionManager.NoticeMarker, saved.Messages[^1].ToolName);
    }

    /// <summary>
    /// ⚠ Live sessions are bounded, and a client never reloads a thread it believes open: closed to make room for a
    /// fifth, a session answered its next prompt with "Unknown session". It is reopened from what it saved.
    /// </summary>
    [Fact]
    public async Task ASessionClosedToMakeRoom_AnswersItsNextPrompt_WithItsConversation()
    {
        var seen = new List<string>();
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, history, _, _) =>
        {
            lock (seen) seen.AddRange(history.Where(m => m.Role == "user").Select(m => m.Content ?? ""));
            return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "remember the word teal")));
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        lock (seen) seen.Clear();

        var again = await PromptAsync(h, first, "which word?");

        Assert.Equal("end_turn", StopReason(again));
        lock (seen) Assert.Contains(seen, q => q.Contains("remember the word teal", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionNeverOpened_IsStillUnknown()
    {
        // Reference arm: an id this agent never opened, or one the client closed, is an error as before.
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var closed = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/close", new { sessionId = closed });

        foreach (var id in new[] { "acp-never-opened", closed })
            Assert.Equal(AcpRpc.InvalidParams, (await PromptAsync(h, id, "hello")).GetProperty("error").GetProperty("code").GetInt32());
    }

    // ── Tools and approvals ────────────────────────────────────────────────────

    [Fact]
    public async Task AToolCall_IsDrawnBeforeItsPermission_WhichShowsTheRealDiff()
    {
        var file = Path.Combine(_root, "notes.txt");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path = file, content = "new text\n" }), CancellationToken.None);
            return new ChatTurnResult("Written.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "allow_once" } }
            : null;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "write it")));

        var call = Assert.Single(h.Client.Updates(sid, "tool_call"));
        var id = call.GetProperty("toolCallId").GetString();
        var permission = Assert.Single(h.Client.Requests, r => r.GetProperty("method").GetString() == "session/request_permission");
        var asked = permission.GetProperty("params").GetProperty("toolCall");
        Assert.Equal(id, asked.GetProperty("toolCallId").GetString());
        var diff = Assert.Single(asked.GetProperty("content").EnumerateArray(), c => c.GetProperty("type").GetString() == "diff");
        Assert.True(Path.IsPathRooted(diff.GetProperty("path").GetString()));
        Assert.Equal("new text\n", diff.GetProperty("newText").GetString());

        var done = Assert.Single(h.Client.Updates(sid, "tool_call_update"));
        Assert.Equal(id, done.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal("new text\n", File.ReadAllText(file));
        // The call is on the wire before the question about it.
        var lines = h.Client.Lines.ToList();
        Assert.True(lines.FindIndex(l => l.Contains("\"tool_call\"", StringComparison.Ordinal))
                    < lines.FindIndex(l => l.Contains("session/request_permission", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ARefusedPermission_WritesNothing_AndEndsTheCallFailed()
    {
        var file = Path.Combine(_root, "kept.txt");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path = file, content = "x" }), CancellationToken.None);
            return new ChatTurnResult("Tried.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "reject_once" } }
            : null;

        await PromptAsync(h, sid, "write it");

        Assert.False(File.Exists(file));
        Assert.Equal("failed", Assert.Single(h.Client.Updates(sid, "tool_call_update")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AFileWithUnsavedChangesInTheClient_IsNeverWrittenOver()
    {
        var file = Path.Combine(_root, "draft.txt");
        File.WriteAllText(file, "on disk\n");
        string? result = null;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = file }), CancellationToken.None);
            result = await tools.ExecuteAsync("write_file", JsonSerializer.SerializeToElement(new { path = file, content = "overwritten\n" }), CancellationToken.None);
            return new ChatTurnResult("Tried.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init(fs: true));
        var sid = await NewSessionAsync(h);
        // The editor holds typing the disk does not have.
        h.Client.Answer = (method, p) => method switch
        {
            "fs/read_text_file" => new { content = "on disk\nthe user is typing\n" },
            "session/request_permission" => new { outcome = new { outcome = "selected", optionId = "allow_once" } },
            _ => null,
        };

        await PromptAsync(h, sid, "overwrite it");

        Assert.Equal("on disk\n", File.ReadAllText(file));
        Assert.Contains("unsaved changes", result);
        Assert.DoesNotContain(h.Client.Requests, r => r.GetProperty("method").GetString() == "session/request_permission");
    }

    [Fact]
    public async Task WithoutFileReading_TheClientIsNeverAskedForAFile()
    {
        var file = Path.Combine(_root, "a.txt");
        File.WriteAllText(file, "content\n");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = file }), CancellationToken.None);
            return new ChatTurnResult("Read.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init(fs: false));
        var sid = await NewSessionAsync(h);

        await PromptAsync(h, sid, "read it");

        Assert.DoesNotContain(h.Client.Requests, r => r.GetProperty("method").GetString()!.StartsWith("fs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheEditorTools_AreNotOffered_AndThePromptNamesTheClient()
    {
        IReadOnlyList<string>? offered = null;
        string? system = null;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, messages, tools, _) =>
        {
            offered = [.. tools.Definitions.Select(d => d.Function.Name)];
            system = messages[0].Content;
            return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init(title: "Zed"));
        var sid = await NewSessionAsync(h);

        await PromptAsync(h, sid, "hello");

        Assert.NotNull(offered);
        Assert.Contains("read_file", offered);
        foreach (var editorTool in new[] { "get_active_document", "get_open_editors", "insert_at_cursor", "replace_selection" })
            Assert.DoesNotContain(editorTool, offered);
        Assert.Contains("Zed", system);
        Assert.DoesNotContain("Visual Studio Code", system);
    }

    // ── Commands ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ACommandOnlyAnEditorServes_IsSaid_AndNeverSentToTheModel()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/xray")));

        Assert.Contains(Strings.AcpCommandNeedsEditor("/xray"), h.Client.Said(sid));
        Assert.Empty(h.Fake.ChatModels);
    }

    [Fact]
    public async Task TheCommandsOffered_LeaveOutTheOnesOnlyAnEditorServes()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        JsonElement update = default;
        var t0 = DateTime.UtcNow;
        while (DateTime.UtcNow - t0 < Wait && !h.Client.Updates(sid, "available_commands_update").Any()) await Task.Delay(50);
        update = h.Client.Updates(sid, "available_commands_update").First();

        var names = update.GetProperty("availableCommands").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("help", names);
        Assert.DoesNotContain("xray", names);
        Assert.DoesNotContain("clear", names);
    }

    // ── Sessions ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ASavedSession_IsListedUnderItsFolder_AndReplayedBeforeTheLoadAnswers()
    {
        string sid;
        await using (var first = await StartAsync(fake: f => f.ChatResult = new ChatTurnResult("Saved answer.", null, 0, 0)))
        {
            await first.Client.RequestAsync("initialize", Init());
            sid = await NewSessionAsync(first);
            await PromptAsync(first, sid, "remember this question");
        }

        await using var second = await StartAsync();
        await second.Client.RequestAsync("initialize", Init());
        var listed = (await second.Client.RequestAsync("session/list", new { cwd = _root })).GetProperty("result").GetProperty("sessions");
        Assert.Contains(listed.EnumerateArray(), s => s.GetProperty("sessionId").GetString() == sid);
        var elsewhere = (await second.Client.RequestAsync("session/list", new { cwd = Path.Combine(_root, "elsewhere") }))
            .GetProperty("result").GetProperty("sessions");
        Assert.DoesNotContain(elsewhere.EnumerateArray(), s => s.GetProperty("sessionId").GetString() == sid);

        var before = second.Client.Lines.Count;
        var loaded = await second.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(loaded.TryGetProperty("result", out _), loaded.GetRawText());
        var lines = second.Client.Lines.Skip(before).ToList();
        var answeredAt = lines.FindIndex(l => l.Contains("\"modes\"", StringComparison.Ordinal));
        var question = lines.FindIndex(l => l.Contains("user_message_chunk", StringComparison.Ordinal) && l.Contains("remember this question", StringComparison.Ordinal));
        var answer = lines.FindIndex(l => l.Contains("agent_message_chunk", StringComparison.Ordinal) && l.Contains("Saved answer.", StringComparison.Ordinal));
        Assert.InRange(question, 0, answeredAt);
        Assert.InRange(answer, 0, answeredAt);
    }

    [Fact]
    public async Task AReplayedSession_DrawsEachToolWithItsTitle_AndSetsANoticeApartFromItsAnswer()
    {
        var sid = "acp-replay-" + Guid.NewGuid().ToString("N");
        await new Services.Persistence.ConversationStore().SaveAsync(sid,
        [
            new("user", "edit alpha.txt"),
            new("tool", "content", "read_file · alpha.txt"),
            new("tool", "content", "list_files"),                                     // a bare name, as VS and VS Code save it
            new("assistant", "Done."),
            new("assistant", "No file was changed.", Services.Persistence.SessionManager.NoticeMarker),
        ], CancellationToken.None, workspaceRoot: _root);

        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var loaded = await h.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(loaded.TryGetProperty("result", out _), loaded.GetRawText());

        var calls = h.Client.Updates(sid, "tool_call").ToList();
        Assert.Equal(["read_file · alpha.txt", "list_files"], calls.Select(c => c.GetProperty("title").GetString()));
        Assert.All(calls, c => Assert.Equal("read", c.GetProperty("kind").GetString()));
        var chunks = h.Client.Updates(sid, "agent_message_chunk")
            .Select(u => u.GetProperty("content").GetProperty("text").GetString()).ToList();
        Assert.Equal(["Done.", "\n\nNo file was changed."], chunks);
    }

    [Fact]
    public async Task APlannedEditTheUserRefused_StaysPending_AndSaysItWasSkipped()
    {
        // A client draws a "completed" step as done: the refused edit read "All Done" under "no file was changed".
        var file = Path.Combine(_root, "plan-target.txt");
        File.WriteAllText(file, "old\n");
        var call = 0;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, _) => Task.FromResult(Interlocked.Increment(ref call) switch
        {
            1 => new ChatTurnResult(
                """{"goal":"add a line","steps":[{"i":1,"desc":"add the line","tool":"write_file"}]}""", null, 0, 0),
            2 => new ChatTurnResult(string.Empty, [new ToolCallDto(new ToolCallFunction("write_file",
                     JsonSerializer.SerializeToElement(new { path = file, content = "new\n" })))], 0, 0),
            _ => new ChatTurnResult("Done.", null, 0, 0),
        }));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "agent" });
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "reject_once" } }
            : null;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "add the line")));

        Assert.Equal("old\n", File.ReadAllText(file));                                // witness: the edit was refused
        var last = h.Client.Updates(sid, "plan").Last().GetProperty("entries")[0];
        Assert.Equal("pending", last.GetProperty("status").GetString());
        Assert.EndsWith(" — " + Strings.AcpPlanStepSkipped, last.GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AToolOutput_IsSentAsACodeBlock_ThatNoRunOfBackticksInItCloses()
    {
        Assert.Equal("```\nC:\\ws\\.inferpal\\history -- kept\n```", AcpToolCalls.Block("C:\\ws\\.inferpal\\history -- kept\n"));
        Assert.Equal("````\nuse ```code``` here\n````", AcpToolCalls.Block("use ```code``` here"));
        Assert.Equal("read_file", AcpToolCalls.ToolOf("read_file · alpha.txt"));
        Assert.Equal("list_files", AcpToolCalls.ToolOf("list_files"));
    }

    [Fact]
    public async Task AToolCallsOutput_ReachesTheClientInACodeBlock()
    {
        var file = Path.Combine(_root, "shown.txt");
        File.WriteAllText(file, "a path: C:\\ws\\.inferpal -- kept\n");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("read_file", JsonSerializer.SerializeToElement(new { path = file }), CancellationToken.None);
            return new ChatTurnResult("Read.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "read it")));

        var done = Assert.Single(h.Client.Updates(sid, "tool_call_update"));
        var text = Assert.Single(done.GetProperty("content").EnumerateArray()).GetProperty("content").GetProperty("text").GetString()!;
        Assert.StartsWith("```\n", text, StringComparison.Ordinal);
        Assert.EndsWith("\n```", text, StringComparison.Ordinal);
        Assert.Contains("C:\\ws\\.inferpal -- kept", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownSession_IsNotFound_WhenLoaded()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var r = await h.Client.RequestAsync("session/load", new { sessionId = "acp-never-" + Guid.NewGuid().ToString("N"), cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.Equal(AcpRpc.NotFound, r.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task TheModeAndTheModel_AreOptionsKeptInStep()
    {
        await using var h = await StartAsync(fake: f => f.ModelNames = ["test-model", "other-model", "nomic-embed-text"]);
        await h.Client.RequestAsync("initialize", Init());
        var created = (await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() })).GetProperty("result");
        var sid = created.GetProperty("sessionId").GetString();
        var model = created.GetProperty("configOptions").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "model");
        var choices = model.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("value").GetString()).ToList();
        Assert.Contains("other-model", choices);
        Assert.DoesNotContain("nomic-embed-text", choices);   // an embedding model answers no question

        var set = (await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "mode", value = "plan" }))
            .GetProperty("result").GetProperty("configOptions");
        Assert.Equal("plan", set.EnumerateArray().Single(o => o.GetProperty("id").GetString() == "mode").GetProperty("currentValue").GetString());
        Assert.Equal(2, set.GetArrayLength());   // the complete list, as the protocol requires

        var unknown = await h.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "yolo" });
        Assert.Equal(AcpRpc.InvalidParams, unknown.GetProperty("error").GetProperty("code").GetInt32());
    }

    // ── Pure pieces ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("text <", 1)]
    [InlineData("text <th", 3)]
    [InlineData("text </thin", 6)]
    [InlineData("a < b", 0)]
    [InlineData("<b>", 0)]
    public void APartlyWrittenReasoningTag_IsHeldBack(string raw, int held) =>
        Assert.Equal(held, AcpShownStream.PartialTagLength(raw));

    [Fact]
    public void TheShownStream_SendsEachPieceOnce_AndNeverTakesBackText()
    {
        var stream = new AcpShownStream();
        var sent = string.Concat(new[] { "Hel", "lo ", "<th", "ink>x</think>", " world" }.Select(stream.Append));
        Assert.Equal("Hello world", sent.Replace("  ", " "));
        Assert.Equal(sent, stream.Sent);
    }

    [Fact]
    public void PromptBlocks_ReadTextLinksAndEmbeddedFiles_AndRefuseAnUnknownBlock()
    {
        var file = Path.Combine(_root, "linked.cs");
        var blocks = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "Look at " },
            new { type = "resource_link", uri = new Uri(file).AbsoluteUri, name = "linked.cs" },
            new { type = "resource", resource = new { uri = "file:///x/embedded.txt", text = "embedded body" } },
            new { type = "image", data = "AAAA", mimeType = "image/png" },
        });
        var content = AcpPromptBlocks.Read(blocks);
        Assert.Equal("Look at ", content.Text);
        Assert.Equal(2, content.Attachments.Count);
        Assert.Equal(Path.GetFullPath(file), content.Attachments[0].Path);
        Assert.Equal("embedded body", content.Attachments[1].Text);
        Assert.Single(content.Notes);

        Assert.Throws<ArgumentException>(() => AcpPromptBlocks.Read(JsonSerializer.SerializeToElement(new[] { new { type = "video" } })));
        Assert.Throws<ArgumentException>(() => AcpPromptBlocks.Read(JsonSerializer.SerializeToElement(Array.Empty<object>())));
    }

    // ── Settings, as the real agent reads them ─────────────────────────────────

    /// <summary>
    /// The settings window saves while a thread is open: the thread follows at its next question — its default model,
    /// told to the client's selector — keeps a model picked in it, and says once that a new server needs a new thread.
    /// </summary>
    [Fact]
    public async Task AnOpenThread_FollowsTheSettingsSavedSince_AtItsNextQuestion()
    {
        var file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-a" };
        await using var h = await StartAsync(
            fake: p => p.ModelNames = ["model-a", "model-b", "model-c"],
            host: p => new HostServer(_ => p, () => new InferpalConfig { RagEnabled = false, DefaultModel = "model-a" },
                                      fileSettings: () => file));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        await PromptAsync(h, sid, "first");
        Assert.Equal("model-a", h.Fake.ChatModels.Last());

        file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-b" };
        await PromptAsync(h, sid, "second");
        Assert.Equal("model-b", h.Fake.ChatModels.Last());
        Assert.Contains(h.Client.Updates(sid, "config_option_update"),
                        u => u.GetRawText().Contains("\"currentValue\":\"model-b\"", StringComparison.Ordinal));

        // A model picked in the thread stays its own when the default changes again.
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "model", value = "model-a" });
        file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-c" };
        await PromptAsync(h, sid, "third");
        Assert.Equal("model-a", h.Fake.ChatModels.Last());

        // A new server: said once — the thread keeps the client it started with.
        file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-c", BaseUrl = "http://elsewhere:1234" };
        await PromptAsync(h, sid, "fourth");
        await PromptAsync(h, sid, "fifth");
        var said = h.Client.Said(sid);
        Assert.Equal(1, said.Split(Strings.AcpServerChangedNewThread).Length - 1);
    }

    [Fact]
    public async Task SlashSettings_OpensTheBrowserWindow_ForTheThreadsFolder_AndOnlyOneOfIt()
    {
        var web = Directory.CreateTempSubdirectory("inferpal-acp-web").FullName;
        foreach (var name in new[] { "settings.css", "settings-standalone.css", "settings-standalone.js" })
            File.WriteAllText(Path.Combine(web, name), "/* */");
        var opened = new ConcurrentQueue<string>();
        string url;
        using var http = new System.Net.Http.HttpClient();
        try
        {
            await using (var h = await StartAsync(webDir: web, openBrowser: u => { opened.Enqueue(u); return true; }))
            {
                await h.Client.RequestAsync("initialize", Init());
                var sid = await NewSessionAsync(h);
                await PromptAsync(h, sid, "/settings");
                url = Assert.Single(opened);
                Assert.Contains(Strings.AcpSettingsOpened(url), h.Client.Said(sid), StringComparison.Ordinal);
                Assert.Contains("settings-standalone.js", await http.GetStringAsync(url), StringComparison.Ordinal);
                Assert.Empty(h.Fake.ChatModels);   // a command of the thread, never a question to the model

                await PromptAsync(h, sid, "/settings");
                Assert.Equal([url, url], opened);
            }
            // The agent gone, its window is too.
            await Assert.ThrowsAnyAsync<System.Net.Http.HttpRequestException>(() => http.GetStringAsync(url));
        }
        finally { Directory.Delete(web, recursive: true); }
    }

    [Fact]
    public async Task SlashSettings_WithoutTheWindowsFiles_SaysWhatIsMissing()
    {
        var empty = Directory.CreateTempSubdirectory("inferpal-acp-noweb").FullName;
        var opened = new ConcurrentQueue<string>();
        try
        {
            await using var h = await StartAsync(webDir: empty, openBrowser: u => { opened.Enqueue(u); return true; });
            await h.Client.RequestAsync("initialize", Init());
            var sid = await NewSessionAsync(h);
            await PromptAsync(h, sid, "/settings");
            Assert.Contains(Strings.SettingsWindowFilesMissing(empty), h.Client.Said(sid), StringComparison.Ordinal);
            Assert.Empty(opened);
        }
        finally { Directory.Delete(empty, recursive: true); }
    }

    [Fact]
    public void AnUnknownTool_IsDrawnAsOther_NeverAsAnEdit()
    {
        Assert.Equal("edit", AcpToolCalls.Kind("apply_edits"));
        Assert.Equal("execute", AcpToolCalls.Kind("run_command"));
        Assert.Equal("other", AcpToolCalls.Kind("mcp__github__create_issue"));
    }
}
