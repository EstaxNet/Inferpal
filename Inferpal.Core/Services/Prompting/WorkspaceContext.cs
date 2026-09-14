using System.Text;

namespace Inferpal.Services.Prompting;

/// <summary>
/// The workspace block the first question of a conversation carries: the solution, then the open editors. One
/// composer for both front-ends — the Visual Studio view model and the VS Code host.
/// </summary>
internal static class WorkspaceContext
{
    public const string Header = "## Workspace context (auto-injected on session start)";

    /// <summary>
    /// The block, or an empty string when there is nothing to say. A section without content is left out: an empty
    /// "Open editors" or a "no solution" line is noise in every workspace it describes.
    /// </summary>
    public static string Compose(string? solutionInfo, string? openEditors)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(solutionInfo))
            sb.Append("### Solution\n\n").Append(solutionInfo.TrimEnd()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(openEditors))
            sb.Append("### Open editors\n\n").Append(openEditors.TrimEnd());

        return sb.Length == 0 ? string.Empty : Header + "\n\n" + sb.ToString().TrimEnd();
    }
}
