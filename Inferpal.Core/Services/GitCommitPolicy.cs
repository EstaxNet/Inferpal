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
    public static string EscapeMessage(string message) =>
        message.Trim().Replace("\"", "\\\"");
}
