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
            var term    = string.Join(" ", parts[1..]);
            var matches = await store.SearchAsync(term, ct);
            return matches.Count == 0
                ? Strings.HistoryNoResults(term)
                : SessionManager.FormatHistorySearch(term, matches, nowUtc);
        }

        var sessions = await store.ListWithPreviewAsync(ct);
        return sessions.Count == 0
            ? Strings.HistoryNoSessions
            : SessionManager.FormatHistoryList(sessions, nowUtc);
    }
}
