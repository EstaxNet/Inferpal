using System.Diagnostics;
using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run on a copy in Visual Studio's window (<c>/run-copy</c>, pari 106): the start card, the question asked in the copy
/// under its command, the review of what its run left, the kept changes applied to the person's file — and only then —, the
/// copy discarded. Regenerate asks a copy question again through its card, Continue continues it in the copy: neither ever
/// sends it into the person's files. Windows only: the window is Visual Studio's.
/// </summary>
[Collection(SignalCollection.Name)]   // the window writes the active solution's signal
public sealed class RunCopyVsTests
{
    private static bool Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    /// <summary>A window on a committed repository whose turns take the basic path, the one the fake server's run answers.</summary>
    private static VsWindowHarness Window()
    {
        var vs = new VsWindowHarness(config: new InferpalConfig { DefaultModel = "m", AgentModeEnabled = false });
        File.WriteAllText(Path.Combine(vs.Root, "Pay.cs"), "class Pay\n{\n    int A() => 1;\n}\n");
        Assert.True(Git(vs.Root, "init", "-q", "-b", "main", ".") && Git(vs.Root, "add", "-A") && Git(vs.Root, "commit", "-q", "-m", "start"),
                    "git is needed: this test is UNDECIDED, not green.");
        return vs;
    }

    private static ChatMessageItem? Card(VsWindowHarness vs, Func<ChatMessageItem, bool> kind) =>
        vs.Window.Messages.LastOrDefault(m => m.Role == "copy" && kind(m));

    private static Task ClickAsync(Microsoft.VisualStudio.Extensibility.UI.AsyncCommand? command) =>
        command!.ExecuteAsync(null, null, CancellationToken.None);

    [Fact]
    public async Task ARunOnACopy_WorksInTheCopy_AndOnlyTheApplyWritesThePersonsFile_ThenTheCopyGoes()
    {
        using var vs = Window();
        var pay = Path.Combine(vs.Root, "Pay.cs");
        string? ranIn = null, prompt = null;
        vs.Fake.OnAgentRun = (history, _, _, _) =>
        {
            ranIn = RunRoot.Copy;
            prompt = history[0].Content;
            File.WriteAllText(Path.Combine(RunRoot.Copy!, "Pay.cs"), "class Pay\n{\n    int A() => 2;\n}\n");
            return Task.FromResult(new AgentResult("Changed A.", [], history));
        };

        await vs.AskAsync("/run-copy Make A return 2");
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is not null);
        Assert.Null(ranIn);                                                                          // nothing ran yet
        await ClickAsync(Card(vs, m => m.IsCopyStart)!.CopyGoCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyReview) is not null);

        Assert.NotNull(ranIn);
        Assert.False(PathComparer.SameDirectory(ranIn, vs.Root));                                    // the turn ran in the copy
        // The system prompt names the copy as the workspace — built on the window's context, it named the person's folder.
        Assert.Contains(ranIn!, prompt);
        Assert.DoesNotContain(Path.GetFullPath(vs.Root) + "`", prompt);
        Assert.Contains(vs.Window.Messages, m => m.Role == "user" && m.Content == "/run-copy Make A return 2");
        Assert.Contains("int A() => 1;", File.ReadAllText(pay));                                    // the person's file as it was
        var review = Card(vs, m => m.IsCopyReview)!;
        Assert.True(Assert.Single(Assert.Single(review.CopyFiles).Hunks).IsKept);

        await ClickAsync(review.CopyApplyCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyApplied) is not null);
        Assert.Contains("int A() => 2;", File.ReadAllText(pay));

        await ClickAsync(Card(vs, m => m.IsCopyApplied)!.CopyDiscardCommand);
        await VsWindowHarness.UntilAsync(() => !vs.Window.Messages.Any(m => m.Role == "copy"));
        Assert.DoesNotContain(Directory.EnumerateDirectories(WorkCopy.Home, "*", SearchOption.AllDirectories),
                              d => File.Exists(Path.Combine(d, "Pay.cs")) && PathComparer.SameDirectory(d, ranIn));
    }

    [Fact]
    public async Task RegenerateOnACopyQuestion_AsksAgainThroughItsCard_AndContinueStaysInTheCopy()
    {
        using var vs = Window();
        var roots = new List<string?>();
        vs.Fake.OnAgentRun = (history, _, _, _) =>
        {
            roots.Add(RunRoot.Copy);
            // The first run stops at its iteration limit: its end notice offers Continue.
            return Task.FromResult(new AgentResult("Done so far.", [], history, ReachedIterationLimit: roots.Count == 1));
        };
        await vs.AskAsync("/run-copy Look at Pay");
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is not null);
        await ClickAsync(Card(vs, m => m.IsCopyStart)!.CopyGoCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyReview) is not null
                                               && vs.Window.Messages.Any(m => m.IsContinuable));
        Assert.Single(roots);

        // Continue: the next steps in the copy, under the command.
        await ClickAsync(vs.Window.Messages.Last(m => m.IsContinuable).ContinueCommand);
        await VsWindowHarness.UntilAsync(() => roots.Count == 2);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyReview) is not null && vs.Window.Messages.Any(m => m.IsRegeneratable));
        Assert.All(roots, r => Assert.False(r is null || PathComparer.SameDirectory(r, vs.Root)));
        Assert.Contains(vs.Window.Messages, m => m.Role == "user" && m.Content.StartsWith("/run-copy ", StringComparison.Ordinal)
                                                 && m.Content != "/run-copy Look at Pay");

        // Regenerate: the start card again, nothing sent meanwhile, and the exchange it would ask again stays.
        var before = vs.Window.Messages.Count(m => m.Role is "user" or "assistant");
        await ClickAsync(vs.Window.Messages.Last(m => m.IsRegeneratable).RegenerateCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is not null);
        Assert.Equal(2, roots.Count);
        Assert.Equal(before, vs.Window.Messages.Count(m => m.Role is "user" or "assistant"));
        await ClickAsync(Card(vs, m => m.IsCopyStart)!.CopyCancelCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is null);
        var copies = new List<string>();
        foreach (var root in roots) if (root is not null && !copies.Contains(root)) copies.Add(root);
        Assert.Single(copies);                                                                       // one copy, continued
    }

    [Fact]
    public async Task ContinueAfterTheCopyWasDiscarded_GoesThroughTheStartCard_NeverIntoANewCopyWithoutAWord()
    {
        using var vs = Window();
        var runs = 0;
        vs.Fake.OnAgentRun = (history, _, _, _) =>
        {
            runs++;
            return Task.FromResult(new AgentResult("Done so far.", [], history, ReachedIterationLimit: true));
        };
        await vs.AskAsync("/run-copy Look at Pay");
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is not null);
        await ClickAsync(Card(vs, m => m.IsCopyStart)!.CopyGoCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyReview) is not null && vs.Window.Messages.Any(m => m.IsContinuable));
        await ClickAsync(Card(vs, m => m.IsCopyReview)!.CopyDiscardCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyReview) is null);

        await ClickAsync(vs.Window.Messages.Last(m => m.IsContinuable).ContinueCommand);
        await VsWindowHarness.UntilAsync(() => Card(vs, m => m.IsCopyStart) is not null);

        Assert.Equal(1, runs);                                                                       // nothing ran meanwhile
        Assert.Contains(Card(vs, m => m.IsCopyStart)!.CopyRows, r => r.Value == ChatMessageItem.Plain(Localization.Strings.CopyStartBranchNew));
    }
}
