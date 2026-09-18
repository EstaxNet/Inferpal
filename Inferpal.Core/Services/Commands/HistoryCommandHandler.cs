using Inferpal.Localization;
using Inferpal.Services.Persistence;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/history [term]</c> — lists the saved sessions, or full-text searches
/// across them. Both front-ends read the same <see cref="ConversationStore"/> files, so the
/// whole command (store access + markdown rendering) is shared; only the bubble differs.
/// </summary>
internal static class HistoryCommandHandler
{
    /// <param name="store">Session store (same files in VS and VS Code).</param>
    /// <param name="parts">Tokenised command; everything after the verb is the search term.</param>
    /// <param name="nowUtc">Clock, injected so the relative ages are testable.</param>
    public static async Task<string> HandleAsync(
        ConversationStore store, string[] parts, DateTime nowUtc, CancellationToken ct)
    {
        if (parts.Length >= 2)
        {
            var term = string.Join(" ", parts[1..]);
            var scan = await store.SearchAsync(term, ct);
            return Append(
                scan.Items.Count == 0
                    ? Strings.HistoryNoResults(term)
                    : SessionManager.FormatHistorySearch(term, scan.Items, nowUtc),
                scan.Unreadable,
                Strings.SessionsUnreadableSearched);
        }

        var sessions = await store.ListWithPreviewAsync(ct);
        return Append(
            sessions.Items.Count == 0
                ? Strings.HistoryNoSessions
                : SessionManager.FormatHistoryList(sessions.Items, nowUtc),
            sessions.Unreadable,
            Strings.SessionsUnreadableListed);
    }

    /// <summary>
    /// Appends the "could not be read" line, or returns <paramref name="answer"/> unchanged.
    /// </summary>
    /// <remarks>
    /// ⚠ It is appended to <b>both</b> branches, including the empty one. The empty branch is the
    /// expensive one: "no saved sessions" and "no results" are claims about the user's own data, and
    /// they were being made about files the product had failed to open. Which sentence is used is a
    /// parameter, because "missing from this list" and "not searched — this is not «absent»" send
    /// the reader to two different conclusions.
    /// </remarks>
    private static string Append(string answer, IReadOnlyList<string> unreadable, Func<int, string, string> sentence) =>
        unreadable.Count == 0
            ? answer
            : answer + "\n\n" + sentence(unreadable.Count, string.Join(", ", unreadable));
}
