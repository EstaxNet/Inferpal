using Inferpal.Services.Debugging;
using StreamJsonRpc;

namespace Inferpal.Host;

/// <summary>
/// VS Code implementation of the §25 capture: asks the adapter (reverse RPC
/// <c>debug/captureTest</c>) to launch the repro runner under an ephemeral inline
/// <c>coreclr</c> configuration, arm the all-exceptions filter mid-session and hand back the
/// first exception stop whose stack reaches the workspace. Recipe probed before being wired
/// (2026-08-20, <c>docs/probes/tdd-debug-launch/</c>): ~1.4 s launch → stop on a warm host,
/// possibly much slower on the very first launch of a cold one — hence the generous budget on
/// the TypeScript side, not here.
/// </summary>
internal sealed class RpcTestDebugCapture(JsonRpc rpc) : TestDebugCaptureBase
{
    public override bool IsAvailable => true;   // constructed only when `debug/*` was declared

    protected override async Task<DebugStopState?> LaunchAndCaptureAsync(
        string runnerDll, string testDll, string failingTestFqn,
        string cwd, string projectRoot, CancellationToken ct)
    {
        try
        {
            var dto = await rpc.InvokeWithParameterObjectAsync<DebugStopStateDto?>(
                "debug/captureTest",
                new DebugCaptureTestParams(runnerDll, [testDll, failingTestFqn], cwd, projectRoot),
                ct);
            return dto is null ? null : new DebugStopState(
                dto.Reason ?? "exception",
                dto.ThreadId,
                dto.Frames?.Select(f => new DebugFrame(f.Id, f.Function, f.File, f.Line)).ToList() ?? [],
                dto.Locals?.Select(v => new DebugVariable(v.Name, v.Type, v.Value)).ToList() ?? [],
                dto.Exception);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Inferpal.Services.Diagnostics.Swallow("RpcTestDebugCapture", ex);
            return null;
        }
    }
}
