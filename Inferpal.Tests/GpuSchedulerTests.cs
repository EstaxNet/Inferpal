using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// GpuScheduler is a process-wide static gate that also writes the ChatBusySignal file — so it runs
// in the serialised signal collection with everything else that touches that folder. Acquiring a
// lease here never clears the busy marker of a chat running in the developer's own editor: a
// single shared file with an unconditional Clear() would, so markers are per writer (signal files
// scoped per instance) and Clear() only deletes its own.
[Collection(SignalCollection.Name)]
public class GpuSchedulerTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();

    public void Dispose() => _scratch.Dispose();

    static async Task<bool> CompletesWithin(Task task, int ms) =>
        await Task.WhenAny(task, Task.Delay(ms)) == task;

    // With in-process hosting, the chat and ghost text share the process. The chat > FIM priority
    // must no longer depend on the file marker, whose write is best-effort (failure swallowed):
    // the lease counter, by contrast, is exact.
    [Fact]
    public void ShouldFimYield_TrueWhileLeased_EvenWithoutTheBusyMarker()
    {
        Assert.False(GpuScheduler.ShouldFimYield());          // repos : ni lease, ni marqueur

        var lease = GpuScheduler.AcquireChatLease();
        Inferpal.Services.Signals.ChatBusySignal.Clear();     // simulates the failed write
        Assert.False(File.Exists(Inferpal.Services.Signals.ChatBusySignal.FilePath));

        Assert.True(GpuScheduler.ShouldFimYield());            // le lease suffit

        lease.Dispose();
        Assert.False(GpuScheduler.ShouldFimYield());
    }

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

    // ── A wait for a person lets the GPU go, the turn stays in flight ───────────

    /// <summary>What the scheduler and this process's marker say at one moment.</summary>
    private readonly record struct Seen(bool Working, bool InFlight, bool? MarkerPaused);

    private static Seen Now() => new(GpuScheduler.IsChatActive, GpuScheduler.IsTurnInFlight,
        SignalFile.TryRead<ChatBusyState>(Inferpal.Services.Signals.ChatBusySignal.FilePath) is { } m ? m.Paused ?? false : null);

    /// <summary>A card that records, while it waits, what the scheduler says, then allows.</summary>
    private sealed class WatchingApproval(List<Seen> seen)
        : Inferpal.Services.Execution.ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        protected override Task<Inferpal.Services.Execution.ApprovalDecision> PromptUserAsync(
            string message, Inferpal.Services.CodeActions.DiffInfo? diff, CancellationToken ct)
        {
            seen.Add(Now());
            return Task.FromResult(Inferpal.Services.Execution.ApprovalDecision.Once);
        }
    }

    [Fact]
    public async Task AnApprovalCard_LetsTheGpuGoWhileItWaits_TheTurnStaysInFlight_AndTheAnswerTakesItBack()
    {
        var seen = new List<Seen>();
        var approval = new WatchingApproval(seen);
        using (GpuScheduler.AcquireChatLease())
        {
            seen.Add(Now());
            await Task.Yield();
            await approval.RequestApprovalAsync("run_command", "dotnet test", CancellationToken.None);
            seen.Add(Now());
        }
        seen.Add(Now());
        Assert.Equal(
        [
            new Seen(true, true, false),     // working: the GPU is busy, the marker says so
            new Seen(false, true, true),     // the card waits: the GPU is free, the turn still in flight, the marker paused
            new Seen(true, true, false),     // answered: working again
            new Seen(false, false, null),    // the run over: no marker at all
        ], seen);
    }

    [Fact]
    public async Task ATurnWaitingForAPerson_HoldsWhatMustNotBeCutUnderIt_NotTheGpu()
    {
        var lease = GpuScheduler.AcquireChatLease();
        var pause = GpuScheduler.PauseThisFlow();
        try
        {
            // The embedding loops go on; an MCP restart, an unload and a /task run wait for the turn.
            Assert.True(await CompletesWithin(GpuScheduler.WaitForChatIdleAsync(CancellationToken.None), 2000));
            var mcp = GpuScheduler.WaitForLocalTurnsToEndAsync(CancellationToken.None);
            var task = GpuScheduler.WaitForTurnsToEndAsync(CancellationToken.None);
            Assert.False(await CompletesWithin(Task.WhenAny(mcp, task), 200));
            Assert.True(Inferpal.Services.Presentation.ModelUnloader.AnsweringAnywhere());

            pause.Dispose();
            lease.Dispose();
            Assert.True(await CompletesWithin(Task.WhenAll(mcp, task), 30_000));
        }
        finally
        {
            pause.Dispose();
            lease.Dispose();
        }
    }

    [Fact]
    public async Task ALoopAlreadyWaiting_GoesOnWhenTheCardOpens()
    {
        using var lease = GpuScheduler.AcquireChatLease();
        var waiting = GpuScheduler.WaitForChatIdleAsync(CancellationToken.None);
        Assert.False(await CompletesWithin(waiting, 200));
        using (GpuScheduler.PauseThisFlow())
            Assert.True(await CompletesWithin(waiting, 30_000));
    }

    [Fact]
    public async Task AShortWait_KeepsTheGpu_ALongOneLetsItGo()
    {
        var grace = GpuScheduler.PauseGrace;
        GpuScheduler.PauseGrace = TimeSpan.FromMilliseconds(300);
        try
        {
            using var lease = GpuScheduler.AcquireChatLease();
            using (GpuScheduler.PauseThisFlow())
                Assert.True(GpuScheduler.IsChatActive);   // answered within the grace: the chat model never left
            using (GpuScheduler.PauseThisFlow())
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (GpuScheduler.IsChatActive && DateTime.UtcNow < deadline) await Task.Delay(20);
                Assert.False(GpuScheduler.IsChatActive);   // the card waits on: the GPU goes to the rest of the machine
            }
            Assert.True(GpuScheduler.IsChatActive);
        }
        finally
        {
            GpuScheduler.PauseGrace = grace;
        }
    }

    [Fact]
    public async Task ATask_DoesNotStartBesideATurnWaitingAtACard()
    {
        var started = new TaskCompletionSource();
        using var queue = new Inferpal.Services.Tasks.BackgroundTaskQueue((_, _, _) =>
        {
            started.TrySetResult();
            return Task.FromResult(Inferpal.Services.Tasks.BackgroundTaskQueue.TaskRunOutcome.Of("report"));
        });
        var lease = GpuScheduler.AcquireChatLease();
        var pause = GpuScheduler.PauseThisFlow();
        try
        {
            Assert.NotNull(queue.Submit("map the callers"));
            // Started now, it would compete with the turn for the GPU as soon as the person answers.
            Assert.False(await CompletesWithin(started.Task, 300));
        }
        finally
        {
            pause.Dispose();
            lease.Dispose();
        }
        Assert.True(await CompletesWithin(started.Task, 30_000));
    }

    /// <summary>
    /// Visual Studio asks whether a turn runs at ONE door (<see cref="TurnDoor"/>): its MCP restarts (Settings, /import, a
    /// new solution) and its settings follows wait for the turn — a long command between two model calls, a turn waiting at
    /// a card included —, never for the GPU. A lease read alone missed those gaps. One named exception: /setup's own door,
    /// which runs as a turn of the window and must still change the server (only a model call in flight keeps it).
    /// </summary>
    [Fact]
    public void VisualStudioAsksWhetherATurnRuns_AtTheTurnDoor()
    {
        var folder = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal", "ToolWindow");
        var code = Directory.EnumerateFiles(folder, "*.cs").ToDictionary(Path.GetFileName, ConventionCoverageTests.CodeOnly);
        Assert.True(code.Count >= 10);   // witness: the folder was read
        Assert.DoesNotContain(code.Values, c => c.Contains("GpuScheduler.IsChatActive", StringComparison.Ordinal));
        Assert.DoesNotContain(code.Values, c => c.Contains("GpuScheduler.WaitForLocalTurnsToEndAsync(", StringComparison.Ordinal));
        Assert.True(code.Values.Sum(c => CountOf(c, "TurnDoor.Busy")) >= 6);
        Assert.True(code.Values.Sum(c => CountOf(c, "TurnDoor.IdleAsync(")) >= 2);

        var leaseReads = code.Where(kv => kv.Value.Contains("GpuScheduler.IsTurnInFlight", StringComparison.Ordinal))
                             .Select(kv => kv.Key).ToList();
        Assert.Equal(["InferpalToolWindowData.Connection.cs"], leaseReads);
        var connection = code["InferpalToolWindowData.Connection.cs"];
        Assert.Equal(1, CountOf(connection, "GpuScheduler.IsTurnInFlight"));
        var at = connection.IndexOf("private void FollowServerForSetup()", StringComparison.Ordinal);
        Assert.True(at >= 0, "/setup's own door is gone: its exception names nothing any more");
        var setup = connection[at..];
        Assert.Contains("GpuScheduler.IsTurnInFlight", setup[..setup.IndexOf('}')], StringComparison.Ordinal);

        static int CountOf(string text, string what) =>
            (text.Length - text.Replace(what, string.Empty, StringComparison.Ordinal).Length) / what.Length;
    }

    [Fact]
    public async Task APauseInAnotherFlow_LeavesThisTurnsLease()
    {
        var holding = new TaskCompletionSource();
        var done    = new TaskCompletionSource();
        var turn = Task.Run(async () =>
        {
            using var lease = GpuScheduler.AcquireChatLease();
            holding.SetResult();
            await done.Task;
        });
        await holding.Task;
        // A dialog shown outside that turn (its own flow holds no lease) must not let the turn's lease go.
        using (GpuScheduler.PauseThisFlow())
            Assert.True(GpuScheduler.IsChatActive);
        done.SetResult();
        await turn;
        Assert.False(GpuScheduler.IsChatActive);
    }

    [Fact]
    public void NestedLeasesOfOneFlow_PauseAndResumeTogether_AndCountExactly()
    {
        var run  = GpuScheduler.AcquireChatLease();
        var step = GpuScheduler.AcquireChatLease();   // a request inside the run, same flow
        using (GpuScheduler.PauseThisFlow())
            Assert.False(GpuScheduler.IsChatActive);
        Assert.True(GpuScheduler.IsChatActive);
        step.Dispose();
        Assert.True(GpuScheduler.IsChatActive);
        run.Dispose();
        Assert.False(GpuScheduler.IsChatActive);
        Assert.False(GpuScheduler.IsTurnInFlight);
    }

    [Fact]
    public void ALeaseEndedDuringItsPause_IsNotTakenBackAtTheAnswer()
    {
        var lease = GpuScheduler.AcquireChatLease();
        var pause = GpuScheduler.PauseThisFlow();
        lease.Dispose();   // Stop on the card: the run ends while it waits
        pause.Dispose();
        Assert.Equal(new Seen(false, false, null), Now());
    }

    // ── Background loops yield to another process's chat ────────────────────────

    [Fact]
    public async Task ABackgroundWait_YieldsToAnotherProcessWorking_NotToAPausedOrOldOne_NorToItsOwnMarker()
    {
        SignalFile._isProcessAliveOverride = _ => true;
        var poll = GpuScheduler.ElsewherePoll;
        GpuScheduler.ElsewherePoll = TimeSpan.FromMilliseconds(20);
        var other = SignalFile.KeyedPathFor("chat_busy", 424242);
        void Other(bool? paused) =>
            SignalFile.Write(other, new ChatBusyState(424242, SignalFile.Now.ToUnixTimeMilliseconds(), paused), "test");
        // Past the cache of an idle answer (one look per poll period).
        static Task PastTheCache() => Task.Delay(100);
        try
        {
            // This process's own marker, without a lease: not waited for — its leases are known in memory.
            Inferpal.Services.Signals.ChatBusySignal.Write();
            Assert.True(await CompletesWithin(GpuScheduler.WaitForChatIdleAsync(CancellationToken.None), 2000));
            Inferpal.Services.Signals.ChatBusySignal.Clear();

            // Another editor's turn waiting at a card, and a writer older than the pause: the GPU is not theirs to keep,
            // but a /task run does not start beside their turn.
            foreach (var paused in new bool?[] { true, null })
            {
                Other(paused);
                await PastTheCache();
                Assert.True(await CompletesWithin(GpuScheduler.WaitForChatIdleAsync(CancellationToken.None), 2000));
                Assert.False(await CompletesWithin(GpuScheduler.WaitForTurnsToEndAsync(CancellationToken.None), 200));
            }

            // Another editor's chat working on the same GPU: the loop waits, and goes on once that chat ends.
            Other(false);
            await PastTheCache();
            var wait = GpuScheduler.WaitForChatIdleAsync(CancellationToken.None);
            Assert.False(await CompletesWithin(wait, 300));
            // An MCP restart waits for this process's turns only: another editor's chat does not hold it.
            Assert.True(await CompletesWithin(GpuScheduler.WaitForLocalTurnsToEndAsync(CancellationToken.None), 2000));
            SignalFile.Delete(other);
            Assert.True(await CompletesWithin(wait, 30_000));
        }
        finally
        {
            SignalFile.Delete(other);
            GpuScheduler.ElsewherePoll = poll;
            SignalFile._isProcessAliveOverride = null;
        }
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

    // ── Chat-busy marker: one file per writer ───────────────────────────────────
    // Why: with one shared file + an unconditional Clear(), the first host to finish
    // erases the other's marker, and FIM resumes mid-chat.

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
        // Migration to per-writer files, one nuance: chat-busy is a machine-wide
        // *hint*, not an identity channel — honouring an old-version writer's unscoped file
        // keeps FIM quiet during that chat, which is correct. Writing or deleting it stays
        // forbidden: a shared file is exactly what erases another host's marker.
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
