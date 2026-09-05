using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The shared doubles of the tool graph: an absent editor, an approval that says yes, a front-end
/// that serves a debugger.
/// </summary>
/// <remarks>
/// They lived as <c>private sealed</c> classes inside <c>DocCountersTests</c>. The second test
/// needing the same graph could then only copy them - and two copies of a double is one copy nobody
/// will remember to update when the interface changes. Same place and same reason as
/// <see cref="FakeInferenceProvider"/>, which already lives here for everyone.
/// </remarks>
internal sealed class NoopApproval : IApprovalService
{
    public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                           string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
        Task.FromResult(true);
}

internal sealed class NullEditorSurface : Services.Editor.IEditorSurface
{
    public bool IsAvailable => false;
    public string? ActiveDocumentPath => null;
    public IReadOnlyList<string> GetOpenDocumentPaths() => [];
    public Task<Services.Editor.ActiveDocument?> GetActiveDocumentAsync(CancellationToken ct) =>
        Task.FromResult<Services.Editor.ActiveDocument?>(null);
    public Task<string?> InsertAtCursorAsync(string text, CancellationToken ct) =>
        Task.FromResult<string?>(null);
    public Task<Services.Editor.EditorEditResult?> ReplaceSelectionAsync(string text, CancellationToken ct) =>
        Task.FromResult<Services.Editor.EditorEditResult?>(null);
    public Task<string?> GetEditorDiagnosticsAsync(CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// A front-end that serves a debugger. Passed on purpose: the two <c>/debug</c> tools are
/// registered only where an <see cref="Services.Debugging.IDebugSession"/> exists, and since
/// tranche 3 of §21 that is both front-ends — Visual Studio through the in-process driver, VS
/// Code through the reverse RPC. The README's number describes what a user is offered, so it
/// counts them.
/// </summary>
internal sealed class NullDebugSession : Services.Debugging.IDebugSession
{
    public bool IsAvailable => true;
    public Task<Services.Debugging.DebugBreakpointInfo?> AddBreakpointAsync(string file, int line, CancellationToken ct) =>
        Task.FromResult<Services.Debugging.DebugBreakpointInfo?>(null);
    public Task<bool> RemoveBreakpointAsync(string file, int line, CancellationToken ct) => Task.FromResult(false);
    public Task<IReadOnlyList<Services.Debugging.DebugBreakpointInfo>> ListBreakpointsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Services.Debugging.DebugBreakpointInfo>>([]);
    public Task<Services.Debugging.DebugStartResult> StartAsync(CancellationToken ct) =>
        Task.FromResult(Services.Debugging.DebugStartResult.RanToCompletion);
    public Task<Services.Debugging.DebugStopState?> ContinueAsync(CancellationToken ct) =>
        Task.FromResult<Services.Debugging.DebugStopState?>(null);
    public Task<Services.Debugging.DebugStopState?> StepAsync(Services.Debugging.DebugStepKind kind, CancellationToken ct) =>
        Task.FromResult<Services.Debugging.DebugStopState?>(null);
    public Task<Services.Debugging.DebugStopState?> GetStateAsync(CancellationToken ct) =>
        Task.FromResult<Services.Debugging.DebugStopState?>(null);
    public Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken ct) =>
        Task.FromResult<string?>(null);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
