using System.Text;
using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// <c>/skill</c>: lists the skills of the repository and of the user; <c>/skill &lt;name&gt; &lt;request&gt;</c> asks the
/// request with the skill joined to it — its <c>SKILL.md</c> body and the index of its files, as a named attachment.
/// Shared by both editors (the VS window, the host's <c>command/slash</c>).
/// </summary>
/// <remarks>
/// ⚠ An attachment, never the question: a <c>SKILL.md</c> runs to hundreds of lines, and as the question it would be the
/// bubble, the saved transcript and what "Regenerate" asks again. Attached, the question is what the user typed and the
/// skill is named under it, like any file joined to a question.
/// </remarks>
internal static class SkillCommandHandler
{
    /// <summary>Either <paramref name="Message"/> to show, or <paramref name="Question"/> to ask with
    /// <paramref name="Attachment"/> joined.</summary>
    internal sealed record Result(string? Message, string? Question = null, AttachmentContent? Attachment = null);

    /// <param name="root">The workspace the agent's tools work in (the catalog reads the repository holding it).</param>
    /// <param name="home">The user's home folder; <c>null</c> = the real one (tests pass their own).</param>
    public static Result Run(InferpalConfig config, string? root, string[] parts, string? home = null)
    {
        var catalog = RepoSkills.Load(root, RepoInstructionFormats.Families(config.RepoInstructionFamilies).On, home);
        if (parts.Length < 2) return new(List(catalog));

        if (catalog.Find(parts[1]) is not { } skill)
            return new(Strings.SkillUnknown(parts[1]) + "\n\n" + List(catalog));

        string body;
        try { body = RepoSkills.Body(skill); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("SkillCommandHandler.Body", ex);
            return new(Strings.SkillUnreadableNow(skill.Shown));
        }
        var (files, omitted) = RepoSkills.FilesOf(skill);
        var request = string.Join(" ", parts[2..]).Trim();
        return new(null,
                   request.Length > 0 ? request : Strings.SkillApplyDefault(skill.Name),
                   new AttachmentContent(Strings.SkillAttachmentLabel(skill.Name),
                                         ModelPrompts.SkillContent(skill.Name, skill.Folder, body, files, omitted)));
    }

    /// <summary>The catalog as the chat shows it: each skill, what it is for and where it lives; then what was not loaded.</summary>
    internal static string List(SkillCatalog catalog)
    {
        if (catalog.Skills.Count == 0 && catalog.Skipped.Count == 0) return Strings.SkillsNone;
        var sb = new StringBuilder();
        if (catalog.Skills.Count == 0) sb.Append(Strings.SkillsNone);
        else
        {
            sb.Append("## ").AppendLine(Strings.SkillsTitle).AppendLine();
            foreach (var s in catalog.Skills)
                sb.Append("- `").Append(s.Name).Append("` — ").Append(s.Description.Replace('\n', ' '))
                  .Append(" · ").Append(s.Origin).Append(" (`").Append(s.Shown).AppendLine("`)");
            sb.AppendLine().Append(Strings.SkillsUsage);
        }
        if (catalog.Skipped.Count > 0)
        {
            sb.AppendLine().AppendLine().AppendLine(Strings.SkillsNotLoaded);
            foreach (var s in catalog.Skipped)
                sb.Append("- `").Append(s.Shown).Append("` — ").AppendLine(s.Reason switch
                {
                    SkillSkipReason.NoDescription => Strings.SkillNoDescription,
                    SkillSkipReason.LinkLeaves    => Strings.SkillLinkLeaves,
                    SkillSkipReason.Shadowed      => Strings.SkillShadowed(s.By!),
                    _                             => Strings.SkillUnreadable,
                });
        }
        return sb.ToString().TrimEnd();
    }
}
