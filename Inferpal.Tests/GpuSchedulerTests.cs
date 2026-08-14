using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// GpuScheduler is a process-wide static gate that also writes the ChatBusySignal file — so it runs
// in the serialised signal collection with everything else that touches that folder. Acquiring a
// lease here used to clear the busy marker of a chat running in the developer's own editor, back
// when ChatBusySignal was a single shared file with an unconditional Clear() (fixed by §22 G4:
// per-writer markers, Clear() only deletes its own).
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

    // ── §22 family B (gate G4): one file per writer ─────────────────────────────
    // The repaired defect: one shared file + an unconditional Clear() → the first host
    // to finish erased the other's marker, and FIM resumed mid-chat.

    /// <summary>A marker from "another" host: same payload shape, different pid — written
    /// through the same <see cref="SignalFile"/> grammar the production reader parses.</summary>
    private static string WriteForeignMarker(int pid, long ts)
    {
        var path = SignalFile.KeyedPathFor("chat_busy", pid);
        SignalFile.Write(path, new ChatBusyState(pid, ts), "test");
        return path;
    }

    [Fact]
    public void Clear_DoesNotSilenceAnotherWritersMarker()
    {
        var ts = SignalFile.Now.ToUnixTimeMilliseconds();
        WriteForeignMarker(424242, ts);   // a chat in flight in another host

        ChatBusySignal.Write();
        ChatBusySignal.Clear();           // this process finishes first

        Assert.True(ChatBusySignal.IsBusy());   // the other chat is still running
    }

    [Fact]
    public void IsBusy_SeesAnyLiveWriter_NotJustSelf()
    {
        WriteForeignMarker(424242, SignalFile.Now.ToUnixTimeMilliseconds());
        Assert.True(ChatBusySignal.IsBusy());
    }

    [Fact]
    public void LegacyUnscopedFile_IsHonoured_ButNeverWrittenOrDeleted()
    {
        // §22 migration, family-B nuance (review 2026-08-15): chat-busy is a machine-wide
        // *hint*, not an identity channel — honouring an old-version writer's unscoped file
        // keeps FIM quiet during that chat, which is correct. Writing or deleting it stays
        // forbidden: that was the repaired defect.
        var legacy = SignalFile.PathFor("chat_busy.json");
        SignalFile.Write(legacy,
            new ChatBusyState(Environment.ProcessId, SignalFile.Now.ToUnixTimeMilliseconds()),
            "test");

        Assert.True(ChatBusySignal.IsBusy());
        ChatBusySignal.Clear();            // clears only our own scoped marker…
        Assert.True(File.Exists(legacy));  // …the legacy file is nobody's to delete
    }

    [Fact]
    public void LegacyFilePresent_DoesNotHideLiveScopedMarkers()
    {
        // The review's headline finding: the legacy name crashed the marker-name parser and
        // the blanket catch emptied the whole enumeration — one leftover chat_busy.json made
        // IsBusy() permanently false, hiding every live scoped marker. This pins the exact
        // production combination: legacy file + a live scoped marker from another writer.
        SignalFile.Write(SignalFile.PathFor("chat_busy.json"),
            new ChatBusyState(999999, 0L), "test");   // stale legacy: not busy by itself
        SignalFile._isProcessAliveOverride = pid => pid == 424242;
        WriteForeignMarker(424242, SignalFile.Now.ToUnixTimeMilliseconds());

        Assert.True(ChatBusySignal.IsBusy());
    }

    [Fact]
    public void DeadLegacyWriter_NotBusy_AndLegacyNotDeleted()
    {
        SignalFile._isProcessAliveOverride = _ => false;
        var legacy = SignalFile.PathFor("chat_busy.json");
        SignalFile.Write(legacy,
            new ChatBusyState(424242, SignalFile.Now.ToUnixTimeMilliseconds()), "test");

        Assert.False(ChatBusySignal.IsBusy());
        Assert.True(File.Exists(legacy));   // dead-marker cleanup never touches the legacy file
    }

    [Fact]
    public void DeadWritersMarker_IsCleanedUp()
    {
        SignalFile._isProcessAliveOverride = pid => pid != 424242;   // 424242 is dead
        var path = WriteForeignMarker(424242, SignalFile.Now.ToUnixTimeMilliseconds());

        Assert.False(ChatBusySignal.IsBusy());
        Assert.False(File.Exists(path));   // opportunistic cleanup: dead markers don't pile up
    }

    [Fact]
    public void StaleForeignMarker_AliveWriter_NotBusy_ButNotDeleted()
    {
        // Alive but past the 10-minute fuse: silent for FIM, but a living writer's file is
        // never deleted by someone else — that deletion is the repaired defect.
        var t0 = DateTimeOffset.UnixEpoch.AddHours(1);
        SignalFile._nowOverride = () => t0;
        var path = WriteForeignMarker(424242, t0.ToUnixTimeMilliseconds());

        SignalFile._nowOverride = () => t0 + TimeSpan.FromMinutes(11);
        Assert.False(ChatBusySignal.IsBusy());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void RefreshBusyMarker_KeepsALongRunUnderTheStalenessFuse()
    {
        // A single agent run holds one lease for its whole loop; without periodic re-stamping
        // the marker trips the 10-minute fuse and FIM resumes against the busy GPU mid-run.
        // The timer cadence is not under test — the refresh itself is, deterministically.
        var t0 = DateTimeOffset.UnixEpoch.AddHours(1);
        SignalFile._nowOverride = () => t0;

        using var lease = GpuScheduler.AcquireChatLease();
        SignalFile._nowOverride = () => t0 + TimeSpan.FromMinutes(11);
        Assert.False(ChatBusySignal.IsBusy());   // stale without a refresh

        GpuScheduler.RefreshBusyMarker();        // what the timer does every 4 minutes
        Assert.True(ChatBusySignal.IsBusy());
    }
}
