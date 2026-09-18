using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class StepModeToolRegistryTests
{
    // ── Minimal fake registry ────────────────────────────────────────────────

    private sealed class FakeRegistry : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions { get; init; } = [];
        public DiffInfo? DiffToReturn { get; set; }
        public string ResultToReturn { get; set; } = "ok";
        public bool ThrowOnExecute { get; set; }
        public int ExecuteCallCount { get; private set; }

        public DiffInfo? ConsumeDiff() => DiffToReturn;

        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
        {
            ExecuteCallCount++;
            if (ThrowOnExecute) throw new InvalidOperationException("inner error");
            return Task.FromResult(ResultToReturn);
        }
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Definitions_DelegatesToInner()
    {
        var def  = new ToolDefinition("function", new ToolFunction("tool", "desc", new { }));
        var fake = new FakeRegistry { Definitions = [def] };
        var sut  = new StepModeToolRegistry(fake, _ => Task.CompletedTask);

        Assert.Same(def, sut.Definitions[0]);
    }

    [Fact]
    public void ConsumeDiff_DelegatesToInner()
    {
        var diff = new DiffInfo("old", "new", "file.cs");
        var fake = new FakeRegistry { DiffToReturn = diff };
        var sut  = new StepModeToolRegistry(fake, _ => Task.CompletedTask);

        Assert.Same(diff, sut.ConsumeDiff());
    }

    [Fact]
    public void ConsumeDiff_NoDiff_ReturnsNull()
    {
        var fake = new FakeRegistry();
        var sut  = new StepModeToolRegistry(fake, _ => Task.CompletedTask);

        Assert.Null(sut.ConsumeDiff());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsInnerResult()
    {
        var fake = new FakeRegistry { ResultToReturn = "tool output" };
        var sut  = new StepModeToolRegistry(fake, _ => Task.CompletedTask);

        var result = await sut.ExecuteAsync("tool", default, CancellationToken.None);

        Assert.Equal("tool output", result);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackInvokedAfterInner()
    {
        var callbackInvoked = false;

        var inner = new FakeRegistry();
        var sut = new StepModeToolRegistry(
            inner,
            _ => { callbackInvoked = true; return Task.CompletedTask; });

        await sut.ExecuteAsync("t", default, CancellationToken.None);

        Assert.Equal(1, inner.ExecuteCallCount);
        Assert.True(callbackInvoked);
    }

    [Fact]
    public async Task ExecuteAsync_InnerThrows_CallbackNotInvoked()
    {
        var callbackInvoked = false;
        var fake = new FakeRegistry { ThrowOnExecute = true };
        var sut  = new StepModeToolRegistry(fake, _ => { callbackInvoked = true; return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.ExecuteAsync("tool", default, CancellationToken.None));

        Assert.False(callbackInvoked);
    }

    [Fact]
    public async Task ExecuteAsync_CallbackReceivesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken captured = default;

        var fake = new FakeRegistry();
        var sut  = new StepModeToolRegistry(fake, ct => { captured = ct; return Task.CompletedTask; });

        await sut.ExecuteAsync("t", default, cts.Token);

        Assert.Equal(cts.Token, captured);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationDuringCallback_Propagates()
    {
        // ⚠ Cancellation falls DURING the callback, as the name says. The previous version
        // cancelled before the call, which the "one tool at a time" gate now intercepts upstream —
        // a different scenario, covered by the next test.
        using var cts = new CancellationTokenSource();
        var fake = new FakeRegistry();
        var sut  = new StepModeToolRegistry(
            fake,
            ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ExecuteAsync("t", default, cts.Token));

        Assert.Equal(1, fake.ExecuteCallCount);   // witness: the tool did run before the pause
    }

    /// <summary>
    /// An already-cancelled token is refused <b>before</b> the tool runs.
    /// </summary>
    /// <remarks>
    /// A consequence of the "one tool at a time" gate: it waits on the token, so it sees the
    /// cancellation first. That is the right behaviour — a cancelled turn must not run one more
    /// tool — and the type thrown is still an <see cref="OperationCanceledException"/>, from which
    /// <c>TaskCanceledException</c> derives: the contract "only cancellation crosses
    /// <c>RunAsync</c>" is unchanged for every caller, all of which catch the base class.
    /// </remarks>
    [Fact]
    public async Task ExecuteAsync_AlreadyCancelledToken_RefusesBeforeRunningTheTool()
    {
        using var cts = new CancellationTokenSource();
        var fake      = new FakeRegistry();
        var callbacks = 0;
        var sut       = new StepModeToolRegistry(fake, _ => { callbacks++; return Task.CompletedTask; });

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ExecuteAsync("t", default, cts.Token));

        Assert.Equal(0, fake.ExecuteCallCount);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task ExecuteAsync_ComposedDecorators_BothCallbacksInvoked()
    {
        var calls = new List<int>();
        var fake  = new FakeRegistry();
        var inner = new StepModeToolRegistry(fake,  _ => { calls.Add(1); return Task.CompletedTask; });
        var outer = new StepModeToolRegistry(inner, _ => { calls.Add(2); return Task.CompletedTask; });

        await outer.ExecuteAsync("t", default, CancellationToken.None);

        Assert.Equal([1, 2], calls);
    }

    // ── Step mode and a parallel batch ────────────────────────────────────────

    /// <summary>
    /// The "stepper" of both front-ends, reduced to what matters: a <b>single resume slot</b>.
    /// </summary>
    /// <remarks>
    /// This is not a simplification for the test's sake — it is the exact shape of the shipped
    /// code: <c>HostServer.PauseForStepAsync</c> writes <c>s.StepResume</c> and
    /// <c>InferpalToolWindowData</c> writes <c>_stepResume</c>, both a single field replaced at
    /// every pause. A second concurrent pause makes the first one <b>unreachable</b>.
    /// </remarks>
    private sealed class SingleSlotStepper
    {
        private TaskCompletionSource<bool>? _resume;

        /// <summary>Released at every announced "pause", like the notification the UI receives.</summary>
        public SemaphoreSlim Announced { get; } = new(0);

        public async Task PauseAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _resume = tcs;
            Announced.Release();
            try { await tcs.Task.WaitAsync(ct); }
            finally { _resume = null; }
        }

        /// <summary>Le clic « Reprendre ».</summary>
        public void Resume() => _resume?.TrySetResult(true);
    }

    /// <summary>
    /// A tool batch run <b>in parallel</b> must stay step-by-step: one pause at a time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <c>AgentOrchestrator</c> runs in parallel (<c>Task.WhenAll</c>) any batch of at least two
    /// safe read-only tools — <c>read_file</c>/<c>list_files</c>/<c>search_in_files</c>, that is,
    /// the commonest batch shape a model emits. In step mode, every call therefore entered the pause
    /// <b>at the same time</b>, each overwriting the previous one's resume slot: a single call
    /// resumed, the others waited for an answer nobody could give them any more, and the
    /// <c>WhenAll</c> never completed. The turn stayed stuck until cancellation, and the "Resume"
    /// button had nothing left to unblock.
    /// </para>
    /// <para>
    /// "Step by step" means one tool at a time: the serialization therefore lives here, in the
    /// shared decorator, and is not copied into each of the two front-ends.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AParallelBatch_StepsOneToolAtATime_InsteadOfDeadlocking()
    {
        // A test that WAITS does not set a budget of a few seconds: the runner builds, then runs two
        // series in parallel. The green path waits for nothing — this budget is only consumed when
        // the guard is broken.
        var budget  = TimeSpan.FromSeconds(30);
        var stepper = new SingleSlotStepper();
        var fake    = new FakeRegistry();
        var sut     = new StepModeToolRegistry(fake, stepper.PauseAsync);

        const int batch = 3;
        var running = Task.WhenAll(Enumerable.Range(0, batch)
            .Select(_ => sut.ExecuteAsync("read_file", default, CancellationToken.None)));

        // The user clicks "Resume" once per announced pause.
        for (var i = 0; i < batch; i++)
        {
            Assert.True(await stepper.Announced.WaitAsync(budget),
                        $"Pause {i + 1}/{batch} never announced: the batch no longer crosses step mode.");
            stepper.Resume();
        }

        var results = await running.WaitAsync(budget);
        Assert.Equal(batch, results.Length);
        Assert.Equal(batch, fake.ExecuteCallCount);   // witness: the three tools really did run
    }
}
