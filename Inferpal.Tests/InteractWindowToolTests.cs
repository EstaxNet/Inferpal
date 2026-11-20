using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Documents;
using Inferpal.Services.Shell;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>interact_window</c> acts in the app the agent launched (pari 126), on every system through the same tool: a call wrong
/// in its steps or its save_to plays nothing, the person's rules are read, the steps stop at the first one not done and a
/// window not in the state asked for is never saved; a file dialog is named by its title alone. The real windows:
/// <c>InteractWindowWindowsTests</c>.
/// </summary>
public sealed class InteractWindowToolTests : IDisposable
{
    private readonly BackgroundShellRegistry _jobs = new();
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-interactwindow").FullName;

    public void Dispose()
    {
        foreach (var (id, _, _) in _jobs.List()) _jobs.Stop(id);
        _jobs.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string LongJob() => _jobs.Start(OperatingSystem.IsWindows() ? "Start-Sleep -Seconds 25" : "sleep 25", "app", Path.GetTempPath());

    /// <summary>Refuses what <see cref="Deny"/> names; every card answered yes.</summary>
    private sealed class Rules(string? deny = null) : IApprovalService
    {
        public readonly List<(string Tool, string Subject)> Asked = [];
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false) => Task.FromResult(true);
        public bool Refuses(string toolName, string subject)
        {
            Asked.Add((toolName, subject));
            return deny is not null && toolName == deny;
        }
    }

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    private InteractWindowTool Tool(Func<IReadOnlyList<WindowStep>, WindowActing> play, IApprovalService? approval = null,
                                    WindowReading? reading = null, FileHistoryService? history = null) =>
        new(_jobs, new ToolImageInbox(), approval ?? new Rules(), () => _root, history ?? new FileHistoryService(),
            (_, steps, _, _) => Task.FromResult(play(steps)),
            (_, _, _) => Task.FromResult(reading ?? new WindowReading([new CapturedWindow("Order form", 7, Png, 1, 1, ["Window \"Order form\""])], null, [])));

    private static WindowActing NeverPlayed(IReadOnlyList<WindowStep> _) => throw new InvalidOperationException("never played");

    private static Task<string> Call(InteractWindowTool tool, object args) =>
        tool.ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);

    [Theory]
    [InlineData("""{"action":"press","control":"x"}""", "unknown action 'press'")]
    [InlineData("""{"action":"click"}""", "needs a control")]
    [InlineData("""{"action":"type","control":"#nameBox"}""", "needs the text to type")]
    [InlineData("""{"action":"select","control":"sizeList"}""", "needs the entry to choose")]
    [InlineData("""{"action":"click","control":"\" \""}""", "needs a control")]   // spaces alone name every unnamed control
    public async Task AStepWrittenWrong_PlaysNothing(string step, string said)
    {
        var job = LongJob();
        var tool = Tool(NeverPlayed);
        var args = JsonDocument.Parse($$"""{"job":"{{job}}","steps":[{{step}}]}""").RootElement;
        Assert.Contains(said, await tool.ExecuteAsync(args, CancellationToken.None));
    }

    [Fact]
    public async Task ASaveToItCannotWrite_OrTheRulesRefuse_PlaysNothing()
    {
        var job = LongJob();
        var click = new object[] { new { action = "click", control = "Order" } };
        Assert.Contains("PNG", await Call(Tool(NeverPlayed), new { job, save_to = "docs/order.jpg", steps = click }));
        Assert.Contains("refuse writing", await Call(Tool(NeverPlayed, new Rules(deny: "write_file")),
                                                     new { job, save_to = "docs/order.png", steps = click }));
        // The person's rules on the tool itself, read on the job's command.
        var rules = new Rules(deny: "interact_window");
        Assert.Equal(ToolFailure.CancelledByUser, await Call(Tool(NeverPlayed, rules), new { job, steps = click }));
        Assert.Contains(("interact_window", "app"), rules.Asked);   // the command run_command was given for the job
    }

    [Fact]
    public async Task TheControlIsTakenAsTheTreeWritesIt_AndEachStepIsSaid()
    {
        var job = LongJob();
        IReadOnlyList<WindowStep>? played = null;
        var tool = Tool(steps => { played = steps; return new([new(true, "typed into nameBox"), new(true, "clicked Order")], null); });

        var said = await Call(tool, new { job, steps = new object[]
        {
            new { action = "type", control = "#nameBox", text = "Ada" },
            new { action = "click", control = "\"Order\"" },
        } });

        Assert.Equal(["nameBox", "Order"], played!.Select(s => s.Target));
        Assert.Contains("1. type nameBox: done — typed into nameBox", said);
        Assert.Contains("2. click Order: done — clicked Order", said);
        Assert.Contains("## Window \"Order form\" — process 7", said);   // what the steps left
    }

    [Fact]
    public async Task AStepNotDone_StopsTheSteps_AndTheWindowIsNotSaved()
    {
        var job = LongJob();
        var history = new FileHistoryService();
        history.BeginRun();
        var tool = Tool(_ => new([new(true, "typed into nameBox"), new(false, "orderButton is disabled")], null), history: history);

        var said = await Call(tool, new { job, save_to = "docs/order.png", steps = new object[]
        {
            new { action = "type", control = "nameBox", text = "Ada" },
            new { action = "click", control = "orderButton" },
            new { action = "wait_for", control = "Ordered" },
        } });

        Assert.Contains("2. click orderButton: NOT DONE — orderButton is disabled", said);
        Assert.Contains("Steps 3-3 not played", said);
        Assert.Contains(CaptureSaving.NotSavedPrefix + "docs/order.png: the steps did not all report done", said);
        Assert.False(File.Exists(Path.Combine(_root, "docs", "order.png")));
    }

    [Fact]
    public async Task AllStepsDone_TheWindowsPictureIsSaved_OnTheWriteCard()
    {
        var job = LongJob();
        var history = new FileHistoryService();
        history.BeginRun();
        var said = await Call(Tool(_ => new([new(true, "clicked Order")], null), history: history),
                              new { job, save_to = "docs/order.png", steps = new object[] { new { action = "click", control = "Order" } } });

        Assert.Contains("Saving the picture of \"Order form\": " + CaptureSaving.SavedPrefix, said);
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(_root, "docs", "order.png")));
    }

    [Fact]
    public async Task StepsStoppedWithoutAWord_SayWhatRan_AndThatTheNextMayHave_ThenLookAtTheWindow()
    {
        var job = LongJob();
        var history = new FileHistoryService();
        history.BeginRun();
        // The script killed at its budget after its first step reported: the second may have run.
        var stopped = WindowActions.Read(new ChildProcessResult(-1, "PLAY 1\nSTEP {\"ok\":true,\"detail\":\"typed into Edit #nameBox\"}\nPLAY 2\n", "", TimedOut: true));
        Assert.True(stopped.MayHavePlayed);
        var said = await Call(Tool(_ => stopped, history: history), new { job, save_to = "docs/order.png", steps = new object[]
        {
            new { action = "type", control = "nameBox", text = "Ada" },
            new { action = "click", control = "Order" },
        } });

        Assert.Contains("1. type nameBox: done — typed into Edit #nameBox", said);
        Assert.Contains("The steps stopped after step 1: the steps did not finish in time and were stopped. Step 2 may have been played", said);
        Assert.DoesNotContain("no step was played", said);
        Assert.Contains("## Window \"Order form\"", said);   // what the window shows now
        Assert.Contains(CaptureSaving.NotSavedPrefix + "docs/order.png: the steps did not all report done", said);
    }

    [Theory]
    [InlineData("PLAY 1\nSTEP {\"ok\":true,\"detail\":\"a\"}\nPLAY 2\nSTEP {\"ok\":false,\"detail\":\"b\"}\nEND\n", 2, null, false)]
    [InlineData("PLAY 1\nSTEP {\"ok\":true,\"detail\":\"a\"}\nREFUSED macOS has not let it\n", 1, "macOS has not let it", false)]
    [InlineData("REFUSED the app shows no window after 20 s\n", 0, "the app shows no window after 20 s", false)]
    // A step that began and did not end may have run; stopped between two steps, none is pending.
    [InlineData("PLAY 1\nSTEP {\"ok\":true,\"detail\":\"a\"}\nPLAY 2\n", 1, "the steps stopped before their end", true)]
    [InlineData("PLAY 1\nSTEP {\"ok\":true,\"detail\":\"a\"}\n", 1, "the steps stopped before their end", false)]
    public void TheReport_IsReadAsTheScriptsWriteIt(string stdout, int steps, string? refusal, bool mayHavePlayed)
    {
        var read = WindowActions.Read(new ChildProcessResult(0, stdout, "", TimedOut: false));
        Assert.Equal(steps, read.Steps.Count);
        Assert.Equal(refusal, read.Refusal);
        Assert.Equal(mayHavePlayed, read.MayHavePlayed);
    }

    [Fact]
    public async Task TheAppsLargestWindow_IsSaved_NeverAMessageBoxOverIt()
    {
        var job = LongJob();
        var history = new FileHistoryService();
        history.BeginRun();
        var box = new CapturedWindow("Order placed", 7, [9, 9, 9], 200, 100, ["Window"]);
        var main = new CapturedWindow("Order form", 7, Png, 800, 600, ["Window"]);
        var said = await Call(Tool(_ => new([new(true, "clicked Order")], null), history: history,
                                   reading: new WindowReading([box, main], null, [])),
                              new { job, save_to = "docs/order.png", steps = new object[] { new { action = "click", control = "Order" } } });

        Assert.Contains("Saving the picture of \"Order form\"", said);
        Assert.Equal(Png, File.ReadAllBytes(Path.Combine(_root, "docs", "order.png")));
    }

    [Fact]
    public async Task ASaveOverAPictureTheRunHasNotSeen_PlaysNothing()
    {
        var job = LongJob();
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        File.WriteAllBytes(Path.Combine(_root, "docs", "order.png"), Png);
        var history = new FileHistoryService();
        history.BeginRun();
        var said = await Call(Tool(NeverPlayed, history: history),
                              new { job, save_to = "docs/order.png", steps = new object[] { new { action = "click", control = "Order" } } });
        Assert.Contains("has not been looked at in this run", said);
        Assert.Contains("Nothing was played", said);
    }

    [Fact]
    public async Task WhatTheSystemRefuses_IsSaid_AndNothingIsSaved()
    {
        var job = LongJob();
        var said = await Call(Tool(_ => new([], "AT-SPI is not reachable from python3")),
                              new { job, save_to = "docs/order.png", steps = new object[] { new { action = "click", control = "Order" } } });

        Assert.Contains("could not be played in the windows of " + job + ": AT-SPI is not reachable from python3", said);
        Assert.Contains(CaptureSaving.NotSavedPrefix + "docs/order.png: no step was played", said);
    }

    [Fact]
    public async Task AFileDialog_IsNamedByItsTitleAlone()
    {
        var job = LongJob();
        var dialog = new CapturedWindow("Open", 7, null, 0, 0, []) { FileDialog = true };
        var said = await Call(Tool(_ => new([new(true, "clicked openButton")], null),
                                   reading: new WindowReading([new CapturedWindow("Order form", 7, Png, 1, 1, ["Window"]), dialog], null, [])),
                              new { job, steps = new object[] { new { action = "click", control = "openButton" } } });

        Assert.Contains("## Window \"Open\" — process 7: a file dialog. Its contents, the person's folders and files, are not shown", said);
        Assert.Contains($"run_command action stop, id {job}", said);
    }

    [Theory]
    [InlineData("FileChooser \"Open File\"", true)]
    [InlineData("  FileChooser", true)]
    [InlineData("Frame \"Order form\"", false)]
    public void ALinuxFileChooser_IsToldByItsTree(string line, bool fileDialog)
    {
        var window = WindowCapture.AsFileDialog(new CapturedWindow("Open File", 3, Png, 1, 1, ["Dialog \"Open File\"", line]));
        Assert.Equal(fileDialog, window.FileDialog);
        if (fileDialog) Assert.True(window.Png is null && window.Tree.Count == 0);
    }
}
