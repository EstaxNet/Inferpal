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
}
