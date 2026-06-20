using Inferpal.Localization;

namespace Inferpal.Services;

/// <summary>Whether a heartbeat check crossed a connection edge worth announcing in the chat.</summary>
internal enum ConnectionTransition { None, Restored, Lost }

/// <summary>The presentation outcome of one heartbeat check.</summary>
internal sealed record ConnectionStatus(
    string               StatusText,
    string               StatusColor,
    string               SendButtonColor,
    bool                 ShowRetry,
    ConnectionTransition Transition);

/// <summary>
/// The connection-guard heartbeat's presentation state machine: maps each reachability check to the
/// status-badge text/colour, the send-button colour, and whether an edge message (lost / restored)
/// should be surfaced. Extracted from the tool-window VM so the first-check and up↔down edge logic
/// is unit-testable; the polling loop, CTS and chat-message insertion stay in the VM.
/// </summary>
/// <remarks>
/// One instance per heartbeat run (a fresh instance restarts optimistically, so reconnecting after
/// <c>RetryConnectionAsync</c> doesn't emit a spurious "reconnected" line on the first success).
/// </remarks>
internal sealed class ConnectionStatusPresenter
{
    // Optimistic start: assume connected so the very first successful check is silent, and the
    // first FAILED check still announces the outage.
    private bool _previouslyConnected = true;
    private bool _firstCheck          = true;

    public ConnectionStatus Evaluate(bool ok)
    {
        var transition = ok
            ? (!_previouslyConnected && !_firstCheck ? ConnectionTransition.Restored : ConnectionTransition.None)
            : (_firstCheck || _previouslyConnected   ? ConnectionTransition.Lost     : ConnectionTransition.None);

        _previouslyConnected = ok;
        _firstCheck          = false;

        return ok
            ? new ConnectionStatus(Strings.StatusConnected,   "#4EC94E", "#7C4DFF", false, transition)
            : new ConnectionStatus(Strings.StatusUnreachable, "#F44747", "#555555", true,  transition);
    }
}
