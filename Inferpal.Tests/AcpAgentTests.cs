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
                                          string? webDir = null, Func<string, bool>? openBrowser = null,
                                          string? limitsTold = null)
    {
        var provider = new FakeInferenceProvider { RunAgentThroughChat = true, ModelNames = ["test-model"] };
        provider.ChatResult = new ChatTurnResult("ok", null, 0, 0);
        fake?.Invoke(provider);
        // Each agent its own record of the editors told the limits, unless a test shares one between "processes".
        var told = new Inferpal.Services.Persistence.AppDataJsonFile<Dictionary<string, string>>("acp-limits-told.json", "Test")
        {
            PathOverride = limitsTold ?? Path.Combine(_root, $"limits-told-{Guid.NewGuid():N}.json"),
        };
        var agent = new AcpAgent(
            () => host?.Invoke(provider)
                  ?? new HostServer(_ => provider, () => new InferpalConfig { RagEnabled = false, DefaultModel = "test-model" }),
            ready ?? (_ => Task.FromResult(true)), webDir, openBrowser ?? (_ => true), told);
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
    public async Task AFolderTheAgentsMachineLacks_IsRefused_AndNamed()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());

        var missing = await h.Client.RequestAsync("session/new", new { cwd = Path.Combine(_root, "not-here"), mcpServers = Array.Empty<object>() });
        var error = missing.GetProperty("error");
        Assert.Equal(AcpRpc.InvalidParams, error.GetProperty("code").GetInt32());
        Assert.Contains("not a folder the agent can open on its machine", error.GetProperty("message").GetString());
        Assert.Contains("not-here", error.GetProperty("message").GetString());

        // The other system's way of writing a folder: a Linux client's to an agent on Windows, a Windows client's elsewhere.
        var other = OperatingSystem.IsWindows() ? "/home/me/project" : @"C:\Users\me\project";
        var foreign = (await h.Client.RequestAsync("session/new", new { cwd = other, mcpServers = Array.Empty<object>() })).GetProperty("error");
        Assert.Contains("a path of another system", foreign.GetProperty("message").GetString());
        Assert.Contains(other, foreign.GetProperty("message").GetString());

        // Reference arm: the folder that exists opens.
        var opened = await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(opened.TryGetProperty("result", out _), opened.GetRawText());
    }

    [Fact]
    public async Task ASavedConversation_IsLookedUp_WhateverBecameOfItsFolder()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var r = await h.Client.RequestAsync("session/load",
            new { sessionId = "acp-never-" + Guid.NewGuid().ToString("N"), cwd = Path.Combine(_root, "moved"), mcpServers = Array.Empty<object>() });

        // Looked up (and not found), never refused for its folder: reading a conversation back needs none.
        Assert.Equal(AcpRpc.NotFound, r.GetProperty("error").GetProperty("code").GetInt32());
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
    /// ⚠ What the client already showed of a FAILED turn's answer is kept with the thread, as both editors keep it: a
    /// notice, never the model's answer. Only the error was saved, and the reloaded thread lost what the user had read.
    /// </summary>
    [Fact]
    public async Task AFailedTurnsShownPart_IsSavedAsANotice_BeforeTheError()
    {
        await using var h = await StartAsync(fake: f => f.OnChat = (onToken, _) =>
        {
            onToken?.Invoke("the shown half");
            throw new System.Net.Http.HttpRequestException("the backend went away");
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var r = await PromptAsync(h, sid, "explain this");
        Assert.True(r.TryGetProperty("error", out _), r.GetRawText());   // WITNESS: the turn failed

        var saved = await new Services.Persistence.ConversationStore().LoadAsync(sid, CancellationToken.None);
        Assert.NotNull(saved);
        var shown = Assert.Single(saved!.Messages, m => m.Content == "the shown half");
        Assert.Equal(Services.Persistence.SessionManager.NoticeMarker, shown.ToolName);
        Assert.Equal(Services.Persistence.SessionManager.NoticeMarker, saved.Messages[^1].ToolName);
        Assert.Equal(saved.Messages.Count - 2, saved.Messages.IndexOf(shown));
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

    /// <summary>
    /// ⚠ A thread's installed models are read again: at each turn's start, and before a pick is refused. Read once as the
    /// thread opened, a server started after the editor — the ordinary order — or a model pulled since left the selector
    /// offering the default alone for the thread's whole life, and a model picked in it was "unknown".
    /// </summary>
    [Fact]
    public async Task ModelsInstalledAfterTheThreadOpened_AreOffered_AndCanBePicked()
    {
        await using var h = await StartAsync(fake: f => f.ModelNames = []);   // the server lists nothing as the thread opens
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Fake.ModelNames = ["test-model", "late-model", "picked-model"];

        // The next turn's options list them.
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "hello")));
        var listed = h.Client.Updates(sid, "config_option_update").LastOrDefault();
        Assert.Contains("late-model", listed.ToString(), StringComparison.Ordinal);

        // And a pick is read again before it is refused.
        h.Fake.ModelNames = ["test-model", "late-model", "picked-model", "pulled-just-now"];
        var picked = await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "model", value = "pulled-just-now" });
        Assert.True(picked.TryGetProperty("result", out _), picked.ToString());

        // Reference arm: a model nobody installed is still refused.
        var unknown = await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "model", value = "no-such-model" });
        Assert.True(unknown.TryGetProperty("error", out _), unknown.ToString());
    }

    /// <summary>
    /// ⚠ A <c>/task</c>'s report — the host's notice, after the turn — is kept with the thread, as both editors keep it.
    /// Shown only, it was gone from the thread once reloaded, or reopened after an eviction: its result with it.
    /// </summary>
    [Fact]
    public async Task ATasksReport_IsKeptWithTheThread()
    {
        const string goal = "count the files";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnAgentRun = async (history, _, _, ct) =>
        {
            if (!history.Any(m => m.Content == goal)) return new AgentResult("ok", [], history);
            await release.Task.WaitAsync(ct);   // the report comes after the turn, as it does
            return new AgentResult("There are three files.", [], history);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/task " + goal)));

        var before = h.Client.Said(sid).Length;
        release.SetResult();
        for (var i = 0; i < 300 && h.Client.Said(sid).Length == before; i++) await Task.Delay(100);
        var report = h.Client.Said(sid)[before..].Trim();
        Assert.NotEmpty(report);   // WITNESS: the report came, after the turn

        SessionData? saved = null;
        for (var i = 0; i < 300; i++)
        {
            saved = await new Services.Persistence.ConversationStore().LoadAsync(sid, CancellationToken.None);
            if (saved?.Messages.Any(m => m.Content.Trim() == report) == true) break;
            await Task.Delay(100);
        }
        var kept = Assert.Single(saved!.Messages, m => m.Content.Trim() == report);
        Assert.Equal(Services.Persistence.SessionManager.NoticeMarker, kept.ToolName);   // a notice: never read back as an answer
    }

    // ── Session notices and compaction (ACP 1.11) ───────────────────────────────

    private static object InitShowing(bool notices, bool compaction) => new
    {
        protocolVersion = 1,
        clientCapabilities = new
        {
            session = new Dictionary<string, object?>
            {
                ["notices"]    = notices ? new { } : null,
                ["compaction"] = compaction ? new { } : null,
            },
        },
        clientInfo = new { name = "test-client", version = "1.0" },
    };

    /// <summary>
    /// What the agent says beside the answer — here, once per editor, what it does not do in it — is a session notice for a
    /// client that declared it shows them, outside the transcript; for one that did not (absent or null), the text it
    /// always was, byte for byte.
    /// </summary>
    [Fact]
    public async Task ANotice_IsANoticeForAClientThatShowsThem_AndTheSameTextAsBeforeForOneThatDoesNot()
    {
        var notice = Strings.AcpLimitsNotice(AcpReadiness.DocsUrl);
        await using (var h = await StartAsync())
        {
            await h.Client.RequestAsync("initialize", InitShowing(notices: true, compaction: false));
            var sid = await NewSessionAsync(h);
            await PromptAsync(h, sid, "first");
            var shown = Assert.Single(h.Client.Updates(sid, "notice"));
            Assert.Equal("info", shown.GetProperty("severity").GetString());
            // A plain-text title, the first sentence; the rest as the description — the notice as a whole, without Markdown.
            var (title, description) = AcpSession.NoticeParts(notice)!.Value;
            Assert.Equal(title, shown.GetProperty("title").GetString());
            Assert.Equal(description, shown.GetProperty("description").GetString());
            Assert.DoesNotContain("`", title + description, StringComparison.Ordinal);
            Assert.True(title.Length < notice.Length, "the title is the whole notice");
            Assert.DoesNotContain("---", h.Client.Said(sid));
            Assert.Equal("ok", h.Client.Said(sid).Trim());   // the answer itself, alone in the transcript
        }
        await using (var h = await StartAsync())
        {
            await h.Client.RequestAsync("initialize", InitShowing(notices: false, compaction: true));
            var sid = await NewSessionAsync(h);
            await PromptAsync(h, sid, "first");
            Assert.Empty(h.Client.Updates(sid, "notice"));
            Assert.EndsWith("\n\n---\n" + notice, h.Client.Said(sid), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The host's sentences keep the grade they were given where they were said — a rule written is news, one that could
    /// not be is a warning — and a RESULT (what a documentation crawl indexed: the only place <c>/docs add</c> says it) stays
    /// the thread's text, kept to come back, for a client that shows notices too. A notice shown as one is not kept: it
    /// would come back as the answer's text when the thread reloads.
    /// </summary>
    [Fact]
    public async Task AHostSentence_IsANoticeWithItsGrade_ButAResultStaysTheThreadsText()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", InitShowing(notices: true, compaction: false));
        var sid = await NewSessionAsync(h);
        await PromptAsync(h, sid, "first");
        var session = h.Agent.Live(sid)!;
        var before = h.Client.Updates(sid, "notice").Count();

        session.HostNotice(new AcpSession.TextNote("Always: commands that start with `dotnet test`. Saved to your rules.", "info"));
        session.HostNotice(new AcpSession.TextNote("The rule could not be saved: access denied.", "warning"));
        session.HostNotice(new AcpSession.TextNote("Docs: ✅ Vue — 120 pages, 900 chunks", Result: true));
        await h.Client.RequestAsync("session/list", new { cwd = _root });   // a round trip: the notifications were sent

        var notices = h.Client.Updates(sid, "notice").Skip(before).ToList();
        Assert.Equal(["info", "warning"], notices.Select(n => n.GetProperty("severity").GetString()!));
        Assert.Equal("Always: commands that start with dotnet test.", notices[0].GetProperty("title").GetString());
        Assert.Contains("Docs: ✅ Vue — 120 pages", h.Client.Said(sid), StringComparison.Ordinal);
        Assert.DoesNotContain("could not be saved", h.Client.Said(sid), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A <c>/task</c>'s report is its result: never a notice, which a client may let be dismissed and never replays —
    /// it stays the text of the thread for a client that shows notices too.
    /// </summary>
    [Fact]
    public async Task ATasksReport_StaysTheThreadsText_ForAClientThatShowsNotices()
    {
        const string goal = "count the files";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnAgentRun = async (history, _, _, ct) =>
        {
            if (!history.Any(m => m.Content == goal)) return new AgentResult("ok", [], history);
            await release.Task.WaitAsync(ct);
            return new AgentResult("There are three files.", [], history);
        });
        await h.Client.RequestAsync("initialize", InitShowing(notices: true, compaction: true));
        var sid = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/task " + goal)));

        var before = h.Client.Said(sid).Length;
        var noticesBefore = h.Client.Updates(sid, "notice").Count();
        release.SetResult();
        for (var i = 0; i < 300 && h.Client.Said(sid).Length == before; i++) await Task.Delay(100);
        Assert.Contains("t1", h.Client.Said(sid)[before..], StringComparison.Ordinal);   // the report came, as text
        Assert.Equal(noticesBefore, h.Client.Updates(sid, "notice").Count());
    }

    /// <summary>
    /// A conversation made to fit before a question is a compaction for a client that declared it shows them — a
    /// terminal <c>compaction_update</c>, never the tool call — and the <c>context_compact</c> call it always was for one
    /// that did not. Each compaction of a session has an identifier of its own.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACompaction_IsACompactionForAClientThatShowsThem_AndTheToolCallForOneThatDoesNot(bool shows)
    {
        var words = string.Join(' ', Enumerable.Range(0, 1_500).Select(i => $"word{i}"));
        await using var h = await StartAsync(
            fake: f => f.OnChatRequest = (_, messages, _, _) => Task.FromResult(new ChatTurnResult(
                messages[^1].Content?.Contains("long", StringComparison.Ordinal) == true ? "done " + words : "done", null, 3, 5)),
            host: provider => new HostServer(_ => provider, () => new InferpalConfig
            {
                RagEnabled = false, DefaultModel = "test-model", ContextWindowSize = 8_192, CompactionEnabled = true,
            }));
        await h.Client.RequestAsync("initialize", InitShowing(notices: shows, compaction: shows));
        var sid = await NewSessionAsync(h);

        int Compactions() => h.Client.Updates(sid, "compaction_update").Count()
                           + h.Client.Updates(sid, "tool_call").Count(u => u.GetProperty("title").GetString() == "context_compact");
        for (var i = 0; i < 16 && Compactions() < 2; i++) await PromptAsync(h, sid, $"long question {i}");
        Assert.True(Compactions() >= 2, "the conversation did not outgrow its window twice");   // WITNESS: two compactions

        var updates = h.Client.Updates(sid, "compaction_update").ToList();
        var calls = h.Client.Updates(sid, "tool_call").Where(u => u.GetProperty("title").GetString() == "context_compact").ToList();
        if (shows)
        {
            Assert.Empty(calls);
            Assert.Equal(updates.Count, updates.Select(u => u.GetProperty("compactionId").GetString()).Distinct().Count());
            foreach (var u in updates)
            {
                var status = u.GetProperty("status").GetString();
                Assert.Contains(status, new[] { "completed", "failed" });
                if (status == "failed") Assert.False(string.IsNullOrEmpty(u.GetProperty("error").GetString()));
                if (u.TryGetProperty("summary", out var summary))
                {
                    var block = Assert.Single(summary.EnumerateArray());
                    Assert.Equal("text", block.GetProperty("type").GetString());
                    Assert.DoesNotContain("[Context Note]", block.GetProperty("text").GetString(), StringComparison.Ordinal);
                }
            }
        }
        else
        {
            Assert.Empty(updates);
            Assert.NotEmpty(calls);
        }
    }

    /// <summary>
    /// ⚠ A thread being closed to make room answers a prompt that comes meanwhile. It left the live sessions first and
    /// was recorded as reopenable only once its host was disposed — its MCP servers shutting down, seconds —: in between
    /// it was nowhere, and the prompt answered "Unknown session", the very error closing for room must never cause.
    /// </summary>
    [Fact]
    public async Task AThreadBeingClosedToMakeRoom_AnswersAPromptMeanwhile()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "remember the word teal")));
        for (var i = 0; i < AcpAgent.MaxLiveSessions - 1; i++) await NewSessionAsync(h);   // at the cap

        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Agent.WhileClosing = id =>
        {
            if (id != first) return Task.CompletedTask;
            closing.TrySetResult();
            return release.Task;
        };
        var opening = NewSessionAsync(h);   // one more: the first is closed for room, its close held open
        await closing.Task.WaitAsync(Wait);
        Assert.False(h.Agent.IsLive(first));   // WITNESS: no longer live, its host not disposed yet

        var answer = await PromptAsync(h, first, "which word?");
        release.SetResult();
        await opening;
        Assert.Equal("end_turn", StopReason(answer));
    }

    /// <summary>
    /// ⚠ A Stop that comes while a thread closed to make room is being reopened — its host started again, the model
    /// server asked, its conversation read back: seconds — stops that prompt. It found no session, or no turn yet, and
    /// was lost: the whole turn then ran, tools and writes included, under the Stop the user had pressed.
    /// </summary>
    [Fact]
    public async Task AStopWhileAThreadIsReopened_StopsItsPrompt_BeforeTheHost()
    {
        var reopening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false;
        var chats = 0;
        await using var h = await StartAsync(fake: f =>
        {
            f.OnChatRequest = (_, _, _, _) =>
            {
                Interlocked.Increment(ref chats);
                return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
            };
            f.BeforeListModels = async () =>
            {
                if (!Volatile.Read(ref hold)) return;
                reopening.TrySetResult();
                await release.Task;
            };
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "remember the word teal")));
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        Assert.False(h.Agent.IsLive(first));   // WITNESS
        Interlocked.Exchange(ref chats, 0);

        Volatile.Write(ref hold, true);
        var prompt = PromptAsync(h, first, "rewrite every file");
        await reopening.Task.WaitAsync(Wait);   // its host is starting again, asking the model server
        await h.Client.NotifyAsync("session/cancel", new { sessionId = first });
        Volatile.Write(ref hold, false);
        release.SetResult();

        Assert.Equal("cancelled", StopReason(await prompt));
        Assert.Equal(0, Volatile.Read(ref chats));

        // Reference arm: the next prompt, not stopped, runs.
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "hello")));
        Assert.Equal(1, Volatile.Read(ref chats));
    }

    /// <summary>An agent whose next reopening of a thread is held until <c>release</c>, and the thread closed for room.</summary>
    private async Task<(Harness H, string First, TaskCompletionSource Reopening, TaskCompletionSource Release, Action Hold)>
        AThreadClosedForRoomAsync()
    {
        var reopening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 0;
        var h = await StartAsync(fake: f => f.BeforeListModels = async () =>
        {
            if (Interlocked.CompareExchange(ref hold, 0, 1) != 1) return;   // the reopening's own listing, once
            reopening.TrySetResult();
            await release.Task;
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "remember the word teal")));
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        Assert.False(h.Agent.IsLive(first));   // WITNESS
        return (h, first, reopening, release, () => Volatile.Write(ref hold, 1));
    }

    /// <summary>
    /// ⚠ Every request that comes while a thread closed for room is being reopened waits for that reopening. The first took
    /// the thread out of the closed ones; a second, during the seconds its host took to start, found it nowhere and was
    /// answered "Unknown session" — or found it live but not restored yet, and the restore then undid it: Agent mode
    /// picked by the user, Plan mode put back.
    /// </summary>
    [Fact]
    public async Task ARequestWhileAThreadIsReopened_IsAppliedAfterTheRestore()
    {
        var offered = new List<string>();
        var reopening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = 0;
        await using var h = await StartAsync(fake: f =>
        {
            f.ModelNames = ["test-model", "other-model"];
            f.OnChatRequest = (_, _, tools, _) =>
            {
                lock (offered) { offered.Clear(); offered.AddRange(tools.Definitions.Select(d => d.Function.Name)); }
                return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
            };
            f.BeforeListModels = async () =>
            {
                if (Interlocked.CompareExchange(ref hold, 0, 1) != 1) return;
                reopening.TrySetResult();
                await release.Task;
            };
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "mode", value = "plan" });
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "remember the word teal")));
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        Assert.False(h.Agent.IsLive(first));   // WITNESS

        Volatile.Write(ref hold, 1);
        var model = h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "model", value = "other-model" });
        await reopening.Task.WaitAsync(Wait);
        var mode = h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "mode", value = "agent" });
        release.SetResult();
        await model.WaitAsync(Wait);
        await mode.WaitAsync(Wait);                 // not "Unknown session"

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "write the notes")));
        lock (offered) Assert.Contains("write_file", offered);   // Agent mode, as the user picked it last
    }

    /// <summary>
    /// ⚠ A thread its client closes while it is being reopened is closed: nothing was live yet to close, and the thread
    /// came back live afterwards — its host and its MCP servers running for nobody.
    /// </summary>
    [Fact]
    public async Task AThreadClosedWhileItIsReopened_StaysClosed()
    {
        var (h, first, reopening, release, hold) = await AThreadClosedForRoomAsync();
        await using var _ = h;

        hold();
        var reopen = h.Client.RequestAsync("session/set_mode", new { sessionId = first, modeId = "plan" });
        await reopening.Task.WaitAsync(Wait);
        var close = h.Client.RequestAsync("session/close", new { sessionId = first });
        release.SetResult();

        await reopen.WaitAsync(Wait);
        await close.WaitAsync(Wait);
        Assert.False(h.Agent.IsLive(first));
    }

    /// <summary>
    /// ⚠ A thread whose <c>/task</c> still runs is not idle. Closed to make room — no turn open, used longest ago —, its
    /// task died with its host, without a word, and its result never came. The idle thread without work goes instead;
    /// once the task is over, the thread is idle like any other.
    /// </summary>
    [Fact]
    public async Task AThreadWhoseTaskStillRuns_IsNotClosedToMakeRoom()
    {
        const string goal = "count the files slowly";
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = await StartAsync(fake: f => f.OnAgentRun = async (history, _, _, ct) =>
        {
            if (history.Any(m => m.Role == "user" && m.Content == goal)) await release.Task.WaitAsync(ct);
            return new AgentResult("ok", [], history);
        });
        await h.Client.RequestAsync("initialize", Init());
        var working = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, working, "/task " + goal)));
        var idle = await NewSessionAsync(h);
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, idle, "hello")));

        for (var i = 0; i < AcpAgent.MaxLiveSessions - 1; i++) await NewSessionAsync(h);   // one past the cap
        Assert.True(h.Agent.IsLive(working), "the thread whose task runs was closed to make room");
        Assert.False(h.Agent.IsLive(idle));   // WITNESS: a thread was closed — the idle one

        // Reference arm: the task over (its end said in the thread), the thread is the idle one used longest ago.
        var said = h.Client.Said(working).Length;
        release.SetResult();
        for (var i = 0; i < 300 && h.Client.Said(working).Length == said; i++) await Task.Delay(100);
        Assert.NotEqual(said, h.Client.Said(working).Length);
        await NewSessionAsync(h);
        Assert.False(h.Agent.IsLive(working));
    }

    /// <summary>
    /// A thread closed to make room comes back as its client still shows it: in Plan mode (read-only tools), on the model
    /// picked in its selector, with the file attached for its next prompt. Reopened from its conversation alone, the next
    /// request ran in the mode a new thread opens in — with the write tools — on the default model.
    /// </summary>
    [Fact]
    public async Task ASessionClosedToMakeRoom_ComesBackInItsModeOnItsModel_WithItsAttachment()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "the word is teal\n");
        var offered = new List<string>();
        var seen = new List<string>();
        await using var h = await StartAsync(fake: f =>
        {
            f.ModelNames = ["test-model", "other-model"];
            f.OnChatRequest = (_, history, tools, _) =>
            {
                lock (seen)
                {
                    offered.Clear();
                    offered.AddRange(tools.Definitions.Select(d => d.Function.Name));
                    seen.AddRange(history.Where(m => m.Role == "user").Select(m => m.Content ?? ""));
                }
                return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
            };
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "mode", value = "plan" });
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "model", value = "other-model" });
        await PromptAsync(h, first, "/read notes.txt");   // attached to the next prompt
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        h.Fake.ChatModels.Clear();

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "which word?")));

        Assert.Equal("other-model", h.Fake.ChatModels.Last());
        lock (seen)
        {
            Assert.Contains("read_file", offered);   // WITNESS: the tools were read
            Assert.DoesNotContain("write_file", offered);
            Assert.Contains(seen, q => q.Contains("the word is teal", StringComparison.Ordinal));
        }
    }

    /// <summary>The same thread asked back by <c>session/resume</c> rather than by a prompt comes back the same way: loaded
    /// bare, it ran in the configured mode on the default model, its attachment gone.</summary>
    [Fact]
    public async Task ASessionClosedToMakeRoom_ResumedByTheClient_ComesBackInItsModeOnItsModel_WithItsAttachment()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "the word is teal\n");
        var offered = new List<string>();
        var seen = new List<string>();
        await using var h = await StartAsync(fake: f =>
        {
            f.ModelNames = ["test-model", "other-model"];
            f.OnChatRequest = (_, history, tools, _) =>
            {
                lock (seen)
                {
                    offered.Clear();
                    offered.AddRange(tools.Definitions.Select(d => d.Function.Name));
                    seen.AddRange(history.Where(m => m.Role == "user").Select(m => m.Content ?? ""));
                }
                return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
            };
        });
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "mode", value = "plan" });
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = first, configId = "model", value = "other-model" });
        await PromptAsync(h, first, "/read notes.txt");   // attached to the next prompt
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room

        await h.Client.RequestAsync("session/resume", new { sessionId = first, cwd = _root, mcpServers = Array.Empty<object>() });
        h.Fake.ChatModels.Clear();
        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "which word?")));

        Assert.Equal("other-model", h.Fake.ChatModels.Last());
        lock (seen)
        {
            Assert.Contains("read_file", offered);   // WITNESS: the tools were read
            Assert.DoesNotContain("write_file", offered);
            Assert.Contains(seen, q => q.Contains("the word is teal", StringComparison.Ordinal));
        }
    }

    /// <summary>A thread closed to make room whose mode's file is gone since comes back in Default — and the client's
    /// selector is told: no load response carries it there.</summary>
    [Fact]
    public async Task ASessionClosedToMakeRoom_WhoseModesFileIsGone_TellsTheClientItIsInDefault()
    {
        var file = Path.Combine(_root, ".inferpal", "modes", "review.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "---\nname: Review\ntools: [read]\n---\nREVIEW-INSTRUCTIONS\n");
        await using var h = await StartAsync(fake: f => f.ChatResult = new ChatTurnResult("ok", null, 0, 0));
        await h.Client.RequestAsync("initialize", Init());
        var first = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_mode", new { sessionId = first, modeId = "mode:Review" });
        await PromptAsync(h, first, "remember this question");
        for (var i = 0; i < AcpAgent.MaxLiveSessions; i++) await NewSessionAsync(h);   // the first is closed for room
        File.Delete(file);
        var before = h.Client.Lines.Count;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, first, "and now?")));

        Assert.Contains(h.Client.Lines.Skip(before), l => l.Contains("\"current_mode_update\"", StringComparison.Ordinal)
                                                          && l.Contains("\"currentModeId\":\"default\"", StringComparison.Ordinal));
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

        var updates = h.Client.Updates(sid, "tool_call_update").ToList();
        Assert.Equal(2, updates.Count);
        // The card shows the change before the question: a client that keeps the card and ignores what the request
        // carries (Qt Creator, Code Wicket) still shows the diff being approved.
        var shown = updates[0];
        Assert.Equal(id, shown.GetProperty("toolCallId").GetString());
        var shownDiff = Assert.Single(shown.GetProperty("content").EnumerateArray(), c => c.GetProperty("type").GetString() == "diff");
        Assert.Equal("new text\n", shownDiff.GetProperty("newText").GetString());
        var done = updates[^1];
        Assert.Equal(id, done.GetProperty("toolCallId").GetString());
        Assert.Equal("completed", done.GetProperty("status").GetString());
        Assert.Equal("new text\n", File.ReadAllText(file));
        // The call, then its diff, are on the wire before the question about it.
        var lines = h.Client.Lines.ToList();
        var asking = lines.FindIndex(l => l.Contains("session/request_permission", StringComparison.Ordinal));
        Assert.True(lines.FindIndex(l => l.Contains("\"tool_call\"", StringComparison.Ordinal)) < asking);
        Assert.True(lines.FindIndex(l => l.Contains("\"tool_call_update\"", StringComparison.Ordinal)) < asking);
    }

    /// <summary>
    /// ⚠ The editor's stdio MCP server is the same server with or without <c>"type": "stdio"</c>: JetBrains IDEs write
    /// the type, and a reader that only takes the untyped form refuses their server as a transport it does support.
    /// </summary>
    [Fact]
    public void AnEditorsStdioServer_IsTaken_WithOrWithoutItsType()
    {
        var p = JsonSerializer.SerializeToElement(new
        {
            mcpServers = new object[]
            {
                new { name = "zed-style", command = "tool", args = new[] { "a" }, env = Array.Empty<object>() },
                new { type = "stdio", name = "rider", command = "rider64.exe", args = new[] { "stdioMcpServer" },
                      env = new[] { new { name = "IJ_MCP_AUTH_TOKEN", value = "t" } } },
                new { type = "http", name = "web", url = "http://127.0.0.1:1/mcp", headers = Array.Empty<object>() },
                new { type = "sse", name = "old", url = "http://127.0.0.1:1/sse", headers = Array.Empty<object>() },
            },
        });

        var servers = AcpAgent.McpServers(p);

        Assert.Equal(["zed-style", "rider", "web"], servers.Select(s => s.Name));
        var rider = servers[1];
        Assert.Equal("rider64.exe", rider.Command);
        Assert.Equal(["stdioMcpServer"], rider.Args!);
        Assert.Equal("t", rider.Env!["IJ_MCP_AUTH_TOKEN"]);
    }

    /// <summary>
    /// ⚠ What Inferpal does not do in an ACP client is told once per editor on the machine, not once per agent process:
    /// a JetBrains IDE starts one agent per chat, and a notice told per process comes after the first answer of every
    /// chat.
    /// </summary>
    [Fact]
    public async Task TheLimitsNotice_IsToldOncePerEditor_NotOncePerAgentProcess()
    {
        var told = Path.Combine(_root, "told.json");
        var notice = Strings.AcpLimitsNotice(AcpReadiness.DocsUrl);
        async Task<(string First, string Second)> ChatAsync(string client)
        {
            await using var h = await StartAsync(limitsTold: told);
            await h.Client.RequestAsync("initialize", new
            {
                protocolVersion = 1,
                clientCapabilities = new { },
                clientInfo = new { name = client, version = "1.0" },
            });
            var sid = await NewSessionAsync(h);
            await PromptAsync(h, sid, "first");
            var first = h.Client.Said(sid);
            await PromptAsync(h, sid, "second");
            return (first, h.Client.Said(sid)[first.Length..]);
        }

        var rider = await ChatAsync("JetBrains.JetBrains Rider");
        Assert.Contains(notice, rider.First);
        Assert.DoesNotContain(notice, rider.Second);
        // The next chat is another agent process: the same editor was told already.
        var next = await ChatAsync("JetBrains.JetBrains Rider");
        Assert.Contains("ok", next.First);
        Assert.DoesNotContain(notice, next.First);
        // Another editor has not been told.
        Assert.Contains(notice, (await ChatAsync("zed")).First);

        // A record that cannot be read leaves the notice said, and is replaced: it does not come back at every chat.
        File.WriteAllText(told, "not json");
        Assert.Contains(notice, (await ChatAsync("JetBrains.JetBrains Rider")).First);
        var after = await ChatAsync("JetBrains.JetBrains Rider");
        Assert.Contains("ok", after.First);
        Assert.DoesNotContain(notice, after.First);
    }

    /// <summary>
    /// One call that changes two files asks one question: the card carries every file's change, before the question
    /// and in it — the second file is approved too, so it is shown too.
    /// </summary>
    [Fact]
    public async Task ACallThatChangesTwoFiles_ShowsBothChanges_BeforeItsOneQuestion()
    {
        var alpha = Path.Combine(_root, "alpha.txt");
        var gamma = Path.Combine(_root, "gamma.txt");
        File.WriteAllText(alpha, "ALPHA is the first word.\n");
        File.WriteAllText(gamma, "BETA is the second word.\n");
        await using var h = await StartAsync(fake: f => f.OnChatRequest = async (_, _, tools, _) =>
        {
            await tools.ExecuteAsync("apply_edits", JsonSerializer.SerializeToElement(new
            {
                edits = new[]
                {
                    new { path = alpha, old_content = "ALPHA", new_content = "OMEGA" },
                    new { path = gamma, old_content = "BETA", new_content = "DELTA" },
                },
            }), CancellationToken.None);
            return new ChatTurnResult("Both edited.", null, 0, 0);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "allow_once" } }
            : null;

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "edit both")));

        var permission = Assert.Single(h.Client.Requests, r => r.GetProperty("method").GetString() == "session/request_permission");
        static string[] Paths(JsonElement content) => [.. content.EnumerateArray()
            .Where(c => c.GetProperty("type").GetString() == "diff")
            .Select(c => Path.GetFileName(c.GetProperty("path").GetString()!))];
        Assert.Equal(["alpha.txt", "gamma.txt"], Paths(permission.GetProperty("params").GetProperty("toolCall").GetProperty("content")));
        var shown = h.Client.Updates(sid, "tool_call_update").First();
        Assert.Equal(["alpha.txt", "gamma.txt"], Paths(shown.GetProperty("content")));
        var lines = h.Client.Lines.ToList();
        Assert.True(lines.FindIndex(l => l.Contains("\"tool_call_update\"", StringComparison.Ordinal))
                    < lines.FindIndex(l => l.Contains("session/request_permission", StringComparison.Ordinal)));
        Assert.Equal("OMEGA is the first word.\n", File.ReadAllText(alpha));
        Assert.Equal("DELTA is the second word.\n", File.ReadAllText(gamma));
    }

    /// <summary>
    /// ⚠ A repository's MCP server is agreed to once the client knows the thread. Asked at <c>session/new</c>, the
    /// question named a session the client had never been told of: refused, and the server stayed off for the thread's
    /// whole life. It is asked at the first prompt instead, inside the turn.
    /// </summary>
    [Fact]
    public async Task ARepositorysMcpServer_IsAskedAboutAtTheFirstPrompt_NotBeforeTheClientKnowsTheThread()
    {
        var name = "repo" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(Path.Combine(_root, ".mcp.json"),
                          "{\"mcpServers\":{\"" + name + "\":{\"command\":\"inferpal-no-such-program\"}}}");
        // The settings file says the same (each turn follows it: config/reload), MCP on.
        static InferpalConfig Settings() => new() { RagEnabled = false, McpEnabled = true, DefaultModel = "test-model" };
        await using var h = await StartAsync(host: p => new HostServer(_ => p, Settings, fileSettings: Settings));
        h.Client.Answer = (method, _) => method == "session/request_permission"
            ? new { outcome = new { outcome = "selected", optionId = "reject_once" } }
            : null;
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        // The start's refresh has decided about the server (asked, or held back) — and no question reached the client.
        for (var i = 0; i < 300 && !Services.Diagnostics.Snapshot().Any(e => e.Detail.Contains($"'{name}'", StringComparison.Ordinal)); i++)
            await Task.Delay(100);
        Assert.Contains(Services.Diagnostics.Snapshot(), e => e.Detail.Contains($"'{name}'", StringComparison.Ordinal));   // WITNESS
        Assert.DoesNotContain(h.Client.Requests, r => r.GetProperty("method").GetString() == "session/request_permission");

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "hello")));
        var asked = Assert.Single(h.Client.Requests, r => r.GetProperty("method").GetString() == "session/request_permission");
        Assert.Equal(sid, asked.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Contains(name, asked.GetProperty("params").GetProperty("toolCall").GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ In a batch of reads run in parallel, a call refused before any tool (its arguments cut mid-call) is drawn, and
    /// each call that ran is drawn once. The loop's report of a call was paired with the calls drawn by counting: the
    /// refusal, reported between two calls that ran, was taken for an echo and vanished, and the last call was drawn
    /// twice.
    /// </summary>
    [Fact]
    public async Task InAParallelBatch_ARefusedCallIsDrawn_AndEachCallThatRanOnce()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(_root, "c.txt"), "gamma\n");
        await using var h = await StartAsync(fake: f => f.OnAgentRun = async (history, tools, reported, ct) =>
        {
            // The basic loop's parallel batch: every call through the funnel at once, then each reported, in order.
            ToolCallFunction Read(string path) =>
                new("read_file", JsonDocument.Parse(JsonSerializer.Serialize(new { path })).RootElement.Clone());
            ToolCallFunction[] calls = [Read("a.txt"), Services.Agent.ToolCallArguments.Parse("read_file", "{\"path\": \"b.t"), Read("c.txt")];
            var results = await Task.WhenAll(calls.Select(c => Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(tools, c, false, ct)));
            for (var i = 0; i < calls.Length; i++)
                reported?.Invoke(new ToolExecution(calls[i].Name, calls[i].WrittenArguments, results[i]));
            return new AgentResult("Read.", [], history);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "read them")));

        var drawn = h.Client.Updates(sid, "tool_call").ToList();
        Assert.Equal(3, drawn.Count);
        var refused = Assert.Single(drawn, d => d.GetRawText().Contains("NOT executed", StringComparison.Ordinal));
        Assert.Equal("completed", refused.GetProperty("status").GetString());
        // The two that ran: drawn when they started, ended once each.
        Assert.Equal(2, drawn.Count(d => d.GetProperty("status").GetString() == "in_progress"));
        Assert.Equal(2, h.Client.Updates(sid, "tool_call_update").Count());
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
        // The card showed the change before the question, then ends failed.
        Assert.Equal("failed", h.Client.Updates(sid, "tool_call_update").Last().GetProperty("status").GetString());
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

    /// <summary>⚠ <c>/test</c> reads the editor's active document: without an editor it could only answer "open a file in the
    /// editor", a remedy an ACP client cannot follow.</summary>
    [Theory]
    [InlineData("/explain")]
    [InlineData("/test")]
    public async Task ACommandOnlyAnEditorServes_IsSaid_AndNeverSentToTheModel(string command)
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, command)));

        Assert.Contains(Strings.AcpCommandNeedsEditor(command), h.Client.Said(sid));
        Assert.DoesNotContain(Strings.SlashNoActiveDocument, h.Client.Said(sid));
        Assert.Empty(h.Fake.ChatModels);
    }

    [Fact]
    public async Task ACommand_IsTheCommand_WithTheFileTheClientAttachesToEveryMessage()
    {
        var file = Path.Combine(_root, "current.cs");
        File.WriteAllText(file, "class Current { }\n");
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        // What Qt Creator sends: the typed text, then the current file.
        var r = await h.Client.RequestAsync("session/prompt", new
        {
            sessionId = sid,
            prompt = new object[]
            {
                new { type = "text", text = "/review" },
                new { type = "resource_link", uri = new Uri(file).AbsoluteUri, name = "current.cs" },
            },
        });

        Assert.Equal("end_turn", StopReason(r));
        Assert.Contains(Strings.AcpCommandNeedsEditor("/review"), h.Client.Said(sid));
        Assert.Empty(h.Fake.ChatModels);
    }

    /// <summary>The host renders <c>/xray</c> as text: an ACP thread shows it, with no panel to open.</summary>
    [Fact]
    public async Task Xray_IsShownAsText_InAThread()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/xray")));

        var said = h.Client.Said(sid);
        Assert.Contains(Strings.XrayHeader("\0").Split('\0')[0], said, StringComparison.Ordinal);   // the breakdown's heading
        Assert.DoesNotContain(Strings.AcpCommandNeedsEditor("/xray"), said);
        Assert.Empty(h.Fake.ChatModels);
    }

    /// <summary><c>/export</c> in a thread: the conversation as the thread showed it, rendered as the editors' export is,
    /// in a block to copy — the client has no file to save it into.</summary>
    [Fact]
    public async Task Export_GivesTheConversation_ToCopy()
    {
        await using var h = await StartAsync(fake: f => f.ChatResult = new ChatTurnResult("EXPORTED-ANSWER", null, 0, 0));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        await PromptAsync(h, sid, "EXPORTED-QUESTION");

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/export")));

        var said = h.Client.Said(sid);
        var block = said[said.LastIndexOf(Strings.AcpCopyThis, StringComparison.Ordinal)..];
        Assert.Contains("EXPORTED-QUESTION", block, StringComparison.Ordinal);
        Assert.Contains("EXPORTED-ANSWER", block, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.AcpCommandNeedsEditor("/export"), said);

        // A second export carries the conversation once, never the first export with it.
        await PromptAsync(h, sid, "/export");
        var second = h.Client.Said(sid)[said.Length..];
        Assert.Contains(Strings.AcpCopyThis, second);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "EXPORTED-ANSWER"));
    }

    /// <summary>A thread with nothing said yet has nothing to export — said, never an empty document.</summary>
    [Fact]
    public async Task Export_OfAnEmptyThread_SaysThereIsNothing()
    {
        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "/export")));

        Assert.Contains(Strings.ExportNoMessages, h.Client.Said(sid));
        Assert.DoesNotContain(Strings.AcpCopyThis, h.Client.Said(sid));
    }

    /// <summary>
    /// A question that starts with a path (or a comment) is a question: read as a command it was answered "unknown
    /// command" and never reached the model, the attached file dropped with it. Reference arm: the test above.
    /// </summary>
    [Fact]
    public async Task AQuestionThatStartsWithAPath_GoesToTheModel_WithWhatTheClientAttached()
    {
        var file = Path.Combine(_root, "main.py");
        File.WriteAllText(file, "print('PATH-QUESTION-BODY')\n");
        var asked = new List<string>();
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, history, _, _) =>
        {
            lock (asked) asked.Add(string.Join("\n", history.Where(m => m.Role == "user").Select(m => m.Content)));
            return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var r = await h.Client.RequestAsync("session/prompt", new
        {
            sessionId = sid,
            prompt = new object[]
            {
                new { type = "text", text = "/home/me/app/main.py crashes on line 12, why?" },
                new { type = "resource_link", uri = new Uri(file).AbsoluteUri, name = "main.py" },
            },
        });

        Assert.Equal("end_turn", StopReason(r));
        lock (asked)
        {
            Assert.Contains(asked, a => a.Contains("crashes on line 12", StringComparison.Ordinal)
                                     && a.Contains("PATH-QUESTION-BODY", StringComparison.Ordinal));
        }
        Assert.True(Inferpal.Services.SlashCommandRouter.LooksLikeCommand("/xray"));
        Assert.False(Inferpal.Services.SlashCommandRouter.LooksLikeCommand("// why does this loop?"));
    }

    [Fact]
    public async Task ACommandThatBecomesAPrompt_KeepsWhatTheClientAttached()
    {
        var file = Path.Combine(_root, "notes.cs");
        File.WriteAllText(file, "class Notes { /* ATTACHED-BODY */ }\n");
        // A command the repository wrote: the host expands it into a prompt.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".claude", "commands"));
        File.WriteAllText(Path.Combine(_root, ".claude", "commands", "standup.md"),
            "---\ndescription: Summarize\n---\nSummarize $ARGUMENTS for me.");
        var asked = new List<string>();
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, history, _, _) =>
        {
            lock (asked) asked.Add(string.Join("\n", history.Where(m => m.Role == "user").Select(m => m.Content)));
            return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        var r = await h.Client.RequestAsync("session/prompt", new
        {
            sessionId = sid,
            prompt = new object[]
            {
                new { type = "text", text = "/standup today" },
                new { type = "resource_link", uri = new Uri(file).AbsoluteUri, name = "notes.cs" },
            },
        });

        Assert.Equal("end_turn", StopReason(r));
        lock (asked)
        {
            Assert.True(asked.Any(q => q.Contains("Summarize today for me.", StringComparison.Ordinal)), "said: " + h.Client.Said(sid));   // WITNESS: expanded
            Assert.Contains(asked, q => q.Contains("ATTACHED-BODY", StringComparison.Ordinal));
        }
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
        Assert.Contains("xray", names);      // rendered as text in the thread
        Assert.Contains("export", names);
        Assert.DoesNotContain("explain", names);
        Assert.DoesNotContain("test", names);
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

    /// <summary>
    /// In Agent mode, an answer given before the tool the plan asked for is not the answer (the host sends the model back
    /// to its plan, and keeps only what follows): shown as the run's thinking, the answer itself is said once — on screen
    /// and in the saved session.
    /// </summary>
    [Fact]
    public async Task InAgentMode_AnAnswerGivenTooEarly_IsShownAsThinking_AndTheAnswerOnce()
    {
        var file = Path.Combine(_root, "orders.cs");
        File.WriteAllText(file, "class OrderService { }\n");
        const string Answer = "OrderService applies pricing rules to customer orders.";
        var call = 0;
        await using var h = await StartAsync(fake: f => f.OnChatRequest = (_, _, _, onToken) =>
        {
            var n = Interlocked.Increment(ref call);
            ChatTurnResult Said(string text) { onToken?.Invoke(text); return new ChatTurnResult(text, null, 0, 0); }
            return Task.FromResult(n switch
            {
                1 => new ChatTurnResult("""{"goal":"explain","steps":[{"i":1,"desc":"read the file","tool":"read_file"}]}""", null, 0, 0),
                2 => Said(Answer),   // the answer, before the tool the plan asked for: sent back to the plan
                3 => new ChatTurnResult(string.Empty, [new ToolCallDto(new ToolCallFunction("read_file",
                         JsonSerializer.SerializeToElement(new { path = file })))], 0, 0),
                _ => Said(Answer),
            });
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "agent" });

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "what does OrderService do?")));

        Assert.True(call >= 4, $"only {call} model calls: the early answer was not sent back to the plan");   // witness
        var said = h.Client.Said(sid);
        Assert.Equal(1, said.Split(Answer).Length - 1);
        var thought = string.Concat(h.Client.Updates(sid, "agent_thought_chunk")
                                         .Select(u => u.GetProperty("content").GetProperty("text").GetString()));
        Assert.Contains(Answer, thought, StringComparison.Ordinal);

        // Reloaded, the thread shows the answer once too.
        var saved = await new Services.Persistence.ConversationStore().LoadAsync(sid, CancellationToken.None);
        Assert.Equal(1, saved!.Messages.Count(m => m.Role == "assistant" && m.Content.Contains(Answer, StringComparison.Ordinal)));
        Assert.DoesNotContain(saved.Messages, m => m.Content.Split(Answer).Length > 2);
    }

    /// <summary>
    /// A client draws thoughts as they come and never updates them: the model's wait is said once when it shows and
    /// once more when it runs longer than usual — with its bound, never a time that would stay frozen — and a new wait
    /// in the same turn is said again.
    /// </summary>
    [Fact]
    public async Task TheModelsWait_IsAThought_SaidWhenItShowsAndWhenItRunsLonger()
    {
        var reading = new ModelWait(18_400, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(10));
        var later   = reading with { Elapsed = TimeSpan.FromSeconds(20) };
        var longer  = reading with { Elapsed = TimeSpan.FromSeconds(55) };
        await using var h = await StartAsync(fake: f => f.DriveWaiting = waiting =>
        {
            waiting(reading); waiting(later); waiting(longer); waiting(longer with { Elapsed = TimeSpan.FromSeconds(70) });
            waiting(null);
            waiting(reading);   // the next request of the turn waits too
            waiting(null);
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "what does OrderService do?")));

        var thoughts = h.Client.Updates(sid, "agent_thought_chunk")
                               .Select(u => u.GetProperty("content").GetProperty("text").GetString()!).ToList();
        Assert.Equal(3, thoughts.Count);
        Assert.Contains(reading.Title, thoughts[0], StringComparison.Ordinal);
        Assert.Contains(reading.DetailWithoutStop, thoughts[0], StringComparison.Ordinal);   // the bound, from the start
        Assert.Contains(longer.Title, thoughts[1], StringComparison.Ordinal);
        // The line stays once the wait is over: no "Stop ends it now", no time — they would go stale.
        Assert.Contains(longer.DetailWithoutStop, thoughts[1], StringComparison.Ordinal);
        Assert.DoesNotContain(longer.Detail, thoughts[1], StringComparison.Ordinal);
        Assert.Contains(reading.Title, thoughts[2], StringComparison.Ordinal);
        foreach (var times in new[] { reading.Times, later.Times, longer.Times })
            Assert.All(thoughts, t => Assert.DoesNotContain(times, t, StringComparison.Ordinal));
    }

    /// <summary>
    /// A reasoning model's last words before a tool call are still held back (sent in batches) when the next request
    /// starts waiting: they go out before the wait's line, never after it — or the line lands inside the sentence.
    /// </summary>
    [Fact]
    public async Task TheReasoningHeldBack_GoesOutBeforeTheWaitsLine()
    {
        var reading = new ModelWait(18_400, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(40), TimeSpan.FromMinutes(10));
        await using var h = await StartAsync(fake: f =>
        {
            f.OnChatRequest = (_, _, _, _) => Task.FromResult(new ChatTurnResult("The answer.", null, 0, 0));
            f.DriveWaiting = waiting => { waiting(reading); waiting(null); };
            // Two pieces within the batch's interval: the second is held back when the wait starts.
            f.DriveThinking = onThinking => { onThinking("so I should read Order"); onThinking("Service.cs first."); };
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "what does OrderService do?")));

        var thought = string.Concat(h.Client.Updates(sid, "agent_thought_chunk")
                                            .Select(u => u.GetProperty("content").GetProperty("text").GetString()));
        Assert.Contains("so I should read OrderService.cs first.", thought, StringComparison.Ordinal);
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

    /// <summary>
    /// ⚠ A thread's <c>/model</c> is the thread's, as its selector is, and refused as the selector refuses: through the
    /// host it saved the default model of every editor, and an embedding model typed by hand was taken.
    /// </summary>
    [Fact]
    public async Task AThreadsModelCommand_ChangesThatThreadOnly_AndRefusesWhatTheSelectorRefuses()
    {
        // The settings file every editor reads: the thread's pick is held in memory (models/useForSession), never saved.
        var settings = Path.Combine(_root, $"settings-{Guid.NewGuid():N}.json");
        await using var h = await StartAsync(
            fake: f => f.ModelNames = ["test-model", "other-model", "nomic-embed-text"],
            host: provider => new HostServer(_ => provider,
                () => new InferpalConfig { RagEnabled = false, DefaultModel = "test-model", SavePathForTests = settings }));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        await PromptAsync(h, sid, "/model other-model");
        Assert.Contains(Strings.SlashModelChanged("other-model"), h.Client.Said(sid), StringComparison.Ordinal);
        Assert.False(File.Exists(settings) && File.ReadAllText(settings).Contains("other-model", StringComparison.Ordinal),
                     "the thread's /model was saved as every editor's default model");

        await PromptAsync(h, sid, "/model nomic-embed-text");
        Assert.Contains(Strings.SlashModelIsEmbedding("nomic-embed-text"), h.Client.Said(sid), StringComparison.Ordinal);
        await PromptAsync(h, sid, "/model no-such-model");
        Assert.Contains(Strings.SlashModelNotServed("no-such-model"), h.Client.Said(sid), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ A load that throws — a damaged session file — leaves no half-open session: kept registered and empty, the next
    /// <c>session/load</c> of the id answered with success on an empty thread, and its next prompt saved over the id.
    /// </summary>
    [Fact]
    public async Task ADamagedSession_IsNotLeftHalfOpen_WhenItsLoadFails()
    {
        var sid = "acp-damaged-" + Guid.NewGuid().ToString("N");
        var dir = Services.Persistence.ConversationStore.OverrideDirForTests!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, sid + ".json"), "{ this is not a session");

        await using var h = await StartAsync();
        await h.Client.RequestAsync("initialize", Init());
        var first = await h.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(first.TryGetProperty("error", out _), first.GetRawText());   // WITNESS: the damaged file fails its load

        // Asked again, it fails again: nothing of the first attempt answers in its place.
        var second = await h.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() });
        Assert.True(second.TryGetProperty("error", out _), second.GetRawText());
        var mode = await h.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "plan" });
        Assert.True(mode.TryGetProperty("error", out _), "a session whose load failed is still served: " + mode.GetRawText());
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

    /// <summary>
    /// A mode the repository defines is one more session mode, beside Inferpal's three: picked, its instructions go with
    /// the question, and the model selector names the model it answers with.
    /// </summary>
    [Fact]
    public async Task ARepositorysMode_IsASessionMode_AndTheSelectorNamesItsModel()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal", "modes"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "modes", "review.md"),
            "---\nname: Review\ndescription: Reads the change\nmodel: [gpt-5, other-model]\ntools: [read]\n---\nREVIEW-INSTRUCTIONS\n");
        await using var h = await StartAsync(fake: f => f.ModelNames = ["test-model", "other-model"]);
        await h.Client.RequestAsync("initialize", Init());
        var created = (await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() })).GetProperty("result");
        var sid = created.GetProperty("sessionId").GetString();
        var modes = created.GetProperty("modes").GetProperty("availableModes").EnumerateArray().ToList();
        Assert.Contains(modes, m => m.GetProperty("id").GetString() == "mode:Review"
                                    && m.GetProperty("description").GetString()!.Contains(".inferpal/modes/review.md", StringComparison.Ordinal));
        Assert.Equal(4, modes.Count);

        var set = await h.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "mode:Review" });
        Assert.False(set.TryGetProperty("error", out _), set.GetRawText());
        var options = (await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "mode", value = "mode:Review" }))
            .GetProperty("result").GetProperty("configOptions");
        Assert.Equal("other-model", options.EnumerateArray().Single(o => o.GetProperty("id").GetString() == "model")
                                           .GetProperty("currentValue").GetString());

        Assert.Equal("end_turn", StopReason(await PromptAsync(h, sid, "review it")));

        var (model, history) = h.Fake.AgentRuns[^1];
        Assert.Equal("other-model", model);
        Assert.Contains("REVIEW-INSTRUCTIONS", history[0].Content, StringComparison.Ordinal);
    }

    /// <summary>A thread held in a mode reopens in it — and when the mode's file is gone since, it still opens, in Default,
    /// the cause said; never a thread that cannot be loaded.</summary>
    [Fact]
    public async Task AThreadHeldInAModeWhoseFileIsGone_StillOpens_InDefault()
    {
        var file = Path.Combine(_root, ".inferpal", "modes", "review.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "---\nname: Review\ntools: [read]\n---\nREVIEW-INSTRUCTIONS\n");
        string sid;
        await using (var first = await StartAsync(fake: f => f.ChatResult = new ChatTurnResult("Saved answer.", null, 0, 0)))
        {
            await first.Client.RequestAsync("initialize", Init());
            sid = await NewSessionAsync(first);
            await first.Client.RequestAsync("session/set_mode", new { sessionId = sid, modeId = "mode:Review" });
            await PromptAsync(first, sid, "remember this question");
        }

        await using (var kept = await StartAsync())
        {
            await kept.Client.RequestAsync("initialize", Init());
            var reopened = (await kept.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() }))
                .GetProperty("result");
            Assert.Equal("mode:Review", reopened.GetProperty("modes").GetProperty("currentModeId").GetString());   // reference arm
        }

        File.Delete(file);
        await using var second = await StartAsync();
        await second.Client.RequestAsync("initialize", Init());
        var loaded = await second.Client.RequestAsync("session/load", new { sessionId = sid, cwd = _root, mcpServers = Array.Empty<object>() });

        Assert.True(loaded.TryGetProperty("result", out var result), loaded.GetRawText());
        Assert.Equal("default", result.GetProperty("modes").GetProperty("currentModeId").GetString());
        // Said in the thread, in the person's language, as a notice or a line.
        var said = string.Concat(second.Client.Lines.Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.TryGetProperty("params", out var p) && p.TryGetProperty("update", out _))
            .Select(e => e.GetProperty("params").GetProperty("update"))
            .Select(u => (u.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var t) ? t.GetString() : null)
                         ?? (u.TryGetProperty("title", out var title) ? title.GetString() : null)));
        Assert.Contains(Localization.Strings.ModeGone("Review").TrimEnd('.'), said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The thread's model selector names the model that answers: in Agent mode, the one set for the agent's role — the
    /// host routes the turn to it —, and a model picked in the selector answers in every mode, Agent included.
    /// </summary>
    [Fact]
    public async Task TheModelSelector_NamesTheModelThatAnswers_InAgentModeToo()
    {
        static string ShownModel(JsonElement options) =>
            options.EnumerateArray().Single(o => o.GetProperty("id").GetString() == "model").GetProperty("currentValue").GetString()!;
        // The same settings in the file: each question re-reads it (config/reload), and the suite's file has neither model.
        static InferpalConfig Settings() => new() { RagEnabled = false, DefaultModel = "chat-model", AgentModel = "agent-model" };
        await using var h = await StartAsync(
            fake: p => p.ModelNames = ["chat-model", "agent-model", "other-model"],
            host: p => new HostServer(_ => p, Settings, fileSettings: Settings));
        await h.Client.RequestAsync("initialize", Init());
        var created = await h.Client.RequestAsync("session/new", new { cwd = _root, mcpServers = Array.Empty<object>() });
        var sid = created.GetProperty("result").GetProperty("sessionId").GetString()!;
        Assert.Equal("chat-model", ShownModel(created.GetProperty("result").GetProperty("configOptions")));

        var agent = await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "mode", value = "agent" });
        Assert.Equal("agent-model", ShownModel(agent.GetProperty("result").GetProperty("configOptions")));
        await PromptAsync(h, sid, "first");
        Assert.Equal("agent-model", h.Fake.ChatModels.Last());   // witness: the host routes Agent mode to the role's model

        // Picked in the selector, a model answers in Agent mode too.
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "model", value = "other-model" });
        await PromptAsync(h, sid, "second");
        Assert.Equal("other-model", h.Fake.ChatModels.Last());

        // A mode set the older way carries no options back: the selector is told at the next turn.
        var second = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_mode", new { sessionId = second, modeId = "agent" });
        await PromptAsync(h, second, "third");
        Assert.Equal("agent-model", ShownModel(h.Client.Updates(second, "config_option_update").Last().GetProperty("configOptions")));
    }

    /// <summary>
    /// A settings file that cannot be read changes nothing in an open thread, and the thread says so once while it
    /// lasts — the user who just edited it by hand would otherwise see their change do nothing, without a word.
    /// </summary>
    [Fact]
    public async Task AnUnreadableSettingsFile_IsSaidOnce_WhileItLasts()
    {
        var file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-a" };
        var broken = true;
        await using var h = await StartAsync(
            fake: p => p.ModelNames = ["model-a"],
            host: p => new HostServer(_ => p, () => new InferpalConfig { RagEnabled = false, DefaultModel = "model-a" },
                                      fileSettings: () => broken ? null : file));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        int Said() => h.Client.Said(sid).Split(Strings.AcpSettingsUnreadable).Length - 1;

        await PromptAsync(h, sid, "first");
        await PromptAsync(h, sid, "second");
        Assert.Equal(1, Said());
        Assert.Equal("model-a", h.Fake.ChatModels.Last());

        // Readable again, then broken again: said again.
        broken = false;
        await PromptAsync(h, sid, "third");
        broken = true;
        await PromptAsync(h, sid, "fourth");
        Assert.Equal(2, Said());
    }

    /// <summary>
    /// A command that saves the agent's model (<c>/onboard apply</c>) is followed by the thread's selector: config/reload
    /// never reports what the thread wrote itself, and the selector kept naming the old model while the new one answered.
    /// </summary>
    [Fact]
    public async Task AModelSavedByACommand_IsTheOneTheSelectorNames()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, ".inferpal", "project.json"), """{ "recommend": { "agentModel": "big-agent" } }""");
        var config = new InferpalConfig { RagEnabled = false, DefaultModel = "chat-model" };
        // The file as the session's own copy: nothing to follow from elsewhere, only what the thread writes.
        InferpalConfig File_() => JsonSerializer.Deserialize<InferpalConfig>(JsonSerializer.Serialize(config))!;
        await using var h = await StartAsync(
            fake: p => p.ModelNames = ["chat-model", "big-agent"],
            host: p => new HostServer(_ => p, () => config, fileSettings: File_));
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = sid, configId = "mode", value = "agent" });

        await PromptAsync(h, sid, "/onboard apply");
        Assert.Equal("big-agent", config.AgentModel);   // WITNESS: the command saved it
        await PromptAsync(h, sid, "go");

        Assert.Equal("big-agent", h.Fake.ChatModels.Last());
        var shown = h.Client.Updates(sid, "config_option_update").Last().GetProperty("configOptions")
                     .EnumerateArray().Single(o => o.GetProperty("id").GetString() == "model").GetProperty("currentValue").GetString();
        Assert.Equal("big-agent", shown);
    }

    /// <summary>
    /// A file attached to a prompt is cut for the window of the model that reads it — the agent's in Agent mode, the one
    /// picked in the thread when there is one, else the chat's —, never for the code-actions model.
    /// </summary>
    [Fact]
    public async Task AnAttachedFile_TheWorkspaceExcludes_IsNamed_NeverSent()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), "secret.cs\n");
        var secret = Path.Combine(_root, "secret.cs");
        var open   = Path.Combine(_root, "open.cs");
        File.WriteAllText(secret, "class Secret { const string K = \"SECRET_51c0\"; }\n");
        File.WriteAllText(open, "class Open { const string K = \"OPEN_51c0\"; }\n");
        var sent = new List<string>();
        await using var h = await StartAsync(fake: p => p.OnChatRequest = (_, history, _, _) =>
        {
            lock (sent) sent.AddRange(history.Select(m => m.Content ?? string.Empty));
            return Task.FromResult(new ChatTurnResult("ok", null, 0, 0));
        });
        await h.Client.RequestAsync("initialize", Init());
        var sid = await NewSessionAsync(h);

        await h.Client.RequestAsync("session/prompt", new
        {
            sessionId = sid,
            prompt = new object[]
            {
                new { type = "text", text = "look at these" },
                new { type = "resource_link", uri = new Uri(secret).AbsoluteUri, name = "secret.cs" },
                new { type = "resource_link", uri = new Uri(open).AbsoluteUri, name = "open.cs" },
            },
        });

        string all;
        lock (sent) all = string.Join("\n", sent);
        Assert.Contains("OPEN_51c0", all);   // WITNESS (reference arm): an attachment nothing excludes is sent
        Assert.DoesNotContain("SECRET_51c0", all);
        Assert.Contains("excluded from AI tools by .aiignore, line 1", all);
    }

    [Fact]
    public async Task AnAttachedFile_IsSizedForTheModelThatAnswers()
    {
        var file = Path.Combine(_root, "orders.cs");
        File.WriteAllText(file, "class Orders { }\n");
        static InferpalConfig Settings() => new()
        {
            RagEnabled = false, DefaultModel = "chat-model", AgentModel = "agent-model", CodeActionsModel = "code-model",
        };
        await using var h = await StartAsync(
            fake: p => p.ModelNames = ["chat-model", "agent-model", "code-model", "other-model"],
            host: p => new HostServer(_ => p, Settings, fileSettings: Settings));
        await h.Client.RequestAsync("initialize", Init());
        async Task<List<string>> WindowsAskedFor(string sid)
        {
            h.Fake.LoadedContextQueries.Clear();
            await h.Client.RequestAsync("session/prompt", new
            {
                sessionId = sid,
                prompt = new object[]
                {
                    new { type = "text", text = "look at this" },
                    new { type = "resource_link", uri = new Uri(file).AbsoluteUri, name = "orders.cs" },
                },
            });
            return [.. h.Fake.LoadedContextQueries];
        }

        var chat = await NewSessionAsync(h);
        var asked = await WindowsAskedFor(chat);
        Assert.Contains("chat-model", asked);   // WITNESS: the windows were looked up
        Assert.DoesNotContain("code-model", asked);

        var agent = await NewSessionAsync(h);
        await h.Client.RequestAsync("session/set_config_option", new { sessionId = agent, configId = "mode", value = "agent" });
        asked = await WindowsAskedFor(agent);
        Assert.Contains("agent-model", asked);
        Assert.DoesNotContain("code-model", asked);

        await h.Client.RequestAsync("session/set_config_option", new { sessionId = agent, configId = "model", value = "other-model" });
        Assert.Equal(["other-model"], (await WindowsAskedFor(agent)).Distinct());
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

        // A new address of the same kind of server is taken at once — the client reads it live — with nothing to say.
        // Only a new KIND waits for a new thread, said once (FollowSavedSettingsTests, on real clients: this fake has
        // no kind).
        file = new InferpalConfig { RagEnabled = false, DefaultModel = "model-c", BaseUrl = "http://elsewhere:1234" };
        await PromptAsync(h, sid, "fourth");
        Assert.DoesNotContain(Strings.AcpServerChangedNewThread, h.Client.Said(sid), StringComparison.Ordinal);
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
                var page = await http.GetStringAsync(url);
                Assert.Contains("settings-standalone.js", page, StringComparison.Ordinal);
                // Stopped, the page names the way back from where it was opened: /settings, not a terminal command.
                Assert.Contains(System.Text.Json.JsonSerializer.Serialize(Strings.SettingsWindowGoneInThread), page, StringComparison.Ordinal);
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
