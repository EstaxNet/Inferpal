using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The restart gate of the FIM sidecar: what tells "this will never work" apart from "this did
/// not work a moment ago".
/// </summary>
/// <remarks>
/// Revue post-1.6.0, §2.3. <c>FimSidecar</c> posait un unique <c>bool _disabled</c> sur trois
/// events - executable missing, <c>Process.Start</c> returning null, <c>Process.Start</c> throwing
/// - and nothing ever cleared it. An antivirus holding the exe for one second therefore killed
/// ghost text for the whole life of that devenv, with no way for the user to find out: the trace
/// lands in the in-process diagnostics ring, which <c>/diagnostics</c> does not read.
/// </remarks>
public class RetryGateTests
{
    private DateTimeOffset _now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private RetryGate Gate(int cooldownSeconds = 30) =>
        new(TimeSpan.FromSeconds(cooldownSeconds), () => _now);

    [Fact]
    public void ANewGate_LetsTheFirstAttemptThrough()
    {
        Assert.True(Gate().MayTry());
    }

    [Fact]
    public void ATransientFailure_HoldsTheDoor_ThenLetsGo()
    {
        var gate = Gate();

        gate.Backoff();
        Assert.False(gate.MayTry());

        _now = _now.AddSeconds(29);
        Assert.False(gate.MayTry());

        _now = _now.AddSeconds(1);
        Assert.True(gate.MayTry());   // the one thing the old boolean could not do
    }

    [Fact]
    public void APermanentFailure_NeverLetsGo()
    {
        var gate = Gate();

        gate.LatchPermanently();
        _now = _now.AddDays(1);

        Assert.False(gate.MayTry());
    }

    /// <summary>
    /// A new configuration clears a cooldown - and does <b>not</b> clear a permanent latch.
    /// </summary>
    /// <remarks>
    /// This asymmetry is the whole fix: changing backend is new evidence about a start that
    /// failed, and no evidence at all about a file that is not there. Conflating them would bring
    /// back exactly the old behaviour, in the other direction: an incomplete VSIX would spawn a
    /// process on every keystroke.
    /// </remarks>
    [Fact]
    public void ClearingTheCooldown_DoesNotUnlatchAPermanentFailure()
    {
        var transient = Gate();
        transient.Backoff();
        transient.ClearCooldown();
        Assert.True(transient.MayTry());

        var permanent = Gate();
        permanent.LatchPermanently();
        permanent.ClearCooldown();
        Assert.False(permanent.MayTry());
    }
}
