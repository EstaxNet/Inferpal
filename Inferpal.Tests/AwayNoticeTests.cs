using System.IO;
using Inferpal.Host;
using Inferpal.Localization;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The notice of a start (<see cref="AwayNotice"/>) says what the closing of the editor left behind — the background tasks
/// it stopped, and the ones that finished and were never read — once: dismissed, never again.
/// </summary>
public sealed class AwayNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"away-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static BackgroundTaskSnapshot Task(string id, BackgroundTaskState state, TaskEndCause cause = TaskEndCause.None,
                                               bool started = true, bool read = false, bool said = false, int proposals = 0,
                                               bool inherited = true) =>
        new(id, "objective of " + id, state, DateTimeOffset.Now, started ? DateTimeOffset.Now : null, DateTimeOffset.Now,
            "report", null, [], 0, Proposals: [.. Enumerable.Range(0, proposals).Select(i => new TaskProposal("write_file", $"f{i}", "", null))],
            StepsTaken: 4, Cause: cause, Read: read, Said: said, Inherited: inherited);

    [Fact]
    public void TasksTheClosingStopped_AndReportsNeverRead_AreSaid_TheRestIsNot()
    {
        var notice = AwayNotice.Build([], "vs", "Visual Studio",
        [
            Task("t1", BackgroundTaskState.Succeeded, proposals: 2),                                   // finished, unread
            Task("t2", BackgroundTaskState.Succeeded, read: true),                                     // read: nothing to say
            Task("t3", BackgroundTaskState.Cancelled, TaskEndCause.EditorClosed),                      // running at the close
            Task("t4", BackgroundTaskState.Cancelled, TaskEndCause.EditorClosed, started: false),      // queued at the close
            Task("t5", BackgroundTaskState.Cancelled),                                                 // the person's own stop
            Task("t6", BackgroundTaskState.Cancelled, TaskEndCause.EditorClosed, said: true),          // said and dismissed
            Task("t7", BackgroundTaskState.Succeeded, inherited: false),                               // this session's own
        ], new HashSet<string>(), DateTime.Now);

        Assert.NotNull(notice);
        Assert.Equal(["t1", "t3", "t4"], notice!.TaskIds);
        Assert.Null(notice.Run);
        var done = notice.Tasks[0];
        Assert.False(done.Retry);
        Assert.EndsWith(Strings.AwayTaskDoneProposalsKept(2), done.Text, StringComparison.Ordinal);
        Assert.Equal(new AwayTask("t3", Strings.AwayTaskStopped("t3", "Visual Studio", 4), true), notice.Tasks[1]);
        Assert.Equal(new AwayTask("t4", Strings.AwayTaskStoppedBeforeStart("t4", "Visual Studio"), true), notice.Tasks[2]);
    }

    /// <summary>Reference arm: nothing left behind, nothing said.</summary>
    [Fact]
    public void NothingLeftBehind_SaysNothing() =>
        Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", [Task("t1", BackgroundTaskState.Succeeded, read: true)],
                                     new HashSet<string>(), DateTime.Now));

    /// <summary>A stopped task run again is dealt with: the next start does not offer it again; the new one is its own.</summary>
    [Fact]
    public void ATaskRunAgain_IsNotOfferedAgain()
    {
        var root = Path.Combine(_dir, "ws");
        var journal = new TaskJournal(Path.Combine(_dir, "tasks", "vs.json"));
        journal.Update(entries =>
        {
            entries.Add(new TaskJournal.Entry("t1", "objective", BackgroundTaskState.Cancelled, DateTimeOffset.Now,
                                              Cause: TaskEndCause.EditorClosed, WorkspaceRoot: root));
            return true;
        });
        BackgroundTaskQueue Queue() => new((_, _, ct) => System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct)
                                               .ContinueWith(_ => BackgroundTaskQueue.TaskRunOutcome.Of("x")),
                                           waitForChatIdle: _ => System.Threading.Tasks.Task.CompletedTask, currentRoot: () => root,
                                           journalFor: _ => new TaskJournal(journal.Path));
        using (var first = Queue())
        {
            Assert.Equal(["t1"], AwayNotice.Build([], "vs", "Visual Studio", first.List(), first.SaidRuns(), DateTime.Now)!.TaskIds);
            var answer = Services.Commands.TaskCommandHandler.Handle(first, ["/task", "retry", "t1"]).Message;
            Assert.Contains("t2", answer, StringComparison.Ordinal);   // witness: it ran again
        }
        using var next = Queue();
        Assert.DoesNotContain("t1", AwayNotice.Build([], "vs", "Visual Studio", next.List(), next.SaidRuns(), DateTime.Now)?.TaskIds ?? []);
    }

    /// <summary>A task another window finished, while that window stays open, is not said "before the editor closed"; once
    /// that window is gone, it is — and a dismissal is never undone by the older copy that window writes.</summary>
    [Fact]
    public async System.Threading.Tasks.Task AnOpenWindowsTask_IsNotSaid_UntilThatWindowIsGone()
    {
        var root = Path.Combine(_dir, "ws");
        var path = Path.Combine(_dir, "tasks", "vs.json");
        BackgroundTaskQueue Queue() => new((_, _, _) => System.Threading.Tasks.Task.FromResult(BackgroundTaskQueue.TaskRunOutcome.Of("x")),
                                           waitForChatIdle: _ => System.Threading.Tasks.Task.CompletedTask, currentRoot: () => root,
                                           journalFor: _ => new TaskJournal(path));
        var open = Queue();
        var id = open.Submit("objective")!;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (open.Get(id)?.State != BackgroundTaskState.Succeeded && DateTime.UtcNow < deadline)
            await System.Threading.Tasks.Task.Delay(10);
        Assert.Equal(BackgroundTaskState.Succeeded, open.Get(id)?.State);
        Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", open.List(), open.SaidRuns(), DateTime.Now));   // its own

        using (var second = Queue())
            Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", second.List(), second.SaidRuns(), DateTime.Now));

        open.Dispose();
        using var later = Queue();
        using var other = Queue();
        Assert.NotNull(other.List());   // another window loads it too, not yet dismissed
        var notice = AwayNotice.Build([], "vs", "Visual Studio", later.List(), later.SaidRuns(), DateTime.Now);
        Assert.Equal([id], notice!.TaskIds);
        later.MarkSaid([], notice.TaskIds);
        using (var after = Queue())
            Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", after.List(), after.SaidRuns(), DateTime.Now));

        other.MarkRead(id);   // writes its copy, which still holds "not said"
        Assert.True(Assert.Single(new TaskJournal(path).Read().Entries!, e => e.Id == id).Said);
    }

    /// <summary>A file of the root whose name starts with two dots is in the workspace; a file beside it is not.</summary>
    [Fact]
    public void AFileNamedUnderTheRoot_StaysInsideIt_EvenWithTwoLeadingDots()
    {
        var root = Path.Combine(_dir, "ws");
        Assert.Equal("..env.cs", Services.Commands.UndoRunCommandHandler.Relativise(Path.Combine(root, "..env.cs"), root));
        Assert.Equal(Path.Combine("src", "A.cs"), Services.Commands.UndoRunCommandHandler.Relativise(Path.Combine(root, "src", "A.cs"), root));
        var beside = Path.Combine(_dir, "other", "B.cs");
        Assert.Equal(beside, Services.Commands.UndoRunCommandHandler.Relativise(beside, root));
    }

    /// <summary>Dismissed: the runs and tasks it said are marked in the journal, and a new start does not say them.</summary>
    [Fact]
    public void ADismissedNotice_IsNotSaidAgain()
    {
        var root    = Path.Combine(_dir, "ws");
        var journal = new TaskJournal(Path.Combine(_dir, "tasks", "vs.json"));
        journal.Update(entries =>
        {
            entries.Add(new TaskJournal.Entry("t1", "objective", BackgroundTaskState.Cancelled, DateTimeOffset.Now,
                                              Cause: TaskEndCause.EditorClosed, WorkspaceRoot: root));
            return true;
        });
        BackgroundTaskQueue Queue() => new((_, _, _) => System.Threading.Tasks.Task.FromResult(BackgroundTaskQueue.TaskRunOutcome.Of("x")),
                                           waitForChatIdle: _ => System.Threading.Tasks.Task.CompletedTask, currentRoot: () => root,
                                           journalFor: _ => new TaskJournal(journal.Path));
        using var first = Queue();
        var notice = AwayNotice.Build([], "vs", "Visual Studio", first.List(), first.SaidRuns(), DateTime.Now)!;
        Assert.Equal(["t1"], notice.TaskIds);   // witness: said before the dismissal

        first.MarkSaid(["run-cut-yesterday"], notice.TaskIds);

        using var next = Queue();
        Assert.Contains("run-cut-yesterday", next.SaidRuns());
        Assert.Null(AwayNotice.Build([], "vs", "Visual Studio", next.List(), next.SaidRuns(), DateTime.Now));
    }
}

public partial class HostServerTests
{
    /// <summary>The host gives VS Code the notice of its start, and forgets it once dismissed.</summary>
    [Fact]
    public async Task TheHost_SaysWhatTheClosingLeft_UntilItIsDismissed()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-away-").FullName;
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            // A task the closing of an earlier host stopped, in this editor's journal.
            new Inferpal.Services.Persistence.ConversationStore(slotOwner: Inferpal.Services.Persistence.ConversationStore.VsCodeSlot)
                .TaskJournal(root)!.Reserve(0, id => new TaskJournal.Entry(id, "the objective", BackgroundTaskState.Running,
                    DateTimeOffset.Now, new TaskJournal.Owner(int.MaxValue, 1, "gone"), StartedAt: DateTimeOffset.Now,
                    StepsTaken: 2, WorkspaceRoot: root));

            var notice = await h.Client.InvokeWithParameterObjectAsync<AwayNoticeDto?>("session/away", new { endsOnAQuestion = true })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var task = Assert.Single(notice!.Tasks);
            Assert.True(task.Retry);
            Assert.Equal(Strings.AwayTaskStopped(task.Id, "VS Code", 2), task.Text);
            Assert.Null(notice.NoAnswer);   // no run was cut: no line under the question

            await h.Client.InvokeWithParameterObjectAsync("session/awayDismissed", new { runIds = Array.Empty<string>(), taskIds = new[] { task.Id } })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Null(await h.Client.InvokeWithParameterObjectAsync<AwayNoticeDto?>("session/away", new { endsOnAQuestion = true })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs)));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>A run of VS Code the closing cut, under a restored question: the host says it, and the line that no answer
    /// was written; the same run under an answered conversation, the line is not given.</summary>
    [Fact]
    public async Task ARunTheClosingCut_UnderARestoredQuestion_GetsTheNoAnswerLine()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-away-run-").FullName;
        try
        {
            using var h = CreateHarness();
            await h.InitializeAsync(rootDir: root).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var live = h.Server.CurrentSession!.Tools.History;
            // A run an earlier host recorded, cut by the closing: open in its manifest, its process gone.
            var dead = new Inferpal.Services.Execution.FileHistoryService
            {
                WorkspaceRoot = () => root, UserHistoryRoot = live.UserHistoryRoot, Editor = Inferpal.Services.Persistence.ConversationStore.VsCodeSlot,
            };
            var file = Path.Combine(root, "A.cs");
            File.WriteAllText(file, "a0\n");
            dead.BeginRun();
            Assert.True((await dead.BackUpBeforeChangeAsync(file, CancellationToken.None)).Item1);
            File.WriteAllText(file, "a0\na1\n");
            var run = Assert.Single(dead.Runs);
            File.WriteAllText(run.ManifestPath!, Inferpal.Services.Execution.RunManifest.Serialize(run, open: true,
                Inferpal.Services.Execution.RunManifest.KeyIn(live.UserHistoryRoot)!, pid: int.MaxValue));

            var answered = await h.Client.InvokeWithParameterObjectAsync<AwayNoticeDto?>("session/away", new { endsOnAQuestion = false })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(run.Id, answered!.Run!.RunId);
            Assert.Null(answered.NoAnswer);
            var asked = await h.Client.InvokeWithParameterObjectAsync<AwayNoticeDto?>("session/away", new { endsOnAQuestion = true })
                .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(Strings.AwayNoAnswer("VS Code"), asked!.NoAnswer);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>An Agent Client Protocol thread's runs are its own: no start of VS Code says them as closed with it.</summary>
    [Theory]
    [InlineData(true, "vscode")]
    [InlineData(false, HostServer.AcpRunsEditor)]
    public async Task TheRunsOfAHost_AreRecordedUnderItsEditor(bool editorSurface, string editor)
    {
        using var h = CreateHarness();
        await h.Client.InvokeWithParameterObjectAsync<InitializeResult>("initialize", new { rootDir = h.RootDir, editorSurface })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Assert.Equal(editor, h.Server.CurrentSession!.Tools.History.Editor);
    }

    /// <summary>Both editors draw the notice at their start and mark it said when dismissed — read on the source: Visual
    /// Studio's view model cannot be built in the suite, and VS Code's webview runs in a browser.</summary>
    [Fact]
    public void BothEditors_ShowTheNoticeOfTheStart_AndMarkItSaid()
    {
        var repo = ConventionCoverageTests.RepoRoot();
        var vs = ConventionCoverageTests.CodeOnly(Path.Combine(repo, "Inferpal", "ToolWindow", "InferpalToolWindowData.Connection.cs"));
        Assert.Contains("await RestoreLateSlotAsync(ct);\n                await ShowAwayNoticeAsync();", vs.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("queue.MarkSaid(notice.RunIds, notice.TaskIds)", vs, StringComparison.Ordinal);
        // The start's own notices come after the conversation: the unanswered question is read past them.
        Assert.Contains("m.ToolName != SessionManager.NoticeMarker", vs, StringComparison.Ordinal);
        // Once per process — a solution opened later did not see Visual Studio close —, after the start's restore settled,
        // and the no-answer line never under a turn running now.
        Assert.Contains("if (_awayAsked || string.IsNullOrEmpty(root)) return;", vs, StringComparison.Ordinal);
        Assert.Contains("if (!_conversationRoot.EverBound && !PathComparer.SameDirectory(root, _lateRestoreTried)) return;", vs, StringComparison.Ordinal);
        Assert.Contains("notice.Run is not null && !IsLoading && !_sendStarting", vs, StringComparison.Ordinal);
        var xaml = File.ReadAllText(Path.Combine(repo, "Inferpal", "ToolWindow", "InferpalToolWindowContent.xaml"));
        foreach (var bound in new[] { "Value=\"away\"", "{Binding AwayDismissCommand}", "{Binding AwayReviewCommand}",
                                      "{Binding AwayUndoCommand}", "ItemsSource=\"{Binding AwayTasks}\"", "{Binding ActCommand}",
                                      "AutomationProperties.Name=\"{Binding ButtonName}\"" })
            Assert.Contains(bound, xaml, StringComparison.Ordinal);
        Assert.Contains("away", Inferpal.Services.Persistence.SessionManager.ScreenOnlyRoles);

        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(provider, @"await this\.showAwayNotice\(host\);").Count);
        Assert.Contains("sessionAwayDismissed(msg.runIds, msg.taskIds)", provider, StringComparison.Ordinal);
        Assert.Contains("if (this.awayAsked) {", provider, StringComparison.Ordinal);   // once per window
        Assert.Contains("(m.role === 'user' || m.role === 'assistant') && !m.notice", provider, StringComparison.Ordinal);
        var webview = WebviewRebuildTests.TsCode("webview/main.ts");
        Assert.Contains("renderAwayNotice(msg.notice)", webview, StringComparison.Ordinal);
        Assert.Contains("post({ type: 'awayDismissed'", webview, StringComparison.Ordinal);
    }
}
