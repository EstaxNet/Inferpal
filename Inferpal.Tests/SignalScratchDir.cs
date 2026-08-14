using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Every suite that touches the signal folder runs here, serialised.
/// </summary>
/// <remarks>
/// The overrides <see cref="SignalScratchDir"/> installs are <b>process-wide statics</b>, so two
/// signal suites running in parallel would each redirect the other's channels and null them out on
/// dispose. One collection replaces the five per-channel ones that used to serialise each suite
/// against itself only — which was enough when the folder was shared by everybody anyway, and is
/// not once each suite claims a private one.
/// </remarks>
[CollectionDefinition(SignalCollection.Name, DisableParallelization = true)]
public class SignalCollection
{
    public const string Name = "Signals";
}

/// <summary>
/// Redirects the file-signal channels to a private directory for the lifetime of a test class.
/// </summary>
/// <remarks>
/// <para>
/// ROADMAP §22. <c>%TEMP%\Inferpal</c> is <b>machine-wide</b>: one folder, one fixed file name per
/// channel, shared by every Inferpal process on the box — including a Visual Studio the developer
/// is using while the suite runs. Before this seam the suite did not merely read that folder, it
/// wrote to it, and the consequences were not theoretical:
/// </para>
/// <list type="bullet">
///   <item><c>DebugCommandSignalTests</c> published a real request carrying <see cref="Environment.ProcessId"/>
///         — alive, therefore claimable — and one of them is <c>op: "start"</c>. A live
///         <c>VsDebugDriver</c> polls, claims it, and <b>launches a debug session</b> in that
///         Visual Studio.</item>
///   <item><c>GpuSchedulerTests</c> acquires a chat lease, and releasing it called
///         <c>ChatBusySignal.Clear()</c> — which, before §22 G4 made the markers per-writer,
///         deleted the single shared file whoever wrote it: the busy marker of a chat in flight
///         elsewhere vanished and its ghost-text FIM resumed against a busy GPU.</item>
///   <item><c>DebuggerStateSignalTests</c> clears the break snapshot, and
///         <c>InlineDiffPreviewSignalTests</c> fires preview requests at the in-process renderer.</item>
/// </list>
/// <para>
/// The seam is inert at runtime: production code never sets an override, so the paths resolve to
/// the real folder exactly as before.
/// </para>
/// <para>
/// <c>BuildSignalFile</c> is covered too since the seam moved into <c>SignalFile.Dir</c> — every
/// channel resolves its path through it, so redirecting the directory redirects all of them
/// (the §22 G1 tests write build signals under the scratch directory).
/// </para>
/// </remarks>
internal sealed class SignalScratchDir : IDisposable
{
    /// <summary>The private directory the signal channels now resolve to.</summary>
    public string Path { get; }

    public SignalScratchDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"Inferpal_signals_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);

        // A HostServer built by an earlier suite latched this to false, process-wide and one way.
        // Production has one role per process; a test process plays both, so every signal suite
        // starts from the VS-peer side rather than from whatever ran before it.
        SignalScope.ResetForTests();

        // One assignment, because there is one directory: the seven channels stopped carrying
        // their own copy of where the folder is.
        SignalFile._directoryOverride = Path;
    }

    public void Dispose()
    {
        SignalFile._directoryOverride      = null;
        SignalFile._isProcessAliveOverride = null;
        SignalFile._nowOverride            = null;
        SignalScope.ResetForTests();

        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
