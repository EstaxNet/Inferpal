using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for <c>/phistory</c> — list the prompt history (optionally filtered by a
/// term) or re-fill the prompt box with a past entry via <c>/phistory use &lt;n&gt;</c>. Extracted
/// from <c>InferpalToolWindowData</c> so it is unit-testable without VS.
/// </summary>
/// <remarks>
/// Fully synchronous and side-effect-free: the VM reads a snapshot of the history on its context and
/// passes it in, then applies whichever single effect the <see cref="PHistoryCommandResult"/> carries
/// (show <see cref="PHistoryCommandResult.Message"/>, or set the prompt to
/// <see cref="PHistoryCommandResult.FillPrompt"/>). Listing/filtering is delegated to
/// <see cref="ChatTurnPolicy.FormatPromptHistory"/>. Same pattern as <see cref="SnippetsCommandHandler"/>.
/// </remarks>
internal static class PHistoryCommandHandler
{
    /// <summary>Outcome of a <c>/phistory</c> invocation: exactly one field is non-null.</summary>
    /// <param name="Message">Markdown message to show the user.</param>
    /// <param name="FillPrompt">When non-null, the VM must set the prompt box to this past entry.</param>
    internal readonly record struct PHistoryCommandResult(string? Message, string? FillPrompt);

    /// <summary>Parses and resolves a <c>/phistory</c> invocation against a history snapshot
    /// (<paramref name="history"/>, oldest-first). <paramref name="parts"/> is the whitespace-split
    /// command line (<c>parts[0]</c> is <c>/phistory</c>).</summary>
    public static PHistoryCommandResult Handle(IReadOnlyList<string> history, string[] parts)
    {
        // /phistory use <n> — re-fill the prompt with entry #n (1-based).
        if (parts.Length >= 3 && parts[1].Equals("use", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(parts[2], out var useIdx))
        {
            var i = useIdx - 1;
            return i < 0 || i >= history.Count
                ? new(Strings.PHistoryNoEntry(useIdx), null)
                : new(null, history[i]);
        }

        if (history.Count == 0)
            return new(Strings.PHistoryEmpty, null);

        // /phistory [term] — filtered list ("use" is reserved, not a search term).
        var term = parts.Length >= 2 && parts[1] != "use"
            ? string.Join(" ", parts[1..]).ToLowerInvariant()
            : null;

        var listing = ChatTurnPolicy.FormatPromptHistory(history, term);
        return listing is null
            ? new(Strings.PHistoryNoMatch(term), null)
            : new(listing, null);
    }
}
