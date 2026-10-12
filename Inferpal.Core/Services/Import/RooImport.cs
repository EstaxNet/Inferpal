using System.IO;
using System.Text.Json.Nodes;
using Inferpal.Services.Inference;
using static Inferpal.Services.Import.ImportPlanBuilder;

namespace Inferpal.Services.Import;

/// <summary>
/// Reads the file Roo Code exports from its settings (<c>roo-code-settings.json</c>: <c>providerProfiles</c> and
/// <c>globalSettings</c>) into an <see cref="ImportPlan"/>: its instructions, its custom modes as commands, its allowed and
/// denied commands as permission rules, a model a user runs — and every other setting named with the reason it stays.
/// </summary>
/// <remarks>
/// <para>⚠ An allowed command lets a command run WITHOUT asking: it is offered unticked, and not at all when Roo's
/// automatic approval of commands was off — the list did nothing there, importing it would grant more than Roo did.</para>
/// <para>⚠ Roo's own approval switches (<c>alwaysAllowWrite</c>…) are never carried: Inferpal's approval prompt is the
/// boundary, and an import does not turn it off.</para>
/// </remarks>
internal static class RooImport
{
    /// <summary>The export's name, where a browser or the save dialog usually leaves it.</summary>
    public static IReadOnlyList<string> DefaultFiles(string home) =>
    [
        Path.Combine(home, "Downloads", "roo-code-settings.json"),
        Path.Combine(home, "Documents", "roo-code-settings.json"),
        Path.Combine(home, "Desktop", "roo-code-settings.json"),
        Path.Combine(home, "roo-code-settings.json"),
    ];

    /// <summary>The languages Inferpal is translated into, as Roo writes them.</summary>
    private static readonly IReadOnlyList<string> Languages = ["en", "fr", "de", "es", "it", "ru", "ja", "ko", "pl", "zh-CN"];

    private static readonly HashSet<string> AutoApprovalKeys =
    [
        "autoApprovalEnabled", "alwaysApproveResubmit", "followupAutoApproveTimeoutMs", "allowedMaxRequests",
        "allowedMaxCost", "requestDelaySeconds",
    ];

    private static readonly HashSet<string> CompactionKeys =
        ["autoCondenseContext", "autoCondenseContextPercent", "condensingApiConfigId", "customCondensingPrompt"];

    public static ImportPlan Read(string file, ImportContext context)
    {
        var root = ReadTree(file);
        var plan = new ImportPlanBuilder(ImportSource.Roo, context);
        var backend = new Backend();

        foreach (var (key, value) in root)
        {
            switch (key)
            {
                case "providerProfiles" when value is JsonObject profiles:
                    Profiles(plan, profiles, backend);
                    break;
                case "globalSettings" when value is JsonObject settings:
                    Settings(plan, settings, backend);
                    break;
                default:
                    plan.Skip(key, ImportSkipCause.UnknownKey, key);
                    break;
            }
        }
        backend.Finish(plan);
        return plan.Build(file);
    }

    /// <summary>The server kept, and the paths that name it — written once both parts of the file are read.</summary>
    private sealed class Backend
    {
        public ImportServer? Server;
        public string? ApiKey;
        public readonly List<string> Paths = [];

        public void Finish(ImportPlanBuilder plan)
        {
            if (Server is null || Paths.Count == 0) return;
            var current = plan.Context.Current;
            var key = Server.Provider == InferenceProviderFactory.OpenAiCompatible ? ApiKey : null;
            if (Server.SameAs(current.Provider, current.BaseUrl) && (key is null || key == current.ApiKey))
                foreach (var p in Paths) plan.Skip(p, ImportSkipCause.AlreadyConfigured, Server.BaseUrl);
            else
                plan.Add(ImportItemKind.Backend, new SetBackend(Server.Provider, Server.BaseUrl, key), Paths, true,
                         new ImportItemNote(ImportNote.TakesEffectAfterRestart));
        }
    }

    // ── Profiles ────────────────────────────────────────────────────────────

    private static void Profiles(ImportPlanBuilder plan, JsonObject profiles, Backend backend)
    {
        var configs = profiles["apiConfigs"] as JsonObject ?? [];
        var currentName = Text(profiles["currentApiConfigName"]);
        var read = configs.Where(p => p.Value is JsonObject)
                          .Select(p => (Name: p.Key, Profile: (JsonObject)p.Value!,
                                        Server: ImportServers.FromRoo((JsonObject)p.Value!, out var m, out var k), Model: m, Key: k))
                          .ToList();
        var kept = read.FirstOrDefault(p => p.Name == currentName && p.Server is not null);
        if (kept.Server is null) kept = read.FirstOrDefault(p => p.Server is not null);
        if (kept.Server is not null) { backend.Server = kept.Server; backend.ApiKey = kept.Key; }

        foreach (var (name, profile, server, model, _) in read)
        {
            var path = $"providerProfiles.apiConfigs[{name}]";
            var provider = Text(profile["apiProvider"]);
            if (server is null)
            {
                // "openai" says only the protocol: the host names the service it reaches.
                var hosted = provider == "openai" && Uri.TryCreate(Text(profile["openAiBaseUrl"]), UriKind.Absolute, out var u) ? u.Host : provider;
                plan.SkipAll(path, profile, ImportSkipCause.HostedProvider, hosted);
                continue;
            }
            if (name != kept.Name)
            {
                plan.SkipAll(path, profile, server.SameAs(kept.Server!.Provider, kept.Server.BaseUrl)
                                            ? ImportSkipCause.OtherChatModel : ImportSkipCause.OtherServer, server.BaseUrl);
                continue;
            }
            foreach (var (key, value) in profile)
            {
                var p = path + "." + key;
                switch (key)
                {
                    case "apiProvider" or "ollamaBaseUrl" or "lmStudioBaseUrl" or "openAiBaseUrl" or "openAiApiKey":
                        backend.Paths.Add(p);
                        break;
                    case "ollamaModelId" or "lmStudioModelId" or "openAiModelId":
                        if (model is null) plan.Skip(p, ImportSkipCause.Empty);
                        else if (model == plan.Context.Current.DefaultModel) plan.Skip(p, ImportSkipCause.AlreadyConfigured, model);
                        else
                        {
                            var paths = new List<string> { p };
                            if (name == currentName) paths.Add("providerProfiles.currentApiConfigName");
                            plan.Add(ImportItemKind.ChatModel, new SetModel(ImportItemKind.ChatModel, model), paths);
                        }
                        break;
                    case "openAiCustomModelInfo" when Int(value?["contextWindow"]) is { } window:
                        if (window == plan.Context.Current.ContextWindowSize) plan.Skip(p, ImportSkipCause.AlreadyConfigured);
                        else plan.Add(ImportItemKind.ContextWindow, new SetContextWindow(window), [p]);
                        break;
                    case "modelTemperature":
                        plan.Skip(p, ImportSkipCause.Sampling);
                        break;
                    default:
                        plan.Skip(p, ImportSkipCause.NoEquivalent, key);
                        break;
                }
            }
        }

        foreach (var (key, value) in profiles)
        {
            switch (key)
            {
                case "apiConfigs":
                    if (read.Count == 0) plan.Skip("providerProfiles." + key, ImportSkipCause.Empty);
                    break;
                case "currentApiConfigName":
                    var current = read.FirstOrDefault(p => p.Name == currentName);
                    if (current.Profile is null) plan.Skip("providerProfiles." + key, ImportSkipCause.Empty);
                    else if (current.Server is null)
                        plan.Skip("providerProfiles." + key, ImportSkipCause.HostedProvider, Text(current.Profile["apiProvider"]));
                    else if (current.Model is null || current.Model == plan.Context.Current.DefaultModel)
                        plan.Skip("providerProfiles." + key, ImportSkipCause.AlreadyConfigured, current.Model);
                    break;      // otherwise carried by the chat model's item
                case "modeApiConfigs":
                    plan.Skip("providerProfiles." + key, ImportSkipCause.ModelPerMode);
                    break;
                default:
                    plan.Skip("providerProfiles." + key, ImportSkipCause.NoEquivalent, key);
                    break;
            }
        }
    }

    // ── Settings ────────────────────────────────────────────────────────────

    private static void Settings(ImportPlanBuilder plan, JsonObject settings, Backend backend)
    {
        var commandsRan = Bool(settings["autoApprovalEnabled"]) == true && Bool(settings["alwaysAllowExecute"]) == true;
        foreach (var (key, value) in settings)
        {
            var path = "globalSettings." + key;
            switch (key)
            {
                case "customInstructions":
                    if (Text(value) is { } instructions)
                        plan.Add(ImportItemKind.Instructions, new AddInstructions("Roo custom instructions", instructions), [path]);
                    else plan.Skip(path, ImportSkipCause.Empty);
                    break;
                case "customModes":
                    Modes(plan, path, value as JsonArray);
                    break;
                case "customModePrompts":
                    ModePrompts(plan, path, value as JsonObject);
                    break;
                case "customSupportPrompts":
                    if (value is JsonObject support && support.Any(p => Text(p.Value) is not null))
                        plan.Skip(path, ImportSkipCause.NoEquivalent, string.Join(", ", support.Where(p => Text(p.Value) is not null).Select(p => p.Key)));
                    else plan.Skip(path, ImportSkipCause.Empty);
                    break;
                case "allowedCommands":
                    Commands(plan, path, value, allow: true, commandsRan);
                    break;
                case "deniedCommands":
                    Commands(plan, path, value, allow: false, commandsRan);
                    break;
                case "language":
                    Language(plan, path, Text(value));
                    break;
                case "mode":
                    plan.Skip(path, ImportSkipCause.ActiveMode, Text(value));
                    break;
                case "codebaseIndexConfig":
                    Index(plan, path, value as JsonObject, backend);
                    break;
                case "codebaseIndexModels":
                    plan.Skip(path, ImportSkipCause.ToolCatalogue);
                    break;
                case "mcpEnabled":
                    plan.Skip(path, ImportSkipCause.NoServersInExport);
                    break;
                case "telemetrySetting":
                    plan.Skip(path, ImportSkipCause.Telemetry);
                    break;
                case "enableCheckpoints" or "checkpointTimeout":
                    plan.Skip(path, ImportSkipCause.Checkpoints);
                    break;
                default:
                    if (key.StartsWith("alwaysAllow", StringComparison.Ordinal) || AutoApprovalKeys.Contains(key))
                        plan.SkipValue(path, value, ImportSkipCause.AutoApproval);
                    else if (CompactionKeys.Contains(key)) plan.SkipValue(path, value, ImportSkipCause.Compaction);
                    else plan.SkipValue(path, value, ImportSkipCause.NoEquivalent, key);
                    break;
            }
        }
    }

    /// <summary>A custom mode becomes a command: the mode's role and instructions, then what the user types.</summary>
    private static void Modes(ImportPlanBuilder plan, string path, JsonArray? modes)
    {
        if (modes is null || modes.Count == 0) { plan.Skip(path, ImportSkipCause.Empty); return; }
        for (var i = 0; i < modes.Count; i++)
        {
            var p = $"{path}[{i}]";
            if (modes[i] is not JsonObject mode || Text(mode["slug"]) is not { } slug) { plan.Skip(p, ImportSkipCause.Empty); continue; }
            var text = ModeText(Text(mode["roleDefinition"]), Text(mode["customInstructions"]));
            if (text is null) { plan.Skip(p, ImportSkipCause.Empty); continue; }
            Command(plan, p, slug, Text(mode["description"]) ?? Text(mode["name"]), text,
                    mode["groups"] is JsonArray { Count: > 0 } ? new ImportItemNote(ImportNote.ModeToolsNotKept) : null);
        }
    }

    /// <summary>What a user added to Roo's own modes (<c>code</c>, <c>debug</c>…): a command when it has instructions.</summary>
    private static void ModePrompts(ImportPlanBuilder plan, string path, JsonObject? prompts)
    {
        if (prompts is null || prompts.Count == 0) { plan.Skip(path, ImportSkipCause.Empty); return; }
        var whenToUse = false;
        foreach (var (slug, node) in prompts)
        {
            if (node is not JsonObject mode) continue;
            whenToUse |= mode.ContainsKey("whenToUse") || mode.ContainsKey("description");
            if (ModeText(Text(mode["roleDefinition"]), Text(mode["customInstructions"])) is { } text)
                Command(plan, path, slug, $"Roo's {slug} mode", text, null);
        }
        if (whenToUse) plan.Skip(path, ImportSkipCause.WhenToUse);
        else if (!prompts.Any(p => p.Value is JsonObject m && ModeText(Text(m["roleDefinition"]), Text(m["customInstructions"])) is not null))
            plan.Skip(path, ImportSkipCause.Empty);
    }

    private static string? ModeText(string? role, string? instructions)
    {
        var parts = new[] { role, instructions }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList();
        return parts.Count == 0 ? null : string.Join("\n\n", parts) + "\n\n{args}";
    }

    private static void Command(ImportPlanBuilder plan, string path, string slug, string? description, string text,
                                ImportItemNote? tools)
    {
        if (plan.Command(slug, text) is not { } command)
        {
            plan.Skip(path, ImportSkipCause.AlreadyConfigured, "/" + slug);
            return;
        }
        var notes = new List<ImportItemNote>();
        if (command.Note is { } renamed) notes.Add(renamed);
        if (tools is not null) notes.Add(tools);
        plan.Add(ImportItemKind.Command, new WriteCommand(command.Name, description, text), [path], true, [.. notes]);
    }

    /// <summary>
    /// Roo's command lists, matched by prefix: <c>git log</c> allows <c>git log --oneline</c>. A permission rule says the
    /// same: the prefix, then the end or a blank.
    /// </summary>
    private static void Commands(ImportPlanBuilder plan, string path, JsonNode? value, bool allow, bool commandsRan)
    {
        var list = value as JsonArray;
        if (list is null || !Strings(list).Any(c => c.Trim().Length > 0)) { plan.SkipValue(path, value, ImportSkipCause.Empty); return; }
        if (allow && !commandsRan) { plan.SkipValue(path, value, ImportSkipCause.AutoApprovalWasOff); return; }
        var existing = (plan.Context.Current.PermissionRules ?? string.Empty)
                       .Split('\n').Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            var element = $"{path}[{i}]";
            if (Text(list[i])?.Trim() is not { Length: > 0 } command) { plan.Skip(element, ImportSkipCause.Empty); continue; }
            var rule = (allow ? "allow" : "deny") + " run_command "
                     + (command == "*" ? ".*" : "^" + System.Text.RegularExpressions.Regex.Escape(command) + @"(\s|$)");
            if (!existing.Add(rule)) { plan.Skip(element, ImportSkipCause.AlreadyConfigured, command); continue; }
            plan.Add(ImportItemKind.PermissionRule, new AddPermissionRule(rule), [element], isChecked: !allow,
                     allow ? [new(ImportNote.RunsWithoutAsking, command)] : []);
        }
    }

    private static void Language(ImportPlanBuilder plan, string path, string? code)
    {
        var match = Languages.FirstOrDefault(l => string.Equals(l, code, StringComparison.OrdinalIgnoreCase));
        if (code is null) plan.Skip(path, ImportSkipCause.Empty);
        else if (match is null) plan.Skip(path, ImportSkipCause.UnsupportedLanguage, code);
        else if (match == plan.Context.Current.Language) plan.Skip(path, ImportSkipCause.AlreadyConfigured, match);
        else plan.Add(ImportItemKind.Language, new SetLanguage(match), [path]);
    }

    /// <summary>Roo's codebase index: its embedding model, when it is served by a server the user runs.</summary>
    private static void Index(ImportPlanBuilder plan, string path, JsonObject? config, Backend backend)
    {
        if (config is null || config.Count == 0) { plan.Skip(path, ImportSkipCause.Empty); return; }
        if (Bool(config["codebaseIndexEnabled"]) != true) { plan.Skip(path, ImportSkipCause.IndexingNotKept, "off"); return; }
        var provider = Text(config["codebaseIndexEmbedderProvider"]);
        var server = provider switch
        {
            "ollama" => ImportServers.FromContinue("ollama", Text(config["codebaseIndexEmbedderBaseUrl"])),
            "openai-compatible" => ImportServers.FromContinue("openai", Text(config["codebaseIndexOpenAiCompatibleBaseUrl"])),
            _ => null,
        };
        var model = Text(config["codebaseIndexEmbedderModelId"]);
        if (server is null || model is null) { plan.Skip(path, ImportSkipCause.IndexingNotKept, provider); return; }

        // No chat model of the user's own: the embedder's server is the one kept.
        if (backend.Server is null) backend.Server = server;
        if (!server.SameAs(backend.Server.Provider, backend.Server.BaseUrl))
        {
            plan.Skip(path, ImportSkipCause.OtherServer, server.BaseUrl);
            return;
        }
        backend.Paths.Add(path);
        var current = plan.Context.Current;
        if (model == current.RagEmbeddingModel) plan.Skip(path, ImportSkipCause.AlreadyConfigured, model);
        else plan.Add(ImportItemKind.EmbeddingModel, new SetModel(ImportItemKind.EmbeddingModel, model), [path]);
        if (!current.RagEnabled) plan.Add(ImportItemKind.Indexing, new SetIndexing(true), [path]);
    }
}
