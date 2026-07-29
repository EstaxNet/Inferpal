using System.IO;
using Inferpal.Config;
using Inferpal.Services.Persistence;

namespace Inferpal.Services;

/// <summary>
/// Single loader for user-defined slash templates: config <c>PromptTemplates</c> lines first,
/// then <c>.inferpal/prompts/*.md</c> files. Config entries shadow a prompt file with the same
/// command name (the router resolves with FirstOrDefault; built-ins always win over both).
/// Shared by the VS view-model and the Host (`command/list`, template expansion).
/// </summary>
internal static class SlashTemplates
{
    public static IReadOnlyList<UserSlashTemplate> Load(InferpalConfig config, string? rootDir)
    {
        var fromConfig = SlashCommandRouter.ParseUserTemplates(config.PromptTemplates);
        if (string.IsNullOrEmpty(rootDir))
            return fromConfig;

        var fromFiles = PromptFilesService.Load(Path.Combine(rootDir, ".inferpal", "prompts"));
        return fromFiles.Count == 0
            ? fromConfig
            : fromConfig.Concat(fromFiles).DistinctBy(t => t.Name).ToList();
    }

    /// <summary>Autocomplete hint of a template: explicit hint, else its text truncated for display.</summary>
    public static string HintOf(UserSlashTemplate t)
        => t.Hint ?? (t.Text.Length > 50 ? t.Text[..50] + "…" : t.Text);
}
