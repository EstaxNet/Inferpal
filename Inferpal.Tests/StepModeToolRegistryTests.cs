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
        using var cts = new CancellationTokenSource();
        var fake = new FakeRegistry();
        var sut  = new StepModeToolRegistry(
            fake,
            ct => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sut.ExecuteAsync("t", default, cts.Token));
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
