using System.IO;
using Inferpal.Config;
using Inferpal.Services.Persistence;

namespace Inferpal.Services;

/// <summary>
/// Single loader for user-defined slash templates: config <c>PromptTemplates</c> lines first,
/// then <c>.inferpal/prompts/*.md</c> files. Config entries shadow a prompt file with the same
/// command name, and a template named like a built-in command is dropped (see <see cref="Load"/>).
/// Shared by the VS view-model and the Host (`command/list`, template expansion).
/// </summary>
internal static class SlashTemplates
{
    public static IReadOnlyList<UserSlashTemplate> Load(InferpalConfig config, string? rootDir)
    {
        var fromConfig = SlashCommandRouter.ParseUserTemplates(config.PromptTemplates);
        IReadOnlyList<UserSlashTemplate> fromFiles = string.IsNullOrEmpty(rootDir)
            ? []
            : PromptFilesService.Load(Path.Combine(rootDir, ".inferpal", "prompts"));

        // A template named like a built-in never runs — the router answers the built-in first — so it is
        // not offered either: in autocomplete, picking it ran the built-in under the template's hint.
        //
        // ⚠ DroppedLineOnce, not DroppedLine: this loader is the AUTOCOMPLETE one
        // (`MatchCommands`), so it runs on every keystroke while a slash command is being typed. A
        // shadowed template laid one entry per key, and the diagnostics ring's 200 entries were
        // gone within seconds of typing.
        var usable = new List<UserSlashTemplate>();
        foreach (var t in fromConfig.Concat(fromFiles).DistinctBy(t => t.Name))
        {
            if (SlashCommandRouter.IsBuiltIn(t.Name))
                Diagnostics.DroppedLineOnce(
                    "UserTemplates", "Command template shadowed by a built-in command, never run", t.Name, t.Name);
            else
            {
                usable.Add(t);
                // The name stopped being shadowed (template renamed, command removed): the next
                // clash on that name will speak again.
                Diagnostics.ForgetDroppedLine("UserTemplates", t.Name);
            }
        }
        return usable;
    }

    /// <summary>Autocomplete hint of a template: explicit hint, else its text truncated for display.</summary>
    public static string HintOf(UserSlashTemplate t)
        => t.Hint ?? (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text);
}
