using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Inferpal.Services.Docs;
using Inferpal.Services.Inference;
using Inferpal.Services.Mcp;
using static Inferpal.Services.Import.ImportPlanBuilder;

namespace Inferpal.Services.Import;

/// <summary>
/// Reads a Continue configuration — <c>~/.continue/config.yaml</c>, or the older <c>config.json</c> — into an
/// <see cref="ImportPlan"/>: every key of the file is either an item to write or a skip with its cause.
/// </summary>
/// <remarks>
/// <para>The reading is tolerant the way users wrote these files: a <c>config.json</c> key written in the YAML
/// (<c>systemMessage</c>, a model's <c>contextLength</c>, <c>customCommands</c>) is read as it was meant, and the preview
/// says Continue itself ignored it there.</para>
/// <para>⚠ Inferpal has ONE server. The one kept is the server of the first chat model a user runs; a model served
/// elsewhere is named, never silently dropped.</para>
/// </remarks>
internal static class ContinueImport
{
    /// <summary>Where Continue keeps its configuration, newest format first.</summary>
    public static IReadOnlyList<string> DefaultFiles(string home) =>
    [
        Path.Combine(home, ".continue", "config.yaml"),
        Path.Combine(home, ".continue", "config.yml"),
        Path.Combine(home, ".continue", "config.json"),
    ];

    /// <summary>A model as Continue declares it, wherever in the file.</summary>
    private sealed record Model(string Path, JsonObject Node, string Provider, string? ApiBase, string? ApiKey,
                                string? Name, IReadOnlyList<string> Roles);

    private static readonly IReadOnlyList<string> DefaultYamlRoles = ["chat", "edit", "apply", "summarize"];

    public static ImportPlan Read(string file, ImportContext context)
    {
        var root = ReadTree(file);
        var yaml = IsYaml(file);
        var plan = new ImportPlanBuilder(ImportSource.Continue, context);
        var models = new List<Model>();

        foreach (var (key, value) in root)
        {
            switch (key)
            {
                case "name" or "version" or "schema" or "metadata":
                    plan.SkipValue(key, value, ImportSkipCause.FileIdentity);
                    break;
                case "models":
                    Each(plan, key, value, (path, node) => models.Add(ModelOf(path, node, yaml ? null : ["chat"])));
                    break;
                case "tabAutocompleteModel":
                    Each(plan, key, value, (path, node) => models.Add(ModelOf(path, node, ["autocomplete"])), single: true);
                    break;
                case "embeddingsProvider":
                    Each(plan, key, value, (path, node) => models.Add(ModelOf(path, node, ["embed"])), single: true);
                    break;
                case "reranker":
                    plan.SkipValue(key, value, ImportSkipCause.RoleNotInInferpal, "rerank");
                    break;
                case "rules":
                    Each(plan, key, value, (path, node) => Rule(plan, path, node));
                    break;
                case "prompts" or "customCommands":
                    Each(plan, key, value, (path, node) => Prompt(plan, path, node, yaml && key == "customCommands"));
                    break;
                case "slashCommands":
                    Each(plan, key, value, (path, node) =>
                        plan.Skip(path, ImportSkipCause.ContinueBuiltinCommand, Text(node["name"]) is { } n ? "/" + n : null));
                    break;
                case "context" or "contextProviders":
                    Each(plan, key, value, (path, node) =>
                        plan.Skip(path, ImportSkipCause.ContextProviders, Text(node["provider"]) ?? Text(node["name"])));
                    break;
                case "docs":
                    Each(plan, key, value, (path, node) => Doc(plan, path, node));
                    break;
                case "mcpServers":
                    Each(plan, key, value, (path, node) => McpServer(plan, path, node));
                    break;
                case "experimental":
                    Experimental(plan, key, value);
                    break;
                case "systemMessage":
                    if (Text(value) is { } system)
                        plan.Add(ImportItemKind.Instructions, new AddInstructions("Continue system message", system), [key]);
                    else plan.SkipValue(key, value, ImportSkipCause.Empty);
                    break;
                case "completionOptions" or "temperature" or "topP" or "topK":
                    plan.SkipValue(key, value, ImportSkipCause.Sampling);
                    break;
                case "requestOptions":
                    plan.SkipValue(key, value, ImportSkipCause.RequestOptions);
                    break;
                case "tabAutocompleteOptions":
                    plan.SkipValue(key, value, ImportSkipCause.AutocompleteOptions);
                    break;
                case "data" or "allowAnonymousTelemetry":
                    plan.SkipValue(key, value, ImportSkipCause.Telemetry);
                    break;
                case "disableIndexing":
                    Indexing(plan, key, value);
                    break;
                case "env" or "disableSummaries" or "disableSessionTitles" or "ui" or "userToken" or "keepAlive":
                    plan.SkipValue(key, value, ImportSkipCause.NoEquivalent, key);
                    break;
                default:
                    plan.SkipValue(key, value, ImportSkipCause.UnknownKey, key);
                    break;
            }
        }
        Models(plan, models, yaml);
        return plan.Build(file);
    }

    /// <summary>Every element of a list (or entry of a map, keyed by name) under <paramref name="key"/>.</summary>
    /// <param name="single">The value may be one object instead of a list (<c>tabAutocompleteModel</c>).</param>
    private static void Each(ImportPlanBuilder plan, string key, JsonNode? value, Action<string, JsonObject> read,
                             bool single = false)
    {
        switch (value)
        {
            case JsonArray list when list.Count > 0:
                for (var i = 0; i < list.Count; i++)
                {
                    var path = $"{key}[{i}]";
                    if (list[i] is JsonObject o) read(path, o);
                    else if (Text(list[i]) is { } s) read(path, new JsonObject { ["rule"] = s, ["name"] = null });
                    else plan.Skip(path, ImportSkipCause.Empty);
                }
                break;
            case JsonObject one when single:
                read(key, one);
                break;
            case JsonObject map when map.Count > 0:
                foreach (var (name, node) in map)
                    if (node is JsonObject o)
                    {
                        if (!o.ContainsKey("name")) o = (JsonObject)o.DeepClone();
                        o["name"] ??= name;
                        read($"{key}.{name}", o);
                    }
                    else plan.Skip($"{key}.{name}", ImportSkipCause.Empty);
                break;
            default:
                plan.SkipValue(key, value, ImportSkipCause.Empty);
                break;
        }
    }

    private static Model ModelOf(string path, JsonObject node, IReadOnlyList<string>? roles) =>
        new(path, node, Text(node["provider"]) ?? string.Empty, Text(node["apiBase"]), Text(node["apiKey"]),
            Text(node["model"]),
            roles ?? (node["roles"] is { } r ? Strings(r) : DefaultYamlRoles));

    // ── Models ──────────────────────────────────────────────────────────────

    private static void Models(ImportPlanBuilder plan, List<Model> models, bool yaml)
    {
        var current = plan.Context.Current;
        var backend = models.Where(m => m.Roles.Contains("chat") && !m.Node.ContainsKey("uses"))
                          .Select(m => ImportServers.FromContinue(m.Provider, m.ApiBase)).FirstOrDefault(s => s is not null)
                   ?? models.Where(m => !m.Node.ContainsKey("uses"))
                            .Select(m => ImportServers.FromContinue(m.Provider, m.ApiBase)).FirstOrDefault(s => s is not null);

        var backendPaths = new List<string>();
        string? backendKey = null;
        string? chatModel = null;
        bool chatTaken = false, editTaken = false, completionTaken = false, utilityTaken = false, embedTaken = false;
        int? window = null;
        var windowPaths = new List<string>();
        var windowNotes = new List<ImportItemNote>();

        foreach (var m in models)
        {
            if (m.Node.ContainsKey("uses")) { plan.SkipAll(m.Path, m.Node, ImportSkipCause.HubBlock); continue; }
            var server = ImportServers.FromContinue(m.Provider, m.ApiBase);
            if (server is null)
            {
                // transformers.js is the embedding model Continue runs inside itself: no server to point at.
                if (m.Provider.Length == 0) plan.SkipAll(m.Path, m.Node, ImportSkipCause.Empty);
                else if (m.Provider == "transformers.js") plan.SkipAll(m.Path, m.Node, ImportSkipCause.NoEquivalent, m.Provider);
                else plan.SkipAll(m.Path, m.Node, ImportSkipCause.HostedProvider, m.Provider);
                continue;
            }
            if (!server.SameAs(backend!.Provider, backend.BaseUrl))
            {
                plan.SkipAll(m.Path, m.Node, ImportSkipCause.OtherServer, server.BaseUrl);
                continue;
            }

            var modelPaths = new[] { "model", "roles" }.Where(m.Node.ContainsKey).Select(k => m.Path + "." + k).ToList();
            // Retained = it took a role (Continue gives each role to the FIRST model that lists it), whether or not that
            // writes anything; a model that took none is named once, by the reason it took none.
            var retained = false;
            var produced = false;
            ImportSkipCause? slotCause = null;
            string? slotDetail = null;
            var notCarried = new List<(ImportSkipCause Cause, string? Detail)>();
            void Because(ImportSkipCause c, string? d = null) { if (!notCarried.Contains((c, d))) notCarried.Add((c, d)); }
            void Set(ImportItemKind role, string? configured)
            {
                retained = true;
                if (m.Name is null || m.Name.Equals("AUTODETECT", StringComparison.OrdinalIgnoreCase)) slotCause ??= ImportSkipCause.Autodetect;
                else if (m.Name == configured) { slotCause ??= ImportSkipCause.AlreadyConfigured; slotDetail ??= m.Name; }
                else { plan.Add(role, new SetModel(role, m.Name), modelPaths); produced = true; }
            }

            var isChat = false;
            foreach (var role in m.Roles)
            {
                switch (role)
                {
                    case "chat" when !chatTaken:
                        chatTaken = true;
                        isChat = true;
                        chatModel = m.Name;
                        Set(ImportItemKind.ChatModel, current.DefaultModel);
                        break;
                    case "chat":
                        Because(ImportSkipCause.OtherChatModel);
                        break;
                    case "edit" when !editTaken && m.Name == chatModel:
                        editTaken = true;
                        Because(ImportSkipCause.SameModelAsChat, role);
                        break;
                    case "summarize" when !utilityTaken && m.Name == chatModel:
                        utilityTaken = true;
                        Because(ImportSkipCause.SameModelAsChat, role);
                        break;
                    case "edit" when !editTaken:
                        editTaken = true;
                        Set(ImportItemKind.EditModel, current.InlineEditModel);
                        break;
                    case "summarize" when !utilityTaken:
                        utilityTaken = true;
                        Set(ImportItemKind.UtilityModel, current.UtilityModel);
                        break;
                    case "autocomplete" when !completionTaken:
                        completionTaken = true;
                        Set(ImportItemKind.CompletionModel, current.InlineCompletionModel);
                        break;
                    case "embed" when !embedTaken:
                        embedTaken = true;
                        Set(ImportItemKind.EmbeddingModel, current.RagEmbeddingModel);
                        break;
                    case "edit" or "summarize" or "autocomplete" or "embed":
                        Because(ImportSkipCause.OneModelPerRole, role);
                        break;
                    default:
                        Because(ImportSkipCause.RoleNotInInferpal, role);
                        break;
                }
            }
            if (!retained)
            {
                var (why, detail) = notCarried.Count > 0 ? notCarried[0] : (ImportSkipCause.Empty, null);
                plan.SkipAll(m.Path, m.Node, why, detail);
                continue;
            }

            foreach (var (key, value) in m.Node)
            {
                var path = m.Path + "." + key;
                switch (key)
                {
                    case "model":
                        if (!produced) plan.Skip(path, slotCause ?? ImportSkipCause.Empty, slotDetail);
                        break;
                    case "roles":
                        if (!produced) plan.Skip(path, slotCause ?? ImportSkipCause.Empty, slotDetail);
                        foreach (var (why, detail) in notCarried) plan.Skip(path, why, detail);
                        break;
                    case "provider" or "apiBase":
                        backendPaths.Add(path);
                        break;
                    case "apiKey":
                        if (plan.Secret(Text(value) ?? string.Empty, out var missing) is { } key2)
                        {
                            backendPaths.Add(path);
                            backendKey ??= key2;
                        }
                        else plan.Skip(path, ImportSkipCause.SecretNotFound, missing);
                        break;
                    case "name" or "title":
                        plan.Skip(path, ImportSkipCause.DisplayName);
                        break;
                    case "contextLength":
                        if (isChat && Int(value) is { } n)
                        {
                            window = n;
                            windowPaths.Add(path);
                            if (yaml) windowNotes.Add(new(ImportNote.IgnoredByContinue, key));
                        }
                        else plan.Skip(path, ImportSkipCause.NoEquivalent, key);
                        break;
                    case "defaultCompletionOptions" or "completionOptions":
                        CompletionOptions(plan, path, value as JsonObject, isChat, ref window, windowPaths);
                        break;
                    case "temperature" or "topP" or "topK" or "minP" or "presencePenalty" or "frequencyPenalty" or "stop"
                        or "mirostat" or "numThreads" or "keepAlive":
                        plan.Skip(path, ImportSkipCause.Sampling);
                        break;
                    case "maxTokens":
                        plan.Skip(path, ImportSkipCause.ResponseLength);
                        break;
                    case "capabilities":
                        plan.Skip(path, ImportSkipCause.DeclaredCapabilities);
                        break;
                    case "requestOptions":
                        plan.Skip(path, ImportSkipCause.RequestOptions);
                        break;
                    case "autocompleteOptions":
                        plan.Skip(path, ImportSkipCause.AutocompleteOptions);
                        break;
                    case "promptTemplates":
                        plan.Skip(path, ImportSkipCause.PromptTemplates);
                        break;
                    case "chatOptions" when isChat && Text(value?["baseSystemMessage"]) is { } baseMessage:
                        plan.Add(ImportItemKind.Instructions,
                                 new AddInstructions($"System message of {m.Name ?? "the chat model"} (Continue)", baseMessage), [path]);
                        break;
                    case "systemMessage" when isChat && Text(value) is { } system:
                        plan.Add(ImportItemKind.Instructions,
                                 new AddInstructions($"System message of {m.Name ?? "the chat model"} (Continue)", system), [path],
                                 true, yaml ? [new(ImportNote.IgnoredByContinue, key)] : []);
                        break;
                    default:
                        plan.Skip(path, ImportSkipCause.NoEquivalent, key);
                        break;
                }
            }
        }

        if (backend is not null && backendPaths.Count > 0) plan.AddBackend(backend, backendKey, backendPaths);
        if (window is { } tokens)
        {
            if (tokens == current.ContextWindowSize)
                foreach (var p in windowPaths) plan.Skip(p, ImportSkipCause.AlreadyConfigured, tokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else plan.Add(ImportItemKind.ContextWindow, new SetContextWindow(tokens), windowPaths, true, [.. windowNotes.Distinct()]);
        }
    }

    /// <summary>A model's completion options: the window of the chat model is kept; the rest is sampling.</summary>
    private static void CompletionOptions(ImportPlanBuilder plan, string path, JsonObject? options, bool isChat,
                                          ref int? window, List<string> windowPaths)
    {
        if (options is null || options.Count == 0) { plan.Skip(path, ImportSkipCause.Empty); return; }
        if (isChat && Int(options["contextLength"]) is { } n) { window = n; windowPaths.Add(path); }
        else if (options.ContainsKey("contextLength")) plan.Skip(path, ImportSkipCause.NoEquivalent, "contextLength");
        if (options.ContainsKey("maxTokens")) plan.Skip(path, ImportSkipCause.ResponseLength);
        if (options.Any(o => o.Key is not ("contextLength" or "maxTokens"))) plan.Skip(path, ImportSkipCause.Sampling);
    }

    // ── Rules, prompts, docs, MCP ───────────────────────────────────────────

    private static void Rule(ImportPlanBuilder plan, string path, JsonObject node)
    {
        if (node.ContainsKey("uses")) { plan.Skip(path, ImportSkipCause.HubBlock, Text(node["uses"])); return; }
        if (Text(node["rule"]) is not { } text) { plan.Skip(path, ImportSkipCause.Empty); return; }
        var name = Text(node["name"]);
        var notes = new List<ImportItemNote>();
        var scope = new List<string>();
        if (Strings(node["globs"]) is { Count: > 0 } globs)
            scope.Add("files matching " + string.Join(", ", globs.Select(g => "`" + g + "`")));
        if (Strings(node["regex"]) is { Count: > 0 } regexes)
            scope.Add("files whose content matches " + string.Join(", ", regexes.Select(r => "`" + r + "`")));
        if (scope.Count > 0)
        {
            text = "Applies when working on " + string.Join(", or on ", scope) + ".\n\n" + text;
            notes.Add(new(ImportNote.ScopeInText));
        }
        else if (Bool(node["alwaysApply"]) == false && Text(node["description"]) is { } when)
            text = "Use when relevant: " + when + "\n\n" + text;
        plan.Add(ImportItemKind.Instructions, new AddInstructions($"Continue rule: {name ?? path}", text), [path], true, [.. notes]);
    }

    private static void Prompt(ImportPlanBuilder plan, string path, JsonObject node, bool legacyKeyInYaml)
    {
        if (node.ContainsKey("uses")) { plan.Skip(path, ImportSkipCause.HubBlock, Text(node["uses"])); return; }
        if (Text(node["name"]) is not { } raw || Text(node["prompt"]) is not { } prompt)
        {
            plan.Skip(path, ImportSkipCause.Empty);
            return;
        }
        var text = RepoCommandFiles.ContinueText(prompt.Trim());
        if (plan.Command(raw, text) is not { } command)
        {
            plan.Skip(path, ImportSkipCause.AlreadyConfigured, "/" + raw);
            return;
        }
        var notes = new List<ImportItemNote>();
        if (command.Note is { } renamed) notes.Add(renamed);
        if (legacyKeyInYaml) notes.Add(new(ImportNote.IgnoredByContinue, "customCommands"));
        // The command Continue wrote into every new config.json: offered, unticked — the user never wrote it.
        var isDefault = raw == "test" && prompt.Trim().StartsWith(ContinueDefaultTestPrompt, StringComparison.Ordinal);
        if (isDefault) notes.Add(new(ImportNote.ContinueDefault));
        plan.Add(ImportItemKind.Command, new WriteCommand(command.Name, Text(node["description"]), text), [path], !isDefault,
                 [.. notes]);
    }

    /// <summary>The start of the <c>/test</c> command Continue put in every new <c>config.json</c>.</summary>
    private const string ContinueDefaultTestPrompt =
        "{{{ input }}}\n\nWrite a comprehensive set of unit tests for the selected code. It should setup, run tests that check for correctness including important edge cases, and teardown.";

    /// <summary>
    /// <c>experimental.contextMenuPrompts</c> of <c>config.json</c>: what the editor's context menu sent with the selected
    /// code. Each one is a command here — the code goes after it, typed or pasted.
    /// </summary>
    private static void ContextMenuPrompts(ImportPlanBuilder plan, string path, JsonObject prompts)
    {
        foreach (var (name, value) in prompts)
        {
            if (Text(value) is not { } prompt) { plan.Skip(path, ImportSkipCause.Empty, name); continue; }
            var text = RepoCommandFiles.ContinueText(prompt.Trim());
            if (plan.Command(name, text) is not { } command) { plan.Skip(path, ImportSkipCause.AlreadyConfigured, "/" + name); continue; }
            plan.Add(ImportItemKind.Command, new WriteCommand(command.Name, null, text), [path], true,
                     command.Note is { } renamed ? [renamed] : []);
        }
    }

    private static void Doc(ImportPlanBuilder plan, string path, JsonObject node)
    {
        if (node.ContainsKey("uses")) { plan.Skip(path, ImportSkipCause.HubBlock, Text(node["uses"])); return; }
        var url = Text(node["startUrl"]) ?? Text(node["url"]);
        if (url is null || !DocSite.IsValidHttpUrl(url)) { plan.Skip(path, ImportSkipCause.Empty); return; }
        var title = Text(node["title"]) ?? Text(node["name"]);
        if (DocSite.Parse(plan.Context.Current.DocSitesJson).Any(s => ImportServers.Comparable(s.StartUrl) == ImportServers.Comparable(url)))
            plan.Skip(path, ImportSkipCause.AlreadyConfigured, url);
        else plan.Add(ImportItemKind.DocSite, new AddDocSite(title ?? url, url), [path]);
    }

    private static void McpServer(ImportPlanBuilder plan, string path, JsonObject node)
    {
        if (node.ContainsKey("uses")) { plan.Skip(path, ImportSkipCause.HubBlock, Text(node["uses"])); return; }
        var command = Text(node["command"]);
        var url = Text(node["url"]);
        if (command is null && url is null) { plan.Skip(path, ImportSkipCause.Empty); return; }
        var named = Text(node["name"]);
        var name = named ?? ServerName(command, Strings(node["args"]), url);

        var notes = new List<ImportItemNote>();
        string Resolve(string value)
        {
            if (plan.Secret(value, out var missing) is { } found) return found;
            notes.Add(new(ImportNote.SecretToFill, missing));
            return value;
        }
        var env = node["env"] is JsonObject e
            ? e.Where(p => Text(p.Value) is not null).ToDictionary(p => p.Key, p => Resolve(Text(p.Value)!))
            : new Dictionary<string, string>();
        // Same fields as McpServerConfig.LiftContinueFields, read here because the key is resolved BEFORE "Bearer " is
        // put in front of it (a secret reference resolves only as a whole value).
        var headers = node["requestOptions"]?["headers"] is JsonObject h
            ? h.Where(p => Text(p.Value) is not null).ToDictionary(p => p.Key, p => Resolve(Text(p.Value)!))
            : new Dictionary<string, string>();
        if (url is not null && Text(node["apiKey"]) is { } apiKey) headers["Authorization"] = "Bearer " + Resolve(apiKey);

        var server = new McpServerConfig(name, command, Strings(node["args"]), env, url, headers.Count > 0 ? headers : null,
                                         WorkingDirectory: Text(node["cwd"]),
                                         DeclaresSse: string.Equals(Text(node["type"]), "sse", StringComparison.OrdinalIgnoreCase));
        AddServer(plan, path, node, server, notes, invented: named is null);
    }

    /// <param name="invented">The source left the server unnamed and <see cref="ServerName"/> made the name up: another
    /// server may have it — a server the user configured, one earlier in this file. It is then told apart by what it runs:
    /// the same server is already there; another gets a free name.</param>
    private static void AddServer(ImportPlanBuilder plan, string path, JsonObject node, McpServerConfig server,
                                  List<ImportItemNote> notes, bool invented = false)
    {
        var configured = McpServerConfig.Parse(plan.Context.Current.McpServersJson);
        if (invented)
        {
            if (configured.FirstOrDefault(s => Runs(s, server)) is { } same)
            {
                plan.Skip(path, ImportSkipCause.AlreadyConfigured, same.Name);
                return;
            }
            if (plan.Servers.FirstOrDefault(s => Runs(s, server)) is { } earlier)
            {
                plan.Skip(path, ImportSkipCause.SameNameEarlier, earlier.Name);
                return;
            }
            var name = server.Name;
            for (var n = 2; configured.Any(s => s.Name == name) || plan.Servers.Any(s => s.Name == name); n++)
                name = server.Name + "-" + n.ToString(CultureInfo.InvariantCulture);
            server = server with { Name = name };
        }
        else if (configured.Any(s => s.Name == server.Name))
        {
            plan.Skip(path, ImportSkipCause.AlreadyConfigured, server.Name);
            return;
        }
        else if (plan.Servers.Any(s => s.Name == server.Name))
        {
            plan.Skip(path, ImportSkipCause.SameNameEarlier, server.Name);
            return;
        }
        plan.Servers.Add(server);
        var carried = new List<string>();
        foreach (var (key, _) in node)
        {
            if (key is "name" or "command" or "args" or "env" or "cwd" or "url" or "type" or "apiKey" or "requestOptions" or "transport")
                carried.Add(path + "." + key);
            else plan.Skip(path + "." + key, ImportSkipCause.NoEquivalent, key);
        }
        plan.Add(ImportItemKind.McpServer, new AddMcpServer(server), carried.Count > 0 ? carried : [path], true,
                 [.. notes.Distinct()]);
    }

    /// <summary><c>experimental</c> of <c>config.json</c>: its MCP servers are imported, the rest is named.</summary>
    private static void Experimental(ImportPlanBuilder plan, string key, JsonNode? value)
    {
        if (value is not JsonObject experimental || experimental.Count == 0) { plan.SkipValue(key, value, ImportSkipCause.Empty); return; }
        var others = experimental.Where(p => p.Key is not ("modelContextProtocolServers" or "contextMenuPrompts"))
                                 .Select(p => p.Key).ToList();
        if (others.Count > 0) plan.SkipValue(key, value, ImportSkipCause.NoEquivalent, string.Join(", ", others));
        if (experimental["contextMenuPrompts"] is JsonObject menu)
            ContextMenuPrompts(plan, key + ".contextMenuPrompts", menu);
        if (experimental["modelContextProtocolServers"] is not JsonArray servers) return;
        for (var i = 0; i < servers.Count; i++)
        {
            var path = $"{key}.modelContextProtocolServers[{i}]";
            if (servers[i]?["transport"] is not JsonObject transport) { plan.Skip(path, ImportSkipCause.Empty); continue; }
            var command = Text(transport["command"]);
            var url = Text(transport["url"]);
            if (command is null && url is null) { plan.Skip(path, ImportSkipCause.Empty); continue; }
            var args = Strings(transport["args"]);
            var name = ServerName(command, args, url);
            var env = transport["env"] is JsonObject e
                ? e.Where(p => Text(p.Value) is not null).ToDictionary(p => p.Key, p => Text(p.Value)!)
                : new Dictionary<string, string>();
            AddServer(plan, path, new JsonObject { ["transport"] = null },
                      new McpServerConfig(name, command, args, env, url), [], invented: true);
        }
    }

    /// <summary>The programs that run a package named in their arguments, and the words between them and it.</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase) { "npx", "bunx", "pnpx", "uvx", "pipx", "pnpm", "yarn", "bun" };
    private static readonly HashSet<string> LauncherVerbs = new(StringComparer.OrdinalIgnoreCase) { "run", "dlx", "x", "exec" };

    /// <summary>
    /// A name for a server the source left unnamed: the package a launcher runs (<c>npx -y @scope/server-x@1</c> →
    /// <c>server-x</c>), else its program, else its host.
    /// </summary>
    /// <remarks>⚠ Named after the program alone, every server <c>npx</c> or <c>uvx</c> starts was called <c>npx</c> or
    /// <c>uvx</c>: two in one file, one name — the second refused at apply, "added in the meantime", which was false.</remarks>
    private static string ServerName(string? command, IReadOnlyList<string> args, string? url)
    {
        if (command is null)
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "server";
        var program = Path.GetFileNameWithoutExtension(command);
        if (!Launchers.Contains(program)) return program;
        var spec = args.SkipWhile(a => a.StartsWith('-') || LauncherVerbs.Contains(a)).FirstOrDefault();
        if (spec is null) return program;
        var version = spec.LastIndexOf('@');
        if (version > 0) spec = spec[..version];                  // @scope/name@1.2 → @scope/name
        spec = spec.Split(['=', '<', '>', '[', '~', '!'])[0];      // name==1.2, name[extra] (Python)
        var package = spec[(spec.LastIndexOf('/') + 1)..];
        return package.Length > 0 ? package : program;
    }

    /// <summary>Whether two servers run the same thing: the same program with the same arguments, or the same address.</summary>
    private static bool Runs(McpServerConfig a, McpServerConfig b) =>
        string.Equals(a.Command, b.Command, StringComparison.Ordinal) && a.Args.SequenceEqual(b.Args, StringComparer.Ordinal)
        && string.Equals(a.Url, b.Url, StringComparison.Ordinal);

    private static void Indexing(ImportPlanBuilder plan, string key, JsonNode? value)
    {
        if (Bool(value) is not { } disabled) { plan.SkipValue(key, value, ImportSkipCause.Empty); return; }
        if (!disabled == plan.Context.Current.RagEnabled) plan.SkipValue(key, value, ImportSkipCause.AlreadyConfigured, key);
        else plan.Add(ImportItemKind.Indexing, new SetIndexing(!disabled), [key]);
    }
}
