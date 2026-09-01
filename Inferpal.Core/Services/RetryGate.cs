namespace Inferpal.Services;

/// <summary>
/// Decides whether a start that has already failed may be attempted again — and tells apart the
/// two failures that a single boolean used to conflate.
/// </summary>
/// <remarks>
/// <para>
/// Written for <c>FimSidecar</c>, where one <c>bool _disabled</c> was set by three different
/// events: the executable being missing (permanent — a VSIX shipped incomplete will not heal),
/// <c>Process.Start</c> returning null, and <c>Process.Start</c> throwing (both of which can be an
/// antivirus holding the file for a second, or a moment of memory pressure). Nothing ever cleared
/// it, so a single transient failure killed ghost text for the whole life of that Visual Studio —
/// with, as its only trace, an entry in the in-process diagnostics ring that <c>/diagnostics</c>
/// does not read.
/// </para>
/// <para>
/// Hence two states. <see cref="LatchPermanently"/> is for a cause that cannot change on its own;
/// <see cref="Backoff"/> is for one that can, and only holds the door for a cooldown. ⚠ A reset
/// clears the cooldown and <b>never</b> the latch: new configuration is new evidence about a
/// transient failure, and none at all about a file that is not there.
/// </para>
/// <para>
/// Pure and clock-injected, compiled into both the Core (net8) and <c>Inferpal.InProc</c> (net472)
/// from this same file — the sidecar client lives on the net472 side, the tests on the other.
/// </para>
/// </remarks>
internal sealed class RetryGate
{
    private readonly TimeSpan _cooldown;
    private readonly Func<DateTimeOffset> _now;

    private bool _latched;
    private DateTimeOffset _notBefore = DateTimeOffset.MinValue;

    internal RetryGate(TimeSpan cooldown, Func<DateTimeOffset>? now = null)
    {
        _cooldown = cooldown;
        _now      = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary><c>true</c> when an attempt is allowed right now.</summary>
    internal bool MayTry() => !_latched && _now() >= _notBefore;

    /// <summary>The cause cannot resolve itself: no further attempt, ever, in this process.</summary>
    internal void LatchPermanently() => _latched = true;

    /// <summary>The cause may pass: hold the door for one cooldown, then try again.</summary>
    internal void Backoff() => _notBefore = _now() + _cooldown;

    /// <summary>
    /// Something changed that makes a previous <see cref="Backoff"/> obsolete (a new
    /// configuration). Leaves <see cref="LatchPermanently"/> untouched.
    /// </summary>
    internal void ClearCooldown() => _notBefore = DateTimeOffset.MinValue;
}
