using System.IO;
using System.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Every file-signal channel asks one question of a signal's writer — "is it still the process running under that PID?"
/// — through <c>SignalFile.WriterAlive</c>: alive, AND started before the signal's stamp.
/// </summary>
/// <remarks>
/// Two channels asked it (the debugger driver's ready marker, the in-process heartbeat, each with its own copy); five
/// asked only "is a process alive under that PID?". A devenv that crashed leaves its files, keyed by its PID, and the
/// next devenv may get that PID: its debugger snapshot, its "answering" marker, its pending requests passed for the new
/// instance's. The property: no channel calls the bare liveness check.
/// </remarks>
public sealed class SignalWriterAliveTests
{
    [Fact]
    public void NoChannel_JudgesAWriterOnLivenessAlone()
    {
        var dir = Path.Combine(ConventionCoverageTests.RepoRoot(), "Inferpal.Core", "Services", "Signals");
        var channels = Directory.EnumerateFiles(dir, "*.cs")
            .Where(f => Path.GetFileName(f) != "SignalFile.cs")
            .Select(f => (File: Path.GetFileName(f), Code: ConventionCoverageTests.CodeOnly(f)))
            .ToList();

        var asking = channels.Count(c => c.Code.Contains("SignalFile.WriterAlive(", StringComparison.Ordinal));
        // WITNESS, under the real count (six: chat busy, debugger command and state, inline diff preview, in-process
        // heartbeat, active solution): it says the reading is alive, not that nothing moved.
        Assert.True(asking >= 4, $"only {asking} channel(s) ask WriterAlive: this test would measure nothing.");

        var bare = channels.Where(c => c.Code.Contains("SignalFile.IsProcessAlive(", StringComparison.Ordinal)
                                       || c.Code.Contains("SignalFile.PredatesProcess(", StringComparison.Ordinal)
                                       || c.Code.Contains(".StartTime", StringComparison.Ordinal))
                           .Select(c => c.File).ToList();
        Assert.True(bare.Count == 0, "judged on liveness alone, or with a copy of the start-time check: " + string.Join(", ", bare));
    }
}
