using System.IO;
using System.Text;
using Inferpal.Localization;

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

        var rules = RulesService.Load(dir);
        if (rules.Count == 0) return new(Strings.RulesNone);

        var sb = new StringBuilder(Strings.RulesListHeader);
        foreach (var r in rules)
        {
            var scope = r.AlwaysApply || r.Globs.Count == 0 ? "always" : string.Join(", ", r.Globs);
            sb.Append("\n- **").Append(r.Name).Append("** — `").Append(scope).Append('`');
        }
        return new(sb.ToString());
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

        var checks = ChecksService.Load(dir);
        if (checks.Count == 0) return new(Strings.ChecksNone);

        var sb = new StringBuilder(Strings.ChecksListHeader);
        foreach (var c in checks)
            sb.Append("\n- **").Append(c.Name).Append("**");
        return new(sb.ToString());
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

    public static CommandListResult Prompts(string projectRoot, string[] parts)
    {
        var dir = Path.Combine(projectRoot, ".inferpal", "prompts");
        if (IsInit(parts))
            return new(null, new ScaffoldRequest(dir, "review-security.md", PromptsExampleContent));

        var prompts = PromptFilesService.LoadUncached(dir);
        if (prompts.Count == 0) return new(Strings.PromptsNone);

        var sb = new StringBuilder(Strings.PromptsListHeader);
        foreach (var p in prompts)
        {
            sb.Append("\n- `").Append(p.Name).Append('`');
            if (p.Hint is not null) sb.Append(" — ").Append(p.Hint);
        }
        return new(sb.ToString());
    }
}
