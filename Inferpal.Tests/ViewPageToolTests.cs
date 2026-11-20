using System.Net.Http;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Governance;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>view_page</c> (pari 95): what it refuses before any browser starts, the card it shares with <c>http_request</c>, and —
/// the fiche's gate C, played on each system the CI runs — a real capture: the width asked, a component wider than a phone
/// off a phone's screen, and nothing loaded from the Internet.
/// </summary>
// Switches Strings.OverrideCulture (process-wide) in one test: serialized collection.
[Collection(CultureSerialCollection.Name)]
public sealed class ViewPageToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-viewpage").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a browser still closing holds a file: temp is swept */ }
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

    private static readonly CaptureBrowser NeverRun = new("Test", "/nonexistent/browser", CaptureEngine.Chromium);

    private Task<string> Call(object args, IApprovalService? approval = null, ToolImageInbox? images = null,
                              Func<IReadOnlyList<CaptureBrowser>>? browsers = null) =>
        new ViewPageTool(approval ?? new NoopApproval(), () => _root, images ?? new ToolImageInbox(), browsers ?? (() => [NeverRun]))
            .ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    [Theory]
    [InlineData("https://example.com/cart", "is not this machine")]
    [InlineData("ftp://localhost/page", "is not an http or https URL")]
    [InlineData("http://shop.localhost:5173/", "is not this machine")]   // a browser would ask the system's resolver
    [InlineData("http://127.0.0.1@evil.example/", "is not this machine")]
    public async Task APageOfAnotherMachine_IsRefused_BeforeAnyCardOrBrowser(string url, string said)
    {
        var card = new Card(ApprovalDecision.Once);
        Assert.Contains(said, await Call(new { url }, card));
        Assert.Empty(card.Shown);
    }

    [Fact]
    public async Task UrlAndPath_OneOfThem()
    {
        Assert.Contains("either url", await Call(new { }));
        Assert.Contains("either url", await Call(new { url = "http://localhost:5000", path = "a.html" }));
    }

    [Fact]
    public async Task AFileThatIsNoPage_IsSentToReadFile()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "x");
        Assert.Contains("read it with read_file", await Call(new { path = "notes.txt" }));
    }

    [Fact]
    public async Task NoBrowser_SaysWhatToInstall_AndAsksNothing()
    {
        var card = new Card(ApprovalDecision.Once);
        var said = await Call(new { url = "http://localhost:5173/" }, card, browsers: () => []);
        Assert.Contains("install Firefox or Google Chrome", said);
        Assert.Empty(card.Shown);
    }

    /// <summary>The card is read by the person: in their language, like every other card (it was English in all three editors).</summary>
    [Fact]
    public async Task APagesCard_IsWrittenInThePersonsLanguage()
    {
        var previous = Inferpal.Localization.Strings.OverrideCulture?.Name;
        try
        {
            Inferpal.Localization.Strings.ApplyLanguage("fr");
            var card = new Card(ApprovalDecision.Deny);
            await Call(new { url = "http://localhost:5173/" }, card);
            Assert.Contains("navigateur coupé du réseau", Assert.Single(card.Shown).Details);
        }
        finally { Inferpal.Localization.Strings.ApplyLanguage(previous); }
    }

    /// <summary>The agent's own work asks nothing, as for interact_page: a page an app it started serves, a file of the
    /// workspace. The browser search follows the decision: no card, a capture tried.</summary>
    [Fact]
    public async Task ThePageOfAnAppTheAgentStarted_AndAWorkspaceFile_AskNothing()
    {
        File.WriteAllText(Path.Combine(_root, "page.html"), "<p>x</p>");
        var card = new Card(ApprovalDecision.Deny);
        var served = new ViewPageTool(card, () => _root, new ToolImageInbox(), () => [NeverRun], (_, _) => Task.FromResult(true));
        // The browser that does not exist fails the capture: reaching it is what says no card stopped the call.
        await Record.ExceptionAsync(() => served.ExecuteAsync(JsonSerializer.SerializeToElement(new { url = "http://localhost:5173/" }), default));
        await Record.ExceptionAsync(() => Call(new { path = "page.html" }, card));
        Assert.Empty(card.Shown);
        // Reference arm: an address no app of the agent's serves is asked.
        await Call(new { url = "http://localhost:5173/" }, card);
        Assert.Single(card.Shown);
    }

    [Fact]
    public async Task AServedPage_AsksWithHttpRequestsSubject_SoOneAgreementCoversBoth()
    {
        var card = new Card(ApprovalDecision.Deny);
        var said = await Call(new { url = "http://localhost:5173/cart?x=1" }, card);

        Assert.Equal(Inferpal.Services.Execution.ToolFailure.CancelledByUser, said);
        var asked = Assert.Single(card.Shown);
        // Asked as http_request: its rules and its "Always" for this address hold for both tools.
        Assert.Equal("http_request", asked.ToolName);
        Assert.Equal("GET http://localhost:5173", asked.Subject);
        // The card is http_request's own for this machine: "Always" for that address only.
        var model = Inferpal.Services.Presentation.ApprovalCard.Build(asked, _root);
        Assert.Contains("localhost:5173", model.AlwaysLabel);
    }

    [Fact]
    public async Task AWorkspacePage_IsServed_WithoutTheFilesTheIgnoreFilesKeep_NorAnythingOutsideTheRoot()
    {
        File.WriteAllText(Path.Combine(_root, "index.html"), "<p>shop</p>");
        File.WriteAllText(Path.Combine(_root, ".env"), "API_KEY=secret-7f3a");
        File.WriteAllText(Path.Combine(_root, ".aiignore"), ".env\n");
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, Path.GetFileName(_root) + "-outside.txt");
        File.WriteAllText(outside, "outside");
        try
        {
            using var pages = WorkspacePages.Start(_root);
            using var http = new HttpClient();
            var page = await http.GetAsync(pages.UrlOf("index.html"));
            var secret = await http.GetAsync(pages.UrlOf(".env"));
            var above = await http.GetAsync(pages.UrlOf("index.html").Replace("index.html", "..%2F" + Path.GetFileName(outside)));

            Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);        // witness: the server serves
            Assert.Contains("shop", await page.Content.ReadAsStringAsync());
            // Refused, by us or by the listener before us (a dotted path): never served.
            Assert.NotEqual(System.Net.HttpStatusCode.OK, secret.StatusCode);
            Assert.DoesNotContain("secret-7f3a", await secret.Content.ReadAsStringAsync());
            Assert.NotEqual(System.Net.HttpStatusCode.OK, above.StatusCode);
            Assert.DoesNotContain("outside", await above.Content.ReadAsStringAsync());
        }
        finally
        {
            File.Delete(outside);
        }
    }

    // ── Gate C: a real capture, on every system ───────────────────────────────────────────────────────────────────────

    // A grey block 600 px wide, then a pure green button: in a 1280 px window the button shows, in a 390 px one it is off the
    // screen. An image of the Internet that exists, which would paint a block magenta if it loaded, blue when it is refused; and
    // one from another port of this machine (OTHER), orange if it loaded, cyan when refused — the scripts ran, the network was cut.
    private const string Page = """
        <!doctype html><html><head><meta charset="utf-8"><style>
          body { margin: 0; background: #ffffff; }
          .row { display: flex; }
          .wide { width: 600px; min-width: 600px; height: 120px; background: #808080; }
          .buy { width: 160px; min-width: 160px; height: 120px; background: #00ff00; }
          #net { width: 200px; height: 60px; background: #ffffff; }
        </style></head><body>
          <div class="row"><div class="wide"></div><div class="buy"></div></div>
          <div id="net"></div>
          <div id="local" style="width:200px;height:60px;background:#ffffff"></div>
          <img src="http://127.0.0.1:OTHER/probe.png" style="width:1px;height:1px"
               onload="document.getElementById('local').style.background='#ff8800'"
               onerror="document.getElementById('local').style.background='#00ffff'">
          <img src="https://www.google.com/favicon.ico" style="width:1px;height:1px"
               onload="document.getElementById('net').style.background='#ff00ff'"
               onerror="document.getElementById('net').style.background='#0000ff'">
        </body></html>
        """;

    private static int Count(StbImageSharp.ImageResult image, byte r, byte g, byte b)
    {
        var n = 0;
        for (var i = 0; i + 3 < image.Data.Length; i += 4)
            if (Math.Abs(image.Data[i] - r) < 40 && Math.Abs(image.Data[i + 1] - g) < 40 && Math.Abs(image.Data[i + 2] - b) < 40) n++;
        return n;
    }

    [Fact]
    public async Task ARealCapture_ByEveryBrowserHere_HasTheWidthAsked_ShowsWhatAPhoneCuts_AndReachesNothingOutside()
    {
        var browsers = PageCapture.FindAll().ToList();
        // Every CI runner has one (WebKit on macOS, Edge on Windows, Chrome and Firefox on Ubuntu): a machine without any has
        // nothing to measure here, which is no green.
        if (browsers.Count == 0)
        {
            Assert.True(Environment.GetEnvironmentVariable("CI") is null, "the CI runner has no browser to capture with");
            return;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) Assert.Equal(CaptureEngine.WebKit, browsers[0].Engine);
        // The system's own browser (the first) must capture; another one that does not even start here — Edge on a Linux
        // whose sandbox helper is not set up — is the machine's, and the product hands over to the next. Whatever captures
        // must capture right.
        for (var i = 0; i < browsers.Count; i++) await CapturesAsExpected(browsers[i], mustStart: i == 0);
    }

    private async Task CapturesAsExpected(CaptureBrowser browser, bool mustStart)
    {
        // Another service of this machine, which the page must not reach: it counts what it is asked.
        var other = new System.Net.HttpListener();
        var otherPort = FreePort();
        other.Prefixes.Add($"http://127.0.0.1:{otherPort}/");
        other.Start();
        var asked = 0;
        _ = Task.Run(async () =>
        {
            while (other.IsListening)
            {
                try { var c = await other.GetContextAsync(); Interlocked.Increment(ref asked); c.Response.StatusCode = 404; c.Response.Close(); }
                catch { return; }
            }
        });
        try
        {
            File.WriteAllText(Path.Combine(_root, "shop.html"), Page.Replace("OTHER", otherPort.ToString()));
            using var pages = WorkspacePages.Start(_root);
            var url = pages.UrlOf("shop.html");
            await CapturesAsExpected(browser, url, mustStart);
            Assert.Equal(0, Volatile.Read(ref asked));                 // the other port was never reached
        }
        finally
        {
            try { other.Stop(); other.Close(); } catch { /* teardown never throws (TeardownDoesNotThrowTests) */ }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task CapturesAsExpected(CaptureBrowser browser, string url, bool mustStart)
    {
        var (wide, whyWide, _) = await PageCapture.CaptureAsync(browser, url, 1280, default);
        var (narrow, whyNarrow, _) = await PageCapture.CaptureAsync(browser, url, 390, default);
        if (!mustStart && wide is null && narrow is null) return;
        Assert.True(wide is not null, $"{browser.Name} at 1280: {whyWide}");
        Assert.True(narrow is not null, $"{browser.Name} at 390: {whyNarrow}");
        var w = StbImageSharp.ImageResult.FromMemory(wide, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        var n = StbImageSharp.ImageResult.FromMemory(narrow, StbImageSharp.ColorComponents.RedGreenBlueAlpha);

        Assert.Equal(1280, w.Width);
        Assert.Equal(390, n.Width);
        Assert.True(Count(w, 0, 255, 0) > 1000, $"{browser.Name}: the green button shows at 1280 px");
        Assert.Equal(0, Count(n, 0, 255, 0));                      // off a phone's screen
        Assert.True(Count(w, 128, 128, 128) > 1000, $"{browser.Name}: the page rendered");
        Assert.Equal(0, Count(w, 255, 0, 255));                    // nothing of the Internet loaded
        Assert.True(Count(w, 0, 0, 255) > 1000, $"{browser.Name}: the page's script saw the Internet refused");
        Assert.Equal(0, Count(w, 255, 136, 0));                    // nothing of another port of this machine loaded
        Assert.True(Count(w, 0, 255, 255) > 1000, $"{browser.Name}: the page's script saw the other port refused");
    }

    [Fact]
    public async Task TheTool_PutsOnePicturePerWidth_ForTheLoopToShow()
    {
        if (PageCapture.Find() is null) return;   // measured by the gate C test above, which says why
        File.WriteAllText(Path.Combine(_root, "shop.html"), Page);
        var images = new ToolImageInbox();

        var said = await Call(new { path = "shop.html" }, images: images, browsers: () => [.. PageCapture.FindAll()]);

        Assert.Contains("captured by", said);
        Assert.Contains("1280×900 and 390×844", said);
        Assert.Equal(2, images.Take().Count);
    }
}
