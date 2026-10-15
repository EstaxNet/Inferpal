using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Commands;
using Inferpal.Services.Tasks;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>/task propose</c> stopped before it ends keeps the changes it had proposed, says so, and <c>/task apply</c> on a
/// task still running says it is running — not that it proposed nothing.
/// </summary>
/// <remarks>
/// Each runner made its own recorder, and the cancellation that ended the run took it away: Stop threw away every change
/// the task had proposed, under "nothing was produced". And while the task ran, <c>/task apply t1 1</c> answered
/// "proposed no change" about a task that was proposing.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class TaskStoppedKeepsProposalsTests
{
    private static Task NoGate(CancellationToken _) => Task.CompletedTask;

    private static (BackgroundTaskQueue Queue, Task Proposed, Task<BackgroundTaskSnapshot> Finished) ProposingTask(bool propose)
    {
        var proposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<BackgroundTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new BackgroundTaskQueue(async (task, onStep, recorder, ct) =>
        {
            if (recorder is not null)
                await recorder.RequestApprovalAsync("write_file", "Write Cart.cs", ct, subject: "/p/Cart.cs",
                                                    diff: new DiffInfo("int a;\n", "int b;\n", "/p/Cart.cs"));
            proposed.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);   // still working when it is stopped
            return new BackgroundTaskQueue.TaskRunOutcome("never", recorder?.Proposals ?? []);
        }, waitForChatIdle: NoGate);
        queue.TaskFinished += s => finished.TrySetResult(s);
        return (queue, proposed.Task, finished.Task);
    }

    [Fact]
    public async Task AStoppedTask_KeepsWhatItProposed_AndApplyWaitsForItsEnd()
    {
        var (queue, proposed, finished) = ProposingTask(propose: true);
        using (queue)
        {
            var id = queue.Submit("tidy Cart.cs", proposeWrites: true)!;
            await proposed.WaitAsync(TimeSpan.FromSeconds(30));

            // Running: apply says so.
            var early = TaskCommandHandler.Handle(queue, ["/task", "apply", id, "1"]);
            Assert.Equal(Strings.TaskApplyWhileRunning(id), early.Message);
            Assert.Null(early.Apply);

            Assert.True(queue.Cancel(id));
            var snapshot = await finished.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(BackgroundTaskState.Cancelled, snapshot.State);
            Assert.Single(snapshot.PendingProposals);
            Assert.Equal(Strings.TaskCancelledWithProposals(id, 1), TaskCommandHandler.FinishedNotice(snapshot));

            // Ended: the kept proposal is applied through the ordinary path.
            var late = TaskCommandHandler.Handle(queue, ["/task", "apply", id, "1"]);
            Assert.NotNull(late.Apply);
            Assert.Equal("/p/Cart.cs", late.Apply!.Diff?.FilePath);
        }
    }

    [Fact]
    public async Task AStoppedTaskThatProposedNothing_SaysNothingWasProduced()
    {
        // Reference arm.
        var (queue, proposed, finished) = ProposingTask(propose: false);
        using (queue)
        {
            var id = queue.Submit("read Cart.cs")!;
            await proposed.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(queue.Cancel(id));
            var snapshot = await finished.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Empty(snapshot.PendingProposals);
            Assert.Equal(Strings.TaskCancelledNotice(id), TaskCommandHandler.FinishedNotice(snapshot));
            Assert.Equal(Strings.TaskNoProposals(id), TaskCommandHandler.Handle(queue, ["/task", "apply", id, "1"]).Message);
        }
    }
}
