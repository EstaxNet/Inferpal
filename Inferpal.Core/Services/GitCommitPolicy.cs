using Inferpal.Models;

namespace Inferpal.Services;

/// <summary>
/// Pure formatting logic for the <c>/commit</c> flow (shared in part by <c>/check</c>)
/// extracted from the tool-window VM: the diff-context assembly with its size cap, the
/// commit-message proposal request, the proposal clean-up, and the git argument
/// escaping. Running git and the chat bubbles stay in the VM.
/// </summary>
internal static class GitCommitPolicy
{
    /// <summary>Diff-context cap — keeps the proposal prompt within a small model's budget.</summary>
    /// <remarks>
    /// ⚠ The reason is written for <c>/commit</c>, which runs on the utility model; <c>/check</c>
    /// reuses the same cap on the chat model, where the budget is not the binding constraint. The
    /// number is therefore a shared floor, not a measured ceiling for both — which is exactly why
    /// what it cuts has to be said rather than assumed harmless.
    /// </remarks>
    public const int MaxDiffChars = 12_000;

    /// <summary>The diff as the model will see it, and how much of it never got there.</summary>
    /// <param name="Text">Capped context, carrying its own marker for the model.</param>
    /// <param name="Kept">Characters the model was shown.</param>
    /// <param name="Total">Characters the diff actually had.</param>
    internal readonly record struct CappedDiff(string Text, int Kept, int Total)
    {
        /// <summary>Characters the model was never shown.</summary>
        public int Cut => Total - Kept;

        public bool IsTruncated => Cut > 0;
    }

    /// <summary>
    /// Cuts the context to <see cref="MaxDiffChars"/> and <b>reports the cut</b>.
    /// </summary>
    /// <remarks>
    /// ⚠ Both callers turn this text into something the user acts on — a review verdict, a commit
    /// message — so the cut shapes a CONCLUSION and cannot stay between the cap and the model.
    /// <c>/check</c> answers "the checks turned up nothing on this diff" about a diff the model saw
    /// a fifth of; <c>/commit</c> names a scope from the part that fit. The marker tells the model;
    /// the count is what lets each caller tell the human, and it names the amount for the same
    /// reason <c>get_git_status</c> does one file away: "truncated" alone cannot be weighed.
    /// </remarks>
    public static CappedDiff CapDiff(string context) =>
        context.Length > MaxDiffChars
            ? new(context[..MaxDiffChars] + $"\n…(truncated — {context.Length - MaxDiffChars} more characters)",
                  MaxDiffChars, context.Length)
            : new(context, context.Length, context.Length);

    public static string BuildStagedContext(string staged) =>
        $"git diff --staged:\n{staged}";

    /// <summary>
    /// Fallback context when nothing is staged: the short status, plus the unstaged
    /// diff when there is one (a blank diff section would only waste prompt budget).
    /// </summary>
    public static string BuildUnstagedContext(string status, string unstagedDiff)
    {
        var ctx = $"git status:\n{status}";
        if (!string.IsNullOrWhiteSpace(unstagedDiff))
            ctx += $"\n\ngit diff (unstaged):\n{unstagedDiff}";
        return ctx;
    }

    /// <summary>The two-message request asking the model for a conventional commit message.</summary>
    public static List<ChatMessageDto> BuildProposalRequest(string diffContext) =>
    [
        new("system",
            "You are a git commit message assistant. " +
            "Reply with ONLY the commit message — no quotes, no backticks, no explanation. " +
            "Use conventional commit format: type(scope): description. " +
            "Keep it under 72 characters. Match the language of the repository."),
        new("user", $"Propose a commit message for these changes:\n\n{diffContext}")
    ];

    /// <summary>
    /// The model's reply cleaned for use as a commit message: think tags stripped (so
    /// reasoning-model output doesn't land in the prompt), then wrapping backticks and
    /// quotes removed.
    /// </summary>
    public static string CleanProposal(string? finalResponse) =>
        MarkdownParser.StripThinkTags(finalResponse).Trim().Trim('`').Trim('"').Trim();

    /// <summary>Escapes the message for interpolation inside <c>git commit -m "…"</c>.</summary>
    /// <remarks>Win32 argument parsing (MSVCRT rule): a backslash only escapes when it precedes a
    /// quote, so every backslash in front of an inserted <c>\"</c> — or at the END of the message,
    /// where the closing quote follows — must itself be doubled. Without it, a message ending in
    /// <c>bin\</c> produced <c>…bin\"</c>: the quote was swallowed and the remaining arguments
    /// merged into the message.</remarks>
    public static string EscapeMessage(string message)
    {
        var m  = message.Trim();
        var sb = new System.Text.StringBuilder(m.Length + 8);
        var pendingBackslashes = 0;
        foreach (var c in m)
        {
            if (c == '\\') { pendingBackslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', pendingBackslashes * 2); // backslashes before a quote: doubled
                sb.Append("\\\"");
            }
            else
            {
                sb.Append('\\', pendingBackslashes);     // backslashes elsewhere: literal
                sb.Append(c);
            }
            pendingBackslashes = 0;
        }
        sb.Append('\\', pendingBackslashes * 2);         // trailing run precedes the closing quote
        return sb.ToString();
    }
}
