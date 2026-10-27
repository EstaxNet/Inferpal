using System.Collections.Concurrent;
using System.IO;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A model call whose caller names no place for its wait says it to the scope the front-end opened for the turn — the
/// compaction, the review, the summary of a turn are said where the person waits, without each site wiring it.
/// </summary>
public class ModelWaitScopeTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromMilliseconds(1);

    private static readonly ModelWait Inner = new(1_000, TimeSpan.FromSeconds(9), null, TimeSpan.FromMinutes(5));
    private static readonly ModelWait Outer = new(2_000, TimeSpan.FromSeconds(9), null, TimeSpan.FromMinutes(5));

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done(), "the wait was never said");
    }

    [Fact]
    public async Task AWaitWithNoLineOfItsOwn_IsSaidToTheOpenScope_ThenGivesItsPlaceBack()
    {
        var said = new ConcurrentQueue<ModelWait?>();
        using (ModelWaitScope.Open(said.Enqueue))
        {
            using var wait = new PrefillWait(null, 12_000, null, TimeSpan.FromMinutes(10), firstSaid: Soon);
            await UntilAsync(() => !said.IsEmpty);
            wait.Arrived();
        }

        var lines = said.ToArray();
        Assert.NotNull(lines[0]);
        Assert.Null(lines[^1]);   // the step it covered shows again
    }

    /// <summary>A caller that names its own place keeps it: the scope is only the default.</summary>
    [Fact]
    public async Task ACallersOwnLine_WinsOverTheScope()
    {
        var scope = new ConcurrentQueue<ModelWait?>();
        var own   = new ConcurrentQueue<ModelWait?>();
        using (ModelWaitScope.Open(scope.Enqueue))
        {
            using var wait = new PrefillWait(own.Enqueue, 12_000, null, TimeSpan.FromMinutes(10), firstSaid: Soon);
            await UntilAsync(() => !own.IsEmpty);
        }
        Assert.Empty(scope);
    }

    /// <summary>Work started under a turn and still running after it carries the scope — closed, it says nothing, so a
    /// late line never lands in a turn that has ended.</summary>
    [Fact]
    public async Task AScopeClosed_SaysNothingMore_ToWorkStartedUnderIt()
    {
        var said    = new ConcurrentQueue<ModelWait?>();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> late;
        bool wasOpen;
        using (ModelWaitScope.Open(said.Enqueue))
        {
            late = Task.Run(async () =>
            {
                started.SetResult(ModelWaitScope.Current is not null);
                await closed.Task;
                return ModelWaitScope.Current is not null;
            });
            wasOpen = await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        closed.SetResult();

        Assert.True(wasOpen);     // witness: the task did carry the scope
        Assert.False(await late.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    /// <summary>
    /// Visual Studio (its window is built in no test, so its source is read): a command's steps (<c>CurrentStep</c>) and
    /// the waits of its model calls are drawn in its status bubble, through ONE envelope — the scope opened in the
    /// command's own flow, never in the posted <c>BeginOwnedTurn</c>, whose context does not reach the calls.
    /// </summary>
    [Fact]
    public void VisualStudio_ALongCommand_SaysItsStepsAndWaits_FromItsOwnFlow()
    {
        var window = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow");
        var helpers = ConventionCoverageTests.CodeOnly(Path.Combine(window, "InferpalToolWindowData.UiHelpers.cs"));
        Assert.Matches(@"IDisposable OwnCommandStatus\(CancellationTokenSource cts\)[\s\S]*?_commandOwner = cts;[\s\S]*?"
                       + @"ModelWaitScope\.Open\(wait => SayCommandWait\(cts, wait\)\)[\s\S]*?waitScope\.Dispose\(\);[\s\S]*?"
                       + @"ReferenceEquals\(_commandOwner, cts\)[\s\S]*?ShowCommandStatus\(\);", helpers);
        // The long commands take it right after the turn, and give it back before the turn is released.
        Assert.Matches(@"cts = BeginOwnedTurn\(ct\)\);\s*var status = OwnCommandStatus\(cts!\);[\s\S]*?await body\("
                       + @"[\s\S]*?finally\s*\{\s*status\.Dispose\(\);\s*await RunOnVMContextAsync\(\(\) => EndOwnedTurn\(cts\)\);", helpers);
        Assert.Matches(@"void SayCommandWait\([\s\S]*?ReferenceEquals\(_commandOwner, owner\)[\s\S]*?ShowCommandStatus\(\);", helpers);
        // The step on the line, the wait as a card under it; shown only while the command still holds the turn.
        Assert.Contains("_commandStatus.ShowWait(_commandWait);", helpers, StringComparison.Ordinal);
        // A command's step is "" between steps, never null: a wait said then still gets its line.
        Assert.Matches(@"!string\.IsNullOrEmpty\(_currentStep\) \? _currentStep\s*:\s*_commandWait is not null \? Strings\.StatusThinking", helpers);
        Assert.Contains("ReferenceEquals(_commandOwner, _currentCts)", helpers, StringComparison.Ordinal);
        var properties = ConventionCoverageTests.CodeOnly(Path.Combine(window, "InferpalToolWindowData.Construction.cs"));
        Assert.Matches(@"string CurrentStep\s*\{[^}]*set \{ if \(SetProperty\(ref _currentStep, value\)\) ShowCommandStatus\(\);", properties);

        // The chat turn: the compaction's step on the turn's bubble, and a scope for the calls that name no place, closed
        // first in the finally (a line said after would bring the bubble back).
        var turn = ConventionCoverageTests.CodeOnly(Path.Combine(window, "InferpalToolWindowData.ChatTurn.cs"));
        Assert.Matches(@"CompactOrTruncateAsync\([^;]*step => UpsertStatusBubble\([^;]*;\s*await RunOnVMContextAsync\([\s\S]{0,80}RemoveStatusBubble\(\)", turn);
        Assert.Matches(@"var waitScope = ModelWaitScope\.Open\([\s\S]*?turnWait = wait;[\s\S]*?statusBubble\?\.ShowWait\(wait\);[\s\S]*?finally\s*\{\s*waitScope\.Dispose\(\);", turn);
        // /tdd and /fix-build give their bubble back before the turn is released.
        foreach (var (name, gesture) in new[] { ("SlashCommands", "HandleTddCommandAsync"), ("PromptHistory", "HandleFixBuildCommandAsync") })
        {
            var source = ConventionCoverageTests.CodeOnly(Path.Combine(window, $"InferpalToolWindowData.{name}.cs"));
            Assert.Matches(gesture + @"\([\s\S]*?var status = OwnCommandStatus\(localCts\);[\s\S]*?finally\s*\{\s*status\.Dispose\(\);\s*await RunOnVMContextAsync", source);
        }
    }

    /// <summary>
    /// Every gesture of the Visual Studio window that takes the turn has a place where its steps and its model's waits
    /// are drawn: the status-bubble envelope (<c>OwnCommandStatus</c>) or a wait scope of its own (the chat turn, the
    /// commit proposal's label). <c>CurrentStep</c> alone is drawn nowhere — /tdd and /fix-build said every step to it.
    /// </summary>
    [Fact]
    public void VisualStudio_EveryGestureThatTakesTheTurn_HasAPlaceForItsStepsAndWaits()
    {
        var window = Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow");
        var takers = new List<string>();
        foreach (var file in Directory.EnumerateFiles(window, "InferpalToolWindowData*.cs"))
        {
            var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(ConventionCoverageTests.CodeOnly(file)).GetRoot();
            foreach (var method in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>())
            {
                if (method.Identifier.ValueText == "BeginOwnedTurn") continue;
                var body = method.ToString();
                if (!body.Contains("BeginOwnedTurn(", StringComparison.Ordinal)) continue;
                takers.Add(method.Identifier.ValueText);
                var envelope = body.Contains("OwnCommandStatus(", StringComparison.Ordinal);
                Assert.True(envelope || body.Contains("ModelWaitScope.Open(", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: {method.Identifier.ValueText} takes the turn with no place for its waits.");
                // A wait scope draws waits only: steps said to CurrentStep need the envelope, or a bubble drawn here.
                if (body.Contains("CurrentStep =", StringComparison.Ordinal))
                    Assert.True(envelope || body.Contains("ChatMessageItem.StatusMsg(", StringComparison.Ordinal),
                        $"{Path.GetFileName(file)}: {method.Identifier.ValueText} says its steps to CurrentStep, drawn nowhere.");
            }
        }
        // Witness: the reading is alive (the long commands, /tdd, /fix-build, /commit, the chat turn).
        Assert.True(takers.Count >= 4, $"only {takers.Count} gesture(s) found taking the turn: {string.Join(", ", takers)}");
    }

    /// <summary>
    /// A <c>/task</c> submitted under a gesture's scope runs after the gesture has ended: its worker leaves the scope, so
    /// the agent loop (which prefers a scope to its caller's step) says the task's waits through the task's own steps.
    /// </summary>
    [Fact]
    public async Task ATasksWorker_LeavesTheScopeOfTheGestureThatSubmittedIt()
    {
        var inRunner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var queue = new Inferpal.Services.Tasks.BackgroundTaskQueue((_, _, _) =>
        {
            inRunner.TrySetResult(ModelWaitScope.Current is null);
            return Task.FromResult(Inferpal.Services.Tasks.BackgroundTaskQueue.TaskRunOutcome.Of("done"));
        });

        // The gesture's scope still open while the task runs: closed, it would read as no scope whatever the worker did.
        using (ModelWaitScope.Open(_ => { }))
        {
            queue.Submit("audit the RAG layer");
            Assert.True(await inRunner.Task.WaitAsync(TimeSpan.FromSeconds(30)), "the task ran under the gesture's scope");
            Assert.NotNull(ModelWaitScope.Current);                       // the gesture keeps its own
        }
    }

    [Fact]
    public void ANestedScope_GivesBackTheOuterOne()
    {
        var outer = new ConcurrentQueue<ModelWait?>();
        var inner = new ConcurrentQueue<ModelWait?>();
        Assert.Null(ModelWaitScope.Current);
        using (ModelWaitScope.Open(outer.Enqueue))
        {
            using (ModelWaitScope.Open(inner.Enqueue)) ModelWaitScope.Current!(Inner);
            ModelWaitScope.Current!(Outer);
        }
        Assert.Null(ModelWaitScope.Current);
        Assert.Equal([Inner], inner);
        Assert.Equal([Outer], outer);
    }
}
