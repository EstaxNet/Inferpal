using System.IO;
using System.Text;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Persistence;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure execution logic for the three "list-or-scaffold" config commands — <c>/rules</c>,
/// <c>/checks</c> and <c>/prompts</c> — extracted from <c>InferpalToolWindowData</c> so the routing
/// and list formatting are unit-testable without VS.
/// </summary>
/// <remarks>
/// Each command either lists the markdown files under its <c>.inferpal/&lt;kind&gt;</c> directory or,
/// with <c>init</c>, scaffolds an example. The handler returns a <see cref="CommandListResult"/>
/// carrying <em>either</em> a list <see cref="CommandListResult.Message"/> <em>or</em> a
/// <see cref="ScaffoldRequest"/> the VM executes (writing the file is VM/IO work, and lets the VM run
/// command-specific follow-ups such as invalidating the prompt-file cache). Same pattern as
/// <see cref="SnippetsCommandHandler"/>.
/// </remarks>
internal static class RulesChecksPromptsCommandHandler
{
    /// <summary>Outcome: exactly one of the two fields is set.</summary>
    /// <param name="Message">List markdown to show.</param>
    /// <param name="Scaffold">Example file the VM must create (for the <c>init</c> sub-command).</param>
    internal readonly record struct CommandListResult(string? Message, ScaffoldRequest? Scaffold = null);

    /// <summary>An example file to write under <paramref name="Dir"/> (created only if absent).</summary>
    internal sealed record ScaffoldRequest(string Dir, string FileName, string Content);

    private static bool IsInit(string[] parts) =>
        parts.Length >= 2 && string.Equals(parts[1], "init", StringComparison.OrdinalIgnoreCase);

    // ── /rules ────────────────────────────────────────────────────────────────--
    internal const string RulesExampleContent =
        "---\n" +
        "description: C# naming conventions\n" +
        "globs: **/*.cs\n" +
        "alwaysApply: false\n" +
        "---\n" +
        "- Use PascalCase for public members and types, camelCase for locals and parameters.\n" +
        "- Prefix private fields with an underscore (`_field`).\n" +
        "- Prefer expression-bodied members for one-line methods.\n\n" +
        "Set `alwaysApply: true` (or remove `globs`) to inject a rule on every file.\n";

    public static CommandListResult Rules(string projectRoot, string[] parts)
    {
        var dir = Path.Combine(projectRoot, ".inferpal", "rules");
        if (IsInit(parts))
            return new(null, new ScaffoldRequest(dir, "example.md", RulesExampleContent));

        var rules = RulesService.Load(dir, out var unreadable);
        // BEFORE the "no rules": if every file is unreadable the list is empty and the absence
        // message would be the worst possible rendering - it would claim the user wrote no rule at
        // all while their files are right there, simply not read.
        if (rules.Count == 0)
            return new(Unreadable(unreadable) is { } only ? Strings.RulesNone + "\n\n" + only : Strings.RulesNone);

        var sb = new StringBuilder(Strings.RulesListHeader);
        foreach (var r in rules)
        {
            var scope = r.AlwaysApply || r.Globs.Count == 0 ? "always" : string.Join(", ", r.Globs);
            sb.Append("\n- **").Append(r.Name).Append("** — `").Append(scope).Append('`');
        }
        return new(Append(sb, unreadable));
    }

    // ── /checks ───────────────────────────────────────────────────────────────--
    internal const string ChecksExampleContent =
        "---\n" +
        "description: No hardcoded secrets\n" +
        "---\n" +
        "Flag any hardcoded credential, API key, password, connection string, or private token\n" +
        "introduced by the diff. Secrets must come from configuration or environment variables,\n" +
        "never be committed in source.\n";

    public static CommandListResult Checks(string projectRoot, string[] parts)
    {
        var dir = Path.Combine(projectRoot, ".inferpal", "checks");
        if (IsInit(parts))
            return new(null, new ScaffoldRequest(dir, "no-secrets.md", ChecksExampleContent));

        var checks = ChecksService.Load(dir, out var unreadable);
        if (checks.Count == 0)
            return new(Unreadable(unreadable) is { } only ? Strings.ChecksNone + "\n\n" + only : Strings.ChecksNone);

        var sb = new StringBuilder(Strings.ChecksListHeader);
        foreach (var c in checks)
            sb.Append("\n- **").Append(c.Name).Append("**");
        return new(Append(sb, unreadable));
    }

    // ── /prompts ──────────────────────────────────────────────────────────────--
    internal const string PromptsExampleContent =
        "---\n" +
        "description: Security-focused review of the given code\n" +
        "---\n" +
        "Review the following code with a security focus: injection risks, unvalidated\n" +
        "inputs, secrets in source, unsafe deserialization, path traversal.\n" +
        "List findings by severity, then propose fixes.\n" +
        "\n" +
        "{args}\n";

    /// <param name="config">The settings: their <c>PromptTemplates</c> win over a file of the same name, and
    /// <c>repoInstructionFamilies</c> says whose command folders are read.</param>
    /// <remarks>
    /// Read from the loader the router uses (<see cref="SlashTemplates.Report"/>), so the listing says which command
    /// runs. ⚠ The settings' templates come first: a file of the same name is listed with its own description, and
    /// typing it runs the settings' text.
    /// </remarks>
    public static CommandListResult Prompts(string projectRoot, string[] parts, InferpalConfig? config = null)
    {
        var dir = Path.Combine(projectRoot, ".inferpal", "prompts");
        if (IsInit(parts))
            return new(null, new ScaffoldRequest(dir, "review-security.md", PromptsExampleContent));

        // A listing reads the folders as they are now, not as the autocomplete cached them a moment ago.
        PromptFilesService.InvalidateCache();
        PromptFilesService.LoadUncached(dir, out var unreadable);
        var report  = SlashTemplates.Report(config ?? new InferpalConfig(), projectRoot);
        var own     = report.Entries.Where(e => e.Template.Source?.StartsWith(".inferpal/prompts/", StringComparison.Ordinal) == true).ToList();
        var foreign = report.Entries.Where(e => e.Template.Origin is not null).ToList();

        var sb = new StringBuilder();
        // "No prompt files" right above the repository's commands read as "no commands".
        if (own.Count == 0 && foreign.Count > 0)
            sb.Append(Unreadable(unreadable) ?? "");
        else if (own.Count == 0)
            sb.Append(Unreadable(unreadable) is { } only ? Strings.PromptsNone + "\n\n" + only : Strings.PromptsNone);
        else
        {
            sb.Append(Strings.PromptsListHeader);
            foreach (var e in own)
            {
                sb.Append("\n- `").Append(e.Template.Name).Append('`').Append(Masked(e));
                if (e.Template.Hint is not null) sb.Append(" — ").Append(e.Template.Hint);
            }
            if (Unreadable(unreadable) is { } line) sb.Append("\n\n").Append(line);
        }

        var repo = report.Repository;
        if (foreign.Count == 0 && repo.LinksLeaving.Count == 0 && repo.Unreadable.Count == 0) return new(sb.ToString());
        if (sb.Length > 0) sb.Append("\n\n");
        sb.Append(Strings.PromptsRepoHeader);
        foreach (var e in foreign)
        {
            var t = e.Template;
            sb.Append("\n- `").Append(t.Name).Append("` — ").Append(t.Origin).Append(" (`").Append(t.Source).Append("`)")
              .Append(Masked(e));
            if (t.Hint is not null) sb.Append(" — ").Append(t.Hint);
            if (t.Unfilled is { Count: > 0 } unfilled) sb.Append(Strings.PromptsUnfilled(Quoted(unfilled)));
            if (t.NotRun is { Count: > 0 } notRun) sb.Append(Strings.PromptsCommandsNotRun(Quoted(notRun)));
        }
        foreach (var file in repo.LinksLeaving) sb.Append("\n\n").Append(Strings.PromptsLinkLeaves(file));
        if (Unreadable(repo.Unreadable) is { } repoLine) sb.Append("\n\n").Append(repoLine);
        return new(sb.ToString());
    }

    /// <summary>Why a listed command never runs — the router answers a built-in first, then the first of a name — or nothing.</summary>
    private static string Masked(SlashTemplateEntry e) =>
        e.BuiltIn                 ? Strings.PromptsShadowedByBuiltIn(e.Template.Name)
        : e.Winner is null        ? ""
        : e.Winner.Source is null ? Strings.PromptsShadowedByConfig(e.Template.Name)
        :                           Strings.PromptsShadowedByFile(e.Template.Name, e.Winner.Source);

    private static string Quoted(IEnumerable<string> items) => string.Join(", ", items.Select(i => "`" + i + "`"));

    // The files that could not be read
    //
    // They are not absent: they are there, and they do not apply. A rule that stops constraining
    // the model and a check that stops being applied announce themselves nowhere else - only this
    // line makes them visible. PlanStore.List had already written the rule, for itself alone:
    // "list it rather than hide it, so a permission problem shows up instead of a silently
    // shorter list."

    /// <summary>The warning line, or <c>null</c> when everything was read (nothing to say).</summary>
    internal static string? Unreadable(IReadOnlyList<string> unreadable) =>
        unreadable.Count == 0
            ? null
            : Strings.GovernanceFilesUnreadable(unreadable.Count, string.Join(", ", unreadable));

    private static string Append(StringBuilder sb, IReadOnlyList<string> unreadable)
    {
        if (Unreadable(unreadable) is { } line) sb.Append("\n\n").Append(line);
        return sb.ToString();
    }
}
