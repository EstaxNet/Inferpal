using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Persistence;
using Inferpal.Services.Signals;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio opened on nothing restores the conversation of the windows without a solution — yesterday's, maybe of
/// another project. The solution opened next takes its own conversation in its place while nothing was done since, and
/// says where the one on screen comes from otherwise: never kept without a word, for the model to take as its context.
/// </summary>
/// <remarks>The conversation of the windows without a solution is the suite's shared slot, read by every window started
/// without a folder: written and deleted here, in the collection of the windows.</remarks>
[Collection(SignalCollection.Name)]
public sealed class RootlessStartTests : IDisposable
{
    private const string Yesterday = "yesterday, about another project";
    private const string Own       = "the solution's own question";

    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-vm").FullName;   // deleted by the window's harness

    public RootlessStartTests() => Forget();

    public void Dispose() => Forget();

    private static void Forget()
    {
        try { new ConversationStore().Delete(ConversationStore.AutoSaveSlot); } catch { }
    }

    private static Task KeepWithoutASolutionAsync() =>
        new ConversationStore().AutoSaveAsync([new SavedMessage("user", Yesterday, null), new SavedMessage("assistant", "an answer", null)],
                                              CancellationToken.None);

    private Task KeepForTheSolutionAsync() =>
        new ConversationStore(slotOwner: ConversationStore.VisualStudioSlot).AutoSaveAsync(
            [new SavedMessage("user", Own, null), new SavedMessage("assistant", "its answer", null)], CancellationToken.None, _root);

    private static bool Shows(VsWindowHarness vs, string text) =>
        vs.Window.Messages.Any(m => m.Content.Contains(text, StringComparison.Ordinal));

    private string Opened => Strings.SolutionOpenedOverRootless(Path.GetFileName(_root), _root);

    [Fact]
    public async Task TheSolutionOpenedNext_TakesItsOwnConversation_InPlaceOfYesterdays()
    {
        await KeepWithoutASolutionAsync();
        await KeepForTheSolutionAsync();
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));

        ActiveSolutionSignal.WriteFolder(_root);   // the solution, opened after the window

        await VsWindowHarness.UntilAsync(() => Shows(vs, Own));
        Assert.False(Shows(vs, Yesterday));
        Assert.False(Shows(vs, Opened));
    }

    [Fact]
    public async Task ASolutionWithoutAConversation_SaysWhereTheOneOnScreenComesFrom()
    {
        await KeepWithoutASolutionAsync();
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));

        ActiveSolutionSignal.WriteFolder(_root);

        await VsWindowHarness.UntilAsync(() => Shows(vs, Opened));
        Assert.True(Shows(vs, Yesterday));
        Assert.False(Shows(vs, Strings.SolutionOwnConversationKept(Path.GetFileName(_root))));
    }

    [Fact]
    public async Task AQuestionAskedBeforeTheSolution_KeepsItsConversation_AndSaysTheSolutionsIsKept()
    {
        await KeepWithoutASolutionAsync();
        await KeepForTheSolutionAsync();
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));
        await vs.AskAsync("asked here, before the solution");

        ActiveSolutionSignal.WriteFolder(_root);

        await VsWindowHarness.UntilAsync(() => Shows(vs, Strings.SolutionOwnConversationKept(Path.GetFileName(_root))));
        Assert.True(Shows(vs, Yesterday));
        Assert.True(Shows(vs, Opened));
        Assert.False(Shows(vs, Own));
    }

    [Fact]
    public async Task ATurnUnderWay_IsNeverStopped_TheNoticeComesOnceItEnds()
    {
        await KeepWithoutASolutionAsync();
        await KeepForTheSolutionAsync();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var stopped = false;
        var fake = new FakeInferenceProvider();
        fake.OnAgentRun = async (history, _, _, ct) =>
        {
            started.TrySetResult();
            try { await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { stopped = true; throw; }
            return new AgentResult("done", [], history);
        };
        using var vs = new VsWindowHarness(fake, root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));
        var ask = vs.AskAsync("asked here, still running");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var ticks = vs.Fake.ConnectionChecks;
        ActiveSolutionSignal.WriteFolder(_root);
        await VsWindowHarness.UntilAsync(() => vs.Fake.ConnectionChecks > ticks);   // a tick met the solution mid-turn

        Assert.False(stopped);
        release.SetResult();
        await ask;
        await VsWindowHarness.UntilAsync(() => Shows(vs, Opened));
        Assert.False(stopped);
        Assert.False(Shows(vs, Own));
    }

    [Fact]
    public async Task AConversationBegunHere_BeforeTheSolution_IsSaidNothingAbout()
    {
        await KeepForTheSolutionAsync();
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await vs.AskAsync("begun here, before any solution");

        var ticks = vs.Fake.ConnectionChecks;
        ActiveSolutionSignal.WriteFolder(_root);
        await VsWindowHarness.UntilAsync(() => vs.Fake.ConnectionChecks > ticks);
        await Task.Delay(500);   // what the tick posted to the window has landed

        Assert.True(Shows(vs, "begun here, before any solution"));
        Assert.False(Shows(vs, Opened));
        Assert.False(Shows(vs, Own));
    }

    [Fact]
    public async Task ADamagedSlotOfTheSolution_IsNamed_AndTheConversationOnScreenIsSaidToComeFromElsewhere()
    {
        await KeepWithoutASolutionAsync();
        var slot = new ConversationStore(slotOwner: ConversationStore.VisualStudioSlot).AutoSaveFile(_root);
        Directory.CreateDirectory(Path.GetDirectoryName(slot)!);
        File.WriteAllText(slot, "{ damaged");
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));

        ActiveSolutionSignal.WriteFolder(_root);

        await VsWindowHarness.UntilAsync(() => Shows(vs, Opened));
        // The store's own sentence, which names the copy it kept: the only part of it known here is the slot's name.
        Assert.Contains(vs.Window.Messages, m => m.Role != "user" && m.Content.Contains(ConversationStore.AutoSaveSlot, StringComparison.Ordinal)
                                                 && !m.Content.Contains(Yesterday, StringComparison.Ordinal));
        Assert.True(Shows(vs, Yesterday));
    }

    [Fact]
    public async Task ARootThatOnlyGrows_AroundTheSameSolution_IsNotSaidAgain()
    {
        await KeepWithoutASolutionAsync();
        var inner = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        using var vs = new VsWindowHarness(root: _root, rootless: true);
        await VsWindowHarness.UntilAsync(() => Shows(vs, Yesterday));
        var saidInner = Strings.SolutionOpenedOverRootless("src", inner);

        ActiveSolutionSignal.WriteFolder(inner);
        await VsWindowHarness.UntilAsync(() => Shows(vs, saidInner));
        var ticks = vs.Fake.ConnectionChecks;
        ActiveSolutionSignal.WriteFolder(_root);   // the same solution, its root grown to the folder that holds it all
        await VsWindowHarness.UntilAsync(() => vs.Fake.ConnectionChecks > ticks);
        await Task.Delay(500);   // what the tick posted to the window has landed

        Assert.False(Shows(vs, Opened));
    }

    /// <summary>Reference arm: a window that restored nothing at its start takes the solution's conversation, as ever, and
    /// has nothing to say about it.</summary>
    [Fact]
    public async Task AStartThatRestoredNothing_TakesTheSolutionsConversation_WithoutANotice()
    {
        await KeepForTheSolutionAsync();
        using var vs = new VsWindowHarness(root: _root, rootless: true);

        ActiveSolutionSignal.WriteFolder(_root);

        await VsWindowHarness.UntilAsync(() => Shows(vs, Own));
        Assert.False(Shows(vs, Opened));
    }
}
