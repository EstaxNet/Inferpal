using System.IO;
using Inferpal.Config;
using Inferpal.Services.Persistence;
using Inferpal.Services.Prompting;

namespace Inferpal.Services;

/// <summary>A user or repository command as <c>/prompts</c> lists it: usable, or masked — and by what.</summary>
/// <param name="BuiltIn">A built-in command has its name: the router answers it first, this never runs.</param>
/// <param name="Winner">Another command of the same name, earlier in precedence, that runs instead.</param>
internal sealed record SlashTemplateEntry(UserSlashTemplate Template, bool BuiltIn, UserSlashTemplate? Winner)
{
    public bool Usable => !BuiltIn && Winner is null;
}

/// <summary>Every command found, in precedence order, and the repository's files that are none.</summary>
internal sealed record SlashTemplateReport(IReadOnlyList<SlashTemplateEntry> Entries, RepoCommandScan Repository)
{
    public IReadOnlyList<UserSlashTemplate> Usable => [.. Entries.Where(e => e.Usable).Select(e => e.Template)];
}

/// <summary>
/// Single loader for user-defined slash commands: the settings' <c>PromptTemplates</c> lines, then
/// <c>.inferpal/prompts/*.md</c>, then the commands the repository wrote for Copilot, Claude Code and Continue
/// (<see cref="RepoCommandFiles"/>). Of two commands with one name the first wins, and a command named like a built-in
/// is dropped (see <see cref="Report"/>). Shared by the VS view-model and the Host (`command/list`, template expansion).
/// </summary>
internal static class SlashTemplates
{
    public static IReadOnlyList<UserSlashTemplate> Load(InferpalConfig config, string? rootDir) =>
        Report(config, rootDir).Usable;

    /// <summary>Every command, usable or masked, in precedence order — what <c>/prompts</c> lists.</summary>
    public static SlashTemplateReport Report(InferpalConfig config, string? rootDir)
    {
        var fromConfig = SlashCommandRouter.ParseUserTemplates(config.PromptTemplates);
        IReadOnlyList<UserSlashTemplate> fromFiles = string.IsNullOrEmpty(rootDir)
            ? []
            : PromptFilesService.Load(Path.Combine(rootDir, ".inferpal", "prompts"));
        var repository = string.IsNullOrEmpty(rootDir)
            ? RepoCommandScan.None
            : RepoCommandFiles.Load(rootDir, RepoInstructionFormats.Families(config.RepoInstructionFamilies).On);

        // A template named like a built-in never runs — the router answers the built-in first — so it is
        // not offered either: in autocomplete, picking it ran the built-in under the template's hint.
        //
        // ⚠ DroppedLineOnce, not DroppedLine: this loader is the AUTOCOMPLETE one
        // (`MatchCommands`), so it runs on every keystroke while a slash command is being typed. A
        // shadowed template laid one entry per key, and the diagnostics ring's 200 entries were
        // gone within seconds of typing.
        var entries = new List<SlashTemplateEntry>();
        var winners = new Dictionary<string, UserSlashTemplate>(StringComparer.Ordinal);
        foreach (var t in fromConfig.Concat(fromFiles).Concat(repository.Commands))
        {
            if (SlashCommandRouter.IsBuiltIn(t.Name))
            {
                Diagnostics.DroppedLineOnce(
                    "UserTemplates", "Command template shadowed by a built-in command, never run", t.Name, t.Name);
                entries.Add(new(t, BuiltIn: true, Winner: null));
                continue;
            }
            // The name stopped being shadowed (template renamed, command removed): the next
            // clash on that name will speak again.
            Diagnostics.ForgetDroppedLine("UserTemplates", t.Name);
            if (winners.TryGetValue(t.Name, out var winner))
            {
                entries.Add(new(t, BuiltIn: false, Winner: winner));
                continue;
            }
            winners[t.Name] = t;
            entries.Add(new(t, BuiltIn: false, Winner: null));
        }
        return new SlashTemplateReport(entries, repository);
    }

    /// <summary>Autocomplete hint of a template: explicit hint, else its text truncated for display — then the tool it
    /// was written for, when it was.</summary>
    public static string HintOf(UserSlashTemplate t)
    {
        var hint = t.Hint ?? (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text);
        return t.Origin is null ? hint : hint + " · " + t.Origin;
    }
}
