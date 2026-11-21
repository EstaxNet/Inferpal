using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>http_request</c> against a real local server: every call through a card, "Always" for one origin only, any other
/// address forced each time unless the person's rule names the whole subject; nothing followed, nothing unbounded.
/// </summary>
public sealed class HttpRequestToolTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly int _port;
    private readonly List<string> _received = [];

    public HttpRequestToolTests()
    {
        _port = FreePort();
        _server.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _server.Start();
        _ = ServeAsync();
    }

    public void Dispose()
    {
        try { _server.Stop(); _server.Close(); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext c;
            try { c = await _server.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(c.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            lock (_received) _received.Add($"{c.Request.HttpMethod} {c.Request.Url!.AbsolutePath} {c.Request.ContentType} {body}");
            var (code, text) = c.Request.Url!.AbsolutePath switch
            {
                "/fail"  => (500, "System.NullReferenceException at OrdersController.Create line 42"),
                "/moved" => (302, ""),
                _        => (200, "{\"ok\":true}"),
            };
            if (code == 302) c.Response.RedirectLocation = "http://example.com/elsewhere";
            c.Response.StatusCode = code;
            var bytes = Encoding.UTF8.GetBytes(text);
            c.Response.ContentType = "application/json; charset=utf-8";
            await c.Response.OutputStream.WriteAsync(bytes);
            c.Response.Close();
        }
    }

    /// <summary>The real approval path, its card answered by <paramref name="answer"/>.</summary>
    private sealed class Card(ApprovalDecision answer) : ApprovalServiceBase(InferpalConfig.LoadOwnCopy(), () => Path.GetTempPath())
    {
        public readonly List<ApprovalPrompt> Shown = [];
        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown.Add(prompt);
            return Task.FromResult(answer);
        }
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            throw new InvalidOperationException("the card path asks");
    }

    private Task<string> Call(IApprovalService approval, object args) =>
        new HttpRequestTool(approval).ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    private string Url(string path) => $"http://127.0.0.1:{_port}{path}";

    [Fact]
    public async Task ALocalPost_IsAskedOnce_ThenAlwaysForThatOriginOnly()
    {
        var card = new Card(ApprovalDecision.Always);

        var first  = await Call(card, new { url = Url("/orders"), method = "POST", body = "{\"qty\":2}" });
        var second = await Call(card, new { url = Url("/orders"), method = "POST", body = "{\"qty\":3}" });
        await Call(card, new { url = $"http://localhost:{FreePort()}/", method = "POST" });   // another local origin

        Assert.Contains("HTTP 200", first);
        Assert.Contains("{\"ok\":true}", second);
        Assert.Equal(2, card.Shown.Count);   // the first call, then the other origin — never the second call
        Assert.False(card.Shown[0].Forced);
        Assert.Contains("POST /orders application/json; charset=utf-8 {\"qty\":2}", _received);
    }

    [Fact]
    public async Task A500_IsReadWithItsBody_AndARedirect_IsNotFollowed()
    {
        var yes = new NoopApproval();

        var failed = await Call(yes, new { url = Url("/fail") });
        var moved  = await Call(yes, new { url = Url("/moved") });

        Assert.StartsWith("HTTP 500", failed);
        Assert.Contains("OrdersController.Create line 42", failed);
        Assert.StartsWith("HTTP 302", moved);
        Assert.Contains("http://example.com/elsewhere", moved);
        Assert.Contains("Redirect not followed", moved);
    }

    private sealed class Rules(bool allows) : IApprovalService
    {
        public bool? Forced;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            Forced = forcePrompt;
            return Task.FromResult(false);   // nothing leaves the machine in a test
        }
        public bool Allows(string toolName, string subject) => allows;
        public Task<bool> RequestScopedApprovalAsync(string toolName, string details, string subject, string grantKey, CancellationToken ct, bool forcePrompt = false)
        {
            Forced = false;   // a rule names it: the shared path decides, its grant (if any) filed under this origin only
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task AnotherAddress_IsForcedEachTime_UnlessThePersonsRuleNamesIt()
    {
        var unlisted = new Rules(allows: false);
        var trusted  = new Rules(allows: true);

        await Call(unlisted, new { url = "http://192.168.1.2:11434/api/tags" });
        await Call(trusted, new { url = "https://staging.example.com/api" });

        Assert.True(unlisted.Forced);
        Assert.False(trusted.Forced);
    }

    [Theory]
    [InlineData(@"^GET https://staging\.example\.com:443$", "GET https://staging.example.com:443", true)]
    [InlineData(@"staging\.example\.com", "GET https://staging.example.com.evil.net:443", false)]   // a whole match only
    [InlineData(@"^GET https://staging\.example\.com:443$", "POST https://staging.example.com:443", false)]
    public void ATrustedHostRule_MatchesTheWholeSubject(string pattern, string subject, bool allowed)
    {
        var policy = new PermissionPolicy(PermissionPolicy.ParseRules($"allow http_request {pattern}"));
        Assert.Equal(allowed, policy.AllowsWhole("http_request", subject));
        Assert.False(new PermissionPolicy(PermissionPolicy.ParseRules($"allow * {pattern}")).AllowsWhole("http_request", subject));
    }

    [Theory]
    [InlineData("http://LOCALHOST.:5000/a?b=c", "POST http://localhost:5000")]
    [InlineData("https://Example.COM/x", "POST https://example.com:443")]
    [InlineData("http://[::1]:8080/", "POST http://[::1]:8080")]
    public void TheSubject_IsMethodAndCanonicalOrigin_Only(string url, string subject) =>
        Assert.Equal(subject, HttpRequestTool.Subject("POST", new Uri(url)));

    [Fact]
    public async Task ANameThatLeadsElsewhere_IsNeverReachedAsLocal()
    {
        var refused = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(
            async () => await HttpRequestTool.ConnectLoopbackAsync("192.0.2.10", 80, default));   // a documentation address
        Assert.Contains("loopback", refused.Message);
    }

    // ── The card as validated (pari 94: HttpLocalFirst, HttpElsewhere) ──

    private static ApprovalCardModel CardFor(string subject, bool forced) =>
        ApprovalCard.Build(new ApprovalPrompt("http_request", "POST http://x/a\n\n{}", subject, null, "message", Forced: forced), root: null);

    [Fact]
    public void TheLocalCard_OffersAlwaysForThatAddressOnly_AndSaysSo()
    {
        var card = CardFor("POST http://localhost:5000", forced: false);

        Assert.True(card.OffersAlways);
        Assert.False(card.Outbound);
        Assert.Contains("localhost:5000", card.AlwaysLabel);
        Assert.Contains("localhost:5000", card.Subject);
        Assert.NotEmpty(card.Note);
        Assert.Contains(card.Preview, l => l.Text == "{}");   // the request is on the card, body included
    }

    [Fact]
    public void TheCardForAnotherMachine_IsAWarning_WithoutAlways_AndNamesTheWholeRule()
    {
        var card = CardFor("POST https://staging.shop.example:443", forced: true);

        Assert.True(card.Outbound);
        Assert.False(card.OffersAlways);
        Assert.NotEmpty(card.AllowLabel);
        Assert.NotEmpty(card.Warning);
        Assert.Contains(@"allow http_request ^POST https://staging\.shop\.example:443$", card.Note);
    }

    [Theory]
    [InlineData("allow http_request staging\\.example\\.com\nallow http_request ^GET https://staging\\.example\\.com:443$")]   // a looser allow first
    [InlineData("deny http_request staging\nallow http_request ^GET https://staging\\.example\\.com:443$")]                      // a deny first
    public void TheFirstRuleThatMatches_Decides_NeverOneFurtherDown(string rules) =>
        Assert.False(new PermissionPolicy(PermissionPolicy.ParseRules(rules)).AllowsWhole("http_request", "GET https://staging.example.com:443"));

    [Fact]
    public async Task ABodyLeavingTheMachine_LongerThanItsCardShows_IsNotSent()
    {
        var approval = new Rules(allows: true);
        var said = await Call(approval, new { url = "https://staging.example.com/api", method = "POST", body = new string('a', HttpRequestTool.MaxCardBody + 1) });
        Assert.Contains("at most", said);
        Assert.Null(approval.Forced);   // refused before any card
    }

    [Theory]
    [InlineData("HTTP 500 Internal Server Error\nContent-Type: text/plain", "500", true)]
    [InlineData("Note: 'timeout_seconds' was 999; this tool accepts 1-120, so the report below uses timeout_seconds=120.\n\nHTTP 201 Created\n", "201", false)]
    public void TheStepLine_ShowsTheStatus(string output, string code, bool failed) =>
        Assert.Equal((code, failed), Inferpal.Services.Presentation.RunSummary.StepStatus("http_request", output));

    [Fact]
    public void TheStepLine_ShowsTheStatus_UnderTwoNotes()
    {
        const string twoNotes = "Note: 'timeout_seconds' was 999.\n\nNote: 'wait_seconds' was 999.\n\nHTTP 404 Not Found\n";
        Assert.Equal(("404", true), Inferpal.Services.Presentation.RunSummary.StepStatus("http_request", twoNotes));
    }

    /// <summary>An Agent Client Protocol client puts its default first: for data leaving the machine, that is Deny; and the
    /// buttons say what the card says.</summary>
    [Fact]
    public void AnAcpClient_IsOfferedDenyFirst_OnAnOutboundCard_WithTheCardsWords()
    {
        static (string Id, string Name)[] Options(object[] options) =>
            [.. options.Select(o => JsonSerializer.SerializeToElement(o)).Select(e => (e.GetProperty("optionId").GetString()!, e.GetProperty("name").GetString()!))];

        var outbound = Options(Inferpal.Host.Acp.AcpSession.Options(null, offersAlways: false, allowLabel: "Send once", outbound: true));
        var local    = Options(Inferpal.Host.Acp.AcpSession.Options(null, offersAlways: true, alwaysLabel: "Always for localhost:5000"));

        Assert.Equal("reject_once", outbound[0].Id);
        Assert.Contains(("allow_once", "Send once"), outbound);
        Assert.Contains(("allow_always", "Always for localhost:5000"), local);
        Assert.Equal("allow_once", local[0].Id);
    }

    [Fact]
    public void TheStepLine_NamesTheMethodBeforeTheAddress() =>
        Assert.Equal("POST http://localhost:5000/api/orders",
            Inferpal.Services.Presentation.RunSummary.Subject("{\"url\":\"http://localhost:5000/api/orders\",\"method\":\"post\"}"));

    [Theory]
    [InlineData("Host")]
    [InlineData("Upgrade")]
    [InlineData("Proxy-Authorization")]
    public async Task AHeaderThatReroutes_IsRefusedBeforeTheCard(string header)
    {
        var card = new Card(ApprovalDecision.Once);
        var said = await Call(card, new { url = Url("/"), headers = new Dictionary<string, string> { [header] = "x" } });
        Assert.Contains("is not one a request may set", said);
        Assert.Empty(card.Shown);
    }

    [Fact]
    public async Task TheUnspecifiedAddress_IsRefused_WithTheOneToUse()
    {
        var said = await Call(new NoopApproval(), new { url = $"http://0.0.0.0:{_port}/" });
        Assert.Contains("127.0.0.1", said);
        Assert.Empty(_received);
    }

    [Fact]
    public async Task NothingListening_IsSaidWithTheWayToWait()
    {
        var said = await Call(new NoopApproval(), new { url = $"http://127.0.0.1:{FreePort()}/" });
        Assert.Contains("Nothing listens", said);
        Assert.Contains("wait_seconds", said);
    }
}
