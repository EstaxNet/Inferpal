using System.IO;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@output</c> in Visual Studio: the Output window's pane — the one shown, else Build — read inside devenv and asked
/// across the process boundary; its end kept, the cut said; nothing to attach said with its cause; offered by Visual
/// Studio only.
/// </summary>
[Collection(SignalCollection.Name)]
public sealed class OutputMentionTests : IDisposable
{
    private readonly SignalScratchDir _signals = new();

    public void Dispose() => _signals.Dispose();

    [Fact]
    public async Task ThePane_ComesBackToTheMentionThatAsked()
    {
        var asking = OutputPaneSignal.AskAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        OutputPaneAsk? ask = null;
        for (var i = 0; i < 600 && ask is null; i++)
        {
            ask = OutputPaneSignal.TryTake(null);
            if (ask is null) await Task.Delay(50);
        }
        Assert.NotNull(ask);
        OutputPaneSignal.Answer(new OutputPaneAnswer(ask!.Id, "Build", "========== Build: 1 succeeded ==========", false, null));

        var answer = await asking;
        Assert.Equal("Build", answer!.Pane);
        Assert.Null(OutputPaneSignal.TryTake(ask.Id));   // served once
    }

    [Fact]
    public async Task NoAnswer_WithdrawsTheRequest_AndSaysWhetherTheInProcPartIsThere()
    {
        var answer = await OutputPaneSignal.AskAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.Null(answer);
        Assert.False(File.Exists(OutputPaneSignal.RequestPath));
        Assert.Equal(Strings.MentionOutputNoAnswer, MentionController.OutputMention(answer, "▤ @output", inProcLoaded: true).Notice);
        Assert.Equal(Strings.MentionOutputNoInProc, MentionController.OutputMention(answer, "▤ @output", inProcLoaded: false).Notice);
    }

    [Fact]
    public async Task AnAnswerToAnotherRequest_IsNotTaken()
    {
        var asking = OutputPaneSignal.AskAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None);
        OutputPaneAsk? ask = null;
        for (var i = 0; i < 100 && ask is null; i++)
        {
            ask = OutputPaneSignal.TryTake(null);
            if (ask is null) await Task.Delay(5);
        }
        Assert.NotNull(ask);   // witness: the request was there to answer
        OutputPaneSignal.Answer(new OutputPaneAnswer("another", "Build", "text", false, null));
        SignalFile.Write(OutputPaneSignal.RequestPath, ask, "test");

        Assert.Null(await asking);
        Assert.False(File.Exists(OutputPaneSignal.RequestPath));
    }

    [Fact]
    public async Task ACancelledMention_WithdrawsItsRequest()
    {
        using var cancel = new CancellationTokenSource();
        var asking = OutputPaneSignal.AskAsync(TimeSpan.FromSeconds(30), cancel.Token);
        for (var i = 0; i < 600 && !File.Exists(OutputPaneSignal.RequestPath); i++) await Task.Delay(5);
        Assert.True(File.Exists(OutputPaneSignal.RequestPath));   // witness: the request was there

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => asking);
        Assert.False(File.Exists(OutputPaneSignal.RequestPath));
    }

    [Fact]
    public void AnOldRequest_IsNotServed()
    {
        var now = DateTimeOffset.UtcNow;
        SignalFile._nowOverride = () => now;
        SignalFile.Write(OutputPaneSignal.RequestPath, new OutputPaneAsk("1", SignalFile.CurrentPid, now.ToUnixTimeMilliseconds()), "test");

        SignalFile._nowOverride = () => now + OutputPaneSignal.MaxAge + TimeSpan.FromSeconds(1);
        Assert.Null(OutputPaneSignal.TryTake(null));
        SignalFile._nowOverride = () => now + TimeSpan.FromSeconds(1);
        Assert.NotNull(OutputPaneSignal.TryTake(null));   // witness: the same request, while it is fresh
    }

    [Fact]
    public void ACutPane_SaysItToBothReaders()
    {
        var kept = string.Join("\n", Enumerable.Range(4001, 1000).Select(i => $"1>line {i} of the build"));

        var outcome = MentionController.OutputMention(new OutputPaneAnswer("1", "Build", kept, true, null), "▤ @output", true);

        // The person — with what is sent, the first line dropped.
        Assert.Equal(Strings.MentionOutputCutLabel("▤ @output: Build", kept.Length - kept.IndexOf('\n') - 1), outcome.Label);
        Assert.Contains("only its end is attached", outcome.Content, StringComparison.Ordinal);       // the model
        // The first line, likely cut in the middle, goes: what follows the marker is a whole line.
        Assert.Matches(@"attached\)\n1>line 4002 of the build\n", outcome.Content!);
        // One long line is kept whole: never a chip with the marker alone.
        var single = MentionController.OutputMention(new OutputPaneAnswer("1", "Build", new string('x', 50) + "\r\n", true, null), "▤ @output", true);
        Assert.Contains(new string('x', 50), single.Content, StringComparison.Ordinal);
        // Uncut: the plain label.
        Assert.Equal("▤ @output: Build", MentionController.OutputMention(new OutputPaneAnswer("1", "Build", "done", false, null), "▤ @output", true).Label);
    }

    [Fact]
    public void AFailure_IsSaidWithItsCause()
    {
        var outcome = MentionController.OutputMention(new OutputPaneAnswer("1", null, null, false, null, "the request could not be written to x"),
                                                      "▤ @output", true);
        Assert.Equal(Strings.MentionOutputFailed("the request could not be written to x"), outcome.Notice);
    }

    [Theory]
    [InlineData(OutputPaneSignal.ReasonNone)]
    [InlineData(OutputPaneSignal.ReasonEmpty)]
    [InlineData(OutputPaneSignal.ReasonUnreadable)]
    public void NothingToAttach_SaysWhy(string reason)
    {
        var outcome = MentionController.OutputMention(new OutputPaneAnswer("1", "Debug", null, false, reason), "▤ @output", true);

        Assert.Null(outcome.Label);
        Assert.Equal(reason switch
        {
            OutputPaneSignal.ReasonEmpty      => Strings.MentionOutputEmpty("Debug"),
            OutputPaneSignal.ReasonUnreadable => Strings.MentionOutputUnreadable("Debug"),
            _                                 => Strings.MentionOutputNone,
        }, outcome.Notice);
    }

    /// <summary>The half inside devenv: started and stopped with the package, the pane read on the UI thread through the
    /// typed interop, a short pane read from its start, the answer written off the UI thread.</summary>
    [Fact]
    public void TheInProcessHalf_IsStartedAndReadsThePane()
    {
        string InProc(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(),
                                                                                   "Inferpal.InProc", "GhostText", file));
        var package = InProc("GhostTextPackage.cs");
        Assert.Contains("OutputPaneWatcher.Start();", package, StringComparison.Ordinal);
        Assert.Contains("OutputPaneWatcher.Stop();", package, StringComparison.Ordinal);
        var watcher = InProc("OutputPaneWatcher.cs");
        Assert.Contains("await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();", watcher, StringComparison.Ordinal);
        Assert.Contains("is EnvDTE80.DTE2 dte", watcher, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic", watcher, StringComparison.Ordinal);
        Assert.Contains("cut = doc.EndPoint.AbsoluteCharOffset > OutputPaneSignal.MaxChars;", watcher, StringComparison.Ordinal);
        Assert.Contains("await Task.Run(() => OutputPaneSignal.Answer(answer));", watcher, StringComparison.Ordinal);
    }

    [Fact]
    public void VisualStudioOffersIt_VsCodeDoesNot()
    {
        var output = Assert.Single(MentionController.Categories, c => c.Token == "@output");
        Assert.Equal(MentionEditors.VisualStudio, output.Editors);
        Assert.Contains(MentionController.MatchCategories("out"), c => c.Token == "@output");
        // The window asks devenv, and waits within a budget.
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal",
                                                              "ToolWindow", "InferpalToolWindowData.Mentions.cs"));
        Assert.Contains("OutputPaneSignal.AskAsync(Services.Signals.OutputPaneSignal.AnswerBudget, ct)", vm, StringComparison.Ordinal);
        Assert.Contains("Services.Signals.InProcAliveSignal.TryRead() is { HasPackage: true }", vm, StringComparison.Ordinal);
    }
}
