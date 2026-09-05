using System.IO;
using System.Linq;
using System.Text;

namespace Inferpal.Services.Governance;

/// <summary>
/// A single review check loaded from <c>.inferpal/checks/*.md</c>.
/// </summary>
/// <param name="Name">Display name (frontmatter <c>description</c> if present, else the file name).</param>
/// <param name="Criteria">The review criteria text (everything after the frontmatter block).</param>
internal sealed record ReviewCheck(string Name, string Criteria);

/// <summary>
/// Loads markdown review checks (Continue-style <c>.continue/checks</c> parity). Each check
/// describes criteria the <c>/check</c> command evaluates the current git diff against — 100% local.
/// Pure/static so it is fully unit-testable.
/// </summary>
internal static class ChecksService
{
    /// <summary>Reads every <c>*.md</c> check from <paramref name="checksDir"/>. Missing dir ⇒ empty.</summary>
    public static IReadOnlyList<ReviewCheck> Load(string checksDir) => Load(checksDir, out _);

    /// <summary>Same, reporting the unreadable files — otherwise <c>/check</c> reviews a diff
    /// against fewer criteria than the user believes. See <see cref="MarkdownFolder"/>.</summary>
    public static IReadOnlyList<ReviewCheck> Load(string checksDir, out IReadOnlyList<string> unreadable)
    {
        var checks = new List<ReviewCheck>();
        foreach (var (file, text) in MarkdownFolder.ReadAll(checksDir, "ChecksService.Load", out unreadable))
        {
            var (fm, body) = RulesService.ParseFrontMatter(text);
            if (string.IsNullOrWhiteSpace(body)) continue;

            var name = fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
                ? d.Trim()
                : Path.GetFileNameWithoutExtension(file);

            checks.Add(new ReviewCheck(name, body.Trim()));
        }
        return checks;
    }

    /// <summary>
    /// The user message for the <c>/check</c> review request: every check as a
    /// "### name + criteria" section, then the diff in a fenced block.
    /// </summary>
    public static string BuildReviewPrompt(IEnumerable<ReviewCheck> checks, string diff)
    {
        var sb = new StringBuilder();
        foreach (var c in checks)
            sb.Append("### ").Append(c.Name).Append('\n').Append(c.Criteria).Append("\n\n");
        return $"## Checks\n\n{sb}## Diff\n\n```diff\n{diff}\n```";
    }
}
