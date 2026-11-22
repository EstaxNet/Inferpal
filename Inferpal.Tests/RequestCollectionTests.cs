using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The requests a project keeps — a .http file, a Postman collection, a Bruno collection — replayed by name through
/// http_request (pari 158): variables of the file and its environment resolved, the same card and rules as a request
/// written by hand; a variable with no value refuses, named; a secret goes on the card, never back to the model.
/// </summary>
public sealed class RequestCollectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-collections-").FullName;
    private readonly HttpListener _server = new();
    private readonly int _port;
    private readonly List<string> _received = [];

    public RequestCollectionTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        _port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _server.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _server.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        try { _server.Stop(); _server.Close(); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext c;
            try { c = await _server.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(c.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            lock (_received) _received.Add($"{c.Request.HttpMethod} {c.Request.Url!.PathAndQuery} auth={c.Request.Headers["Authorization"]} {body}");
            // /echo answers with what it was sent: the token, the path — as an echo endpoint or a Location header would.
            var bytes = Encoding.UTF8.GetBytes(c.Request.Url!.AbsolutePath.StartsWith("/echo", StringComparison.Ordinal)
                ? $"{{\"auth\":\"{c.Request.Headers["Authorization"]}\",\"path\":\"{c.Request.Url!.PathAndQuery}\"}}"
                : "{\"ok\":true}");
            c.Response.StatusCode = 200;
            c.Response.ContentType = "application/json";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    private sealed class Card(ApprovalDecision answer) : ApprovalServiceBase(InferpalConfig.LoadOwnCopy(), () => Path.GetTempPath())
    {
        public readonly List<string> Shown = [];
        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown.Add(JsonSerializer.Serialize(prompt));
            return Task.FromResult(answer);
        }
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            throw new InvalidOperationException("the card path asks");
    }

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private Task<string> Call(IApprovalService approval, object args) =>
        new HttpRequestTool(approval, () => _root).ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    private const string HttpFile = """
        @api = {{host}}/api

        ### List orders
        GET {{api}}/orders?page=1

        ###
        # @name createOrder
        POST {{api}}/orders
        Content-Type: application/json
        Authorization: Bearer {{token}}

        { "qty": 2, "id": "{{$guid}}" }
        """;

    [Fact]
    public async Task AnHttpFile_IsListed_ThenReplayedByName_WithItsEnvironment()
    {
        Write("api/orders.http", HttpFile);
        Write("api/http-client.env.json", $$"""{ "dev": { "host": "http://127.0.0.1:{{_port}}" } }""");
        Write("api/http-client.env.json.user", """{ "dev": { "token": "sk-live-0123456789abcdef0123456789abcd" } }""");
        var card = new Card(ApprovalDecision.Once);

        var listed = await Call(card, new { collection = "api/orders.http" });
        var answer = await Call(card, new { collection = "api/orders.http", request = "createOrder", environment = "dev" });

        Assert.Contains("holds 2 requests: 'List orders', 'createOrder'. Environments: dev.", listed, StringComparison.Ordinal);
        Assert.StartsWith($"Replayed 'createOrder' from api{Path.DirectorySeparatorChar}orders.http: POST http://127.0.0.1:{_port}/api/orders.", answer, StringComparison.Ordinal);
        Assert.Contains("HTTP 200", answer, StringComparison.Ordinal);
        var received = Assert.Single(_received);
        Assert.StartsWith("POST /api/orders auth=Bearer sk-live-0123456789abcdef0123456789abcd { \"qty\": 2, \"id\": \"", received, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", received, StringComparison.Ordinal);
        // The token goes on the card the person reads, never back to the model.
        Assert.Contains("sk-live-0123456789abcdef0123456789abcd", Assert.Single(card.Shown), StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789abcdef0123456789abcd", answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVariableWithNoValue_RefusesTheRequest_AndNamesTheEnvironmentThatHoldsIt()
    {
        Write("orders.http", HttpFile);
        Write("http-client.env.json", $$"""{ "dev": { "host": "http://127.0.0.1:{{_port}}", "token": "t" } }""");
        var card = new Card(ApprovalDecision.Once);

        var answer = await Call(card, new { collection = "orders.http", request = "createOrder" });

        Assert.Contains("uses {{host}}, {{token}}, which have no value here — name an environment: dev. Nothing was sent.", answer, StringComparison.Ordinal);
        Assert.Empty(card.Shown);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task APostmanCollection_IsReplayed_WithItsEnvironment_ItsAuth_AndItsScriptsSaidNotRun()
    {
        Write("postman/shop.postman_collection.json", """
            {
              "info": { "_postman_id": "1", "name": "Shop", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}", "type": "string" } ] },
              "variable": [ { "key": "path", "value": "/api/orders" } ],
              "item": [ { "name": "Orders", "item": [ {
                "name": "Create order",
                "event": [ { "listen": "test", "script": { "exec": ["pm.test('ok')"] } } ],
                "request": { "method": "POST", "header": [ { "key": "X-Off", "value": "1", "disabled": true } ],
                  "body": { "mode": "raw", "raw": "{\"qty\": 3}" },
                  "url": { "raw": "{{baseUrl}}{{path}}?source=postman" } } } ] } ]
            }
            """);
        Write("postman/local.postman_environment.json", $$"""
            { "name": "Local", "values": [ { "key": "baseUrl", "value": "http://127.0.0.1:{{_port}}", "enabled": true },
                                           { "key": "token", "value": "abc123token", "enabled": true } ] }
            """);

        var answer = await Call(new Card(ApprovalDecision.Once),
                                new { collection = "postman/shop.postman_collection.json", request = "Orders/Create order", environment = "Local" });

        Assert.Contains("(its scripts are not run)", answer, StringComparison.Ordinal);
        Assert.Equal("POST /api/orders?source=postman auth=Bearer abc123token {\"qty\": 3}", Assert.Single(_received));
    }

    [Fact]
    public async Task ABrunoCollection_IsReplayed_AndASecretItKeepsElsewhere_IsNamed()
    {
        Write("bruno/bruno.json", """{ "version": "1", "name": "Shop", "type": "collection" }""");
        Write("bruno/orders/Create order.bru", """
            meta {
              name: Create order
              type: http
              seq: 1
            }

            post {
              url: {{baseUrl}}/api/orders
              body: json
              auth: basic
            }

            headers {
              X-Source: bruno
              ~X-Off: 1
            }

            auth:basic {
              username: admin
              password: {{password}}
            }

            body:json {
              {
                "qty": 4
              }
            }
            """);
        Write("bruno/environments/Local.bru", $$"""
            vars {
              baseUrl: http://127.0.0.1:{{_port}}
              password: hunter22
            }
            """);
        Write("bruno/environments/Prod.bru", """
            vars {
              baseUrl: https://shop.example.com
            }
            vars:secret [
              password
            ]
            """);

        var local = await Call(new Card(ApprovalDecision.Once), new { collection = "bruno", request = "Create order", environment = "Local" });
        var prod = await Call(new Card(ApprovalDecision.Once), new { collection = "bruno/orders/Create order.bru", request = "Create order", environment = "Prod" });

        Assert.Contains("HTTP 200", local, StringComparison.Ordinal);
        Assert.Equal($"POST /api/orders auth=Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:hunter22"))} {{\n  \"qty\": 4\n}}", Assert.Single(_received));
        Assert.Contains("uses {{password}}", prod, StringComparison.Ordinal);
        Assert.Contains("password is a secret Bruno keeps outside the files", prod, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretThePatternsDoNotRecognise_IsMaskedEverywhereItGoesBack()
    {
        Write("hooks.http", "### Hook\nPOST {{base}}/echo/{{hookSecret}}?code={{code}}\nAuthorization: Bearer {{token}}\n");
        Write("http-client.env.json", $$"""
            { "dev": { "base": "http://127.0.0.1:{{_port}}", "hookSecret": "whsec9f8e7d6c", "code": "c0de4711", "token": "tok-5544332211" } }
            """);

        var answer = await Call(new Card(ApprovalDecision.Once), new { collection = "hooks.http", request = "Hook", environment = "dev" });

        Assert.Contains("HTTP 200", answer, StringComparison.Ordinal);
        foreach (var secret in new[] { "whsec9f8e7d6c", "c0de4711", "tok-5544332211" })
            Assert.DoesNotContain(secret, answer, StringComparison.Ordinal);
        Assert.Contains($"POST http://127.0.0.1:{_port}/echo/{{{{hookSecret}}}}?code={{{{code}}}}", answer, StringComparison.Ordinal);
        Assert.Contains("Bearer {{token}}", answer, StringComparison.Ordinal);   // the echoed header, masked by name
    }

    [Theory]
    // Variables that refer to each other, and ones that double at each level: refused, the host's memory never spent.
    [InlineData("@a = {{b}}\n@b = {{a}}\n\n### R\nGET http://127.0.0.1/{{a}}\n", "refer to each other (a, b)")]
    [InlineData("@a0 = xxxxxxxxxx\n@a1 = {{a0}}{{a0}}{{a0}}{{a0}}{{a0}}{{a0}}{{a0}}{{a0}}{{a0}}{{a0}}\n@a2 = {{a1}}{{a1}}{{a1}}{{a1}}{{a1}}{{a1}}{{a1}}{{a1}}{{a1}}{{a1}}\n"
              + "@a3 = {{a2}}{{a2}}{{a2}}{{a2}}{{a2}}{{a2}}{{a2}}{{a2}}{{a2}}{{a2}}\n@a4 = {{a3}}{{a3}}{{a3}}{{a3}}{{a3}}{{a3}}{{a3}}{{a3}}{{a3}}{{a3}}\n"
              + "@a5 = {{a4}}{{a4}}{{a4}}{{a4}}{{a4}}{{a4}}{{a4}}{{a4}}{{a4}}{{a4}}\n@a6 = {{a5}}{{a5}}{{a5}}{{a5}}{{a5}}{{a5}}{{a5}}{{a5}}{{a5}}{{a5}}\n"
              + "@a7 = {{a6}}{{a6}}{{a6}}{{a6}}{{a6}}{{a6}}{{a6}}{{a6}}{{a6}}{{a6}}\n@a8 = {{a7}}{{a7}}{{a7}}{{a7}}{{a7}}{{a7}}{{a7}}{{a7}}{{a7}}{{a7}}\n"
              + "\n### R\nGET http://127.0.0.1/{{a8}}\n", "expands its variables past")]
    public void ACollectionThatExpandsWithoutEnd_IsRefused(string file, string said)
    {
        var path = Write("bomb.http", file);

        var (request, error) = RequestCollection.Resolve(path, "R", null, _root);

        Assert.Null(request);
        Assert.Contains(said, error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHttpFilesOwnVariable_WinsOverTheEnvironment_AsVisualStudioReadsIt()
    {
        var path = Write("app.http", "@App_HostAddress = http://127.0.0.1:5103\n\n### R\nGET {{App_HostAddress}}/weather\n");
        Write("http-client.env.json", """{ "dev": { "App_HostAddress": "https://prod.example.com" } }""");

        var (request, _) = RequestCollection.Resolve(path, "R", "dev", _root);

        Assert.Equal("http://127.0.0.1:5103/weather", request!.Url);
    }

    [Fact]
    public void OneRequestWithABodyFromAFile_LeavesTheOthersReplayable_AndAHandlerIsNotSent()
    {
        var path = Write("mixed.http", """
            ### Upload
            POST http://127.0.0.1/upload
            Content-Type: application/json

            < ./payload.json

            ### Note
            POST http://127.0.0.1/notes
            Content-Type: application/json

            {"note":"a > b"}

            > {% client.global.set("id", response.body.id) %}
            """);

        var (note, _) = RequestCollection.Resolve(path, "Note", null, _root);
        var (_, upload) = RequestCollection.Resolve(path, "Upload", null, _root);

        Assert.Equal("{\"note\":\"a > b\"}", note!.Body);
        Assert.Contains("its response handler is not run", note.Notes);
        Assert.Contains("takes its body from a file", upload, StringComparison.Ordinal);
    }

    [Fact]
    public void APostmanForm_PathVariables_AndInheritedAuth_AreSentAsPostmanSendsThem()
    {
        var path = Write("c.postman_collection.json", """
            {
              "info": { "_postman_id": "1", "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json" },
              "auth": { "type": "bearer", "bearer": [ { "key": "token", "value": "{{token}}" } ] },
              "variable": [ { "key": "token", "value": "t0k3n-abc" }, { "key": "who", "value": "a b&c" } ],
              "item": [ { "name": "Login", "request": { "method": "POST", "auth": { "type": "inherit" },
                "url": { "raw": "http://127.0.0.1/users/:id/login", "variable": [ { "key": "id", "value": "42" } ] },
                "body": { "mode": "urlencoded", "urlencoded": [ { "key": "user", "value": "{{who}}" } ] } } } ]
            }
            """);

        var (request, error) = RequestCollection.Resolve(path, "Login", null, _root);

        Assert.Null(error);
        Assert.Equal("http://127.0.0.1/users/42/login", request!.Url);
        Assert.Equal("user=a%20b%26c", request.Body);
        Assert.Contains(request.Headers, h => h.Key == "Authorization" && h.Value == "Bearer t0k3n-abc");
    }

    [Fact]
    public void ABrunoRequestThatInherits_TakesItsCollectionsAuthAndHeaders()
    {
        Write("b/bruno.json", """{ "version": "1", "name": "B", "type": "collection" }""");
        Write("b/collection.bru", "headers {\n  X-Team: core\n}\n\nauth {\n  mode: bearer\n}\n\nauth:bearer {\n  token: coll-token-77\n}\n");
        Write("b/users/Get user.bru", "meta {\n  name: Get user\n}\n\nget {\n  url: http://127.0.0.1/users/:id\n  auth: inherit\n}\n\nparams:path {\n  id: 7\n}\n");

        var (request, error) = RequestCollection.Resolve(Path.Combine(_root, "b"), "Get user", null, _root);

        Assert.Null(error);
        Assert.Equal("http://127.0.0.1/users/7", request!.Url);
        Assert.Contains(request.Headers, h => h.Key == "X-Team" && h.Value == "core");
        Assert.Contains(request.Headers, h => h.Key == "Authorization" && h.Value == "Bearer coll-token-77");
    }

    [Fact]
    public async Task ACollectionOutsideTheWorkspace_IsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.http");
        File.WriteAllText(outside, $"GET http://127.0.0.1:{_port}/x\n");
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Call(new Card(ApprovalDecision.Once), new { collection = outside, request = $"GET http://127.0.0.1:{_port}/x" }));
            Assert.Empty(_received);
        }
        finally { File.Delete(outside); }
    }

    [Fact]
    public async Task ACollectionAndAnUrl_AreNotMixed()
    {
        var answer = await Call(new Card(ApprovalDecision.Once), new { collection = "orders.http", request = "x", url = "http://127.0.0.1/" });

        Assert.StartsWith("Error: give either collection and request, or url", answer, StringComparison.Ordinal);
    }
}
