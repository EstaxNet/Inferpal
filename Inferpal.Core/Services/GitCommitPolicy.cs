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
    public const int MaxDiffChars = 12_000;

    public static string CapDiff(string context) =>
        context.Length > MaxDiffChars ? context[..MaxDiffChars] + "\n…(truncated)" : context;

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
    /// merged into the message (pre-1.6.0 architecture review).</remarks>
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
