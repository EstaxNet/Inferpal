using System.IO;
using System.Text;

namespace Inferpal.Services.Persistence;

/// <summary>One plan on disk, as the listing shows it.</summary>
/// <param name="Name">File stem — what the user types (<c>/plan open port-index</c>).</param>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Title">Heading of the document.</param>
/// <param name="Done">Steps ticked.</param>
/// <param name="Total">Steps in total.</param>
internal sealed record PlanSummary(string Name, string Path, string Title, int Done, int Total)
{
    /// <summary><c>true</c> when every step is ticked (and there is at least one).</summary>
    public bool Complete => Total > 0 && Done == Total;
}

/// <summary>
/// Reads and writes the plans of a workspace: <c>.inferpal/plans/*.md</c> (roadmap §17).
/// </summary>
/// <remarks>
/// <para>
/// Committed markdown, deliberately, like <c>rules/</c>, <c>checks/</c> and <c>prompts/</c> next to
/// it: re-readable, hand-editable, diffable and shareable across a team. A plan that lived in
/// <c>%AppData%</c> would survive a restart but not reach a colleague, and would not appear in the
/// review of the change it describes.
/// </para>
/// <para>
/// Every write goes through <see cref="AtomicFile"/>. A plan is edited while it is open in an editor
/// and read back on the next command; a truncated file would lose work the user typed by hand, which
/// this store has no way to reconstruct.
/// </para>
/// <para>
/// ⚠ See <see cref="PlanDocument"/> for the rule that governs this whole area: a plan file arrives
/// with any clone, so only its <i>structure</i> is ever parsed and nothing in it can grant anything.
/// </para>
/// </remarks>
internal static class PlanStore
{
    /// <summary>Directory holding the plans of a workspace.</summary>
    public static string DirectoryFor(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".inferpal", "plans");

    /// <summary>
    /// Turns free text into a file stem: lower-case, separators collapsed to '-', anything a file
    /// system would refuse dropped. Same convention as a prompt file's command name.
    /// </summary>
    /// <remarks>
    /// This is also the containment boundary: a name reaching here may come from the model
    /// (<c>/plan save</c> names a plan after the task), so <c>..</c>, slashes and drive letters must
    /// not survive it. The result cannot leave the plans directory — path traversal is impossible by
    /// construction rather than caught afterwards.
    /// </remarks>
    public static string SanitizeName(string? raw)
    {
        var sb = new StringBuilder();
        foreach (var c in (raw ?? string.Empty).Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))                          sb.Append(c);
            // Separators become '-' rather than vanishing: "feature/login" should read
            // "feature-login", not "featurelogin". A dash cannot traverse, so this is a
            // readability choice inside an already-closed boundary.
            else if (c is ' ' or '_' or '-' or '.' or '/' or '\\') sb.Append('-');
            // everything else — colons, quotes, control characters — is dropped
        }

        var name = sb.ToString().Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
            name = name.Replace("--", "-", StringComparison.Ordinal);

        return name.Length == 0 ? "plan" : Truncate(name, 60);
    }

    /// <summary>Absolute path of a plan, from a name that has already been sanitised here.</summary>
    public static string PathFor(string workspaceRoot, string name) =>
        Path.Combine(DirectoryFor(workspaceRoot), SanitizeName(name) + ".md");

    /// <summary>Every plan of the workspace, alphabetical. Empty when the directory does not exist.</summary>
    public static IReadOnlyList<PlanSummary> List(string workspaceRoot)
    {
        var dir = DirectoryFor(workspaceRoot);
        if (!Directory.Exists(dir)) return [];

        var result = new List<PlanSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                                      .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            string text;
            try { text = File.ReadAllText(file, Encoding.UTF8); }
            catch (Exception ex)
            {
                // A plan we cannot read is still a plan the user has: list it rather than hide it,
                // so a permission problem shows up instead of a silently shorter list.
                Diagnostics.Swallow($"PlanStore.List({file})", ex);
                result.Add(new PlanSummary(name, file, name, 0, 0));
                continue;
            }

            var doc = PlanDocument.Parse(text, name);
            result.Add(new PlanSummary(name, file, doc.Title, doc.DoneCount, doc.Steps.Count));
        }
        return result;
    }

    /// <summary>Loads one plan, or <c>null</c> when it does not exist or cannot be read.</summary>
    public static PlanDocument? Load(string workspaceRoot, string name)
    {
        var path = PathFor(workspaceRoot, name);
        try
        {
            return File.Exists(path)
                ? PlanDocument.Parse(File.ReadAllText(path, Encoding.UTF8), SanitizeName(name))
                : null;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"PlanStore.Load({path})", ex);
            return null;
        }
    }

    /// <summary>Writes a plan. Returns its path.</summary>
    public static string Save(string workspaceRoot, string name, string content)
    {
        var path = PathFor(workspaceRoot, name);
        AtomicFile.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Ticks (or unticks) one step and writes the file back.
    /// </summary>
    /// <returns>
    /// The reloaded plan on success; <c>null</c> when the plan is missing, the step does not exist,
    /// or it already had that state — in which case nothing was written.
    /// </returns>
    public static PlanDocument? SetStepDone(string workspaceRoot, string name, int step, bool done)
    {
        var doc = Load(workspaceRoot, name);
        var updated = doc?.WithStepDone(step, done);
        if (updated is null) return null;

        try
        {
            AtomicFile.WriteAllText(PathFor(workspaceRoot, name), updated);
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"PlanStore.SetStepDone({name})", ex);
            return null;
        }

        return PlanDocument.Parse(updated, SanitizeName(name));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd('-');
}
