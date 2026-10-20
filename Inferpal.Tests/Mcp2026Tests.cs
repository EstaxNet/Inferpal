using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The MCP client against servers of the 2026-07-28 revision — no handshake, <c>_meta</c> on every request, request
/// headers, <c>x-mcp-header</c>, <c>input_required</c>, subscriptions — and its fallback to the handshake of the
/// earlier ones. Measured against real servers by <c>docs/probes/mcp-2026/</c>; these tests hold the rules.
/// </summary>
public class Mcp2026Tests
{
    // ── Era decision ─────────────────────────────────────────────────────────

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static McpRpcException Rpc(string error) => new(El(error));

    [Fact]
    public void ADiscoverResult_NamingThisRevision_IsTheModernEra_WithItsListChangedAnnouncement()
    {
        var d = McpModern.Classify(El("""{"resultType":"complete","supportedVersions":["2026-07-28","2025-11-25"],"capabilities":{"tools":{"listChanged":true}}}"""));
        Assert.Equal(McpEra.Modern, d.Era);
        Assert.Null(d.Failure);
        Assert.True(d.ToolsListChanged);

        Assert.False(McpModern.Classify(El("""{"supportedVersions":["2026-07-28"],"capabilities":{"tools":{}}}""")).ToolsListChanged);
    }

    [Fact]
    public void AnythingButADiscoverResult_IsTheEarlierEra_AndAVersionInCommonIsRequired()
    {
        // A server that only knows the earlier revisions and answered with an object, or lists only them.
        Assert.Equal(McpDiscovery.Legacy, McpModern.Classify(El("{}")));
        Assert.Equal(McpDiscovery.Legacy, McpModern.Classify(El("""{"supportedVersions":["2025-11-25"]}""")));
        // Only a revision Inferpal does not speak: named, never a handshake attempted blind.
        var future = McpModern.Classify(El("""{"supportedVersions":["2027-03-01"]}"""));
        Assert.Contains("2027-03-01", future.Failure);
    }

    [Fact]
    public void OnlyARecognisedModernRefusal_IsTheModernEra()
    {
        // -32022 listing earlier versions: the handshake of those. Listing none in common: named.
        Assert.Equal(McpDiscovery.Legacy, McpModern.FromError(Rpc("""{"code":-32022,"message":"Unsupported","data":{"supported":["2025-11-25"]}}""")));
        Assert.Contains("2027-01-01", McpModern.FromError(Rpc("""{"code":-32022,"message":"Unsupported","data":{"supported":["2027-01-01"]}}""")).Failure);
        // A header or capability refusal is a modern server refusing THIS request: no fallback.
        Assert.Contains("refused the discovery request", McpModern.FromError(Rpc("""{"code":-32020,"message":"Header mismatch"}""")).Failure);
        // ⚠ Never keyed to one code: -32601, a 2025 session error, no JSON-RPC error at all — the earlier era.
        Assert.Equal(McpDiscovery.Legacy, McpModern.FromError(Rpc("""{"code":-32601,"message":"Method not found"}""")));
        Assert.Equal(McpDiscovery.Legacy, McpModern.FromError(Rpc("""{"code":-32000,"message":"No valid session ID provided"}""")));
        Assert.Equal(McpDiscovery.Legacy, McpModern.FromError(new HttpRequestException("HTTP 404")));
    }

    [Fact]
    public void TheMeta_NamesTheVersion_TheClient_AndNoCapability()
    {
        var p = McpModern.WithMeta(new JsonObject { ["name"] = "x", ["_meta"] = new JsonObject { ["progressToken"] = 1 } });
        var meta = p["_meta"]!.AsObject();
        Assert.Equal("2026-07-28", (string?)meta[McpModern.VersionKey]);
        Assert.Equal("Inferpal", (string?)meta[McpModern.ClientInfoKey]!["name"]);
        Assert.Empty(meta[McpModern.CapabilitiesKey]!.AsObject());   // no elicitation, sampling or roots: none can be asked
        Assert.Equal(1, (int?)meta["progressToken"]);                 // what the caller put there stays
        Assert.Equal("x", (string?)p["name"]);
    }

    // ── x-mcp-header ─────────────────────────────────────────────────────────

    private static (IReadOnlyList<McpParamHeaders.Annotation> A, string? R) Headers(string schema) => McpParamHeaders.Read(El(schema));

    [Fact]
    public void AnAnnotationReachedThroughPropertiesAlone_IsRead_NestedOnesIncluded()
    {
        var (a, r) = Headers("""
            {"type":"object","properties":{
              "region":{"type":"string","x-mcp-header":"Region"},
              "opts":{"type":"object","properties":{"tenant":{"type":"integer","x-mcp-header":"Tenant"}}},
              "q":{"type":"string"}}}
            """);
        Assert.Null(r);
        Assert.Equal(["Region", "Tenant"], a.Select(x => x.Name));
        Assert.Equal(["opts", "tenant"], a[1].Path);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"n":{"type":"number","x-mcp-header":"N"}}}""", "'number'")]
    [InlineData("""{"type":"object","properties":{"l":{"type":"array","items":{"type":"string","x-mcp-header":"L"}}}}""", "'properties' alone")]
    [InlineData("""{"type":"object","properties":{"o":{"anyOf":[{"type":"string","x-mcp-header":"O"}]}}}""", "'properties' alone")]
    [InlineData("""{"type":"object","properties":{"e":{"type":"string","x-mcp-header":""}}}""", "empty")]
    [InlineData("""{"type":"object","properties":{"s":{"type":"string","x-mcp-header":"Bad Name"}}}""", "not an HTTP header name")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"string","x-mcp-header":"Dup"},"b":{"type":"string","x-mcp-header":"dup"}}}""", "used twice")]
    [InlineData("""{"type":"object","x-mcp-header":"Root"}""", "schema root")]
    public void AnAnnotationThatBreaksTheRules_RejectsTheTool_WithItsReason(string schema, string reason)
    {
        var (a, r) = Headers(schema);
        Assert.Empty(a);
        Assert.Contains(reason, r);
    }

    [Fact]
    public void AnAnnotationLookingKeyword_InsideData_IsNotAnAnnotation()
    {
        // Reference arm: a "x-mcp-header" key inside a default value, or a PARAMETER named so, is not an annotation.
        var (a, r) = Headers("""{"type":"object","properties":{"x-mcp-header":{"type":"string"},"c":{"type":"object","default":{"x-mcp-header":"no"}}}}""");
        Assert.Null(r);
        Assert.Empty(a);
    }

    [Theory]
    [InlineData("us-west1", "us-west1")]
    [InlineData("Hello, 世界", "=?base64?SGVsbG8sIOS4lueVjA==?=")]
    [InlineData(" padded ", "=?base64?IHBhZGRlZCA=?=")]
    [InlineData("line1\nline2", "=?base64?bGluZTEKbGluZTI=?=")]
    [InlineData("=?base64?literal?=", "=?base64?PT9iYXNlNjQ/bGl0ZXJhbD89?=")]
    public void AHeaderValue_IsEncodedAsTheSpecificationTableHasIt(string value, string encoded) =>
        Assert.Equal(encoded, McpParamHeaders.Encode(value));

    [Fact]
    public void AMirroredValue_IsTheArgumentAtItsPath_OmittedWhenAbsentOrNull()
    {
        var args = JsonNode.Parse("""{"region":"eu","n":42,"b":false,"z":null,"o":{"t":7}}""");
        Assert.Equal("eu", McpParamHeaders.ValueFor(args, ["region"]));
        Assert.Equal("42", McpParamHeaders.ValueFor(args, ["n"]));
        Assert.Equal("false", McpParamHeaders.ValueFor(args, ["b"]));
        Assert.Equal("7", McpParamHeaders.ValueFor(args, ["o", "t"]));
        Assert.Null(McpParamHeaders.ValueFor(args, ["z"]));
        Assert.Null(McpParamHeaders.ValueFor(args, ["missing"]));
    }

    // ── $ref ─────────────────────────────────────────────────────────────────

    /// <summary>What the Python SDK writes for <c>place(address: Address)</c> — measured on mcp 1.30 and 2.3.</summary>
    private const string PydanticSchema = """
        {"$defs":{"Address":{"properties":{"street":{"title":"Street","type":"string"},"city":{"title":"City","type":"string"}},
          "required":["street","city"],"title":"Address","type":"object"}},
         "properties":{"address":{"$ref":"#/$defs/Address","description":"Where"}},"required":["address"],"type":"object"}
        """;

    [Fact]
    public void ALocalRef_IsInlined_TheParameterTyped_TheDefinitionsDropped()
    {
        var inlined = McpSchemaRefs.Inline(El(PydanticSchema));
        var text = inlined.GetRawText();

        Assert.DoesNotContain("$ref", text);
        Assert.DoesNotContain("$defs", text);
        var address = inlined.GetProperty("properties").GetProperty("address");
        Assert.Equal("object", address.GetProperty("type").GetString());
        Assert.True(address.GetProperty("properties").TryGetProperty("street", out _));
        Assert.Equal("Where", address.GetProperty("description").GetString());   // a keyword beside $ref is kept
    }

    [Fact]
    public void ARecursiveRef_StaysWhereItRecurs_AndItsDefinitionWithIt()
    {
        var inlined = McpSchemaRefs.Inline(El("""
            {"$defs":{"Node":{"type":"object","properties":{"child":{"$ref":"#/$defs/Node"}}}},
             "type":"object","properties":{"root":{"$ref":"#/$defs/Node"}}}
            """));
        var root = inlined.GetProperty("properties").GetProperty("root");
        Assert.Equal("object", root.GetProperty("type").GetString());
        Assert.Equal("#/$defs/Node", root.GetProperty("properties").GetProperty("child").GetProperty("$ref").GetString());
        Assert.True(inlined.TryGetProperty("$defs", out _));   // still pointed at: kept
    }

    [Fact]
    public void AnExternalRef_AndARefInsideData_AreLeftAsWritten()
    {
        // Never fetched: a schema is the server's text, not an address to follow. And a default value is data.
        var inlined = McpSchemaRefs.Inline(El("""
            {"type":"object","properties":{"x":{"$ref":"https://example.com/s.json"},"y":{"type":"object","default":{"$ref":"#/nope"}}}}
            """)).GetProperty("properties");
        Assert.Equal("https://example.com/s.json", inlined.GetProperty("x").GetProperty("$ref").GetString());
        Assert.Equal("#/nope", inlined.GetProperty("y").GetProperty("default").GetProperty("$ref").GetString());
    }

    [Fact]
    public void ASchemaWithoutRef_IsReturnedUntouched()
    {
        var schema = El("""{"type":"object","properties":{"a":{"type":"string"}}}""");
        Assert.Equal(schema.GetRawText(), McpSchemaRefs.Inline(schema).GetRawText());
    }

    // ── HTTP: a modern server ────────────────────────────────────────────────

    private sealed record Seen(string HttpMethod, string? Method, JsonNode? Body, HttpRequestHeaders Headers);

    private sealed class ModernServer : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];
        public bool ListChanged { get; init; }
        public string Tools { get; init; } = """[{"name":"echo","inputSchema":{"type":"object","properties":{"text":{"type":"string"}}}}]""";
        public Func<JsonNode, int, string>? Call { get; init; }
        private int _calls;

        /// <summary>A <see cref="Call"/> answer that starts with this is sent as the JSON-RPC error that follows it.</summary>
        public const string ErrorPrefix = "error:";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var body = text.Length == 0 ? null : JsonNode.Parse(text);
            var method = (string?)body?["method"];
            lock (Requests) Requests.Add(new Seen(request.Method.Method, method, body, request.Headers));
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);

            var id = body?["id"]?.GetValue<long>() ?? 0;
            string Result(string inner) => $$"""{"jsonrpc":"2.0","id":{{id}},"result":{{inner}}}""";
            return method switch
            {
                "server/discover" => Json(Result("""{"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{"tools":{"listChanged":"""
                                                 + (ListChanged ? "true" : "false") + "}}}")),
                "tools/list"      => Json(Result("""{"resultType":"complete","ttlMs":1000,"cacheScope":"private","tools":""" + Tools + "}")),
                "tools/call"      => CallAnswer(Call?.Invoke(body!, Interlocked.Increment(ref _calls)), id),
                "subscriptions/listen" => Sse(
                    """{"jsonrpc":"2.0","method":"notifications/subscriptions/acknowledged","params":{"notifications":{"toolsListChanged":true},"_meta":{"io.modelcontextprotocol/subscriptionId":""" + id + "}}}",
                    """{"jsonrpc":"2.0","method":"notifications/tools/list_changed","params":{"_meta":{"io.modelcontextprotocol/subscriptionId":""" + id + "}}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"jsonrpc":"2.0","error":{"code":-32601,"message":"Method not found"},"id":""" + id + "}", Encoding.UTF8, "application/json"),
                },
            };
        }

        private static HttpResponseMessage CallAnswer(string? answer, long id) =>
            answer is not null && answer.StartsWith(ErrorPrefix, StringComparison.Ordinal)
                ? Json($$"""{"jsonrpc":"2.0","id":{{id}},"error":{{answer[ErrorPrefix.Length..]}}}""")
                : Json($$"""{"jsonrpc":"2.0","id":{{id}},"result":{{answer ?? """{"resultType":"complete","content":[{"type":"text","text":"ok"}]}"""}}}""");

        private static HttpResponseMessage Json(string json)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            r.Headers.TryAddWithoutValidation("Mcp-Session-Id", "never-echoed");   // a modern client keeps no session
            return r;
        }

        private static HttpResponseMessage Sse(params string[] events)
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Concat(events.Select(e => $"event: message\ndata: {e}\n\n")), Encoding.UTF8),
            };
            r.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return r;
        }
    }

    private static McpHttpClient Client(HttpMessageHandler handler) =>
        new(new McpServerConfig("modern", null, [], new Dictionary<string, string>(), Url: "https://mcp.example.com/mcp"), handler);

    private static string? Header(Seen s, string name) => s.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    private static JsonElement Args(string json) => El(json);

    [Fact]
    public async Task AModernServer_IsSpokenToWithoutAHandshake_EveryRequestCarryingItsMetaAndHeaders()
    {
        var server = new ModernServer();
        await using var client = Client(server);

        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        Assert.Equal(["echo"], (await client.ListToolsAsync(CancellationToken.None))!.Select(t => t.Name));
        Assert.Equal("ok", await client.CallToolAsync("echo", Args("""{"text":"hi"}"""), CancellationToken.None));

        var posts = server.Requests.Where(r => r.HttpMethod == "POST").ToList();
        Assert.Equal(["server/discover", "tools/list", "tools/call"], posts.Select(p => p.Method));
        Assert.All(posts, p =>
        {
            Assert.Equal("2026-07-28", (string?)p.Body!["params"]!["_meta"]![McpModern.VersionKey]);
            Assert.Equal("2026-07-28", Header(p, "MCP-Protocol-Version"));
            Assert.Equal(p.Method, Header(p, "Mcp-Method"));
            Assert.Null(Header(p, "Mcp-Session-Id"));                    // a session id the server sent is not echoed
        });
        Assert.Equal("echo", Header(posts[2], "Mcp-Name"));
        Assert.Null(Header(posts[1], "Mcp-Name"));                       // only calls that target a name carry one
        Assert.DoesNotContain(server.Requests, r => r.HttpMethod == "GET");   // no GET stream in this era
    }

    [Fact]
    public async Task AToolsXMcpHeaderParameters_AreMirrored_EncodedWhenTheyMustBe()
    {
        var server = new ModernServer
        {
            Tools = """[{"name":"regional","inputSchema":{"type":"object","properties":{"region":{"type":"string","x-mcp-header":"Region"},"q":{"type":"string"}}}}]""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        await client.ListToolsAsync(CancellationToken.None);

        await client.CallToolAsync("regional", Args("""{"region":"eu-west","q":"x"}"""), CancellationToken.None);
        await client.CallToolAsync("regional", Args("""{"region":"Zürich","q":"x"}"""), CancellationToken.None);
        await client.CallToolAsync("regional", Args("""{"q":"no region"}"""), CancellationToken.None);

        var calls = server.Requests.Where(r => r.Method == "tools/call").ToList();
        Assert.Equal("eu-west", Header(calls[0], "Mcp-Param-Region"));
        Assert.Equal("=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Zürich")) + "?=", Header(calls[1], "Mcp-Param-Region"));
        Assert.Null(Header(calls[2], "Mcp-Param-Region"));               // absent argument ⇒ header omitted
    }

    [Fact]
    public async Task AToolWhoseAnnotationBreaksTheRules_IsLeftOut_TheOthersOffered_AndItIsSaid()
    {
        var server = new ModernServer
        {
            Tools = """[{"name":"fine","inputSchema":{"type":"object"}},{"name":"broken_2026_annotation","inputSchema":{"type":"object","properties":{"n":{"type":"number","x-mcp-header":"N"}}}}]""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        Assert.Equal(["fine"], (await client.ListToolsAsync(CancellationToken.None))!.Select(t => t.Name));
        Assert.Contains(Inferpal.Services.Diagnostics.Snapshot(),
            e => e.Context == "Mcp" && e.Detail.Contains("broken_2026_annotation", StringComparison.Ordinal) && e.Detail.Contains("not offered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AToolWithABrokenAnnotation_IsStillOffered_ByAnEarlierServer()
    {
        // Reference arm: the rule is the modern HTTP binding's; a server of the earlier revisions keeps its tools.
        var handler = new LegacyServer("""[{"name":"broken","inputSchema":{"type":"object","properties":{"n":{"type":"number","x-mcp-header":"N"}}}}]""");
        await using var client = Client(handler);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        Assert.Equal(["broken"], (await client.ListToolsAsync(CancellationToken.None))!.Select(t => t.Name));
    }

    [Fact]
    public async Task AnInputRequiredWithStateAlone_IsRetriedWithThatState_AsANewRequest()
    {
        var server = new ModernServer
        {
            Call = (body, n) => n == 1
                ? """{"resultType":"input_required","requestState":"opaque-1"}"""
                : $$"""{"resultType":"complete","content":[{"type":"text","text":"done with {{(string?)body["params"]!["requestState"]}}"}]}""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        Assert.Equal("done with opaque-1", await client.CallToolAsync("echo", Args("{}"), CancellationToken.None));
        var calls = server.Requests.Where(r => r.Method == "tools/call").ToList();
        Assert.Equal(2, calls.Count);
        Assert.NotEqual((long)calls[0].Body!["id"]!, (long)calls[1].Body!["id"]!);   // the retry is a new request
        Assert.Null(calls[0].Body!["params"]!["requestState"]);
    }

    [Fact]
    public async Task AnInputRequiredAskingForInputInferpalCannotGive_IsNamedToTheModel_NeverReadAsAnEmptyResult()
    {
        var server = new ModernServer
        {
            Call = (_, _) => """{"resultType":"input_required","inputRequests":{"login":{"method":"elicitation/create","params":{"message":"name?"}}},"requestState":"s"}""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        var said = await client.CallToolAsync("echo", Args("{}"), CancellationToken.None);
        Assert.Contains("elicitation/create", said);
        Assert.Contains("did not complete", said);
        Assert.Single(server.Requests, r => r.Method == "tools/call");   // not retried: the input cannot be given
    }

    // ── forms (elicitation, form mode) ─────────────────────────────────────────────

    private const string AskForRepo = """
        {"resultType":"input_required","requestState":"rs-1","inputRequests":{"repo":{"method":"elicitation/create","params":{
          "mode":"form","message":"Which repository?","requestedSchema":{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}}}}}
        """;

    private static string Capabilities(Seen s) => s.Body!["params"]!["_meta"]!["io.modelcontextprotocol/clientCapabilities"]!.ToJsonString();

    [Fact]
    public async Task AFormTheServerAsksFor_IsFilledByThePerson_AndTheCallIsMadeAgainWithTheAnswer_AndTheStateAsItCame()
    {
        var server = new ModernServer
        {
            Call = (body, n) => n == 1
                ? AskForRepo
                : $$"""{"resultType":"complete","content":[{"type":"text","text":"released to {{(string?)body["params"]!["inputResponses"]!["repo"]!["content"]!["name"]}}"}]}""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        McpInputForm? asked = null;
        var said = await client.CallToolAsync("echo", Args("{}"), (form, _) =>
        {
            asked = form;
            return Task.FromResult(new McpInputAnswer(McpInputAction.Accept, new JsonObject { ["name"] = "dams/inferpal" }));
        }, CancellationToken.None);

        Assert.Equal(("modern", "echo", "Which repository?"), (asked!.Server, asked.Tool, asked.Message));
        // The model reads what the person did above the result, never the values themselves.
        Assert.EndsWith("\n\nreleased to dams/inferpal", said);
        Assert.Contains("answered the question MCP server 'modern' asked", said);
        var calls = server.Requests.Where(r => r.Method == "tools/call").ToList();
        Assert.Equal(2, calls.Count);
        Assert.All(calls, c => Assert.Equal("""{"elicitation":{"form":{}}}""", Capabilities(c)));
        Assert.Equal("""{"repo":{"action":"accept","content":{"name":"dams/inferpal"}}}""", calls[1].Body!["params"]!["inputResponses"]!.ToJsonString());
        Assert.Equal("rs-1", (string?)calls[1].Body!["params"]!["requestState"]);
        Assert.NotEqual((long)calls[0].Body!["id"]!, (long)calls[1].Body!["id"]!);
    }

    [Fact]
    public async Task WithoutState_TheRetryInventsNone_AndADeclineIsSentAsOne()
    {
        var server = new ModernServer
        {
            Call = (body, n) => n == 1
                ? AskForRepo.Replace("\"requestState\":\"rs-1\",", "", StringComparison.Ordinal)
                : """{"resultType":"complete","content":[{"type":"text","text":"nothing released"}]}""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        var said = await client.CallToolAsync("echo", Args("{}"),
            (_, _) => Task.FromResult(new McpInputAnswer(McpInputAction.Decline)), CancellationToken.None);

        Assert.Contains("declined", said);
        var retry = server.Requests.Where(r => r.Method == "tools/call").Last().Body!["params"]!;
        Assert.Null(retry["requestState"]);
        Assert.Equal("""{"repo":{"action":"decline"}}""", retry["inputResponses"]!.ToJsonString());
    }

    [Fact]
    public async Task AServerThatAnswersADeclineWithAnError_StillLeavesTheModelWhatThePersonDid()
    {
        // Without the note, the model calls again and the person is asked the same thing again.
        var server = new ModernServer
        {
            Call = (_, n) => n == 1 ? AskForRepo : ModernServer.ErrorPrefix + """{"code":-32000,"message":"user declined"}""",
        };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        var said = await client.CallToolAsync("echo", Args("{}"),
            (_, _) => Task.FromResult(new McpInputAnswer(McpInputAction.Decline)), CancellationToken.None);

        Assert.Contains("declined to answer the question MCP server 'modern' asked", said);
        Assert.Contains("failed after the user's answer: MCP error: user declined", said);
    }

    [Fact]
    public async Task WhereNoCardCanBeDrawn_NoFormIsDeclared_AndAFormAskedAnywayIsNamed()
    {
        // Reference arm of the first test: the same server, called where nobody can be asked.
        var server = new ModernServer { Call = (_, _) => AskForRepo };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        var said = await client.CallToolAsync("echo", Args("{}"), ask: null, CancellationToken.None);
        Assert.Contains("asked for input Inferpal does not provide (elicitation/create)", said);
        Assert.Equal("{}", Capabilities(Assert.Single(server.Requests, r => r.Method == "tools/call")));
    }

    [Theory]
    [InlineData("""{"resultType":"input_required","inputRequests":{"m":{"method":"sampling/createMessage","params":{}}}}""", "sampling/createMessage")]
    [InlineData("""{"resultType":"input_required","inputRequests":{"k":{"method":"elicitation/create","params":{"mode":"url","url":"https://x.example","message":"key"}}}}""", "url mode")]
    public async Task ARequestThePersonCannotAnswerHere_IsNamed_TheCardNeverShown_TheCallNotMadeAgain(string answer, string said)
    {
        var server = new ModernServer { Call = (_, _) => answer };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        var asked = false;
        var result = await client.CallToolAsync("echo", Args("{}"), (_, _) =>
        {
            asked = true;
            return Task.FromResult(new McpInputAnswer(McpInputAction.Cancel));
        }, CancellationToken.None);

        Assert.False(asked);
        Assert.Contains(said, result);
        Assert.Contains("did not complete", result);
        Assert.Single(server.Requests, r => r.Method == "tools/call");
    }

    [Fact]
    public async Task StoppedWhileThePersonFillsTheForm_TheRunStops_AndNothingIsSent()
    {
        var server = new ModernServer { Call = (_, _) => AskForRepo };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync("echo", Args("{}"),
            (_, _) => throw new OperationCanceledException(), CancellationToken.None));
        Assert.Single(server.Requests, r => r.Method == "tools/call");
    }

    [Fact]
    public async Task AToolAsksThePersonOnlyThroughAPipelineThatDrawsTheCard()
    {
        // McpTool reads its approval service at each call: the /task recorder, an ACP host, draw none.
        var client = new AskRecordingClient();
        var info = new McpToolInfo("echo", "", McpJsonRpc.EmptyObject());
        await new McpTool(client, info, new DrawingApproval(draws: false)).ExecuteAsync(Args("{}"), CancellationToken.None);
        Assert.False(client.Asked);
        await new McpTool(client, info, new DrawingApproval(draws: true)).ExecuteAsync(Args("{}"), CancellationToken.None);
        Assert.True(client.Asked);
    }

    private sealed class DrawingApproval(bool draws) : Inferpal.Services.Execution.IApprovalService
    {
        public bool CanAskForInput => draws;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
            Inferpal.Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
    }

    private sealed class AskRecordingClient : IMcpClient
    {
        public bool Asked { get; private set; }
        public string ServerName => "s";
        public string? LastError => null;
        public bool NeedsAuthorization => false;
#pragma warning disable CS0067 // the fake never changes its tools nor closes
        public event Action? ToolsChanged;
        public event Action? Closed;
#pragma warning restore CS0067
        public Task<bool> StartAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<IReadOnlyList<McpToolInfo>?> ListToolsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<McpToolInfo>?>([]);
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, CancellationToken ct) => Task.FromResult("ok");
        public Task<string> CallToolAsync(string toolName, JsonElement arguments, McpInputAsker? ask, CancellationToken ct)
        {
            Asked = ask is not null;
            return Task.FromResult("ok");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── roots ─────────────────────────────────────────────────────────────────────

    private static string RootFolder() => Directory.CreateTempSubdirectory("inferpal-mcp-roots").FullName;

    /// <summary>
    /// ⚠ The 2026-07-28 revision deprecates roots ("new implementations SHOULD NOT adopt it"), and over HTTP a remote server
    /// would be handed a path of this machine: in this era none are declared, a folder to give or not, and a server that asks
    /// anyway is named to the model.
    /// </summary>
    [Fact]
    public async Task TheModernEra_DeclaresNoRoots_AndARootsRequestIsNamed_EvenWithAFolderToGive()
    {
        var folder = RootFolder();
        try
        {
            var server = new ModernServer
            {
                Call = (_, _) => """{"resultType":"input_required","inputRequests":{"r":{"method":"roots/list"}},"requestState":"s"}""",
            };
            await using var client = Client(server);
            client.WorkspaceRoot = () => folder;
            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

            var said = await client.CallToolAsync("echo", Args("{}"), CancellationToken.None);
            Assert.Contains("roots/list", said);
            Assert.Contains("did not complete", said);
            Assert.Single(server.Requests, r => r.Method == "tools/call");
            var posted = server.Requests.Where(r => r.Body is not null).ToList();
            Assert.NotEmpty(posted);
            Assert.All(posted, r => Assert.Equal("{}", r.Body!["params"]!["_meta"]!["io.modelcontextprotocol/clientCapabilities"]!.ToJsonString()));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ARemoteServer_IsNeverGivenRoots_ALocalOneAsItsEntryDecides()
    {
        var servers = McpServerConfig.Parse("""{ "remote": { "url": "https://mcp.example.com/mcp", "roots": true }, "local": { "command": "x" } }""");
        Assert.False(McpToolService.GivesRootsTo(servers.Single(s => s.Name == "remote"), workspaceRoot: null));
        Assert.True(McpToolService.GivesRootsTo(servers.Single(s => s.Name == "local"), workspaceRoot: null));
    }

    [Fact]
    public void AServerStartedWithAFolderOfItsOwn_KeepsIt_UnlessItsEntrySaysOtherwise()
    {
        // ⚠ The reference filesystem server replaces the folders it was started with by the roots it is given.
        var folder = RootFolder();
        try
        {
            var json = $$"""
                {
                  "fs":      { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", {{JsonSerializer.Serialize(folder)}}] },
                  "fsAsked": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", {{JsonSerializer.Serialize(folder)}}], "roots": true },
                  "plain":   { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem"] },
                  "kept":    { "command": "uvx", "args": ["mcp-server-git"], "roots": false }
                }
                """;
            var servers = McpServerConfig.Parse(json);
            bool Gives(string name) => servers.Single(s => s.Name == name).GivesRoots(workspaceRoot: null);
            Assert.False(Gives("fs"));
            Assert.True(Gives("fsAsked"));
            Assert.True(Gives("plain"));
            Assert.False(Gives("kept"));

            // The settings list rewrites the map from what it read, and its editor rebuilds an entry: the choice stays.
            var again = McpServerConfig.Parse(McpServerConfig.Serialize(servers));
            Assert.Equal(true, again.Single(s => s.Name == "fsAsked").Roots);
            Assert.Equal(false, again.Single(s => s.Name == "kept").Roots);
            Assert.Null(again.Single(s => s.Name == "plain").Roots);
            var edited = McpServerConfig.FromEditor(servers.Single(s => s.Name == "kept"), "kept", "uvx", ["mcp-server-git"],
                                                    new Dictionary<string, string>(), null, null, enabled: true);
            Assert.Equal(false, edited.Roots);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(@"C:\dev\my app", true, "file:///C:/dev/my%20app")]
    [InlineData(@"C:\dev\a%20b", true, "file:///C:/dev/a%2520b")]
    [InlineData(@"C:\", true, "file:///C:/")]
    [InlineData(@"\\server\share\src", true, "file://server/share/src")]
    [InlineData(@"\\?\C:\dev\src", true, "file:///C:/dev/src")]
    [InlineData(@"\\?\UNC\server\share\src", true, "file://server/share/src")]
    [InlineData("/home/me/my app", false, "file:///home/me/my%20app")]
    [InlineData(@"/home/me/a\b", false, "file:///home/me/a%5Cb")]
    [InlineData("/", false, "file:///")]
    public void ARootsUri_EscapesThePathAsItIs(string fullPath, bool windows, string expected) =>
        Assert.Equal(expected, McpModern.FileUri(fullPath, windows));

    [Fact]
    public async Task AServerThatKeepsAskingForAnotherRoundTrip_IsStoppedAndSaid_AndAnUnknownResultTypeIsNamed()
    {
        var looping = new ModernServer { Call = (_, n) => $$"""{"resultType":"input_required","requestState":"again-{{n}}"}""" };
        await using (var client = Client(looping))
        {
            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            Assert.Contains("3 times in a row", await client.CallToolAsync("echo", Args("{}"), CancellationToken.None));
            Assert.Equal(3, looping.Requests.Count(r => r.Method == "tools/call"));
        }

        var weird = new ModernServer { Call = (_, _) => """{"resultType":"task_started"}""" };
        await using (var client = Client(weird))
        {
            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            Assert.Contains("'task_started'", await client.CallToolAsync("echo", Args("{}"), CancellationToken.None));
        }
    }

    [Fact]
    public async Task AResultCarriedAsStructuredContentAlone_IsWhatTheModelReads()
    {
        var server = new ModernServer { Call = (_, _) => """{"resultType":"complete","content":[],"structuredContent":[1,2,3]}""" };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        Assert.Equal("[1,2,3]", await client.CallToolAsync("echo", Args("{}"), CancellationToken.None));
    }

    [Fact]
    public async Task AModernServerAnnouncingListChanges_IsSubscribedTo_AndItsChangeIsSeen()
    {
        var server = new ModernServer { ListChanged = true };
        await using var client = Client(server);
        var changed = new TaskCompletionSource();
        client.ToolsChanged += () => changed.TrySetResult();

        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);

        // 30 s: we wait for the event to ARRIVE; the delay is only a deadlock guard.
        Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(30))));
        var listen = server.Requests.First(r => r.Method == "subscriptions/listen");
        Assert.True((bool)listen.Body!["params"]!["notifications"]!["toolsListChanged"]!);
        Assert.Equal("subscriptions/listen", Header(listen, "Mcp-Method"));
    }

    [Fact]
    public async Task AModernServerWithoutListChanges_IsNotSubscribedTo()
    {
        // Reference arm: nothing announced, nothing asked for.
        var server = new ModernServer { ListChanged = false };
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        await Task.Delay(300);
        Assert.DoesNotContain(server.Requests, r => r.Method == "subscriptions/listen" || r.HttpMethod == "GET");
    }

    // ── HTTP: the fallback ───────────────────────────────────────────────────

    /// <summary>A 2025-era server: the discovery probe gets what the official SDK v1 answers (400, -32000).</summary>
    private sealed class LegacyServer(string tools = "[]", string? discoverAnswer = null) : HttpMessageHandler
    {
        public List<string?> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var method = (string?)body["method"];
            lock (Methods) Methods.Add(method);
            var id = body["id"]?.GetValue<long>() ?? 0;
            return method switch
            {
                "server/discover" => new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(discoverAnswer ?? """{"jsonrpc":"2.0","error":{"code":-32000,"message":"Bad Request: No valid session ID provided"},"id":null}""",
                                                Encoding.UTF8, "application/json"),
                },
                "initialize" => Ok("""{"jsonrpc":"2.0","result":{"protocolVersion":"2024-11-05"},"id":""" + id + "}"),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => Ok("""{"jsonrpc":"2.0","id":""" + id + ""","result":{"tools":""" + tools + "}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Ok(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task AnEarlierServer_RefusingTheProbe_GetsTheHandshake_ThenWhatItAlwaysGot()
    {
        var server = new LegacyServer();
        await using var client = Client(server);

        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        await client.ListToolsAsync(CancellationToken.None);

        Assert.Equal(["server/discover", "initialize", "notifications/initialized", "tools/list"], server.Methods);
    }

    [Fact]
    public async Task AModernServerListingOnlyEarlierVersions_GetsTheHandshake()
    {
        var server = new LegacyServer(discoverAnswer: """{"jsonrpc":"2.0","id":1,"error":{"code":-32022,"message":"Unsupported protocol version","data":{"supported":["2025-11-25"],"requested":"2026-07-28"}}}""");
        await using var client = Client(server);
        Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
        Assert.Contains("initialize", server.Methods);
    }

    [Fact]
    public async Task AModernRefusalOfTheProbe_IsNamed_AndNoHandshakeIsTried()
    {
        var server = new LegacyServer(discoverAnswer: """{"jsonrpc":"2.0","id":1,"error":{"code":-32020,"message":"Header mismatch: Mcp-Method"}}""");
        await using var client = Client(server);

        Assert.False(await client.StartAsync(CancellationToken.None));
        Assert.Contains("refused the discovery request", client.LastError);
        Assert.Contains("Header mismatch", client.LastError);
        Assert.DoesNotContain("initialize", server.Methods);
    }

    // ── stdio ────────────────────────────────────────────────────────────────

    /// <summary>A modern stdio server: it refuses any request without this revision's _meta, journals what it receives,
    /// never answers the tool <c>slow</c>, and opens the subscription it is asked for when <c>listChanged</c>.</summary>
    private const string ModernStdioServer = """
        import json, sys
        journal = open(sys.argv[1], "a", encoding="utf-8")
        list_changed = sys.argv[2] == "1"
        def send(m):
            sys.stdout.write(json.dumps(m) + "\n"); sys.stdout.flush()
        for line in sys.stdin:
            m = json.loads(line)
            journal.write(line); journal.flush()
            mid, method = m.get("id"), m.get("method")
            if mid is None:
                continue
            meta = (m.get("params") or {}).get("_meta") or {}
            if meta.get("io.modelcontextprotocol/protocolVersion") != "2026-07-28":
                send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32022, "message": "Unsupported protocol version",
                      "data": {"supported": ["2026-07-28"]}}})
            elif method == "server/discover":
                send({"jsonrpc": "2.0", "id": mid, "result": {"resultType": "complete", "supportedVersions": ["2026-07-28"],
                      "capabilities": {"tools": {"listChanged": list_changed}}}})
            elif method == "subscriptions/listen":
                tag = {"io.modelcontextprotocol/subscriptionId": mid}
                send({"jsonrpc": "2.0", "method": "notifications/subscriptions/acknowledged", "params": {"_meta": tag, "notifications": {"toolsListChanged": True}}})
                send({"jsonrpc": "2.0", "method": "notifications/tools/list_changed", "params": {"_meta": tag}})
            elif method == "tools/list":
                send({"jsonrpc": "2.0", "id": mid, "result": {"resultType": "complete", "tools": [{"name": "slow", "inputSchema": {"type": "object"}}]}})
            elif method == "tools/call":
                pass
        """;

    /// <param name="probeWait">The client's probe window. ⚠ Left at the product's 3 s, a test asserting that a server
    /// answering the probe is never sent <c>initialize</c> measured the runner: under a full suite the interpreter alone
    /// can take longer to answer, and the client then rightly sends the handshake too.</param>
    private static async Task<(McpStdioClient Client, string Journal, string Script)?> StartModernStdioAsync(
        bool listChanged, TimeSpan? probeWait = null)
    {
        if (!await PythonForTests.IsInstalledAsync()) return null;   // UNDECIDED without Python — never read as a pass
        var script  = Path.Combine(Path.GetTempPath(), $"inferpal-mcp2026-{Guid.NewGuid():N}.py");
        var journal = script + ".journal";
        File.WriteAllText(script, ModernStdioServer);
        var client = new McpStdioClient(new("modern", OperatingSystem.IsWindows() ? "python" : "python3",
                                            [script, journal, listChanged ? "1" : "0"], new Dictionary<string, string>()))
        {
            ProbeWait = probeWait ?? McpStdioClient.DefaultProbeWait,
        };
        return (client, journal, script);
    }

    /// <summary>What the fake server received. Read with sharing: the server still holds the file open.</summary>
    private static List<JsonNode> Journal(string path)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n').Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!).ToList();
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(100);   // 30 s: only a deadlock guard
        return condition();
    }

    [Fact]
    public async Task AModernStdioServer_IsSpokenToWithoutAHandshake_AndItsSubscriptionDeliversTheChange()
    {
        // The window is the runner's, not the product's: "answers the probe in time" is what is asserted.
        if (await StartModernStdioAsync(listChanged: true, TimeSpan.FromSeconds(60)) is not var (client, journal, script)) return;
        try
        {
            var changed = new TaskCompletionSource();
            client.ToolsChanged += () => changed.TrySetResult();

            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            Assert.Equal(["slow"], (await client.ListToolsAsync(CancellationToken.None))!.Select(t => t.Name));
            Assert.Same(changed.Task, await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(30))));

            var seen = Journal(journal);
            Assert.DoesNotContain(seen, m => (string?)m["method"] == "initialize");
            Assert.Contains(seen, m => (string?)m["method"] == "subscriptions/listen");
            Assert.All(seen.Where(m => m["id"] is not null),
                       m => Assert.Equal("2026-07-28", (string?)m["params"]!["_meta"]![McpModern.VersionKey]));
        }
        finally
        {
            await client.DisposeAsync();
            try { File.Delete(script); File.Delete(journal); } catch { }
        }
    }

    [Fact]
    public async Task ACancelledCall_IsCancelledInAModernStdioServer_NotLeftRunning()
    {
        // ⚠ On stdio nothing closes with the call: without notifications/cancelled the server goes on working on it.
        if (await StartModernStdioAsync(listChanged: false) is not var (client, journal, script)) return;
        try
        {
            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CallToolAsync("slow", Args("{}"), stop.Token));

            var callId = Journal(journal).Last(m => (string?)m["method"] == "tools/call")["id"]!.GetValue<long>();
            Assert.True(await EventuallyAsync(() => Journal(journal).Any(m =>
                (string?)m["method"] == "notifications/cancelled" && m["params"]!["requestId"]!.GetValue<long>() == callId)));
            // Reference arm: no subscription without the announcement.
            Assert.DoesNotContain(Journal(journal), m => (string?)m["method"] == "subscriptions/listen");
        }
        finally
        {
            await client.DisposeAsync();
            try { File.Delete(script); File.Delete(journal); } catch { }
        }
    }

    /// <summary>A 2025-era stdio server that answers an unknown request before its handshake with -32601, as the
    /// official SDK v1 does.</summary>
    private const string RefusingLegacyStdioServer = """
        import json, sys
        journal = open(sys.argv[1], "a", encoding="utf-8")
        def send(m):
            sys.stdout.write(json.dumps(m) + "\n"); sys.stdout.flush()
        for line in sys.stdin:
            m = json.loads(line)
            journal.write(line); journal.flush()
            mid, method = m.get("id"), m.get("method")
            if mid is None:
                continue
            if method == "initialize":
                send({"jsonrpc": "2.0", "id": mid, "result": {"protocolVersion": m["params"]["protocolVersion"],
                      "capabilities": {"tools": {}}, "serverInfo": {"name": "legacy", "version": "1"}}})
            elif method == "tools/list":
                send({"jsonrpc": "2.0", "id": mid, "result": {"tools": [{"name": "old", "inputSchema": {"type": "object"}}]}})
            else:
                send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "Method not found"}})
        """;

    [Fact]
    public async Task AnEarlierStdioServer_RefusingTheProbe_GetsTheUnchangedHandshakeRightAfter()
    {
        if (!await PythonForTests.IsInstalledAsync()) return;   // UNDECIDED without Python — never read as a pass
        var script  = Path.Combine(Path.GetTempPath(), $"inferpal-mcp2025-{Guid.NewGuid():N}.py");
        var journal = script + ".journal";
        File.WriteAllText(script, RefusingLegacyStdioServer);
        var client = new McpStdioClient(new("legacy", OperatingSystem.IsWindows() ? "python" : "python3",
                                            [script, journal], new Dictionary<string, string>()));
        try
        {
            Assert.True(await client.StartAsync(CancellationToken.None), client.LastError);
            Assert.Equal(["old"], (await client.ListToolsAsync(CancellationToken.None))!.Select(t => t.Name));

            var seen = Journal(journal);
            Assert.Equal(["server/discover", "initialize", "notifications/initialized", "tools/list"],
                         seen.Select(m => (string?)m["method"]));
            Assert.Equal(McpModern.LegacyVersion, (string?)seen[1]["params"]!["protocolVersion"]);   // what it always got
            Assert.Null(seen[3]["params"]!["_meta"]);                                                 // no meta in this era
        }
        finally
        {
            await client.DisposeAsync();
            try { File.Delete(script); File.Delete(journal); } catch { }
        }
    }
}
