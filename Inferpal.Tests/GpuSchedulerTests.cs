using System;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// GpuScheduler is a process-wide static gate that also writes the ChatBusySignal file — so it runs
// in the serialised signal collection with everything else that touches that folder. Acquiring a
// lease here used to clear the busy marker of a chat running in the developer's own editor, since
// ChatBusySignal.Clear() deletes the shared file whoever wrote it.
[Collection(SignalCollection.Name)]
public class GpuSchedulerTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public void Dispose() => _scratch.Dispose();

    static async Task<bool> CompletesWithin(Task task, int ms) =>
        await Task.WhenAny(task, Task.Delay(ms)) == task;

    [Fact]
    public async Task WaitForChatIdle_NoLease_ReturnsImmediately()
    {
        Assert.False(GpuScheduler.IsChatActive);
        await GpuScheduler.WaitForChatIdleAsync(CancellationToken.None); // must not block
    }

    [Fact]
    public async Task WaitForChatIdle_BlocksWhileLeased_ResumesOnDispose()
    {
        var lease = GpuScheduler.AcquireChatLease();
        Assert.True(GpuScheduler.IsChatActive);

        var waiter = GpuScheduler.WaitForChatIdleAsync(CancellationToken.None);
        Assert.False(await CompletesWithin(waiter, 60));   // blocked while the lease is held

        lease.Dispose();
        Assert.True(await CompletesWithin(waiter, 1000));   // released once disposed
        Assert.False(GpuScheduler.IsChatActive);
    }

    [Fact]
    public async Task ChatLease_IsReferenceCounted()
    {
        var a = GpuScheduler.AcquireChatLease();
        var b = GpuScheduler.AcquireChatLease();

        var waiter = GpuScheduler.WaitForChatIdleAsync(CancellationToken.None);

        a.Dispose();
        Assert.False(await CompletesWithin(waiter, 60));    // still one lease outstanding
        Assert.True(GpuScheduler.IsChatActive);

        b.Dispose();
        Assert.True(await CompletesWithin(waiter, 1000));   // last lease gone → idle
    }

    [Fact]
    public async Task WaitForChatIdle_HonoursCancellation_WithoutHanging()
    {
        using var lease = GpuScheduler.AcquireChatLease();
        using var cts   = new CancellationTokenSource();

        var waiter = GpuScheduler.WaitForChatIdleAsync(cts.Token);
        Assert.False(await CompletesWithin(waiter, 60));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var lease = GpuScheduler.AcquireChatLease();
        lease.Dispose();
        lease.Dispose();                       // second dispose must not under-count
        Assert.False(GpuScheduler.IsChatActive);
    }
}

[Collection(SignalCollection.Name)]
public class ChatBusySignalTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public ChatBusySignalTests()
    {
        SignalFile._isProcessAliveOverride = _ => true;
        SignalFile._nowOverride = () => DateTimeOffset.UnixEpoch.AddHours(1);
        ChatBusySignal.Clear();
    }

    public void Dispose()
    {
        ChatBusySignal.Clear();
        SignalFile._isProcessAliveOverride = null;
        SignalFile._nowOverride = null;
        ChatBusySignal.MaxAge = TimeSpan.FromMinutes(10);
        _scratch.Dispose();
    }

    [Fact]
    public void Write_ThenIsBusy_True()
    {
        ChatBusySignal.Write();
        Assert.True(ChatBusySignal.IsBusy());
    }

    [Fact]
    public void Clear_MakesNotBusy()
    {
        ChatBusySignal.Write();
        ChatBusySignal.Clear();
        Assert.False(ChatBusySignal.IsBusy());
    }

    [Fact]
    public void NoFile_IsNotBusy() =>
        Assert.False(ChatBusySignal.IsBusy());   // ctor cleared it

    [Fact]
    public void DeadWriterProcess_IsNotBusy()
    {
        ChatBusySignal.Write();
        SignalFile._isProcessAliveOverride = _ => false;   // writer crashed
        Assert.False(ChatBusySignal.IsBusy());
    }

    [Fact]
    public void StaleSignal_BeyondMaxAge_IsNotBusy()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddHours(1);
        SignalFile._nowOverride = () => t0;
        ChatBusySignal.Write();

        SignalFile._nowOverride = () => t0 + TimeSpan.FromMinutes(11); // past the 10-min fuse
        Assert.False(ChatBusySignal.IsBusy());
    }
}
