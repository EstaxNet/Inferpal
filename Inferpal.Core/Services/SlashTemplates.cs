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
        var usable = new List<UserSlashTemplate>();
        foreach (var t in fromConfig.Concat(fromFiles).DistinctBy(t => t.Name))
        {
            if (SlashCommandRouter.IsBuiltIn(t.Name))
                Diagnostics.DroppedLine("UserTemplates", "Command template shadowed by a built-in command, never run", t.Name);
            else
                usable.Add(t);
        }
        return usable;
    }

    /// <summary>Autocomplete hint of a template: explicit hint, else its text truncated for display.</summary>
    public static string HintOf(UserSlashTemplate t)
        => t.Hint ?? (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text);
}
