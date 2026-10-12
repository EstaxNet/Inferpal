using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Config;
using Inferpal.Services.Docs;
using Inferpal.Services.Mcp;

namespace Inferpal.Services.Import;

/// <summary>An item that could not be written, and why (in English: the detail of a translated sentence).</summary>
internal sealed record ImportFailure(ImportItem Item, string Reason);

/// <summary>What an application of the import did.</summary>
/// <param name="Backup">The copy of the settings file made before anything changed; <c>null</c> when none was needed.</param>
/// <param name="ConfigChanged">The settings changed in memory: the caller saves them.</param>
/// <param name="ServerChanged">The server changed: the editor takes it after a restart.</param>
/// <param name="Refused">Nothing was written, and why — the copy of the settings could not be made.</param>
internal sealed record ImportOutcome(IReadOnlyList<ImportItem> Applied, IReadOnlyList<ImportFailure> Failed,
                                     string? Backup, bool ConfigChanged, bool ServerChanged, string? Refused = null);

/// <summary>
/// Writes the items the user ticked, and only those: the settings in memory (the caller saves them) and one file per
/// command in the machine's prompts folder.
/// </summary>
/// <remarks>
/// <para>⚠ The settings file is copied aside BEFORE anything changes, and an import whose copy fails writes nothing —
/// an import touches the server, the models and the system prompt at once, and the copy is the way back.</para>
/// <para>⚠ The MCP list is merged as JSON, entry by entry: parsed into records and written back, it would drop every entry
/// the reader skips — the user's own servers among them.</para>
/// <para>⚠ A command file that exists is never overwritten: the plan already renamed around the names it saw, so a file
/// there now appeared in between.</para>
/// </remarks>
internal static class ImportApplier
{
    public static ImportOutcome Apply(ImportPlan plan, IReadOnlySet<string> ids, InferpalConfig config, string configFile,
                                      string commandsFolder, DateTime now)
    {
        var chosen = plan.Items.Where(i => ids.Contains(i.Id)).ToList();
        string? backup = null;
        if (chosen.Any(i => i.Kind != ImportItemKind.Command) && File.Exists(configFile))
        {
            try { backup = CopyAside(configFile, now); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Swallow("ImportApplier.Backup", ex);
                return new ImportOutcome([], [], null, false, false, ex.Message);
            }
        }

        var applied = new List<ImportItem>();
        var failed = new List<ImportFailure>();
        var serverChanged = false;
        foreach (var item in chosen)
        {
            try
            {
                if (Write(item, config, commandsFolder) is { } reason) failed.Add(new(item, reason));
                else
                {
                    applied.Add(item);
                    serverChanged |= item.Kind == ImportItemKind.Backend;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Diagnostics.Swallow("ImportApplier.Write", ex);
                failed.Add(new(item, ex.Message));
            }
        }
        if (applied.Any(i => i.Kind == ImportItemKind.Command)) PromptFilesService.InvalidateCache();
        return new ImportOutcome(applied, failed, backup, applied.Any(i => i.Kind != ImportItemKind.Command), serverChanged);
    }

    /// <summary>The copy of the settings before the import, beside them; a second import in the same second gets its own.</summary>
    private static string CopyAside(string configFile, DateTime now)
    {
        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var n = 1; ; n++)
        {
            var copy = configFile + ".before-import-" + stamp + (n == 1 ? "" : "-" + n.ToString(CultureInfo.InvariantCulture)) + ".json";
            if (File.Exists(copy)) continue;
            File.Copy(configFile, copy, overwrite: false);
            return copy;
        }
    }

    /// <summary>Applies one item; the reason it could not be, or <c>null</c>.</summary>
    private static string? Write(ImportItem item, InferpalConfig config, string commandsFolder)
    {
        switch (item.Effect)
        {
            case SetBackend backend:
                config.Provider = backend.Provider;
                config.BaseUrl = backend.BaseUrl;
                if (backend.ApiKey is not null) config.ApiKey = backend.ApiKey;
                return null;
            case SetModel model:
                switch (model.Role)
                {
                    case ImportItemKind.ChatModel:       config.DefaultModel = model.Model; break;
                    case ImportItemKind.CompletionModel: config.InlineCompletionModel = model.Model; break;
                    case ImportItemKind.EditModel:       config.InlineEditModel = model.Model; break;
                    case ImportItemKind.UtilityModel:    config.UtilityModel = model.Model; break;
                    case ImportItemKind.EmbeddingModel:  config.RagEmbeddingModel = model.Model; break;
                    default: return "not a model setting: " + model.Role;
                }
                return null;
            case SetContextWindow window:
                config.ContextWindowSize = window.Tokens;
                return null;
            case AddInstructions instructions:
                config.CustomSystemPrompt = Append(config.CustomSystemPrompt, $"## {instructions.Title}\n\n{instructions.Text.Trim()}", "\n\n");
                return null;
            case WriteCommand command:
                return WriteCommandFile(command, commandsFolder);
            case AddMcpServer server:
                return AddServer(config, server.Server);
            case AddDocSite site:
                // The id is chosen among the sources as the file holds them (see InferpalConfig.FollowFile).
                config.FollowFile(nameof(InferpalConfig.DocSitesJson));
                if (!DocSite.TryParse(config.DocSitesJson, out var sites, out var problem) && !string.IsNullOrWhiteSpace(config.DocSitesJson))
                    return "the documentation sources in the settings are not readable: " + problem;
                config.DocSitesJson = DocSite.Serialize(DocSite.Upsert(sites, DocSite.CreateAmong(site.StartUrl, site.Title, sites)));
                return null;
            case AddPermissionRule rule:
                // ⚠ The first rule that matches decides. Appended after an allow — imported (Roo's allowed list comes
                // before its denied one) or the user's own — a deny never matches: `allow git` lets `git push --force`
                // run unasked while the card showed "deny git push" ticked. A deny goes first; an allow, last.
                config.PermissionRules = rule.Rule.StartsWith("deny", StringComparison.OrdinalIgnoreCase)
                    ? Prepend(config.PermissionRules, rule.Rule)
                    : Append(config.PermissionRules, rule.Rule, "\n");
                return null;
            case SetLanguage language:
                config.Language = language.Code;
                return null;
            case SetIndexing indexing:
                config.RagEnabled = indexing.Enabled;
                return null;
            default:
                return "unknown item";
        }
    }

    private static string Append(string? existing, string text, string separator) =>
        string.IsNullOrWhiteSpace(existing) ? text : existing.TrimEnd() + separator + text;

    private static string Prepend(string? existing, string line) =>
        string.IsNullOrWhiteSpace(existing) ? line : line + "\n" + existing.TrimStart('\r', '\n');

    private static string? WriteCommandFile(WriteCommand command, string folder)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, command.Name + ".md");
        if (File.Exists(file)) return "a file of that name appeared in the meantime: " + file;
        var header = string.IsNullOrWhiteSpace(command.Description)
            ? string.Empty
            : "---\ndescription: " + string.Join(' ', command.Description.Split('\n', StringSplitOptions.TrimEntries)) + "\n---\n\n";
        AtomicFile.WriteAllText(file, header + command.Text.Trim() + "\n");
        return null;
    }

    private static string? AddServer(InferpalConfig config, McpServerConfig server)
    {
        JsonObject root;
        if (string.IsNullOrWhiteSpace(config.McpServersJson)) root = new JsonObject();
        else if (JsonNode.Parse(config.McpServersJson, documentOptions: new JsonDocumentOptions
                 {
                     CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                 }) is JsonObject parsed) root = parsed;
        else return "the MCP server list in the settings is not a JSON object";

        var map = root.ContainsKey("mcpServers") ? root["mcpServers"] as JsonObject : root;
        if (map is null) return "\"mcpServers\" in the settings is not a JSON object";
        if (map.ContainsKey(server.Name)) return "a server of that name was added in the meantime: " + server.Name;
        var entry = (JsonNode.Parse(McpServerConfig.Serialize([server])) as JsonObject)?[server.Name];
        if (entry is null) return "the server could not be written";
        map[server.Name] = entry.DeepClone();
        config.McpServersJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        config.McpEnabled = true;
        return null;
    }
}
