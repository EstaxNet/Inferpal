using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Inferpal.Services.Hardware;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The machine stays awake while the model works and may sleep again once nothing does — taken by the first chat lease,
/// released after the last, let go while a turn waits for an approval, released on every way out.
/// </summary>
/// <remarks>
/// ⚠ Without the guard, a laptop left alone during a long run goes to sleep in the middle of it. Each test takes its own
/// guard over a counting platform; the suite's shared guard is silent (<c>TestConfigIsolation</c>), and the two tests
/// that hold the real chain swap it in a collection that runs alone. The Diagnostics collection: one test reads the
/// ring that others clear.
/// </remarks>
[Collection("Diagnostics")]
public sealed class SleepGuardTests
{
    /// <summary>Counts the locks taken and released; refuses when told to.</summary>
    internal sealed class CountingPlatform(bool refuse = false) : SleepGuard.IPlatform
    {
        public int Taken;
        public int Released;
        public Action<string>? Lost;

        public IDisposable? Inhibit(string reason, Action<string> lost, out string? failure)
        {
            Taken++;
            Lost = lost;
            if (refuse)
            {
                failure = "refused by the test";
                return null;
            }
            failure = null;
            return new Lock(this);
        }

        private sealed class Lock(CountingPlatform owner) : IDisposable
        {
            public void Dispose() => owner.Released++;
        }
    }

    [Fact]
    public void TheFirstHoldTakesTheLock_AndTheLastReleasesIt()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        Assert.False(guard.IsHeld);   // reference arm: nothing works, nothing is held
        var a = guard.Hold();
        var b = guard.Hold();
        Assert.True(guard.IsHeld);
        Assert.Equal(1, platform.Taken);   // one lock for two works

        a.Dispose();
        Assert.True(guard.IsHeld);         // the other still works
        b.Dispose();
        Assert.False(guard.IsHeld);
        Assert.Equal(1, platform.Released);

        b.Dispose();                       // twice is once
        Assert.Equal(1, platform.Released);
    }

    [Fact]
    public void WithinTheGrace_TheLockIsKept_AndTheNextHoldUsesIt()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.FromMinutes(5));   // never runs out within the test

        guard.Hold().Dispose();            // a turn's first request
        Assert.True(guard.IsHeld);         // kept for the request that follows
        using (guard.Hold()) Assert.True(guard.IsHeld);
        Assert.Equal(1, platform.Taken);   // no second lock: on macOS and Linux each one starts a process
        Assert.Equal(0, platform.Released);
    }

    [Fact]
    public async Task ATurnWaitingForAnApproval_LetsTheMachineSleep_AndTakesItBackOnTheAnswer()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        await Turn();

        async Task Turn()
        {
            using var run = guard.Hold();   // the run's lease, in this flow
            await Task.Yield();
            using (guard.PauseThisFlow())   // the card waits
            {
                Assert.False(guard.IsHeld);
                await Task.Yield();
            }
            Assert.True(guard.IsHeld);      // answered: the run works again
        }

        Assert.False(guard.IsHeld);
        Assert.Equal(2, platform.Taken);
        Assert.Equal(2, platform.Released);
    }

    [Fact]
    public async Task ACardShownOutsideTheTurn_NeverReleasesAnotherWorksLock()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        // A background task holds in its own flow…
        var task = await Task.Run(() => guard.Hold());
        // …and a dialog asks something here, in a flow that holds nothing.
        using (guard.PauseThisFlow())
            Assert.True(guard.IsHeld);

        task.Dispose();
        Assert.False(guard.IsHeld);
    }

    [Fact]
    public async Task AChildTaskThatOutlivesItsTurnsHold_NeverPausesAnotherWorksLock()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);
        var go       = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IDisposable> child;

        // A task the turn starts inherits the turn's flow — and outlives the turn's hold.
        using (guard.Hold())
            child = Task.Run(async () => { await go.Task; return guard.PauseThisFlow(); });
        var task = await Task.Run(() => guard.Hold());   // a background task, in its own flow
        go.SetResult();

        using (await child)
            Assert.True(guard.IsHeld);   // the turn's hold is gone: nothing of the task's is paused
        task.Dispose();
        Assert.False(guard.IsHeld);
    }

    [Fact]
    public async Task AStoppedOrFailedTurn_ReleasesTheLock()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            using var lease = guard.Hold();
            await Task.Yield();
            throw new OperationCanceledException();   // Stop
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = guard.Hold();
            await Task.Yield();
            throw new InvalidOperationException("the backend refused");
        });

        Assert.False(guard.IsHeld);
        Assert.Equal(platform.Taken, platform.Released);
    }

    [Fact]
    public void ARefusedLock_IsSaidOnce_AndNotAskedForAgainAtEveryRequest()
    {
        var platform = new CountingPlatform(refuse: true);
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        for (var i = 0; i < 5; i++) guard.Hold().Dispose();

        Assert.Equal(1, platform.Taken);
        Assert.Contains(Inferpal.Services.Diagnostics.Snapshot(),
                        e => e.Context == "SleepGuard" && e.Detail.Contains("refused by the test", StringComparison.Ordinal));
    }

    [Fact]
    public void ALockLostAtOnce_IsTakenForARefusal_AndNotRetried()
    {
        var platform = new CountingPlatform();
        var guard    = new SleepGuard(platform, TimeSpan.Zero);

        using (guard.Hold())
        {
            platform.Lost!("systemd-inhibit ended with code 1: Access denied");   // the command died at once
            Assert.False(guard.IsHeld);
        }
        guard.Hold().Dispose();
        Assert.Equal(1, platform.Taken);
    }

    // ── the funnel, end to end ──────────────────────────────────────────────

    /// <summary>A card that records whether the machine is kept awake while it waits, then allows.</summary>
    private sealed class WatchingApproval(Func<bool> held, List<bool> seen) : Inferpal.Services.Execution.ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        protected override Task<Inferpal.Services.Execution.ApprovalDecision> PromptUserAsync(
            string message, Inferpal.Services.CodeActions.DiffInfo? diff, CancellationToken ct)
        {
            seen.Add(held());
            return Task.FromResult(Inferpal.Services.Execution.ApprovalDecision.Once);
        }
    }

    [Fact]
    public async Task ARunsLease_KeepsTheMachineAwake_AndItsApprovalCardLetsItSleepUntilAnswered()
    {
        // The real chain: a chat lease taken by a run, a tool awaited inside it, its approval through the funnel.
        var previous = SleepGuard.Shared;
        var guard    = new SleepGuard(new CountingPlatform(), TimeSpan.Zero);
        SleepGuard.Shared = guard;
        try
        {
            var seen     = new List<bool>();
            var approval = new WatchingApproval(() => guard.IsHeld, seen);

            await RunAsync();
            Assert.Equal([true, false, true], seen);   // working, the card waiting, answered
            Assert.False(guard.IsHeld);                 // the run over

            async Task RunAsync()
            {
                using var lease = GpuScheduler.AcquireChatLease();
                seen.Add(guard.IsHeld);
                await Task.Yield();
                await ToolCallAsync();
                seen.Add(guard.IsHeld);
            }

            async Task ToolCallAsync()
            {
                await Task.Yield();
                await approval.RequestApprovalAsync("run_command", "dotnet test", CancellationToken.None);
            }
        }
        finally
        {
            SleepGuard.Shared = previous;
        }
    }

    [Fact]
    public async Task AStepPause_LetsTheMachineSleepUntilResumed()
    {
        var previous = SleepGuard.Shared;
        var guard    = new SleepGuard(new CountingPlatform(), TimeSpan.Zero);
        SleepGuard.Shared = guard;
        try
        {
            var duringPause = true;
            var step = new Inferpal.Services.Execution.StepModeToolRegistry(
                Inferpal.Services.Execution.EmptyToolRegistry.Instance, _ => { duringPause = guard.IsHeld; return Task.CompletedTask; });

            using (GpuScheduler.AcquireChatLease())
            {
                await step.ExecuteAsync("read_file", System.Text.Json.JsonDocument.Parse("{}").RootElement, CancellationToken.None);
                Assert.True(guard.IsHeld);   // resumed
            }
            Assert.False(duringPause);
        }
        finally
        {
            SleepGuard.Shared = previous;
        }
    }

    // ── the funnel ──────────────────────────────────────────────────────────

    [Fact]
    public void EveryChatLease_HoldsTheMachineAwake_AndEveryApprovalPausesItsOwnHold()
    {
        var core = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services");
        var scheduler = ConventionCoverageTests.CodeOnly(Path.Combine(core, "Hardware", "GpuScheduler.cs"));
        Assert.Contains("SleepGuard.Shared.Hold()", scheduler, StringComparison.Ordinal);
        Assert.Contains("awake.Dispose()", scheduler, StringComparison.Ordinal);

        var approval = ConventionCoverageTests.CodeOnly(Path.Combine(core, "Execution", "ApprovalServiceBase.cs"));
        Assert.Matches(new Regex(@"using \(Services\.Hardware\.SleepGuard\.Shared\.PauseThisFlow\(\)\)\s*promptDecision = await PromptUserAsync"),
                       approval);

        // A step pause waits for a person too, in the decorator both editors share.
        var step = ConventionCoverageTests.CodeOnly(Path.Combine(core, "Execution", "StepModeToolRegistry.cs"));
        Assert.Matches(new Regex(@"using \(Services\.Hardware\.SleepGuard\.Shared\.PauseThisFlow\(\)\)\s*await onAfterTool\(ct\);"), step);
    }

    [Fact]
    public void EveryChatLease_IsTakenInAUsing_SoEveryWayOutReleasesIt()
    {
        // ⚠ The property behind "released on Stop, on an error": a lease the code forgets to dispose keeps the machine
        // awake until the process ends. The subject is the call, wherever it is in the Core.
        var sites = 0;
        var offenders = new List<string>();
        foreach (var file in ConventionCoverageTests.CoreSources(""))
        {
            var code = ConventionCoverageTests.CodeOnly(file);
            foreach (Match m in Regex.Matches(code, @"[^\n]*GpuScheduler\.AcquireChatLease\(\)[^\n]*"))
            {
                sites++;
                if (!Regex.IsMatch(m.Value, @"\busing\b")) offenders.Add($"{Path.GetFileName(file)}: {m.Value.Trim()}");
            }
        }
        Assert.True(sites >= 5, $"only {sites} chat lease site(s) read: the scan is blind.");
        Assert.True(offenders.Count == 0, "Chat leases not taken in a using:\n" + string.Join("\n", offenders));
    }

    // ── the real platform ───────────────────────────────────────────────────

    [Fact]
    public void OnWindows_ThePowerRequestIsAccepted()
    {
        if (!OperatingSystem.IsWindows()) return;   // the other systems have their own test
        // Its EFFECT cannot be read without elevation (powercfg /requests): measured, docs/probes/long-runs. The
        // acceptance is what the product can know.
        using var held = new SystemPlatform.Windows().Inhibit(SleepGuard.Reason, _ => { }, out var failure);
        Assert.Null(failure);
        Assert.NotNull(held);
    }

    [Fact]
    public async Task OnMacOSAndLinux_TheSystemListsTheLock_WhileItIsHeld_AndNotAfter()
    {
        if (OperatingSystem.IsWindows()) return;
        var (list, listArgs, mark) = OperatingSystem.IsMacOS()
            ? ("pmset", "-g assertions", "caffeinate")
            : ("systemd-inhibit", "--list", "Inferpal");

        var lost = false;
        var held = SystemPlatform.ForThisSystem().Inhibit(SleepGuard.Reason, _ => lost = true, out var failure);
        if (held is null) return;   // UNDECIDED: the tool is not there (no systemd) — never read as a pass
        try
        {
            await Task.Delay(500);
            if (lost) return;       // UNDECIDED: refused here (a CI runner without a login session)
            Assert.Contains(mark, await ListAsync(list, listArgs), StringComparison.Ordinal);
        }
        finally
        {
            held.Dispose();
        }
        if (OperatingSystem.IsMacOS())
        {
            // Ours by its arguments — another caffeinate may run on the machine — and gone once released.
            var ours = $"caffeinate -i -w {Environment.ProcessId}";
            Assert.DoesNotContain(ours, await ListAsync("ps", "-A -o args"), StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain(mark, await ListAsync(list, listArgs), StringComparison.Ordinal);
        }
    }

    private static async Task<string> ListAsync(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return output;
    }
}
