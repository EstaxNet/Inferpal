using Inferpal.Localization;

namespace Inferpal.Services.Presentation;

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
    // first FAILED check still announces the outage, unless a first run already said it.
    private bool _previouslyConnected = true;
    private bool _firstCheck          = true;
    private bool _lostSaid;   // "reconnected" answers a "lost" that was said: never connected is no reconnection

    /// <summary>A Test button's line once a server answered, the same in both editors: its kind and how many models it
    /// lists; <paramref name="models"/> <c>null</c> (not listed) says its kind only — never a count it did not read.</summary>
    /// <remarks>⚠ Zero says no count either: a listing that failed comes back empty (the clients log it and return
    /// none), so "0 models installed" could be a timeout. The model box, left empty, says the rest.</remarks>
    public static string Answered(string server, int? models) =>
        models is > 0 and var n ? Strings.StatusAnswered(server, n) : $"{Strings.StatusConnected} — {server}";

    /// <param name="refusal">The status the server refused the check with, when it answered at all: the badge says
    /// "refused", not "unreachable" — a server that answers 401 is running.</param>
    /// <param name="firstRunSpoke">The first run said what answers where: the first failed check is no edge. Announced,
    /// "cannot reach Ollama, start it" reaches someone who has no Ollama, just before "server found". A server that refused
    /// is running: that stays said.</param>
    public ConnectionStatus Evaluate(bool ok, string? refusal = null, bool firstRunSpoke = false)
    {
        var transition = ok
            ? (_lostSaid ? ConnectionTransition.Restored : ConnectionTransition.None)
            : (_firstCheck && firstRunSpoke && refusal is null ? ConnectionTransition.None
               : _firstCheck || _previouslyConnected           ? ConnectionTransition.Lost
               :                                                 ConnectionTransition.None);

        _previouslyConnected = ok;
        _firstCheck          = false;
        _lostSaid            = transition == ConnectionTransition.Lost || (_lostSaid && !ok);

        return ok
            ? new ConnectionStatus(Strings.StatusConnected,   "#4EC94E", "#7C4DFF", false, transition)
            : new ConnectionStatus(refusal is null ? Strings.StatusUnreachable : Strings.StatusRefused(refusal),
                                   "#F44747", "#555555", true, transition);
    }
}
