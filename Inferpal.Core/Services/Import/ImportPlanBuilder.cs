using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Mcp;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Import;

/// <summary>What an import reads besides its source: the settings in place, and where things land.</summary>
/// <param name="Current">The settings as they are: what is already there is said, not written again.</param>
/// <param name="Home">The user's home folder (Continue's <c>.env</c>, the default files).</param>
/// <param name="CommandsFolder">The machine's prompts folder, where imported commands are written.</param>
/// <param name="EnvironmentVariable">The process environment, injectable for tests.</param>
internal sealed record ImportContext(InferpalConfig Current, string Home, string CommandsFolder,
                                     Func<string, string?>? EnvironmentVariable = null)
{
    /// <summary>The context of an editor: this user's home, the commands beside these settings.</summary>
    public static ImportContext For(InferpalConfig config) =>
        new(config, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), config.MachinePromptsFolder);
}

/// <summary>A source the import could not read, and why — the message names the file.</summary>
internal sealed class ImportReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Builds an <see cref="ImportPlan"/> for one source: the items to write and the parts skipped, every one with its path.
/// Shared by the Continue and Roo readers, so both name a command, compare with what is in place and order the preview
/// the same way.
/// </summary>
internal sealed class ImportPlanBuilder
{
    private readonly List<ImportItem> _items = [];
    private readonly List<ImportSkip> _skips = [];
    private readonly HashSet<string> _commandNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _existingCommandFiles = new(StringComparer.Ordinal);

    public ImportPlanBuilder(ImportSource source, ImportContext context)
    {
        Source = source;
        Context = context;
        foreach (var t in SlashCommandRouter.ParseUserTemplates(context.Current.PromptTemplates))
            _commandNames.Add(t.Name.TrimStart('/'));
        if (Directory.Exists(context.CommandsFolder))
            foreach (var t in PromptFilesService.LoadUncached(context.CommandsFolder))
            {
                _commandNames.Add(t.Name.TrimStart('/'));
                _existingCommandFiles[t.Name.TrimStart('/')] = t.Text;
            }
    }

    public ImportSource Source { get; }
    public ImportContext Context { get; }

    public void Skip(string path, ImportSkipCause cause, string? detail = null) => _skips.Add(new(path, cause, detail));

    /// <summary>A key skipped with everything it holds: a list's elements are named too, each one being a path.</summary>
    public void SkipValue(string path, JsonNode? value, ImportSkipCause cause, string? detail = null)
    {
        Skip(path, cause, detail);
        if (value is JsonArray list)
            for (var i = 0; i < list.Count; i++) Skip($"{path}[{i}]", cause, detail);
    }

    /// <summary>Every key of <paramref name="node"/>, under <paramref name="path"/>, skipped for one cause.</summary>
    public void SkipAll(string path, JsonObject node, ImportSkipCause cause, string? detail = null)
    {
        if (node.Count == 0) Skip(path, cause, detail);
        foreach (var (key, _) in node) Skip(path + "." + key, cause, detail);
    }

    public ImportItem Add(ImportItemKind kind, ImportEffect effect, IEnumerable<string> paths, bool isChecked = true,
                          params ImportItemNote[] notes)
    {
        var sources = paths.Distinct(StringComparer.Ordinal).ToList();
        // ⚠ Unique: an application ticks ids, and two items under one id (a mode's several commands, from one key) were
        // both written when one was ticked. Stable across two readings — the order of reading is the file's.
        var id = kind + ":" + (sources.FirstOrDefault() ?? "");
        for (var n = 2; _items.Any(i => i.Id == id); n++)
            id = kind + ":" + (sources.FirstOrDefault() ?? "") + "#" + n.ToString(CultureInfo.InvariantCulture);
        var item = new ImportItem(id, kind, effect, sources, isChecked, notes);
        _items.Add(item);
        return item;
    }

    /// <summary>
    /// The command a source's <paramref name="raw"/> name becomes: its own name when free, else suffixed with the tool
    /// (<c>/review-continue</c>) — a built-in always answers first, so a command under its name would never run.
    /// <c>null</c> when the very same command was imported before (same name, same text).
    /// </summary>
    public (string Name, ImportItemNote? Note)? Command(string raw, string text)
    {
        var name = PromptFilesService.CommandName(raw.Trim().TrimStart('/'));
        name = Regex.Replace(name, @"[^\p{L}\p{N}:.\-]", "-", RegexOptions.None, MatchTimeout).Trim('-');
        if (name.Length == 0) name = "command";
        if (_existingCommandFiles.TryGetValue(name, out var existing) && existing.Trim() == text.Trim()) return null;

        var chosen = name;
        if (SlashCommandRouter.IsBuiltIn("/" + chosen) || _commandNames.Contains(chosen))
        {
            var suffix = Source == ImportSource.Continue ? "continue" : "roo";
            chosen = name + "-" + suffix;
            for (var n = 2; SlashCommandRouter.IsBuiltIn("/" + chosen) || _commandNames.Contains(chosen); n++)
                chosen = name + "-" + suffix + "-" + n.ToString(CultureInfo.InvariantCulture);
        }
        _commandNames.Add(chosen);
        return (chosen, chosen == name ? null : new ImportItemNote(ImportNote.Renamed, "/" + name));
    }

    /// <summary>
    /// The value of a Continue secret reference (<c>${{ secrets.NAME }}</c>), looked for where Continue looks on a
    /// machine — <c>~/.continue/.env</c>, then the environment; a literal value is itself.
    /// </summary>
    /// <returns>The value, or <c>null</c> with <paramref name="secret"/> naming what was not found.</returns>
    public string? Secret(string value, out string? secret)
    {
        secret = null;
        var m = SecretReference.Match(value);
        if (!m.Success) return value;
        var name = m.Groups[1].Value;
        if (EnvFile(Path.Combine(Context.Home, ".continue", ".env")).TryGetValue(name, out var found)) return found;
        if ((Context.EnvironmentVariable ?? Environment.GetEnvironmentVariable)(name) is { Length: > 0 } env) return env;
        secret = name;
        return null;
    }

    // The names and values matched come from a file someone else wrote.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex SecretReference =
        new(@"^\s*\$\{\{\s*secrets\.([A-Za-z0-9_\-]+)\s*\}\}\s*$", RegexOptions.None, MatchTimeout);

    private Dictionary<string, string>? _env;

    private Dictionary<string, string> EnvFile(string path)
    {
        if (_env is not null) return _env;
        _env = new(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return _env;
            foreach (var raw in TextFileEncoding.ReadText(path).Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0]) value = value[1..^1];
                _env[line[..eq].Trim()] = value;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("ImportPlanBuilder.EnvFile", ex);
        }
        return _env;
    }

    /// <summary>The plan: items in the order of the preview (server, models, instructions, commands…), skips as read.</summary>
    public ImportPlan Build(string file) =>
        new(Source, file, [.. _items.Select((item, i) => (item, i)).OrderBy(p => p.item.Kind).ThenBy(p => p.i).Select(p => p.item)],
            _skips);

    // ── Reading values ──────────────────────────────────────────────────────

    /// <summary>The file as a JSON tree: YAML by its ending, else JSON with comments and trailing commas.</summary>
    public static JsonObject ReadTree(string file)
    {
        string text;
        try { text = TextFileEncoding.ReadText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ImportReadException(ex.Message, ex);
        }
        try
        {
            var root = IsYaml(file)
                ? MiniYaml.Parse(text)
                : JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                  {
                      CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
                  });
            return root as JsonObject ?? throw new ImportReadException("the file holds no settings (its top level is not a map)");
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ImportReadException(ex.Message, ex);
        }
    }

    public static bool IsYaml(string file) =>
        file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase);

    /// <summary>A scalar as text (MiniYaml keeps numbers as text; JSON does not); <c>null</c> when absent or empty.</summary>
    public static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s) ? null : s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    public static int? Int(JsonNode? node) =>
        int.TryParse(Text(node), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    public static bool? Bool(JsonNode? node) => Text(node) switch { "true" => true, "false" => false, _ => null };

    public static IReadOnlyList<string> Strings(JsonNode? node) => node switch
    {
        JsonArray a => [.. a.Select(Text).OfType<string>()],
        JsonValue => Text(node) is { } s ? [s] : [],
        _ => [],
    };
}
