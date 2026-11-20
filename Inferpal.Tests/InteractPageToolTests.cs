using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Documents;
using Inferpal.Services.Governance;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>interact_page</c> (pari 126): what it refuses before any browser starts, and — played in a real Chromium where one
/// starts, undecided elsewhere — steps as a person plays them: a click by its text, typing, a choice in a list, a page's
/// <c>confirm()</c> answered, an element covered, the page kept for the next call of the run and fresh in another run —
/// and the one card it shows: an address no app of the agent's serves, asked once.
/// </summary>
public sealed class InteractPageToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-interactpage").FullName;
    private readonly List<InteractPageTool> _tools = [];

    public void Dispose()
    {
        foreach (var tool in _tools) tool.Dispose();
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* a browser still closing holds a file: temp is swept */ }
    }

    /// <summary>Refuses every subject while <see cref="Deny"/> is set: a rule the person writes while the page is open.</summary>
    private sealed class Denying(bool deny = true) : IApprovalService
    {
        public bool Deny = deny;
        public readonly List<string> Subjects = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
        public bool Refuses(string toolName, string subject)
        {
            Subjects.Add(subject);
            return Deny;
        }
    }

    private static readonly CaptureBrowser NeverRun = new("Test", "/nonexistent/browser", CaptureEngine.Chromium);

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

    private InteractPageTool Tool(Func<string?> runId, ToolImageInbox? images = null, IApprovalService? approval = null,
                                  Func<IReadOnlyList<CaptureBrowser>>? browsers = null, bool servedByAgent = true,
                                  FileHistoryService? history = null)
    {
        var tool = new InteractPageTool(approval ?? new NoopApproval(), () => _root, images ?? new ToolImageInbox(), runId,
                                        (_, _) => Task.FromResult(servedByAgent), browsers, history ?? new FileHistoryService());
        _tools.Add(tool);
        return tool;
    }

    private static Task<string> Call(InteractPageTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), default);

    private Task<string> CallOnce(object args, IApprovalService? approval = null) =>
        Call(Tool(() => "run-1", approval: approval, browsers: () => [NeverRun]), args);

    [Theory]
    [InlineData("https://example.com/cart", "is not this machine")]
    [InlineData("http://shop.localhost:5173/", "is not this machine")]
    [InlineData("file:///C:/secret.html", "is not an http or https URL")]
    public async Task APageOfAnotherMachine_IsRefused_BeforeAnyBrowser(string url, string said) =>
        Assert.Contains(said, await CallOnce(new { url, steps = Array.Empty<object>() }));

    [Fact]
    public async Task AnUnknownAction_IsRefusedBeforeAnyStepIsPlayed()
    {
        var said = await CallOnce(new { url = "http://localhost:5173/", steps = new object[] { new { action = "click", text = "Add" }, new { action = "tap" } } });
        Assert.Contains("step 2: unknown action 'tap'", said);
        Assert.DoesNotContain("browser", said);
    }

    [Theory]
    [InlineData("click", "a selector or a text")]
    [InlineData("type", "the field's selector")]
    [InlineData("select", "the list's selector")]
    [InlineData("press", "a key")]
    [InlineData("goto", "a url")]
    public async Task AStepWithoutWhatItActsOn_IsRefused(string action, string needs) =>
        Assert.Contains($"step 1 ({action}) needs {needs}", await CallOnce(new { url = "http://localhost:5173/", steps = new[] { new { action } } }));

    [Fact]
    public async Task AKeyItDoesNotKnow_IsNamedWithTheOnesItDoes()
    {
        var said = await CallOnce(new { url = "http://localhost:5173/", steps = new[] { new { action = "press", key = "F13" } } });
        Assert.Contains("unknown key 'F13'", said);
        Assert.Contains("Enter", said);
    }

    [Fact]
    public async Task WithoutAPageOpenInTheRun_ItAsksForOne() =>
        Assert.Contains("no page is open in this run", await CallOnce(new { steps = new[] { new { action = "click", text = "Add" } } }));

    [Fact]
    public async Task WithoutAChromiumBrowser_ItNamesWhatToInstall()
    {
        var said = await Call(Tool(() => "run-1", browsers: () => []), new { url = "http://localhost:5173/", steps = Array.Empty<object>() });
        Assert.Contains("Google Chrome, Microsoft Edge or Chromium", said);
    }

    [Fact]
    public async Task ThePersonsDenyRule_StillRefuses()
    {
        var said = await CallOnce(new { url = "http://localhost:5173/", steps = Array.Empty<object>() }, new Denying());
        Assert.Equal(ToolFailure.CancelledByUser, said);
    }

    [Fact]
    public async Task AnAddressNoAppOfTheAgentsServes_IsAskedOnce_ItsAlwaysForThatAddress()
    {
        var card = new Card(ApprovalDecision.Always);
        var tool = Tool(() => "run-1", approval: card, browsers: () => [], servedByAgent: false);
        await Call(tool, new { url = "http://localhost:8888/tree", steps = Array.Empty<object>() });
        var shown = Assert.Single(card.Shown);
        Assert.Equal("interact_page", shown.ToolName);
        Assert.Equal("GET http://localhost:8888", shown.Subject);
        Assert.Contains("localhost:8888", shown.Details);

        var drawn = ApprovalCard.Build(shown, _root);
        Assert.Equal(Strings.ApprovalActInPage, drawn.Title);
        Assert.Equal(Strings.ApprovalAlwaysFor("localhost:8888"), drawn.AlwaysLabel);

        // "Always" holds for that address, and only for it.
        await Call(tool, new { url = "http://localhost:8888/lab", steps = Array.Empty<object>() });
        Assert.Single(card.Shown);
        await Call(tool, new { url = "http://localhost:8889/", steps = Array.Empty<object>() });
        Assert.Equal(2, card.Shown.Count);
    }

    [Fact]
    public async Task APageTheAgentsAppServed_IsCheckedAgainAtEveryCall()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        using var pages = WorkspacePages.Start(_root);
        var url = pages.UrlOf("shop.html");
        var served = true;
        var card = new Card(ApprovalDecision.Deny);
        var tool = new InteractPageTool(card, () => _root, new ToolImageInbox(), () => "run-1", (_, _) => Task.FromResult(served));
        _tools.Add(tool);
        var first = await Call(tool, new { url, steps = Array.Empty<object>() });
        if (first.StartsWith("No browser", StringComparison.Ordinal)) return;   // undecided: no Chromium browser starts here
        Assert.Empty(card.Shown);
        Assert.Contains("Continued", await Call(tool, new { steps = Array.Empty<object>() }));   // reference arm

        served = false;   // the agent's app stopped; another one listens there now
        Assert.Contains("no longer serves", await Call(tool, new { steps = Array.Empty<object>() }));
        Assert.Equal(ToolFailure.CancelledByUser, await Call(tool, new { url, steps = Array.Empty<object>() }));
        Assert.Single(card.Shown);
    }

    [Fact]
    public async Task ACardDenied_OpensNothing()
    {
        var said = await Call(Tool(() => "run-1", approval: new Card(ApprovalDecision.Deny), browsers: () => throw new InvalidOperationException("no browser is looked for"),
                                   servedByAgent: false),
                              new { url = "http://localhost:8888/", steps = Array.Empty<object>() });
        Assert.Equal(ToolFailure.CancelledByUser, said);
    }

    [Fact]
    public async Task TheAppTheAgentStarted_AndTheWorkspacesFiles_AskNothing()
    {
        var card = new Card(ApprovalDecision.Deny);
        await Call(Tool(() => "run-1", approval: card, browsers: () => [], servedByAgent: true),
                   new { url = "http://localhost:5173/", steps = Array.Empty<object>() });
        File.WriteAllText(Path.Combine(_root, "page.html"), "<p>hi</p>");
        await Call(Tool(() => "run-1", approval: card, browsers: () => [], servedByAgent: false),
                   new { path = "page.html", steps = Array.Empty<object>() });
        Assert.Empty(card.Shown);
    }

    [Theory]
    [InlineData("notes.txt", "is not a .png file")]
    [InlineData("../outside.png", "")]
    [InlineData("docs", "is a folder")]
    public async Task ASaveToItCannotWrite_IsRefusedBeforeAnyStep(string saveTo, string said)
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        var result = await Call(Tool(() => "run-1", browsers: () => throw new InvalidOperationException("no browser is looked for")),
                                new { url = "http://localhost:5173/", save_to = saveTo, steps = new[] { new { action = "click", text = "Add" } } });
        Assert.StartsWith(said.Length == 0 ? "Error: save_to:" : "Error: save_to", result);
        Assert.Contains(said, result);
    }

    /// <summary>The person's rules on writing, before any click: a capture they refuse to have written is no reason to act.</summary>
    private sealed class RefusesWrites : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
        public bool Refuses(string toolName, string subject) => toolName == "write_file";
    }

    [Fact]
    public async Task ASaveToTheRulesRefuse_IsRefusedBeforeAnyStep()
    {
        var said = await Call(Tool(() => "run-1", approval: new RefusesWrites(), browsers: () => throw new InvalidOperationException("no browser is looked for")),
                              new { url = "http://localhost:5173/", save_to = "docs/cart.png", steps = new[] { new { action = "click", text = "Add" } } });
        Assert.StartsWith("Error: save_to: the person's rules refuse writing", said);
    }

    [Fact]
    public async Task AWaitPastItsBound_IsClampedAndSaid()
    {
        var said = await Call(Tool(() => "run-1", browsers: () => []),
                              new { url = "http://localhost:5173/", steps = new[] { new { action = "wait_for", text = "Done", timeout_ms = 60_000 } } });
        Assert.StartsWith("Step 1:", said);
        Assert.Contains("10000", said);
    }

    // ── In a real browser ──────────────────────────────────────────────────────────────────────────────────────────────

    private const string Shop = """
        <!doctype html><html><head><title>Tea shop</title><style>
          body { margin: 0; font: 16px sans-serif } header { background: #2f5d50; color: #fff; padding: 12px }
          #overlay { position: fixed; left: 0; top: 300px; width: 400px; height: 60px; background: rgba(0,0,0,.4) }
          #under { position: absolute; left: 20px; top: 310px }
        </style></head><body>
        <header>Tea shop — <span id="count">Cart: 0</span></header>
        <p><span>Green tea</span> <button class="add" id="add-green">Add</button></p>
        <p><span>Black tea</span> <button class="add" id="add-black">Add</button></p>
        <p><label for="email">Email</label> <input id="email" type="email"> <input id="total" readonly value="12.00"> <input id="code" oninput="this.value = ''"></p>
        <p><label for="category">Category</label> <select id="category"><option value="">All</option><option value="t">Tea</option><option value="c">Coffee</option></select></p>
        <p><button id="delete">Delete</button> <span id="status"></span>
           <button id="break" onclick="window.getComputedStyle = undefined">Break</button></p>
        <button id="under">Hidden</button><div id="overlay"></div>
        <script>
          let n = Number(sessionStorage.getItem("n") || 0);
          const show = () => document.getElementById("count").textContent = "Cart: " + n;
          show();
          document.querySelectorAll(".add").forEach(b => b.onclick = () => { n++; sessionStorage.setItem("n", n); show(); });
          document.getElementById("category").onchange = e => document.getElementById("status").textContent = "Category " + e.target.value;
          document.getElementById("overlay").onclick = () => { n += 100; show(); };
          document.getElementById("delete").onclick = () => { if (confirm("Delete the cart?")) document.getElementById("status").textContent = "Deleted"; };
          console.error("boom from the page");
        </script></body></html>
        """;

    /// <summary>A page whose button keeps its script busy for three seconds: slow, not stuck.</summary>
    private const string Busy = """
        <!doctype html><html><head><title>Busy</title></head><body>
        <button id="work" onclick="setTimeout(() => { const end = Date.now() + 3000; while (Date.now() < end) {} document.title = 'Done'; }, 0)">Work</button>
        <p id="state">Ready</p>
        </body></html>
        """;

    /// <summary>The tool's result, or <c>null</c> when no Chromium browser starts here (the test is then undecided).</summary>
    private static async Task<string?> Played(InteractPageTool tool, object args)
    {
        var said = await Call(tool, args);
        return said.StartsWith("No browser", StringComparison.Ordinal) ? null : said;
    }

    [Fact]
    public async Task Steps_ArePlayedAsAPersonPlaysThem_AndTheStateIsCaptured()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var images = new ToolImageInbox();
        var said = await Played(Tool(() => "run-1", images), new
        {
            path = "shop.html",
            steps = new object[]
            {
                new { action = "click", text = "Add" },
                new { action = "click", selector = "#add-black" },
                new { action = "type", selector = "#email", text = "marie.dupont@example.com" },
                new { action = "select", selector = "#category", value = "Tea" },
                new { action = "wait_for", text = "Category t" },
                new { action = "click", text = "Delete" },
                new { action = "wait_for", text = "Deleted" },
            },
        });
        if (said is null) return;   // undecided: no Chromium browser starts here

        Assert.Contains("1. click: done — clicked button#add-green.add (the first of 2 matching \"Add\"", said);
        Assert.Contains("2. click: done — clicked button#add-black.add", said);
        Assert.Contains("3. type: done — typed \"marie.dupont@example.com\" into input#email", said);
        Assert.Contains("4. select: done — chose \"Tea\" in select#category", said);
        Assert.Contains("the page showed a confirm \"Delete the cart?\": answered OK", said);
        Assert.Contains("7. wait_for: done — \"Deleted\" appeared", said);
        Assert.Contains("input:email \"Email\" #email [value: \"marie.dupont@example.com\"]", said);
        Assert.Contains("select \"Category\" #category [chosen: \"Tea\"]", said);
        Assert.Contains("error: boom from the page", said);
        Assert.Contains("button \"Hidden\" #under [covered by div#overlay]", said);
        Assert.Contains("(/favicon.ico)", said);
        var image = Assert.Single(images.Take());
        Assert.Equal(PageSession.Width, image.Width);
        Assert.Equal(PageSession.Height, image.Height);
    }

    /// <summary>A page whose script is busy a few seconds (a machine under load runs it as late) is slow, not looping: it is
    /// kept, with the state the calls brought it to, for the next call.</summary>
    [Fact]
    public async Task APageBusyForAFewSeconds_IsKeptForTheNextCall()
    {
        File.WriteAllText(Path.Combine(_root, "busy.html"), Busy);
        var tool = Tool(() => "run-1");
        var said = await Played(tool, new { path = "busy.html", steps = new object[] { new { action = "click", selector = "#work" } } });
        if (said is null) return;

        Assert.DoesNotContain("stopped answering", said, StringComparison.Ordinal);
        Assert.Contains("1. wait_for: done", await Call(tool, new { steps = new object[] { new { action = "wait_for", text = "Ready", timeout_ms = 1000 } } }));
    }

    [Fact]
    public async Task AClickOnACoveredElement_IsNotDone_AndTheStepsAfterItAreNotPlayed()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var tool = Tool(() => "run-1");
        var said = await Played(tool, new
        {
            path = "shop.html",
            steps = new object[] { new { action = "click", selector = "#under" }, new { action = "click", text = "Add" } },
        });
        if (said is null) return;

        Assert.Contains("1. click: NOT DONE — button#under is covered by div#overlay: a click would land on it, so none was made", said);
        Assert.Contains("Steps 2-2 not played", said);
        // Still empty: neither the second step nor the banner a click on the covered button would have hit ran.
        Assert.Contains("1. wait_for: done", await Call(tool, new { steps = new object[] { new { action = "wait_for", text = "Cart: 0", timeout_ms = 1000 } } }));
    }

    [Fact]
    public async Task EachStep_IsSaidOnTheRunsStatusLine_AsItEnds()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var said = new List<string>();
        string? report;
        using (ToolProgress.Route(said.Add))
            report = await Played(Tool(() => "run-1"), new { path = "shop.html", steps = new object[]
            {
                new { action = "click", selector = "#add-green" },
                new { action = "click", selector = "#nothing-here" },
            } });
        if (report is null) return;   // undecided: no Chromium browser starts here

        Assert.Equal(2, said.Count);
        Assert.StartsWith(Localization.Strings.ToolStepProgress("interact_page", 1, 2, ""), said[0]);
        Assert.StartsWith(Localization.Strings.ToolStepNotDone("interact_page", 2, 2, ""), said[1]);
    }

    [Fact]
    public async Task ThePage_StaysOpenForTheNextCallOfTheRun_AndAnotherRunOpensAFreshOne()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var run = "run-1";
        var tool = Tool(() => run);
        var first = await Played(tool, new { path = "shop.html", steps = new object[] { new { action = "click", selector = "#add-green" } } });
        if (first is null) return;
        Assert.Contains("A fresh browser", first);

        var next = await Call(tool, new { steps = new object[] { new { action = "click", selector = "#add-black" },
                                                                  new { action = "wait_for", text = "Cart: 2", timeout_ms = 2000 } } });
        Assert.Contains("Continued in the page the last call left.", next);
        Assert.Contains("2. wait_for: done", next);

        run = "run-2";
        var fresh = await Call(tool, new { path = "shop.html", steps = new object[] { new { action = "wait_for", text = "Cart: 0", timeout_ms = 2000 } } });
        Assert.Contains("A fresh browser", fresh);
        Assert.Contains("1. wait_for: done", fresh);
    }

    [Fact]
    public async Task AClickOnAListsEntry_ChoosesIt()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var said = await Played(Tool(() => "run-1"), new
        {
            path = "shop.html",
            steps = new object[] { new { action = "click", text = "Coffee" }, new { action = "wait_for", text = "Category c", timeout_ms = 2000 } },
        });
        if (said is null) return;
        Assert.Contains("1. click: done — chose \"Coffee\" in select#category", said);
        Assert.Contains("2. wait_for: done", said);
    }

    [Fact]
    public async Task AStepThePageRefuses_IsNotDone_AndThePageStaysOpen()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var tool = Tool(() => "run-1");
        var said = await Played(tool, new
        {
            path = "shop.html",
            steps = new object[] { new { action = "click", selector = "#add-green" }, new { action = "wait_for", selector = "button:contains(\"Add\")" } },
        });
        if (said is null) return;
        Assert.Contains("2. wait_for: NOT DONE — button:contains(\"Add\") is not a valid CSS selector", said);
        Assert.Contains("Address now: /shop.html", said);

        var next = await Call(tool, new { steps = new object[] { new { action = "type", selector = "#total", text = "0" },
                                                                  new { action = "wait_for", text = "Cart: 1", timeout_ms = 1000 } } });
        Assert.Contains("Continued in the page the last call left.", next);
        Assert.Contains("1. type: NOT DONE — input#total is not a field to type in", next);

        var reset = await Call(tool, new { steps = new object[] { new { action = "type", selector = "#code", text = "1234" } } });
        Assert.Contains("1. type: NOT DONE — input#code did not take the text: it still shows \"\"", reset);

        // A page whose own script breaks what a step relies on: the step is not done, the page is not closed.
        var broken = await Call(tool, new { steps = new object[] { new { action = "click", selector = "#break" }, new { action = "click", selector = "#add-green" } } });
        Assert.Contains("2. click: NOT DONE — the page's script failed: TypeError", broken);
        Assert.Contains("Controls: not read — the page's script failed", broken);
        Assert.Contains("Continued in the page the last call left.", await Call(tool, new { steps = Array.Empty<object>() }));
    }

    [Fact]
    public async Task ThePersonsRules_AreReadAtEveryCall_OnTheSubjectViewPageGivesThePage()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var rules = new Denying(deny: false);
        var tool = Tool(() => "run-1", approval: rules);
        var first = await Played(tool, new { path = "shop.html", steps = Array.Empty<object>() });
        if (first is null) return;
        rules.Deny = true;
        Assert.Equal(ToolFailure.CancelledByUser, await Call(tool, new { steps = new object[] { new { action = "click", text = "Add" } } }));
        Assert.Equal(Path.Combine(_root, "shop.html"), rules.Subjects[^1]);
        Assert.Equal("GET http://localhost:5173", (await SubjectOf("http://localhost:5173/cart")));
    }

    private static async Task<string> SubjectOf(string url)
    {
        var rules = new Denying();
        var tool = new InteractPageTool(rules, () => ".", new ToolImageInbox(), () => "run-1", (_, _) => Task.FromResult(true), () => []);
        await Call(tool, new { url, steps = Array.Empty<object>() });
        tool.Dispose();
        return rules.Subjects.Single();
    }

    [Fact]
    public async Task APage_ClosesWhenItsRunEnds()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        string? run = "run-1";
        var tool = Tool(() => run);
        if (await Played(tool, new { path = "shop.html", steps = Array.Empty<object>() }) is null) return;
        tool.CloseWhenDone();
        Assert.Contains("Continued", await Call(tool, new { steps = Array.Empty<object>() }));   // reference arm: same run
        run = null;
        tool.CloseWhenDone();
        run = "run-1";
        Assert.Contains("no page is open in this run", await Call(tool, new { steps = Array.Empty<object>() }));
    }

    /// <summary>The capture written into the project on the usual write card, its picture on it; the image it replaces
    /// beside it; a "No" writes nothing.</summary>
    [Fact]
    public async Task SaveTo_WritesTheCaptureOnTheWriteCard_WithItsPicture()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var card = new Card(ApprovalDecision.Once);
        var tool = Tool(() => "run-1", approval: card);
        var said = await Played(tool, new { path = "shop.html", save_to = "docs/images/cart.png",
                                            steps = new object[] { new { action = "click", selector = "#add-green" } } });
        if (said is null) return;   // undecided: no Chromium browser starts here

        var saved = Path.Combine(_root, "docs", "images", "cart.png");
        Assert.Contains("The capture is saved to docs/images/cart.png (1280×800", said);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(saved)[..4]);
        var asked = Assert.Single(card.Shown);
        Assert.Equal("write_file", asked.ToolName);
        Assert.True(asked.Diff is { Creates: true, Binary.After: not null, Binary.Before: null });
        var drawn = ApprovalCard.Build(asked, _root);
        Assert.Equal(Strings.ApprovalCreateFile, drawn.Title);
        Assert.Equal(asked.Diff!.Binary!.Label, drawn.Meta);
        Assert.Empty(drawn.Preview);

        // Again over it: the card shows the image it replaces.
        var again = await Call(tool, new { save_to = "docs/images/cart.png", steps = new object[] { new { action = "click", selector = "#add-black" } } });
        Assert.Contains("The capture is saved to docs/images/cart.png", again);
        Assert.True(card.Shown[^1].Diff is { Creates: false, Binary.Before: not null, Binary.After: not null });
        Assert.Equal(Strings.ApprovalChangeFile, ApprovalCard.Build(card.Shown[^1], _root).Title);

        // Declined: nothing written.
        var declined = Tool(() => "run-2", approval: new Card(ApprovalDecision.Deny));
        Assert.Contains("was not saved to docs/other.png: the person declined",
                        await Call(declined, new { path = "shop.html", save_to = "docs/other.png", steps = Array.Empty<object>() }));
        Assert.False(File.Exists(Path.Combine(_root, "docs", "other.png")));
    }

    /// <summary>A card that, while it waits, has the file changed under it — by the person, by a build.</summary>
    private sealed class ChangedWhileAsked(string path) : ApprovalServiceBase(InferpalConfig.LoadOwnCopy(), () => Path.GetTempPath())
    {
        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7]);
            return Task.FromResult(ApprovalDecision.Once);
        }
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct) =>
            throw new InvalidOperationException("the card path asks");
    }

    /// <summary>An existing picture is replaced only as the card showed it, and only once the run has seen it (refused
    /// before any step otherwise); a call that never captured says nothing was saved.</summary>
    [Fact]
    public async Task SaveTo_NeverReplacesAPictureUnseen_OrChangedUnderTheCard_AndSaysWhenNothingWasSaved()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var logo = Path.Combine(_root, "logo.png");
        File.WriteAllBytes(logo, [0x89, (byte)'P', (byte)'N', (byte)'G', 0, 0, 0, 0]);
        var card = new Card(ApprovalDecision.Once);
        var history = new FileHistoryService();
        history.BeginRun();   // as the agent's loop does: what the run has seen is known only inside one
        var tool = Tool(() => "run-1", approval: card, history: history);
        var unseen = await Played(tool, new { path = "shop.html", save_to = "logo.png", steps = Array.Empty<object>() });
        if (unseen is null) return;   // undecided: no Chromium browser starts here
        // Refused before any step: a call never acts for a capture it cannot save.
        Assert.Contains("Error: save_to: logo.png: that file already exists and has not been looked at in this run", unseen);
        Assert.Contains("Nothing was played", unseen);
        Assert.Empty(card.Shown);
        Assert.Equal(8, new FileInfo(logo).Length);

        // Seen in the run, then changed while the card waits: not written over.
        history.NoteRead(logo);
        var changed = Tool(() => "run-1", approval: new ChangedWhileAsked(logo), history: history);
        Assert.Contains("the file changed while the card waited",
                        await Call(changed, new { path = "shop.html", save_to = "logo.png", steps = Array.Empty<object>() }));
        Assert.Equal(7, new FileInfo(logo).Length);

        // A page that does not open captures nothing, and says so for the file it was to write.
        var nothing = await Call(Tool(() => "run-2", approval: card),
                                 new { path = "shop.html", save_to = "docs/none.png", steps = new object[] { new { action = "goto", url = "http://localhost:9/" } } });
        Assert.Contains("The capture was not saved to docs/none.png", nothing);
    }

    [Fact]
    public async Task AStepToAnotherAddress_IsRefused_TheBrowserReachesOnlyThePagesOrigin()
    {
        File.WriteAllText(Path.Combine(_root, "shop.html"), Shop);
        var said = await Played(Tool(() => "run-1"), new { path = "shop.html", steps = new object[] { new { action = "goto", url = "http://localhost:9/" } } });
        if (said is null) return;
        Assert.Contains("1. goto: NOT DONE — refused: http://localhost:9/ is not this page's address", said);
    }
}
