using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// Covers LM Studio's native-payload parsing that the OpenAI-compatible base can't do — specifically
// extracting the *loaded* context window (n_ctx the running instance was loaded with), which feeds the
// proactive context-fit guard. The model's max_context_length is its capability, not the loaded n_ctx,
// so a model can be loaded well below what it supports (the root of the LM Studio context-overflow bug).
// [Collection]: the empty turn records its finding in the STATIC Diagnostics ring, like
// DiagnosticsTests — the two classes must therefore be serialized against each other.
[Collection("Diagnostics")]
public class LmStudioClientTests
{
    private static List<JsonElement> Instances(string json)
        => JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    [Fact]
    public void LoadedContextFromInstances_ReadsNestedConfigContextLength()
    {
        // v1 nests the loaded n_ctx under loaded_instances[].config.context_length.
        var instances = Instances("""
            [{ "instance_id": "qwen/qwen3-27b", "config": { "context_length": 8192, "flash_attention": true } }]
            """);
        Assert.Equal(8192, LmStudioClient.LoadedContextFromInstances(instances));
    }

    [Fact]
    public void LoadedContextFromInstances_ToleratesFlatShapes()
    {
        Assert.Equal(16384, LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "context_length": 16384 }]""")));
        Assert.Equal(4096, LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "loaded_context_length": 4096 }]""")));
    }

    [Fact]
    public void LoadedContextFromInstances_NoneOrEmpty_ReturnsNull()
    {
        Assert.Null(LmStudioClient.LoadedContextFromInstances(null));
        Assert.Null(LmStudioClient.LoadedContextFromInstances([]));
        // An instance with no context field at all → unknown, not zero (must not block requests).
        Assert.Null(LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "instance_id": "x", "config": { "flash_attention": true } }]""")));
        // A zero/garbage value is rejected (treated as unknown).
        Assert.Null(LmStudioClient.LoadedContextFromInstances(
            Instances("""[{ "context_length": 0 }]""")));
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────
    //  The connection badge and the model list do NOT query the same surface.
    //
    //  `CheckConnectionAsync` is inherited from OpenAiCompatibleClient and probes
    //  {base}/v1/models — the surface the chat actually talks. The listing comes from the native
    //  API {base}/api/v1|v0/models, the only one carrying loaded state and size. A server that
    //  serves only the OpenAI-compatible surface — a reverse proxy routing just /v1, the most
    //  common shape of an LM Studio exposed on a domain — therefore answered "connected" with
    //  ZERO models; and on the UI side an empty list does not read as a failure: the picker puts
    //  the configured model back, which looks exactly like a backend serving a single model.
    //
    //  ⚠ The stand-in server is a TcpListener, not an HttpListener: on Windows an HttpListener
    //  prefix needs a URL reservation, so a test passing here could refuse to start on a runner.
    //  A raw socket needs nothing.
    // ──────────────────────────────────────────────────────────────────────────────────────────

    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _paths = [];

        /// <summary>Paths actually probed — the witness that the stand-in was really called.</summary>
        public IReadOnlyList<string> Paths { get { lock (_paths) return _paths.ToList(); } }

        public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        public LoopbackServer(Func<string, string?> body)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            var stream = client.GetStream();
                            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                            var line = await reader.ReadLineAsync();
                            var path = line?.Split(' ') is [_, var p, ..] ? p : string.Empty;
                            lock (_paths) _paths.Add(path);
                            while (true)
                            {
                                var header = await reader.ReadLineAsync();
                                if (header is null || header.Length == 0) break;
                            }
                            var payload = body(path);
                            var bytes = Encoding.UTF8.GetBytes(payload ?? "{}");
                            var status = payload is null ? "404 Not Found" : "200 OK";
                            // Explicit CRLF: the HTTP grammar requires it, Environment.NewLine is
                            // not CRLF everywhere, and this test also runs on a Linux runner.
                            const string crlf = "\r\n";
                            var head = Encoding.ASCII.GetBytes(
                                $"HTTP/1.1 {status}{crlf}"
                                + $"Content-Type: application/json{crlf}"
                                + $"Content-Length: {bytes.Length}{crlf}"
                                + $"Connection: close{crlf}{crlf}");
                            await stream.WriteAsync(head);
                            await stream.WriteAsync(bytes);
                            await stream.FlushAsync();
                        }
                    });
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    private const string OpenAiPayload =
        """{"object":"list","data":[{"id":"servi-par-v1","object":"model"}]}""";

    private const string NativePayload =
        """{"models":[{"key":"servi-par-api-native","max_context_length":8192,"size_bytes":42}]}""";

    [Fact]
    public async Task ListModels_FallsBackToTheOpenAiSurface_WhenTheNativeApiServesNothing()
    {
        using var server = new LoopbackServer(path => path == "/v1/models" ? OpenAiPayload : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        // The badge says "connected" — that half was never the problem.
        Assert.True(await client.CheckConnectionAsync(server.BaseUrl, CancellationToken.None));

        var models = await client.ListModelsAsync(CancellationToken.None);
        Assert.Equal(["servi-par-v1"], models);

        // The installed list follows the same fallback: without it, VRAM estimation and /hardware
        // went silent on a reachable backend.
        Assert.Single(await client.ListInstalledModelsAsync(CancellationToken.None));

        // Witness: the probe did try the native surface first, otherwise the test measures nothing.
        Assert.Contains("/api/v1/models", server.Paths);
    }

    /// <summary>
    /// A turn that returns NOTHING leaves a trace of what the stream contained.
    /// </summary>
    /// <remarks>
    /// ⚠ Reported from a machine on the network: every message answered "the model returned no
    /// response", and the investigation stopped there — the accused server turned out perfectly
    /// healthy (listing, chat, streaming, 28 tools, forced tool_choice), and <b>nothing recorded
    /// what the stream contained</b>. It was the client's only failure leaving no trace at all:
    /// everything else throws an AgentHttpException that carries its cause to the screen.
    /// </remarks>
    [Fact]
    public async Task AnEmptyTurn_RecordsWhatTheStreamContained()
    {
        Diagnostics.Clear();

        // A perfectly valid SSE stream… and an empty one: the exact shape of the reported symptom.
        const string empty = """
            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;
        using var server = new LoopbackServer(path => path.StartsWith("/v1/chat/completions") ? empty : null);
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        var turn = await client.SendChatAsync(
            "my-model", [new ChatMessageDto("user", "hello")],
            EmptyToolRegistry.Instance, onToken: null, CancellationToken.None);

        Assert.Equal(string.Empty, turn.TextContent);

        // The trace names what was needed to diagnose: the requested model, the server, the shape
        // of the stream. Without it, all that is left is "no response".
        var entry = Assert.Single(Diagnostics.Snapshot());
        Assert.Contains("my-model",     entry.Context, StringComparison.Ordinal);
        Assert.Contains(server.BaseUrl, entry.Context, StringComparison.Ordinal);
        Assert.Contains("stop",         entry.Context, StringComparison.Ordinal);
    }

    /// <summary>
    /// The URL passed to <c>ListModelsAsync</c> holds for <b>both</b> surfaces, the native one
    /// included.
    /// </summary>
    /// <remarks>
    /// ⚠ The parameter was honoured by the two other providers and <b>silently ignored</b> here:
    /// the native probe queried the CONFIGURED server while the OpenAI-compatible fallback queried
    /// the one it was handed. No caller paid for it — the one that passes a URL rebuilds a client
    /// whose config already carries it — but the obvious gesture (passing the URL to the existing
    /// client) would have listed one server's models while presenting them as another's.
    /// </remarks>
    [Fact]
    public async Task ListModels_HonoursTheUrlOverride_OnTheNativeSurfaceToo()
    {
        using var configured = new LoopbackServer(path => path switch
        {
            "/api/v1/models" => """{"models":[{"key":"the-one-from-config"}]}""",
            "/v1/models"     => OpenAiPayload,
            _                => null,
        });
        using var typed = new LoopbackServer(path => path switch
        {
            "/api/v1/models" => NativePayload,
            "/v1/models"     => OpenAiPayload,
            _                => null,
        });

        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = configured.BaseUrl });

        Assert.Equal(["servi-par-api-native"],
                     await client.ListModelsAsync(CancellationToken.None, typed.BaseUrl));

        // Witness in both directions: the requested server was probed, the other was not touched.
        Assert.Contains("/api/v1/models", typed.Paths);
        Assert.Empty(configured.Paths);
    }

    [Fact]
    public async Task ListModels_PrefersTheNativeApi_WhenItAnswers()
    {
        // Negative control: the fallback must NEVER shadow the native surface, the only one
        // carrying loaded state and size — a real development server returns its native ids.
        using var server = new LoopbackServer(path => path switch
        {
            "/api/v1/models" => NativePayload,
            "/v1/models"     => OpenAiPayload,
            _                => null,
        });
        var client = new LmStudioClient(new InferpalConfig { Provider = "lmstudio", BaseUrl = server.BaseUrl });

        Assert.Equal(["servi-par-api-native"], await client.ListModelsAsync(CancellationToken.None));
        Assert.Equal(42, (await client.ListInstalledModelsAsync(CancellationToken.None)).Single().SizeBytes);
    }
}
