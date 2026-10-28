using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Mcp;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Governance;

/// <summary>Where a mode was found.</summary>
internal enum ChatModeOrigin
{
    /// <summary><c>.inferpal/modes/*.md</c> in the workspace.</summary>
    Inferpal,

    /// <summary><c>.github/agents/*.agent.md</c>: a custom agent of GitHub Copilot.</summary>
    Copilot,

    /// <summary><c>.roomodes</c>: a custom mode of Roo Code.</summary>
    Roo,

    /// <summary><c>%AppData%\Inferpal\modes\*.md</c>: the user's own, in every workspace.</summary>
    User,
}

/// <summary>
/// A mode the person picks for a conversation: instructions, a model (or a list, the first installed wins), the tools it
/// keeps and a reasoning effort. It runs on the Chat loop; Agent and Plan stay Inferpal's own modes.
/// </summary>
/// <param name="Name">What the picker shows, and what the conversation keeps.</param>
/// <param name="Description">One line under the name.</param>
/// <param name="Instructions">What goes to the system prompt, its last layer (<see cref="SystemPromptBuilder"/>).</param>
/// <param name="Models">The models the file asks for, in order of preference; empty = the chat's.</param>
/// <param name="Tools">The tools it keeps; <c>null</c> = every tool the conversation has.</param>
/// <param name="Effort">The reasoning effort it asks for; <c>null</c> = the chat's setting.</param>
/// <param name="Source">The file it was read from: relative to the workspace with <c>/</c>, or a full path for the user's.</param>
/// <param name="Origin">Which product's format it was written in.</param>
/// <param name="NotCarried">What the file sets that Inferpal does not carry — keys and tools, as written there.</param>
internal sealed record ChatMode(
    string Name, string Description, string Instructions, IReadOnlyList<string> Models, ModeTools? Tools,
    ReasoningEffort? Effort, string Source, ChatModeOrigin Origin, IReadOnlyList<string> NotCarried)
{
    /// <summary>The first model of <see cref="Models"/> the server has, by its name there; <c>null</c> when none is (the
    /// chat's model answers, and that is said) or the mode asks for none. Nothing is ever downloaded for a mode.</summary>
    public string? PickModel(IEnumerable<string> installed)
    {
        var have = installed as IReadOnlyCollection<string> ?? [.. installed];
        foreach (var wanted in Models)
            if (have.FirstOrDefault(m => ModelCatalog.SameModelName(m, wanted)) is { } found) return found;
        return null;
    }
}

/// <summary>Why a file that could hold a mode gave none, or gave one that was set aside.</summary>
internal enum ChatModeNoteKind
{
    /// <summary>The file could not be read or parsed: <see cref="ChatModeNote.Detail"/> says why.</summary>
    Unreadable,

    /// <summary>A mode with the name of one found before it: the first is kept.</summary>
    Duplicate,

    /// <summary>A mode named like one of Inferpal's own (Chat, Agent, Plan, Default).</summary>
    ReservedName,

    /// <summary>A Copilot agent its file hides from the picker (<c>user-invocable: false</c>): a sub-agent only.</summary>
    SubAgentOnly,

    /// <summary>A Roo mode without a usable <c>slug</c>.</summary>
    NoSlug,

    /// <summary>A file or folder reached through a link that leaves the repository: not read.</summary>
    LeavesTheRepository,

    /// <summary>A folder the process cannot list: the modes in it are unknown.</summary>
    Unlistable,

    /// <summary>A <c>.roomodes</c> without a <c>customModes</c> list, or an entry of it that is no mode.</summary>
    NoModes,

    /// <summary>A Roo mode's own rules past <see cref="ChatModes.MaxFileBytes"/> in all: the rest is not read.</summary>
    RulesCut,
}

/// <summary>A file, or a mode in it, that gave no mode — and why.</summary>
internal sealed record ChatModeNote(string Source, ChatModeNoteKind Kind, string? Detail = null);

/// <summary>The modes found, and what was set aside.</summary>
internal sealed record ChatModeCatalog(IReadOnlyList<ChatMode> Modes, IReadOnlyList<ChatModeNote> Notes)
{
    public static readonly ChatModeCatalog Empty = new([], []);

    /// <summary>The mode by its name, case aside.</summary>
    public ChatMode? Find(string? name) =>
        name is null ? null : Modes.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the modes of a workspace and of the user: <c>.inferpal/modes/*.md</c> and <c>.github/agents/*.agent.md</c> in
/// Copilot's format (front matter, body = instructions), <c>.roomodes</c> in Roo's, and <c>%AppData%\Inferpal\modes</c>.
/// </summary>
/// <remarks>
/// ⚠ Every one of these files is written by the repository (or the user) and becomes the system prompt: writing one
/// forces the approval card (<c>AgentInstructionFiles</c>), and none of them can grant anything — a mode narrows tools.
/// </remarks>
internal static class ChatModes
{
    /// <summary>The names of Inferpal's own modes, never taken by a file (the picker and ACP would show two).</summary>
    internal static readonly IReadOnlySet<string> ReservedNames =
        new HashSet<string>(["Chat", "Agent", "Plan", "Default"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A mode file larger than this is not read (said): instructions are not a document.</summary>
    internal const int MaxFileBytes = 256 * 1024;

    private const string InferpalDir = ".inferpal/modes";

    /// <summary>The person's own modes, in every workspace: <c>%AppData%\Inferpal\modes</c> (where <c>/import roo</c> puts a
    /// Roo user's global modes).</summary>
    internal static string UserDir => Path.Combine(UserFolders.Of(Environment.SpecialFolder.ApplicationData), "Inferpal", "modes");

    /// <summary>The modes of this workspace and of the person, with the repository formats the settings read, read now —
    /// a mode file edited between two questions is the one the next question uses.</summary>
    public static ChatModeCatalog For(InferpalConfig config, string? root) =>
        Discover(root, UserDir, RepoInstructionFormats.Families(config.RepoInstructionFamilies).On);

    /// <summary>The registry a turn in <paramref name="mode"/> offers: the mode's tools only, or <paramref name="inner"/>
    /// itself when no mode is picked or the mode keeps every tool.</summary>
    public static IToolRegistry ToolsFor(IToolRegistry inner, ChatMode? mode, string? root) =>
        mode?.Tools is null ? inner : new ModeToolRegistry(inner, mode, root);

    /// <summary>
    /// The model a turn in <paramref name="mode"/> asks: the first of its models the server lists for chat, and when it
    /// names some and the server has none of them, <c>null</c> with the sentence that says so (<paramref name="answering"/>
    /// is the model that answers instead). Nothing is ever downloaded for a mode.
    /// </summary>
    /// <remarks>Judged on the CHAT models the server lists: an embedding model named by a mode would end the turn on the
    /// server's refusal. A server that lists nothing (unreachable) says nothing here — the turn says why it fails.</remarks>
    public static async Task<(string? Model, string? Notice)> PickModelAsync(
        IInferenceProvider client, ChatMode mode, string answering, CancellationToken ct)
    {
        if (mode.Models.Count == 0) return (null, null);
        var listed = await client.ListModelsByKindAsync(ct).ConfigureAwait(false);
        if (listed.Listed.Count == 0) return (null, null);
        return mode.PickModel(listed.Chat) is { } model
            ? (model, null)
            : (null, Strings.ModeModelMissing(mode.Name, string.Join(", ", mode.Models), answering));
    }
    private const string CopilotDir  = ".github/agents";
    private const string RooFile     = ".roomodes";

    private static readonly Regex RooSlug = new("^[a-zA-Z0-9-]+$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The front-matter keys a mode carries; every other key of the file is named in <see cref="ChatMode.NotCarried"/>.</summary>
    private static readonly HashSet<string> CarriedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "description", "tools", "groups", "model", "reasoning-effort",
    };

    /// <summary>Whether a path, cut on its separators, ends at a file a mode is read from — this reader's own folders,
    /// so that a write to one asks first (<c>AgentInstructionFiles</c>): <c>.inferpal/modes/*.md</c>,
    /// <c>.github/agents/*.md</c>, <c>.roomodes</c>, <c>.roo/rules-&lt;slug&gt;/…</c>, <c>.roorules-*</c>,
    /// <c>.clinerules-*</c>.</summary>
    /// <remarks>Read case aside: on a file system that folds case, <c>.GitHub/Agents</c> is the same folder.</remarks>
    internal static bool IsModeFile(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return false;
        var last = segments[^1];
        if (last.Equals(RooFile, StringComparison.OrdinalIgnoreCase)
            || last.StartsWith(".roorules-", StringComparison.OrdinalIgnoreCase)
            || last.StartsWith(".clinerules-", StringComparison.OrdinalIgnoreCase))
            return true;
        var markdown = last.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var next = segments[i + 1];
            if (markdown && i == segments.Count - 3 && (Is(segments[i], ".inferpal") && Is(next, "modes")
                                                       || Is(segments[i], ".github") && Is(next, "agents")))
                return true;
            if (Is(segments[i], ".roo") && next.StartsWith("rules-", StringComparison.OrdinalIgnoreCase) && i + 1 < segments.Count - 1)
                return true;
        }
        return false;

        static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The modes of <paramref name="userDir"/>, then of <paramref name="root"/>; a later mode named like an
    /// earlier one is set aside — the person's own first, so that a repository never takes the place of a mode they wrote.
    /// <paramref name="families"/> are the repository formats the user reads (<c>repoInstructionFamilies</c>): Copilot's and
    /// Roo's modes follow their family; <c>null</c> = all.</summary>
    public static ChatModeCatalog Discover(string? root, string? userDir, IReadOnlySet<RepoInstructionFamily>? families = null)
    {
        var modes = new List<ChatMode>();
        var notes = new List<ChatModeNote>();
        if (!string.IsNullOrEmpty(userDir))
            foreach (var file in MarkdownFiles(userDir, userDir, notes))
                AddFile(file, file, ChatModeOrigin.User, confineTo: null);
        if (!string.IsNullOrEmpty(root))
        {
            foreach (var file in MarkdownFiles(Path.Combine(root, InferpalDir), root, notes))
                AddFile(file, Relative(root, file), ChatModeOrigin.Inferpal, root);
            if (families is null || families.Contains(RepoInstructionFamily.Copilot))
                foreach (var file in MarkdownFiles(Path.Combine(root, CopilotDir), root, notes))
                    AddFile(file, Relative(root, file), ChatModeOrigin.Copilot, root);
            var roo = Path.Combine(root, RooFile);
            if ((families is null || families.Contains(RepoInstructionFamily.Roo)) && File.Exists(roo)
                && ReadBounded(roo, RooFile, root, notes) is { } text)
                foreach (var mode in ReadRooModes(root, text, RooFile, notes)) Add(mode);
        }
        return new ChatModeCatalog(modes, notes);

        void AddFile(string file, string source, ChatModeOrigin origin, string? confineTo)
        {
            if (ReadBounded(file, source, confineTo, notes) is { } text
                && ReadAgentFile(text, source, origin, Path.GetFileName(file), notes) is { } mode)
                Add(mode);
        }

        void Add(ChatMode mode)
        {
            if (ReservedNames.Contains(mode.Name))
                notes.Add(new ChatModeNote(mode.Source, ChatModeNoteKind.ReservedName, mode.Name));
            else if (modes.Any(m => m.Name.Equals(mode.Name, StringComparison.OrdinalIgnoreCase)))
                notes.Add(new ChatModeNote(mode.Source, ChatModeNoteKind.Duplicate, mode.Name));
            else
                modes.Add(mode);
        }
    }

    /// <summary>A mode in Copilot's format: front matter (<c>name</c>, <c>description</c>, <c>tools</c>, <c>model</c>,
    /// <c>reasoning-effort</c>, case aside), and the body as its instructions. <c>null</c> when the file is no mode (said
    /// in <paramref name="notes"/>).</summary>
    internal static ChatMode? ReadAgentFile(string text, string source, ChatModeOrigin origin, string fileName, List<ChatModeNote> notes)
    {
        var (header, body) = RulesService.SplitFrontMatter(text);
        JsonObject fm;
        try { fm = header is null ? [] : MiniYaml.Parse(header) as JsonObject ?? []; }
        catch (FormatException ex)
        {
            notes.Add(new ChatModeNote(source, ChatModeNoteKind.Unreadable, ex.Message));
            return null;
        }
        if (Scalar(Key(fm, "user-invocable").Value) is { } invocable && invocable.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(new ChatModeNote(source, ChatModeNoteKind.SubAgentOnly));
            return null;
        }

        var notCarried = new List<string>(fm.Where(p => !CarriedKeys.Contains(p.Key)).Select(p => p.Key));
        var name = CleanName(Scalar(Key(fm, "name").Value)) is { Length: > 0 } n ? n : StemOf(fileName);
        ModeTools? tools = null;
        // ⚠ Read case aside: `Tools:` read as no key at all would leave every tool to a mode written as read-only.
        var groups = Key(fm, "groups");
        if (Key(fm, "tools") is { Present: true } listed)
        {
            tools = ModeTools.FromCopilot(List(listed.Value));
            notCarried.AddRange(tools.NotCarried);
            if (groups.Present) notCarried.Add("groups");   // one list of tools: `tools` is this format's own
        }
        // Roo's groups — what /import roo writes —, read as a .roomodes reads them: the files its edits are limited to
        // come with them.
        else if (groups.Present)
        {
            tools = ModeTools.FromRoo(RooGroups(groups.Value));
            notCarried.AddRange(tools.NotCarried);
        }
        var effort = Effort(Scalar(Key(fm, "reasoning-effort").Value), notCarried);
        return new ChatMode(name, Scalar(Key(fm, "description").Value) ?? string.Empty, body.Trim(), List(Key(fm, "model").Value),
                            tools, effort, source, origin, notCarried);
    }

    /// <summary>The modes of a <c>.roomodes</c> (YAML or JSON, <c>customModes:</c>): <c>roleDefinition</c>, then
    /// <c>customInstructions</c>, then the mode's rules (<c>.roo/rules-&lt;slug&gt;/</c>, else <c>.roorules-&lt;slug&gt;</c>)
    /// as instructions; <c>groups</c> as tools. Roo keeps no model in the file.</summary>
    internal static IReadOnlyList<ChatMode> ReadRooModes(string root, string text, string source, List<ChatModeNote> notes)
    {
        JsonNode? tree;
        try
        {
            tree = text.TrimStart().StartsWith('{')
                ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                : MiniYaml.Parse(text);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            notes.Add(new ChatModeNote(source, ChatModeNoteKind.Unreadable, ex.Message));
            return [];
        }
        if (tree is not JsonObject top || top["customModes"] is not JsonArray list)
        {
            notes.Add(new ChatModeNote(source, ChatModeNoteKind.NoModes));
            return [];
        }

        var modes = new List<ChatMode>();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject mode)
            {
                notes.Add(new ChatModeNote(source, ChatModeNoteKind.NoModes, $"customModes[{i}]"));
                continue;
            }
            if (Scalar(mode["slug"]) is not { } slug || !RegexBudget.IsMatch(RooSlug, slug))
            {
                notes.Add(new ChatModeNote(source, ChatModeNoteKind.NoSlug, Scalar(mode["name"])));
                continue;
            }
            var notCarried = new List<string>(mode.Where(p => p.Key is not ("slug" or "name" or "description" or "roleDefinition"
                                                                                or "customInstructions" or "groups" or "source"))
                                                  .Select(p => p.Key));
            var tools = ModeTools.FromRoo(RooGroups(mode["groups"]));
            notCarried.AddRange(tools.NotCarried);
            var instructions = string.Join("\n\n", new[]
            {
                Scalar(mode["roleDefinition"]), Scalar(mode["customInstructions"]), RooModeRules(root, slug, notes),
            }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
            modes.Add(new ChatMode(CleanName(Scalar(mode["name"])) is { Length: > 0 } name ? name : slug,
                                   Scalar(mode["description"]) ?? string.Empty, instructions, [], tools, null,
                                   $"{source} ({slug})", ChatModeOrigin.Roo, notCarried));
        }
        return modes;
    }

    /// <summary>Roo's <c>groups</c> as <see cref="ModeTools.FromRoo"/> takes them: a group's name alone, or
    /// <c>[group, {fileRegex, description}]</c>; anything but a list is no group at all.</summary>
    internal static List<(string Group, string? FileRegex, string? Description, bool Unreadable)> RooGroups(JsonNode? node)
    {
        var groups = new List<(string, string?, string?, bool)>();
        foreach (var g in node as JsonArray ?? [])
        {
            if (Scalar(g) is { } plain) groups.Add((plain, null, null, false));
            else if (g is JsonArray { Count: > 0 } pair && Scalar(pair[0]) is { } group)
            {
                // ["edit", {fileRegex, description}]: options in another shape, or a pattern that is no text, narrow the
                // group to nothing — Roo itself refuses such a mode — never to all.
                var options = pair.Count > 1 ? pair[1] : null;
                var regex   = (options as JsonObject)?["fileRegex"];
                var unread  = options is not null && (options is not JsonObject || regex is not null && Scalar(regex) is null);
                groups.Add((group, Scalar(regex), Scalar((options as JsonObject)?["description"]), unread));
            }
        }
        return groups;
    }

    /// <summary>A mode file of this reader's format for a Roo mode: its name, description, model, Roo's own
    /// <c>groups</c> and its instructions — read back as the same mode (<see cref="ReadAgentFile"/>). What
    /// <c>/import roo</c> writes.</summary>
    /// <remarks>⚠ A group's options in a shape <see cref="RooGroups"/> cannot read as a pattern are written as one text,
    /// which it reads as unreadable again: narrowed to nothing, as Roo's own, never turned into a pattern that lets edits
    /// through.</remarks>
    internal static string RooModeFile(string name, string? description, string? model, JsonArray? groups, string instructions)
    {
        var sb = new StringBuilder("---\n");
        sb.Append("name: ").Append(Quoted(OneLine(name))).Append('\n');
        if (!string.IsNullOrWhiteSpace(description)) sb.Append("description: ").Append(Quoted(OneLine(description))).Append('\n');
        if (!string.IsNullOrWhiteSpace(model)) sb.Append("model: ").Append(Quoted(model.Trim())).Append('\n');
        // No group: no tool at all, as a .roomodes mode without groups — never "every tool", which a missing key means here.
        sb.Append(groups is { Count: > 0 } ? "groups:\n" : "groups: []\n");
        foreach (var g in groups ?? [])
        {
            if (Scalar(g) is { } plain) sb.Append("  - ").Append(Quoted(plain)).Append('\n');
            else if (g is JsonArray { Count: > 0 } pair && Scalar(pair[0]) is { } group)
            {
                sb.Append("  - - ").Append(Quoted(group)).Append('\n');
                if (pair.Count < 2 || pair[1] is null) continue;
                // Readable as the reader reads them (RooGroups): an object whose fileRegex, if any, is text. Its other keys
                // are not read there, so they are not written here.
                var options = pair[1] as JsonObject;
                if (options is not null && (options["fileRegex"] is null || Scalar(options["fileRegex"]) is not null))
                {
                    var first = true;
                    foreach (var key in new[] { "fileRegex", "description" })
                    {
                        if (Scalar(options[key]) is not { } value) continue;
                        sb.Append(first ? "    - " : "      ").Append(key).Append(": ").Append(Quoted(value)).Append('\n');
                        first = false;
                    }
                    if (first) sb.Append("    - {}\n");
                }
                else sb.Append("    - ").Append(Quoted(pair[1]!.ToJsonString())).Append('\n');
            }
        }
        sb.Append("---\n\n").Append(instructions.Trim()).Append('\n');
        return sb.ToString();

        static string OneLine(string text) => string.Join(' ', text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A value as YAML reads it back unchanged: single-quoted, or double-quoted with its escapes when it holds a
    /// control character (a line break would end the line).</summary>
    /// <remarks>⚠ A double quote there is <c>\x22</c>, never <c>\"</c>: the reader finds a comment by its quotes without
    /// reading escapes, so <c>\"</c> would close the value and a later <c> #</c> cut the line.</remarks>
    private static string Quoted(string value) =>
        value.Any(char.IsControl)
            ? "\"" + string.Concat(value.Select(c => c switch
              {
                  '\\' => "\\\\", '"' => "\\x22", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                  _ when char.IsControl(c) => "\\x" + ((int)c).ToString("x2", System.Globalization.CultureInfo.InvariantCulture),
                  _ => c.ToString(),
              })) + "\""
            : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>A Roo mode's own rules: the files of <c>.roo/rules-&lt;slug&gt;/</c> and its sub-folders (links not
    /// followed), in case-insensitive order of their paths, <see cref="MaxFileBytes"/> in all; without them,
    /// <c>.roorules-&lt;slug&gt;</c>, then the older <c>.clinerules-&lt;slug&gt;</c>.</summary>
    private static string? RooModeRules(string root, string slug, List<ChatModeNote> notes)
    {
        var folder = Path.Combine(root, ".roo", "rules-" + slug);
        if (Directory.Exists(folder))
        {
            var texts = new List<string>();
            var total = 0;
            foreach (var file in WorkspaceScan.EnumerateAll(folder, "*")
                                              .Where(f => !Path.GetFileName(f).StartsWith('.'))
                                              .OrderBy(f => Path.GetRelativePath(folder, f), StringComparer.OrdinalIgnoreCase))
            {
                if (ReadBounded(file, Relative(root, file), root, notes) is not { } text || string.IsNullOrWhiteSpace(text)) continue;
                if (total + text.Length > MaxFileBytes)
                {
                    notes.Add(new ChatModeNote(Relative(root, folder), ChatModeNoteKind.RulesCut, Relative(root, file)));
                    break;
                }
                total += text.Length;
                texts.Add(text.Trim());
            }
            if (texts.Count > 0) return string.Join("\n\n", texts);
        }
        foreach (var name in new[] { ".roorules-" + slug, ".clinerules-" + slug })
        {
            var file = Path.Combine(root, name);
            if (File.Exists(file)) return ReadBounded(file, name, root, notes);
        }
        return null;
    }

    /// <summary>Copilot's <c>reasoning-effort</c> (<c>low</c> to <c>max</c>) and <c>off</c>; what has no level here is
    /// carried as the nearest and named.</summary>
    private static ReasoningEffort? Effort(string? value, List<string> notCarried)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "":  return null;
            case "off" or "none": return ReasoningEffort.Off;
            case "low":       return ReasoningEffort.Low;
            case "medium":    return ReasoningEffort.Medium;
            case "high":      return ReasoningEffort.High;
            case "xhigh" or "max":
                notCarried.Add("reasoning-effort: " + value.Trim() + " (high)");
                return ReasoningEffort.High;
            default:
                notCarried.Add("reasoning-effort: " + value.Trim());
                return null;
        }
    }

    /// <summary>A key of the front matter, case aside — whether it is written, and its value.</summary>
    private static (bool Present, JsonNode? Value) Key(JsonObject fm, string key)
    {
        foreach (var (k, v) in fm)
            if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) return (true, v);
        return (false, null);
    }

    /// <summary>A name as the picker shows it: no control or invisible format characters, no surrounding spaces — so that
    /// <c>"Agent "</c> or <c>"Agent\u200B"</c> is Inferpal's own name, refused.</summary>
    internal static string? CleanName(string? name) =>
        name is null ? null
        : new string(name.Where(c => !char.IsControl(c)
                                     && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format)
                         .ToArray()).Trim();

    /// <summary>A scalar value as text; <c>null</c> for a list, a map or nothing.</summary>
    private static string? Scalar(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    /// <summary>A list written as a YAML list or as one string with commas.</summary>
    private static IReadOnlyList<string> List(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())],
        JsonValue => [.. (Scalar(node) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                         .Select(s => s.Trim('\'', '"'))],
        _ => [],
    };

    /// <summary>The <c>*.md</c> files of a folder (not its sub-folders), in ordinal order; none when it does not exist,
    /// and a folder that cannot be listed is said.</summary>
    private static IEnumerable<string> MarkdownFiles(string folder, string root, List<ChatModeNote> notes)
    {
        if (!Directory.Exists(folder)) return [];
        try { return Directory.GetFiles(folder, "*.md").OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add(new ChatModeNote(Relative(root, folder), ChatModeNoteKind.Unlistable, ex.Message));
            return [];
        }
    }

    /// <summary>The text of a mode's file, or <c>null</c> with the reason in <paramref name="notes"/>: over
    /// <see cref="MaxFileBytes"/>, unreadable, or — under <paramref name="confineTo"/> — reached through a link that leaves
    /// the repository (a cloned link to <c>~/.ssh</c> would send its keys to the model as instructions).</summary>
    private static string? ReadBounded(string path, string source, string? confineTo, List<ChatModeNote> notes)
    {
        try
        {
            if (confineTo is not null)
            {
                try { PathSanitizer.AssertUnderRoot(path, confineTo); }
                catch (ArgumentException)
                {
                    notes.Add(new ChatModeNote(source, ChatModeNoteKind.LeavesTheRepository));
                    return null;
                }
            }
            var size = new FileInfo(path).Length;
            if (size > MaxFileBytes)
            {
                notes.Add(new ChatModeNote(source, ChatModeNoteKind.Unreadable, $"{size} bytes, over {MaxFileBytes}"));
                return null;
            }
            return TextFileEncoding.ReadText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add(new ChatModeNote(source, ChatModeNoteKind.Unreadable, ex.Message));
            return null;
        }
    }

    private static string StemOf(string fileName) =>
        fileName.EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase) ? fileName[..^".agent.md".Length]
        : Path.GetFileNameWithoutExtension(fileName);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
