using System.IO;

namespace Inferpal.Services.Debugging;

/// <summary>
/// Shared half of <see cref="ITestDebugCapture"/> (§25 tranche B): locates the test assembly,
/// makes sure the repro runner exists, and delegates only the debugger drive to the front-end.
/// Everything above the adapter line is testable without an editor.
/// </summary>
internal abstract class TestDebugCaptureBase : ITestDebugCapture
{
    public abstract bool IsAvailable { get; }

    public async Task<DebugStopState?> CaptureAsync(string failingTestFqn, string projectRoot, CancellationToken ct)
    {
        try
        {
            var testDll = TestAssemblyLocator.Locate(projectRoot, failingTestFqn);
            if (testDll is null) return null;

            var runnerDll = await TestReproScaffold.EnsureBuiltAsync(ct);
            if (runnerDll is null) return null;

            // cwd = the test output directory, so relative fixture paths behave as under the runner.
            return await LaunchAndCaptureAsync(
                runnerDll, testDll, failingTestFqn,
                Path.GetDirectoryName(testDll)!, projectRoot, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("TestDebugCapture", ex);
            return null;
        }
    }

    /// <summary>
    /// Front-end half: runs <c>dotnet «runnerDll» «testDll» «fqn»</c> under this editor's
    /// debugger, breaks on the first thrown exception whose stack reaches
    /// <paramref name="projectRoot"/>, and returns that state — or <c>null</c>, best-effort.
    /// </summary>
    protected abstract Task<DebugStopState?> LaunchAndCaptureAsync(
        string runnerDll, string testDll, string failingTestFqn,
        string cwd, string projectRoot, CancellationToken ct);
}
