using System.Text;

namespace Inferpal.Services.Prompting;

/// <summary>
/// The workspace block the first question of a conversation carries: the solution, then the open editors. One
/// composer for both front-ends — the Visual Studio view model and the VS Code host.
/// </summary>
internal static class WorkspaceContext
{
    public const string Header = "## Workspace context (auto-injected on session start)";

    /// <summary>What the block may take, both sections together.</summary>
    public const int BudgetChars = 4000;

    /// <summary>
    /// The block, or an empty string when there is nothing to say. A section without content is left out: an empty
    /// "Open editors" or a "no solution" line is noise in every workspace it describes.
    /// </summary>
    /// <remarks>
    /// ⚠ Budgeted, as the other block injected unasked (the RAG auto-context) is: this one carried the
    /// WHOLE of <c>get_solution_info</c>, and on a solution of a hundred projects that is tens of
    /// thousands of characters — enough to fill the default context window before the question is even
    /// read. The open editors keep their room first (they say what the user is looking at); the
    /// solution takes the rest, cut on a line and naming the tool that gives all of it.
    /// </remarks>
    public static string Compose(string? solutionInfo, string? openEditors)
    {
        const int EditorsChars = 1000;
        var editors = string.IsNullOrWhiteSpace(openEditors) ? null : Fit(openEditors.TrimEnd(), EditorsChars, "get_open_editors");
        var room    = BudgetChars - (editors?.Length ?? 0);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(solutionInfo))
            sb.Append("### Solution\n\n").Append(Fit(solutionInfo.TrimEnd(), room, "get_solution_info")).Append("\n\n");
        if (editors is not null)
            sb.Append("### Open editors\n\n").Append(editors);

        return sb.Length == 0 ? string.Empty : Header + "\n\n" + sb.ToString().TrimEnd();
    }

    /// <summary>The text when it fits, else its first whole lines and the tool that gives the rest.</summary>
    private static string Fit(string text, int budget, string tool)
    {
        if (text.Length <= budget) return text;

        var cut   = text.LastIndexOf('\n', Math.Max(0, budget - 1));
        var head  = cut > 0 ? text[..cut] : SafeTruncate.Truncate(text, budget);
        var shown = head.Count(c => c == '\n') + 1;
        var total = text.Count(c => c == '\n') + 1;
        return head + $"\n…(first {shown} of {total} lines — call {tool} for all of it)";
    }
}
