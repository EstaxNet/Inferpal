using Inferpal.Services;
using Inferpal.Services.Debugging;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// <see cref="IDebugSession"/> for the VS Code front-end: every port call becomes a reverse
/// <c>debug/*</c> request served by the TypeScript adapter, which drives <c>vscode.debug</c> and the
/// Debug Adapter Protocol on its side.
/// </summary>
/// <remarks>
/// <para>
/// The mirror of <see cref="Services.Debugging.SignalDebugSession"/> under Visual Studio, and the
/// reason this port exists at all: the §21 probe drove <b>both</b> APIs through the same seven
/// conditions, and VS Code was the faster of the two on the inner loop (0,016 s to a breakpoint set
/// during a break, against 0,15 s). The asymmetry is elsewhere — see <see cref="IsAvailable"/>.
/// </para>
/// <para>
/// <b>No serialising semaphore here, unlike the Visual Studio side.</b> That one exists because a
/// single pair of files carries every request; JSON-RPC correlates by id natively, and the adapter
/// serialises against the one debug session it owns. Adding a second lock would only hide which
/// layer is responsible.
/// </para>
/// <para>
/// Best-effort as the port demands: a missing handler, a dead adapter or an adapter-side throw
/// degrades to <c>null</c>/empty and is traced through <see cref="Diagnostics"/>. Only
/// <see cref="OperationCanceledException"/> propagates.
/// </para>
/// </remarks>
internal sealed class RpcDebugSession(JsonRpc rpc, bool declared) : IDebugSession
{
    /// <summary>
    /// Whether the adapter said it serves <c>debug/*</c> in its `initialize` handshake.
    /// </summary>
    /// <remarks>
    /// ⚠ It answers "this editor can drive a debugger", never "this workspace can be debugged".
    /// The §21 probe was explicit about the limit it did not clear: it drove the Node adapter
    /// bundled with VS Code, while debugging C# there needs a third-party extension that is not
    /// guaranteed to be installed — and a workspace with no launch configuration cannot start
    /// anything at all. Both surface at <see cref="StartAsync"/>, as a <c>Failure</c> that says so.
    /// </remarks>
    public bool IsAvailable => declared;

    public async Task<DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct)
    {
        var dto = await CallAsync<DebugBreakpointDto?>(
            "debug/addBreakpoint", new DebugBreakpointParams(file, line), ct);
        return dto is null ? null : new DebugBreakpointInfo(dto.File, dto.Line, dto.Enabled);
    }

    public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) =>
        CallAsync<bool>("debug/removeBreakpoint", new DebugBreakpointParams(file, line), ct);

    public async Task<IReadOnlyList<DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct)
    {
        var dtos = await CallAsync<List<DebugBreakpointDto>?>("debug/listBreakpoints", null, ct);
        return dtos?.Select(b => new DebugBreakpointInfo(b.File, b.Line, b.Enabled)).ToList()
               ?? (IReadOnlyList<DebugBreakpointInfo>)[];
    }

    public async Task<DebugStartResult> StartAsync(CancellationToken ct)
    {
        DebugStartDto? dto;
        try
        {
            dto = await rpc.InvokeWithCancellationAsync<DebugStartDto?>("debug/start", cancellationToken: ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The one call whose failure is worth repeating to the model: everywhere else a dead
            // adapter reads as "nothing is paused", which is true enough. Here, silence would be
            // rendered as "the program ran to completion", which is exactly the lie the three-way
            // result was introduced to prevent.
            Diagnostics.Swallow("RpcDebugSession.Start", ex);
            return DebugStartResult.Failed("The editor could not be asked to start a debugging session.");
        }

        if (dto?.Failure is { Length: > 0 } failure) return DebugStartResult.Failed(failure);
        return dto?.State is { } state ? DebugStartResult.Stopped(ToState(state)) : DebugStartResult.RanToCompletion;
    }

    public Task<DebugStopState?> ContinueAsync(CancellationToken ct) => StateAsync("debug/continue", null, ct);

    public Task<DebugStopState?> StepAsync(DebugStepKind kind, CancellationToken ct) =>
        StateAsync("debug/step", new DebugStepParams(kind switch
        {
            DebugStepKind.Into => "into",
            DebugStepKind.Out  => "out",
            _                  => "over",
        }), ct);

    public Task<DebugStopState?> GetStateAsync(CancellationToken ct) => StateAsync("debug/state", null, ct);

    public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct) =>
        CallAsync<string?>("debug/evaluate", new DebugEvaluateParams(expression, frameId), ct);

    public Task StopAsync(CancellationToken ct) => CallAsync<object?>("debug/stop", null, ct);

    // ── Plumbing ────────────────────────────────────────────────────────────────

    private async Task<DebugStopState?> StateAsync(string method, object? p, CancellationToken ct)
    {
        var dto = await CallAsync<DebugStopStateDto?>(method, p, ct);
        return dto is null ? null : ToState(dto);
    }

    private async Task<T?> CallAsync<T>(string method, object? p, CancellationToken ct)
    {
        try
        {
            return p is null
                ? await rpc.InvokeWithCancellationAsync<T?>(method, cancellationToken: ct)
                : await rpc.InvokeWithParameterObjectAsync<T?>(method, p, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"RpcDebugSession.{method}", ex);
            return default;
        }
    }

    private static DebugStopState ToState(DebugStopStateDto dto) => new(
        dto.Reason ?? "break",
        dto.ThreadId,
        dto.Frames?.Select(f => new DebugFrame(f.Id, f.Function, f.File, f.Line)).ToList() ?? [],
        dto.Locals?.Select(v => new DebugVariable(v.Name, v.Type, v.Value)).ToList() ?? [],
        dto.Exception);
}
