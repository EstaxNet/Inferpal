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
/// The mirror of <see cref="Services.Debugging.SignalDebugSession"/> under Visual Studio. Of the two
/// APIs, VS Code is the faster on the inner loop — setting a breakpoint during a break is an order
/// of magnitude quicker. The asymmetry is elsewhere; see <see cref="IsAvailable"/>.
/// </para>
/// <para>
/// <b>No serialising semaphore here, unlike the Visual Studio side.</b> That one exists because a
/// single pair of files carries every request; JSON-RPC correlates by id natively, and the adapter
/// serialises against the one debug session it owns. Adding a second lock would only hide which
/// layer is responsible.
/// </para>
/// <para>
/// A missing handler, a dead adapter or an adapter-side throw is NOT an ordinary debugger answer: it is traced
/// through <see cref="Diagnostics"/> and thrown as <see cref="DebuggerNotAnsweringException"/>, the adapter's own words
/// included (a start and a resume say it in their result instead).
/// </para>
/// </remarks>
internal sealed class RpcDebugSession(JsonRpc rpc, bool declared) : IDebugSession
{
    /// <summary>
    /// Whether the adapter said it serves <c>debug/*</c> in its `initialize` handshake.
    /// </summary>
    /// <remarks>
    /// ⚠ It answers "this editor can drive a debugger", never "this workspace can be debugged".
    /// Debugging C# under VS Code needs a third-party extension that is not guaranteed to be
    /// installed, and a workspace with no launch configuration cannot start anything at all. Both
    /// surface at <see cref="StartAsync"/>, as a <c>Failure</c> that says so.
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
        if (dto?.State is { } state) return DebugStartResult.Stopped(ToState(state));
        return dto?.StillRunning == true ? DebugStartResult.NoStopYet : DebugStartResult.RanToCompletion;
    }

    public Task<DebugResumeResult> ContinueAsync(CancellationToken ct) => ResumeAsync("debug/continue", null, ct);

    public Task<DebugResumeResult> StepAsync(DebugStepKind kind, CancellationToken ct) =>
        ResumeAsync("debug/step", new DebugStepParams(kind switch
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

    /// <summary>A resume and what it came to, from the adapter's answer (the outcomes are <see cref="DebugOps"/>'s).</summary>
    private async Task<DebugResumeResult> ResumeAsync(string method, object? p, CancellationToken ct)
    {
        DebugResumeDto? dto;
        try { dto = await CallAsync<DebugResumeDto?>(method, p, ct); }
        catch (DebuggerNotAnsweringException ex) { return DebugResumeResult.Failed(ex.Message); }

        if (dto?.Failure is { Length: > 0 } failure) return DebugResumeResult.Failed(failure);
        if (dto?.State is { } state) return DebugResumeResult.Stopped(ToState(state));
        return dto?.Outcome switch
        {
            DebugOps.Resumed.NotPaused    => DebugResumeResult.NotPaused,
            DebugOps.Resumed.StillRunning => DebugResumeResult.StillRunning,
            DebugOps.Resumed.Ended        => DebugResumeResult.Ended,
            _ => DebugResumeResult.Failed("The editor answered the resume without saying what came of it."),
        };
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
            throw new DebuggerNotAnsweringException(
                $"The debugger did not answer: the editor's request failed — {Diagnostics.RootMessage(ex).TrimEnd('.')}.", ex);
        }
    }

    private static DebugStopState ToState(DebugStopStateDto dto) => new(
        dto.Reason ?? "break",
        dto.ThreadId,
        dto.Frames?.Select(f => new DebugFrame(f.Id, f.Function, f.File, f.Line)).ToList() ?? [],
        dto.Locals?.Select(v => new DebugVariable(v.Name, v.Type, v.Value)).ToList() ?? [],
        dto.Exception,
        dto.LocalsFrameId);
}
