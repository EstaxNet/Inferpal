using System.Collections.Concurrent;
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

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(done(), "the wait was never said");
    }

    [Fact]
    public async Task AWaitWithNoLineOfItsOwn_IsSaidToTheOpenScope_ThenGivesItsPlaceBack()
    {
        var said = new ConcurrentQueue<string?>();
        using (ModelWaitScope.Open(said.Enqueue))
        {
            using var wait = new PrefillWait(null, 12_000, null, firstSaid: Soon);
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
        var scope = new ConcurrentQueue<string?>();
        var own   = new ConcurrentQueue<string?>();
        using (ModelWaitScope.Open(scope.Enqueue))
        {
            using var wait = new PrefillWait(own.Enqueue, 12_000, null, firstSaid: Soon);
            await UntilAsync(() => !own.IsEmpty);
        }
        Assert.Empty(scope);
    }

    /// <summary>Work started under a turn and still running after it carries the scope — closed, it says nothing, so a
    /// late line never lands in a turn that has ended.</summary>
    [Fact]
    public async Task AScopeClosed_SaysNothingMore_ToWorkStartedUnderIt()
    {
        var said    = new ConcurrentQueue<string?>();
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

    [Fact]
    public void ANestedScope_GivesBackTheOuterOne()
    {
        var outer = new ConcurrentQueue<string?>();
        var inner = new ConcurrentQueue<string?>();
        Assert.Null(ModelWaitScope.Current);
        using (ModelWaitScope.Open(outer.Enqueue))
        {
            using (ModelWaitScope.Open(inner.Enqueue)) ModelWaitScope.Current!("inner");
            ModelWaitScope.Current!("outer");
        }
        Assert.Null(ModelWaitScope.Current);
        Assert.Equal(["inner"], inner);
        Assert.Equal(["outer"], outer);
    }
}
