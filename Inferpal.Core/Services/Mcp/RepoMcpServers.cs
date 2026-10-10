using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Mcp;

/// <summary>A file in which a repository declares MCP servers for some tool.</summary>
/// <param name="Location">Relative to the repository's root, with <c>/</c>: a file, or a folder of files.</param>
/// <param name="Origin">The tool that reads it, as a person knows it.</param>
internal sealed record RepoMcpFormat(string Location, string Origin, bool Folder = false);

/// <summary>A value <c>${input:id}</c> asks for: what VS Code's <c>inputs</c> say of it.</summary>
internal sealed record RepoMcpInput(string Id, string Description, bool Password, string? Default);

/// <summary>A server a repository declares, as written — its variables not yet replaced.</summary>
/// <param name="Source">The file it is declared in, relative to the repository's root, with <c>/</c>.</param>
/// <param name="Definition">Its definition, as written.</param>
/// <param name="Fingerprint">What an agreement to start it is given to: the file and the definition, hashed.</param>
/// <param name="Inputs">The <c>${input:…}</c> values it uses, with how VS Code describes them.</param>
internal sealed record RepoMcpServer(string Name, string Source, string Origin, JsonObject Definition, string Fingerprint,
                                     IReadOnlyList<RepoMcpInput> Inputs)
{
    /// <summary>
    /// What starting it runs, as the agreement question shows it: the command line or the address, then everything else
    /// the definition sets that changes what runs — its environment, its folder, its headers — as written.
    /// </summary>
    /// <remarks>
    /// ⚠ The agreement is given to the whole definition (<see cref="Fingerprint"/>): shown the command alone, a user
    /// agreed to a <c>NODE_OPTIONS</c>, a working folder or a header they never saw — and the question is the boundary.
    /// The labels are the file's own keys, not prose: the question around them is the translated part.
    /// </remarks>
    public string Runs
    {
        get
        {
            var parts = new List<string>();
            if (Text(Definition["command"]) is { } command)
            {
                parts.Add(string.Join(" ", new[] { command }.Concat(Definition["args"] is JsonArray a ? a.Select(x => x?.ToString() ?? "") : [])));
                if (Text(Definition["cwd"]) is { Length: > 0 } cwd) parts.Add($"cwd: {cwd}");
                if (Pairs(Definition["env"], "=") is { Length: > 0 } env) parts.Add($"env: {env}");
            }
            else
            {
                parts.Add(Definition["url"]?.ToString() ?? "");
                if (Pairs(Definition["headers"], ": ") is { Length: > 0 } headers) parts.Add($"headers: {headers}");
            }
            return string.Join(" · ", parts);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Pairs(JsonNode? node, string separator) =>
        node is JsonObject map ? string.Join(", ", map.Select(kv => kv.Key + separator + kv.Value?.ToString())) : string.Empty;
}

/// <summary>A server declaration that could not be read, and why.</summary>
internal sealed record RepoMcpProblem(string Source, string? Name, string Reason);

internal sealed record RepoMcpScan(string? Root, IReadOnlyList<RepoMcpServer> Servers, IReadOnlyList<RepoMcpProblem> Problems)
{
    public static readonly RepoMcpScan None = new(null, [], []);
}

/// <summary>
/// The MCP servers a repository declares for VS Code (<c>.vscode/mcp.json</c>), Claude Code and VS Code's portable format
/// (<c>.mcp.json</c>), Visual Studio (<c>.vs/mcp.json</c>), Cursor (<c>.cursor/mcp.json</c>), Roo (<c>.roo/mcp.json</c>)
/// and Continue (<c>.continue/mcpServers/*.yaml</c>): read as written, their variables replaced only when they start.
/// </summary>
/// <remarks>
/// ⚠ A server of the repository runs a program the repository chose: nothing here starts it. <see cref="McpToolService"/>
/// asks the user first, every time its <see cref="RepoMcpServer.Fingerprint"/> has no agreement — the definition, not the
/// name: a changed command under an agreement given to the old one would run unasked.
/// </remarks>
internal static class RepoMcpServers
{
    internal static readonly IReadOnlyList<RepoMcpFormat> Formats =
    [
        new(".vscode/mcp.json", "VS Code"),
        new(".mcp.json", "Claude Code / VS Code"),
        new(".vs/mcp.json", "Visual Studio"),
        new(".cursor/mcp.json", "Cursor"),
        new(".roo/mcp.json", "Roo Code"),
        new(".continue/mcpServers", "Continue", Folder: true),
    ];

    private const long MaxFileBytes = 1024 * 1024;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex Variable = new(@"\$\{\{\s*([^}]*?)\s*\}\}|\$\{([^}]+)\}", RegexOptions.None, MatchTimeout);
    private static readonly Regex InputRef = new(@"\$\{input:([^}]+)\}", RegexOptions.None, MatchTimeout);

    /// <summary>The servers the repository holding <paramref name="workspace"/> declares, first file first.</summary>
    public static RepoMcpScan Read(string? workspace, string? home = null)
    {
        if (string.IsNullOrWhiteSpace(workspace)) return RepoMcpScan.None;
        string root;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            if (!Directory.Exists(full)) return RepoMcpScan.None;
            root = RepoInstructionDiscovery.SearchRootFor(full, GitProcess.WorkTreeOf(full),
                                                          home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return RepoMcpScan.None; }

        var servers = new List<RepoMcpServer>();
        var problems = new List<RepoMcpProblem>();
        foreach (var format in Formats)
        {
            var at = Path.Combine(root, format.Location.Replace('/', Path.DirectorySeparatorChar));
            var files = format.Folder
                ? Directory.Exists(at)
                    ? Directory.EnumerateFiles(at).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                                                           || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                                                           || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                                  .OrderBy(f => f, StringComparer.Ordinal).ToList()
                    : []
                : File.Exists(at) ? [at] : new List<string>();
            foreach (var file in files)
            {
                var source = Path.GetRelativePath(root, file).Replace('\\', '/');
                try { PathSanitizer.AssertUnderRoot(file, root); }
                catch (ArgumentException) { problems.Add(new(source, null, "its link leads out of the repository")); continue; }
                // ⚠ What a repository got wrong costs THAT file: an exception out of here stopped every MCP server the
                // refresh had just torn down, the user's own included, and nothing said why.
                try { ReadFile(file, source, format, servers, problems); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problems.Add(new(source, null, $"it cannot be read ({ex.Message})"));
                }
            }
        }
        return new RepoMcpScan(root, servers, problems);
    }

    private static void ReadFile(string file, string source, RepoMcpFormat format, List<RepoMcpServer> servers, List<RepoMcpProblem> problems)
    {
        JsonNode? document;
        try
        {
            if (new FileInfo(file).Length > MaxFileBytes) { problems.Add(new(source, null, "it is larger than 1 MB")); return; }
            var text = TextFileEncoding.ReadText(file);
            document = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                : MiniYaml.Parse(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            problems.Add(new(source, null, $"it cannot be read ({ex.Message})"));
            return;
        }
        if (document is not JsonObject top) { problems.Add(new(source, null, "it holds no object of servers")); return; }

        var inputs = new Dictionary<string, RepoMcpInput>(StringComparer.Ordinal);
        if (top["inputs"] is JsonArray declared)
            foreach (var i in declared.OfType<JsonObject>())
                if (i["id"]?.ToString() is { Length: > 0 } id)
                    inputs[id] = new RepoMcpInput(id, i["description"]?.ToString() ?? id,
                                                  i["password"] is JsonValue p && p.TryGetValue<bool>(out var pw) && pw,
                                                  i["default"]?.ToString());

        // VS Code and Visual Studio write "servers", the others "mcpServers"; Continue a LIST of named servers.
        var entries = new List<(string Name, JsonNode? Definition)>();
        switch (top["servers"] ?? top["mcpServers"])
        {
            case JsonObject map:
                entries.AddRange(map.Select(kv => (kv.Key, kv.Value)));
                break;
            case JsonArray list:
                foreach (var item in list)
                    if (item is JsonObject o && o["name"]?.ToString() is { Length: > 0 } name) entries.Add((name, o));
                    else problems.Add(new(source, null, "a server of its list has no name"));
                break;
            default:
                problems.Add(new(source, null, "it declares no \"servers\" or \"mcpServers\""));
                return;
        }

        foreach (var (name, node) in entries)
        {
            if (node is not JsonObject definition) { problems.Add(new(source, name, "its entry is not an object")); continue; }
            var copy = (JsonObject)definition.DeepClone();
            copy.Remove("name");
            var text = copy.ToJsonString();
            var used = InputRef.Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)
                               .Select(id => inputs.TryGetValue(id, out var known) ? known : new RepoMcpInput(id, id, false, null))
                               .ToList();
            servers.Add(new RepoMcpServer(name.Trim(), source, format.Origin, copy, Fingerprint(source, name, text), used));
        }
    }

    /// <summary>The definition as an agreement holds it: the file, the name and the text, hashed.</summary>
    internal static string Fingerprint(string source, string name, string definition) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + "\n" + name + "\n" + definition)));

    /// <summary>
    /// The server ready to start: its variables replaced, run in the repository (or the <c>cwd</c> it declares, resolved
    /// against it) — or <c>null</c> with the variables nothing fills, and nothing started on an empty value.
    /// </summary>
    /// <param name="inputs">The values given for its <c>${input:…}</c>.</param>
    public static (McpServerConfig? Config, IReadOnlyList<string> Missing, string? Rejected) Prepare(
        RepoMcpServer server, string root, IReadOnlyDictionary<string, string> inputs, string? home = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var missing = new List<string>();
        string Expand(string value) => Variable.Replace(value, m =>
        {
            if (m.Groups[1].Success) { missing.Add(m.Value); return m.Value; }                 // Continue's ${{ secrets.X }}
            var name = m.Groups[2].Value;
            string? found = name switch
            {
                "workspaceFolder"         => root,
                "workspaceFolderBasename" => Path.GetFileName(root),
                "userHome"                => home,
                "pathSeparator" or "/"    => Path.DirectorySeparatorChar.ToString(),
                _ when name.StartsWith("env:", StringComparison.Ordinal)   => environment(name[4..]),
                _ when name.StartsWith("input:", StringComparison.Ordinal) => inputs.TryGetValue(name[6..], out var v) ? v : null,
                _ => ClaudeVariable(name, environment),
            };
            if (found is null) missing.Add(m.Value);
            return found ?? m.Value;
        });

        var expanded = (JsonObject)server.Definition.DeepClone();
        foreach (var key in new[] { "command", "url", "cwd" })
            if (expanded[key] is JsonValue v && v.TryGetValue<string>(out var s)) expanded[key] = Expand(s);
        if (expanded["args"] is JsonArray args)
            for (var i = 0; i < args.Count; i++)
                if (args[i] is JsonValue a && a.TryGetValue<string>(out var s)) args[i] = Expand(s);
        foreach (var key in new[] { "env", "headers" })
            if (expanded[key] is JsonObject map)
                foreach (var name in map.Select(kv => kv.Key).ToList())
                    if (map[name] is JsonValue v && v.TryGetValue<string>(out var s)) map[name] = Expand(s);

        if (missing.Count > 0) return (null, missing.Distinct(StringComparer.Ordinal).ToList(), null);

        var parsed = McpServerConfig.Parse(new JsonObject { [server.Name] = expanded }.ToJsonString(), out var rejected);
        if (parsed.Count == 0) return (null, [], rejected.FirstOrDefault()?.Reason ?? "it cannot be read");
        var folder = expanded["cwd"]?.ToString() is { Length: > 0 } cwd ? Path.GetFullPath(Path.Combine(root, cwd)) : root;
        return (parsed[0] with { WorkingDirectory = folder }, [], null);
    }

    /// <summary>Claude Code's <c>${NAME}</c> and <c>${NAME:-default}</c>: the environment, else the default.</summary>
    private static string? ClaudeVariable(string text, Func<string, string?> environment)
    {
        var cut = text.IndexOf(":-", StringComparison.Ordinal);
        var name = cut < 0 ? text : text[..cut];
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || !name.All(c => char.IsLetterOrDigit(c) || c == '_'))
            return null;
        return environment(name) ?? (cut < 0 ? null : text[(cut + 2)..]);
    }
}
