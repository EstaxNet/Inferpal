using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Inferpal.Services.JsonFields;

namespace Inferpal.Services.Tools;

/// <summary>A request of a collection, its variables resolved: what <c>http_request</c> sends, under its usual card.</summary>
/// <param name="Notes">What of the request is not replayed (a script, an auth kind), said to the model.</param>
/// <param name="Secrets">Each value a variable put into the request, with the variable's name, longest first: what
/// <c>http_request</c> masks in everything it returns to the model (<see cref="Mask"/>).</param>
internal sealed record CollectedRequest(string Name, string Method, string Url, List<KeyValuePair<string, string>> Headers,
                                        string? Body, List<string> Notes, IReadOnlyList<(string Value, string Name)> Secrets)
{
    /// <summary><paramref name="text"/> with every value a variable gave this request replaced by its <c>{{name}}</c>.</summary>
    /// <remarks>⚠ A token's place in a request is not always one a pattern recognises — a path segment, a query parameter
    /// of any name, an echo in the answer: the VALUES are masked wherever they appear, their escaped form too.</remarks>
    public string Mask(string text)
    {
        foreach (var (value, name) in Secrets)
        {
            text = text.Replace(value, "{{" + name + "}}", StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(value);
            if (escaped != value) text = text.Replace(escaped, "{{" + name + "}}", StringComparison.Ordinal);
        }
        return text;
    }
}

/// <summary>
/// The requests a project keeps to try its API — a <c>.http</c>/<c>.rest</c> file (Visual Studio, VS Code's REST Client),
/// a Postman collection (v2.x JSON), a Bruno collection (its folder or a <c>.bru</c> file) — read by name, their variables
/// resolved from the file, its environment and the collection, in the order the tool that wrote them uses.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Nothing is guessed: a variable with no value — one an environment holds when none is named, a secret the tool keeps
/// outside the files, a value a script would set, a machine's environment variable, variables that refer to each other —
/// refuses the request and is named. A script is never run; it is said. A request this cannot send as its tool would (a
/// body taken from a file) is refused when named, never sent otherwise.
/// </para>
/// <para>
/// ⚠ A collection is the repository's text: its variables are expanded within <see cref="MaxExpanded"/> characters, so a
/// file whose variables double at every level cannot exhaust the memory of the host.
/// </para>
/// <para>
/// Every file read is one of the workspace, judged as any read (<see cref="PathSanitizer.AssertUnderRoot"/>, the ignore
/// files); an environment file that the ignore files keep out, or that does not parse, is not read, and said.
/// </para>
/// </remarks>
internal static partial class RequestCollection
{
    private const int MaxFileBytes = 4 * 1024 * 1024, MaxDepth = 10, MaxExpanded = 1024 * 1024, MinMasked = 4;

    /// <summary>A Basic authorisation still to encode: <c>user:password</c> follows it.</summary>
    private const string BasicUnencoded = "inferpal-basic-unencoded:";

    private sealed record Raw(string Name, string Method, string Url, List<KeyValuePair<string, string>> Headers, string? Body,
                              List<string> Notes, Dictionary<string, string> Vars)
    {
        /// <summary>A form body, its pairs encoded once their variables are resolved.</summary>
        public List<KeyValuePair<string, string>>? Form { get; init; }
        /// <summary>A GraphQL body, built once its variables are resolved (a value with a quote stays valid JSON).</summary>
        public (string Query, string Variables)? GraphQl { get; init; }
        /// <summary>The values of <c>:name</c> path segments (Postman <c>url.variable</c>, Bruno <c>params:path</c>).</summary>
        public Dictionary<string, string>? PathVars { get; init; }
        /// <summary>Why this request is not replayed here, said when it is the one asked for.</summary>
        public string? Refusal { get; init; }
    }

    /// <summary>A collection read: its requests, the variable scopes BEFORE the environment and AFTER it, its environments.</summary>
    private sealed record Loaded(List<Raw> Requests, Dictionary<string, string> BeforeEnvironment, Dictionary<string, string> AfterEnvironment,
                                 Dictionary<string, Dictionary<string, string?>> Environments, List<string> Notes, string Kind);

    /// <summary>The names of the requests <paramref name="path"/> holds, or why it cannot be read.</summary>
    internal static (IReadOnlyList<string> Names, string Kind, IReadOnlyList<string> Environments, string? Error) List(string path, string? root)
    {
        var (loaded, error) = Load(path, root);
        return loaded is null ? ([], "", [], error)
            : ([.. loaded.Requests.Select(r => r.Name)], loaded.Kind, [.. loaded.Environments.Keys.Where(k => k != "$shared")], null);
    }

    private sealed class Expansion
    {
        public readonly SortedSet<string> Missing = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Cycle = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Used = new(StringComparer.Ordinal);   // value → name
        public long Size;
        public bool Overflow;
    }

    /// <summary>The request named <paramref name="name"/> in <paramref name="path"/>, its variables resolved with
    /// <paramref name="environment"/>; or why not.</summary>
    internal static (CollectedRequest? Request, string? Error) Resolve(string path, string name, string? environment, string? root)
    {
        var (loaded, error) = Load(path, root);
        if (loaded is null) return (null, error);
        var matches = loaded.Requests.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            matches = loaded.Requests.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            return (null, $"no request named '{name}' in {Path.GetFileName(path)}; its requests: {string.Join(", ", loaded.Requests.Select(r => "'" + r.Name + "'").Take(40))}");
        if (matches.Count > 1) return (null, $"{matches.Count} requests are named '{name}' in {Path.GetFileName(path)}: rename one");
        var raw = matches[0];
        if (raw.Refusal is not null) return (null, $"'{raw.Name}' {raw.Refusal}. Nothing was sent.");

        static Dictionary<string, string?> Scope(Dictionary<string, string> vars) => vars.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
        // An environment's values are masked in what goes back to the model; the collection file's own ones it can read anyway.
        var scopes = new List<(Dictionary<string, string?> Vars, bool Masked)> { (Scope(raw.Vars), false), (Scope(loaded.BeforeEnvironment), false) };
        if (environment is not null)
        {
            if (!loaded.Environments.TryGetValue(environment, out var env))
                return (null, $"no environment '{environment}' for {Path.GetFileName(path)}"
                            + (loaded.Environments.Count == 0 ? " (none found)" : "; its environments: " + string.Join(", ", loaded.Environments.Keys.Where(k => k != "$shared"))));
            scopes.Add((env, true));
        }
        if (loaded.Environments.TryGetValue("$shared", out var shared)) scopes.Add((shared, true));
        scopes.Add((Scope(loaded.AfterEnvironment), false));

        var x = new Expansion();
        string Fill(string text) => Substitute(text, scopes, x, []);
        var url = Fill(raw.Url).Trim();
        if (raw.PathVars is { } pathVars)
            url = PathVariable().Replace(url, m =>
            {
                var key = m.Groups[1].Value;
                if (!pathVars.TryGetValue(key, out var value)) return m.Value;
                if (value.Length == 0) { x.Missing.Add(":" + key); return m.Value; }
                return "/" + Uri.EscapeDataString(Fill(value));
            });
        var headers = raw.Headers.Select(h => new KeyValuePair<string, string>(Fill(h.Key), Fill(h.Value))).ToList();
        var body = raw.Body is null ? null : Fill(raw.Body);
        if (raw.Form is { } form)
            body = string.Join("&", form.Select(p => Uri.EscapeDataString(Fill(p.Key)) + "=" + Uri.EscapeDataString(Fill(p.Value))));
        if (raw.GraphQl is { } graphQl)
        {
            var (query, variables) = graphQl;
            var filledVars = Fill(variables).Trim();
            object? parsed = null;
            if (filledVars.Length > 0)
            {
                try { parsed = JsonDocument.Parse(filledVars).RootElement.Clone(); }
                catch (JsonException) { return (null, $"'{raw.Name}' has GraphQL variables that are not JSON once filled. Nothing was sent."); }
            }
            body = JsonSerializer.Serialize(new { query = Fill(query), variables = parsed });
        }
        if (x.Overflow)
            return (null, $"'{raw.Name}' expands its variables past {MaxExpanded / 1024} KB: not sent. Nothing was sent.");
        if (x.Cycle.Count > 0)
            return (null, $"'{raw.Name}' uses variables that refer to each other ({string.Join(", ", x.Cycle)}). Nothing was sent.");
        if (x.Missing.Count > 0)
        {
            var inEnvironments = loaded.Environments.Where(e => e.Key != "$shared" && x.Missing.Any(e.Value.ContainsKey)).Select(e => e.Key).ToList();
            return (null, $"'{raw.Name}' uses {string.Join(", ", x.Missing.Select(m => m.StartsWith(':') ? m : "{{" + m + "}}"))}, which "
                        + (x.Missing.Count == 1 ? "has" : "have") + " no value here"
                        + (environment is null && inEnvironments.Count > 0 ? $" — name an environment: {string.Join(", ", inEnvironments)}" : "")
                        + (loaded.Notes.Count > 0 ? " (" + string.Join("; ", loaded.Notes) + ")" : "")
                        + ". Nothing was sent.");
        }
        // Basic is encoded once its parts are known; the encoded credential is masked like any value.
        for (var i = 0; i < headers.Count; i++)
            if (headers[i].Value.StartsWith(BasicUnencoded, StringComparison.Ordinal))
            {
                var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(headers[i].Value[BasicUnencoded.Length..]));
                headers[i] = new(headers[i].Key, "Basic " + encoded);
                x.Used[encoded] = "basic credential";
            }
        // A bare address (http://host:port, nothing in it but where) is no secret, and the model needs to know where it went.
        static bool BareOrigin(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out var u) && u.UserInfo.Length == 0 && u.Query.Length == 0 && u.AbsolutePath.Length <= 1;
        var secrets = x.Used.Where(p => p.Key.Length >= MinMasked && !BareOrigin(p.Key)).OrderByDescending(p => p.Key.Length)
                            .Select(p => (p.Key, p.Value)).ToList();
        return (new CollectedRequest(raw.Name, raw.Method.ToUpperInvariant(), url, headers, body, raw.Notes, secrets), null);
    }

    /// <summary><paramref name="text"/> with each <c>{{name}}</c> replaced from the first scope that holds it, a value's own
    /// variables too; what has no value, refers back to itself, or expands too far is recorded, and stays as written.</summary>
    private static string Substitute(string text, List<(Dictionary<string, string?> Vars, bool Masked)> scopes, Expansion x, List<string> stack) =>
        Variable().Replace(text, m =>
        {
            if (x.Overflow) return m.Value;
            var name = m.Groups[1].Value.Trim();
            if (Dynamic(name) is { } generated) return generated;
            if (stack.Contains(name) || stack.Count >= MaxDepth)
            {
                foreach (var looped in stack.Append(name)) x.Cycle.Add(looped);
                return m.Value;
            }
            foreach (var (vars, masked) in scopes)
                if (vars.TryGetValue(name, out var value))
                {
                    if (value is null) break;   // declared with no value here (a secret kept elsewhere)
                    var expanded = Substitute(value, scopes, x, [.. stack, name]);
                    x.Size += expanded.Length;
                    if (x.Size > MaxExpanded) { x.Overflow = true; return m.Value; }
                    if (masked) x.Used.TryAdd(expanded, name);
                    return expanded;
                }
            x.Missing.Add(name);
            return m.Value;
        });

    /// <summary>The values a tool generates itself: a fresh id, the time, a number. Any other <c>$</c> name (a machine's
    /// environment variable, a .env file, a response of another request) has none here.</summary>
    private static string? Dynamic(string name) => name switch
    {
        "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        "$randomInt" => Random.Shared.Next(0, 1000).ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex Variable();

    [GeneratedRegex(@"/:([A-Za-z_]\w*)(?=/|\?|#|$)")]
    private static partial Regex PathVariable();

    // ── Loading ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static (Loaded? Loaded, string? Error) Load(string path, string? root)
    {
        try
        {
            if (Directory.Exists(path)) return LoadBruno(path, root, single: null);
            if (!File.Exists(path)) return (null, $"'{path}' does not exist");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".http" or ".rest") return LoadHttpFile(path, root);
            if (ext == ".bru") return LoadBruno(BrunoRoot(path, root), root, single: path);
            if (ext == ".json")
            {
                var text = ReadText(path, root);
                if (text.Contains("\"_postman_id\"", StringComparison.Ordinal) || text.Contains("schema.getpostman.com", StringComparison.Ordinal)
                    || text.Contains("\"item\"", StringComparison.Ordinal) && text.Contains("\"info\"", StringComparison.Ordinal))
                    return LoadPostman(path, text, root);
            }
            return (null, $"'{Path.GetFileName(path)}' is no request collection this reads: a .http or .rest file, a Postman collection (.json), a Bruno collection (its folder or a .bru file)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return (null, $"'{Path.GetFileName(path)}' cannot be read: {Diagnostics.RootMessage(ex)}");
        }
    }

    /// <summary>A file of the workspace as text: under the root, outside the ignore files, bounded.</summary>
    private static string ReadText(string path, string? root)
    {
        PathSanitizer.AssertUnderRoot(path, root);
        if (Governance.ContentExclusions.Of(path) is { } excluded)
            throw new InvalidOperationException($"{Path.GetFileName(path)} is kept out of AI tools by {excluded.Named}");
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidOperationException($"it is larger than {MaxFileBytes / (1024 * 1024)} MB");
        return TextFileEncoding.ReadText(path);
    }

    /// <summary>An environment file read when it is there and allowed; why not, in <paramref name="notes"/>, otherwise.</summary>
    private static string? ReadOptional(string path, string? root, List<string> notes)
    {
        if (!File.Exists(path)) return null;
        try { return ReadText(path, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            notes.Add($"{Path.GetFileName(path)} not read: {Diagnostics.RootMessage(ex)}");
            return null;
        }
    }

    /// <summary>An environment file's JSON; <c>null</c>, said in <paramref name="notes"/>, when it does not parse.</summary>
    private static JsonElement? ParseOptional(string? json, string file, List<string> notes)
    {
        if (json is null) return null;
        try { return JsonDocument.Parse(json, Lenient).RootElement.Clone(); }
        catch (JsonException ex)
        {
            notes.Add($"{file} not read: it is not valid JSON ({ex.Message})");
            return null;
        }
    }

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // ── .http / .rest ──

    private static (Loaded?, string?) LoadHttpFile(string path, string? root)
    {
        var text = ReadText(path, root).ReplaceLineEndings("\n");
        var fileVars = new Dictionary<string, string>(StringComparer.Ordinal);
        var requests = new List<Raw>();
        var marks = HttpSeparator().Matches(text);
        for (var b = 0; b <= marks.Count; b++)
        {
            var title = b == 0 ? "" : marks[b - 1].Groups[1].Value.Trim();
            var from = b == 0 ? 0 : marks[b - 1].Index + marks[b - 1].Length;
            var to = b < marks.Count ? marks[b].Index : text.Length;
            var lines = text[from..to].Split('\n');
            string? name = title.Length > 0 ? title : null;
            string? method = null, url = null;
            var headers = new List<KeyValuePair<string, string>>();
            var body = new List<string>();
            var notes = new List<string>();
            var stage = 0;   // 0: before the request line, 1: headers, 2: body, 3: a response handler (never sent)
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (stage == 0)
                {
                    if (trimmed.Length == 0) continue;
                    if (FileVariable().Match(trimmed) is { Success: true } v) { fileVars[v.Groups[1].Value] = v.Groups[2].Value.Trim(); continue; }
                    if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        if (NameTag().Match(trimmed) is { Success: true } n) name = n.Groups[1].Value.Trim();
                        continue;
                    }
                    var request = RequestLine().Match(trimmed);
                    // Prose ("Get orders") is no request line: its address starts as one does.
                    if (!request.Success || !(request.Groups[2].Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                                              || request.Groups[2].Value.StartsWith("{{", StringComparison.Ordinal)
                                              || request.Groups[2].Value.StartsWith('/'))) continue;
                    method = request.Groups[1].Success ? request.Groups[1].Value : "GET";
                    url = request.Groups[2].Value;
                    stage = 1;
                    continue;
                }
                if (stage == 1)
                {
                    if (trimmed.Length == 0) { stage = 2; continue; }
                    // A query continued on the next lines (REST Client): "?a=1" then "&b=2".
                    if (headers.Count == 0 && (trimmed.StartsWith('?') || trimmed.StartsWith('&'))) { url += trimmed; continue; }
                    if (trimmed.StartsWith('#') || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                    var colon = trimmed.IndexOf(':');
                    if (colon > 0) headers.Add(new(trimmed[..colon].Trim(), trimmed[(colon + 1)..].Trim()));
                    continue;
                }
                // A body ends at its first line that hands the answer on (> handler, >> file): a script, never sent.
                if (stage == 3 || line.StartsWith('>'))
                {
                    if (stage != 3) notes.Add("its response handler is not run");
                    stage = 3;
                    continue;
                }
                body.Add(line);
            }
            if (method is null || url is null) continue;
            var bodyText = string.Join("\n", body).Trim();
            string? refusal = null;
            // A body taken from a file (< ./x.json, <@ ./x.json) is the tool's to read: refused when named, never sent as text.
            if (bodyText.StartsWith('<'))
            {
                refusal = $"takes its body from a file ('{bodyText.Split('\n')[0]}'): not replayed here; send it with url and body";
                bodyText = "";
            }
            requests.Add(new Raw(name ?? $"{method} {url}", method, url, headers, bodyText.Length == 0 ? null : bodyText, notes,
                                 new Dictionary<string, string>(StringComparer.Ordinal)) { Refusal = refusal });
        }
        if (requests.Count == 0) return (null, $"'{Path.GetFileName(path)}' holds no request (a line such as GET http://localhost:5000/api)");

        // Visual Studio uses the NEAREST http-client.env.json (and its .user beside it), from the file's folder up to the
        // root; REST Client's environments live in the workspace settings. The file's own variables win over both.
        var environments = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var notesOfEnvs = new List<string>();
        for (var dir = Path.GetDirectoryName(path); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var found = false;
            foreach (var file in new[] { "http-client.env.json", "http-client.env.json.user" })
            {
                var json = ParseOptional(ReadOptional(Path.Combine(dir, file), root, notesOfEnvs), file, notesOfEnvs);
                if (File.Exists(Path.Combine(dir, file))) found = true;
                if (json is { } env) MergeEnvironments(environments, env, notesOfEnvs);
            }
            if (found || root is null || PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root))) break;
        }
        if (root is not null
            && ParseOptional(ReadOptional(Path.Combine(root, ".vscode", "settings.json"), root, notesOfEnvs), "settings.json", notesOfEnvs) is { } settings
            && settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty("rest-client.environmentVariables", out var restEnvs))
            MergeEnvironments(environments, restEnvs, notesOfEnvs);
        return (new Loaded(requests, fileVars, [], environments, notesOfEnvs, "HTTP file"), null);
    }

    private static void MergeEnvironments(Dictionary<string, Dictionary<string, string?>> into, JsonElement envs, List<string> notes)
    {
        if (envs.ValueKind != JsonValueKind.Object) return;
        foreach (var env in envs.EnumerateObject())
        {
            if (env.Value.ValueKind != JsonValueKind.Object) continue;
            if (!into.TryGetValue(env.Name, out var vars)) into[env.Name] = vars = new(StringComparer.Ordinal);
            foreach (var v in env.Value.EnumerateObject())
            {
                // A secret kept by a provider (Visual Studio: { "provider": "AzureKeyVault", ... }) has no value in the file.
                vars[v.Name] = v.Value.AsText();
                if (vars[v.Name] is null && !notes.Contains($"{v.Name} is kept by a secret provider, not in the file"))
                    notes.Add($"{v.Name} is kept by a secret provider, not in the file");
            }
        }
    }

    [GeneratedRegex(@"^###[^\S\n]*(.*)$", RegexOptions.Multiline)]
    private static partial Regex HttpSeparator();

    [GeneratedRegex(@"^@([A-Za-z_][\w.-]*)\s*=\s*(.*)$")]
    private static partial Regex FileVariable();

    [GeneratedRegex(@"^(?:#|//)\s*@name\s+(.+)$")]
    private static partial Regex NameTag();

    [GeneratedRegex(@"^(?:(" + HttpRequestTool.MethodNames + @")\s+)?(\S+?)(?:\s+HTTP/[\d.]+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex RequestLine();

    // ── Postman ──

    private static (Loaded?, string?) LoadPostman(string path, string text, string? root)
    {
        var doc = JsonDocument.Parse(text, Lenient).RootElement;
        var requests = new List<Raw>();
        // An auth that is absent, null or "inherit" is the parent's.
        static JsonElement? Own(JsonElement holder, JsonElement? parent) =>
            holder.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.Object && Str(a, "type") != "inherit" ? a : parent;
        void Walk(JsonElement items, string folder, JsonElement? inheritedAuth)
        {
            foreach (var item in items.EnumerateArray())
            {
                var auth = Own(item, inheritedAuth);
                var name = item.TryGetProperty("name", out var n) ? n.AsText() ?? "" : "";
                if (item.TryGetProperty("item", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    Walk(children, folder.Length == 0 ? name : folder + "/" + name, auth);
                    continue;
                }
                if (!item.TryGetProperty("request", out var request)) continue;
                requests.Add(PostmanRequest(folder.Length == 0 ? name : folder + "/" + name, item, request,
                                            request.ValueKind == JsonValueKind.Object ? Own(request, auth) : auth));
            }
        }
        if (doc.TryGetProperty("item", out var top) && top.ValueKind == JsonValueKind.Array)
            Walk(top, "", doc.TryGetProperty("auth", out var ca) && ca.ValueKind == JsonValueKind.Object ? ca : null);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc.TryGetProperty("variable", out var cv) && cv.ValueKind == JsonValueKind.Array)
            foreach (var v in cv.EnumerateArray())
                if (Str(v, "key") is { Length: > 0 } key && (!v.TryGetProperty("disabled", out var d) || d.ValueKind != JsonValueKind.True))
                    vars[key] = ValueText(v);

        // Environments exported next to it: *.postman_environment.json, by their name. An environment wins over the
        // collection's own variables, as in Postman.
        var environments = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var notes = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.postman_environment.json"))
            if (ParseOptional(ReadOptional(file, root, notes), Path.GetFileName(file), notes) is { } env && env.ValueKind == JsonValueKind.Object)
            {
                var name = Str(env, "name") is { Length: > 0 } en ? en : Path.GetFileName(file).Replace(".postman_environment.json", "", StringComparison.OrdinalIgnoreCase);
                var values = new Dictionary<string, string?>(StringComparer.Ordinal);
                if (env.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    foreach (var v in vs.EnumerateArray())
                        if (Str(v, "key") is { Length: > 0 } key && (!v.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False))
                            values[key] = ValueText(v);
                environments[name] = values;
            }
        return (new Loaded(requests, [], vars, environments, notes, "Postman collection"), null);
    }

    private static Raw PostmanRequest(string name, JsonElement item, JsonElement request, JsonElement? auth)
    {
        var notes = new List<string>();
        if (request.ValueKind == JsonValueKind.String)
            return new Raw(name, "GET", request.AsText() ?? "", [], null, notes, new(StringComparer.Ordinal));
        var method = Str(request, "method") is { Length: > 0 } m ? m : "GET";
        string url = "";
        Dictionary<string, string>? pathVars = null;
        if (request.TryGetProperty("url", out var u))
        {
            if (u.ValueKind == JsonValueKind.String) url = u.AsText() ?? "";
            else if (u.ValueKind == JsonValueKind.Object)
            {
                url = Str(u, "raw") is { Length: > 0 } rawUrl ? rawUrl : PostmanUrl(u);
                if (u.TryGetProperty("variable", out var pv) && pv.ValueKind == JsonValueKind.Array)
                    pathVars = pv.EnumerateArray().Where(p => Str(p, "key").Length > 0)
                                 .GroupBy(p => Str(p, "key")).ToDictionary(g => g.Key, g => ValueText(g.Last()), StringComparer.Ordinal);
            }
        }
        var headers = new List<KeyValuePair<string, string>>();
        if (request.TryGetProperty("header", out var hs) && hs.ValueKind == JsonValueKind.Array)
            foreach (var h in hs.EnumerateArray())
                if (Str(h, "key") is { Length: > 0 } key && (!h.TryGetProperty("disabled", out var d) || d.ValueKind != JsonValueKind.True))
                    headers.Add(new(key, Str(h, "value")));
        string? body = null;
        List<KeyValuePair<string, string>>? form = null;
        (string, string)? graphQl = null;
        if (request.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object)
            switch (Str(b, "mode"))
            {
                case "raw": body = Str(b, "raw"); break;
                case "urlencoded":
                    form = [.. (b.TryGetProperty("urlencoded", out var pairs) && pairs.ValueKind == JsonValueKind.Array ? pairs.EnumerateArray() : default)
                        .Where(p => !p.TryGetProperty("disabled", out var pd) || pd.ValueKind != JsonValueKind.True)
                        .Select(p => new KeyValuePair<string, string>(Str(p, "key"), Str(p, "value")))];
                    if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                        headers.Add(new("Content-Type", "application/x-www-form-urlencoded"));
                    break;
                case "graphql" when b.TryGetProperty("graphql", out var g):
                    graphQl = (Str(g, "query"), Str(g, "variables"));
                    if (!headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                        headers.Add(new("Content-Type", "application/json"));
                    break;
                case "": break;
                default: notes.Add($"its {Str(b, "mode")} body is not replayed (send it with url and body)"); break;
            }
        if (auth is { } a && a.ValueKind == JsonValueKind.Object) AddAuth(Str(a, "type"), k => AuthValue(a, Str(a, "type"), k), headers, notes);
        if (item.TryGetProperty("event", out var events) && events.ValueKind == JsonValueKind.Array && events.GetArrayLength() > 0)
            notes.Add("its scripts are not run");
        return new Raw(name, method, url, headers, body, notes, new(StringComparer.Ordinal)) { Form = form, GraphQl = graphQl, PathVars = pathVars };
    }

    /// <summary>A Postman URL written as its parts (no <c>raw</c>): protocol, host, port, path, query.</summary>
    private static string PostmanUrl(JsonElement u)
    {
        static string Joined(JsonElement e, string name, string by) =>
            e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.Array ? string.Join(by, v.EnumerateArray().Select(x => x.AsText() ?? Str(x, "value"))) : v.AsText() ?? "" : "";
        var sb = new StringBuilder();
        if (Str(u, "protocol") is { Length: > 0 } protocol) sb.Append(protocol).Append("://");
        sb.Append(Joined(u, "host", "."));
        if (Str(u, "port") is { Length: > 0 } port) sb.Append(':').Append(port);
        if (Joined(u, "path", "/") is { Length: > 0 } p) sb.Append('/').Append(p);
        if (u.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.Array)
        {
            var query = q.EnumerateArray().Where(x => !x.TryGetProperty("disabled", out var d) || d.ValueKind != JsonValueKind.True)
                         .Select(x => Str(x, "key") + "=" + Str(x, "value")).ToList();
            if (query.Count > 0) sb.Append('?').Append(string.Join("&", query));
        }
        return sb.ToString();
    }

    /// <summary>A Postman auth field: <c>auth.bearer[{ key: "token", value }]</c>.</summary>
    private static string? AuthValue(JsonElement auth, string type, string key) =>
        auth.TryGetProperty(type, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => Str(e, "key") == key).Select(ValueText).FirstOrDefault()
            : null;

    /// <summary>The header an auth kind writes, its values left as written (variables resolved with the rest).</summary>
    private static void AddAuth(string type, Func<string, string?> value, List<KeyValuePair<string, string>> headers, List<string> notes)
    {
        switch (type)
        {
            case "" or "noauth" or "none": return;
            case "bearer" when value("token") is { } token:
                headers.Add(new("Authorization", "Bearer " + token));
                return;
            case "basic" when value("username") is { } user:
                // Encoded once its variables are resolved (Resolve): encoded now, "{{user}}" would be sent as written.
                headers.Add(new("Authorization", BasicUnencoded + user + ":" + (value("password") ?? "")));
                return;
            case "apikey" when value("key") is { } key && (value("in") ?? "header") == "header":
                headers.Add(new(key, value("value") ?? ""));
                return;
            default:
                notes.Add($"its {type} authorisation is not replayed: add the header it needs");
                return;
        }
    }

    // ── Bruno ──

    /// <summary>The collection a .bru file belongs to: the nearest folder above it with a bruno.json, within the root.</summary>
    private static string BrunoRoot(string file, string? root)
    {
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "bruno.json"))) return dir;
            if (root is not null && PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(root))) break;
        }
        return Path.GetDirectoryName(file)!;
    }

    private static bool IsBrunoSettings(string folder, string file) =>
        Path.GetRelativePath(folder, file).Split('/', '\\')[0].Equals("environments", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(file).Equals("collection.bru", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(file).Equals("folder.bru", StringComparison.OrdinalIgnoreCase);

    private static (Loaded?, string?) LoadBruno(string folder, string? root, string? single)
    {
        var requests = new List<Raw>();
        var notesOfEnvs = new List<string>();
        var files = single is not null ? [single]
            : WorkspaceScan.EnumerateFiles(folder, "*.bru").Where(f => !IsBrunoSettings(folder, f))
                           .OrderBy(f => f, StringComparer.Ordinal).Take(500).ToList();   // the same 500 on every machine
        foreach (var file in files)
        {
            var blocks = BruBlocks(ReadText(file, root));
            var method = HttpRequestTool.MethodNames.ToLowerInvariant().Split('|').FirstOrDefault(blocks.ContainsKey);
            if (method is null) continue;
            var http = Pairs(blocks[method]);
            var meta = blocks.TryGetValue("meta", out var mt) ? Pairs(mt) : new Dictionary<string, string>(StringComparer.Ordinal);
            var name = meta.TryGetValue("name", out var n) ? n : Path.GetFileNameWithoutExtension(file);
            var notes = new List<string>();
            var headers = new List<KeyValuePair<string, string>>();
            // What the collection and its folders set for every request comes first, the request's own after (and wins).
            var inherited = Inherited(folder, file, root, notes);
            if (inherited.Headers is { } shared) headers.AddRange(shared);
            if (blocks.TryGetValue("headers", out var hs)) headers.AddRange(Pairs(hs));
            string? body = null;
            List<KeyValuePair<string, string>>? form = null;
            (string, string)? graphQl = null;
            var kind = http.TryGetValue("body", out var bk) ? bk : "none";
            if (kind != "none")
            {
                blocks.TryGetValue("body:" + kind, out var content);
                switch (kind)
                {
                    case "form-urlencoded": form = [.. Pairs(content ?? "")]; break;
                    case "multipart-form" or "file": notes.Add($"its {kind} body is not replayed (send it with url and body)"); break;
                    case "graphql": graphQl = (content ?? "", blocks.TryGetValue("body:graphql:vars", out var gv) ? gv : ""); break;
                    default: body = content; break;
                }
                if ((body ?? content) is not null && kind is not ("multipart-form" or "file")
                    && !headers.Any(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                    headers.Add(new("Content-Type", kind switch
                    {
                        "json" or "graphql" => "application/json", "xml" => "application/xml",
                        "form-urlencoded" => "application/x-www-form-urlencoded", _ => "text/plain",
                    }));
            }
            var authKind = http.TryGetValue("auth", out var ak) ? ak : "none";
            var authBlocks = blocks;
            if (authKind == "inherit")
            {
                (authKind, authBlocks) = inherited.Auth ?? ("none", blocks);
                if (inherited.Auth is null) notes.Add("it inherits an authorisation its collection does not set");
            }
            if (authKind != "none")
            {
                var fields = authBlocks.TryGetValue("auth:" + authKind, out var af) ? Pairs(af) : new Dictionary<string, string>(StringComparer.Ordinal);
                AddAuth(authKind, k => fields.TryGetValue(k switch { "in" => "placement", _ => k }, out var v) ? v : null, headers, notes);
            }
            var vars = blocks.TryGetValue("vars:pre-request", out var pv) ? Pairs(pv) : new Dictionary<string, string>(StringComparer.Ordinal);
            var pathVars = blocks.TryGetValue("params:path", out var pp) ? Pairs(pp) : null;
            if (blocks.Keys.Any(k => k.StartsWith("script:", StringComparison.Ordinal) || k == "tests")) notes.Add("its scripts are not run");
            requests.Add(new Raw(name, method, http.TryGetValue("url", out var url) ? url : "", headers, body, notes, vars)
                         { Form = form, GraphQl = graphQl, PathVars = pathVars });
        }
        if (requests.Count == 0) return (null, $"'{Path.GetFileName(folder)}' holds no Bruno request (.bru files with a get, post… block)");

        var environments = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
        var envFolder = Path.Combine(folder, "environments");
        if (Directory.Exists(envFolder))
            foreach (var file in Directory.EnumerateFiles(envFolder, "*.bru"))
                if (ReadOptional(file, root, notesOfEnvs) is { } text)
                {
                    var blocks = BruBlocks(text);
                    var vars = (blocks.TryGetValue("vars", out var v) ? Pairs(v) : new Dictionary<string, string>(StringComparer.Ordinal))
                               .ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
                    // vars:secret [ token, password ]: Bruno keeps their values outside the files.
                    if (blocks.TryGetValue("vars:secret", out var secret))
                        foreach (var key in secret.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            vars[key.TrimStart('~')] = null;
                            notesOfEnvs.Add($"{key.TrimStart('~')} is a secret Bruno keeps outside the files");
                        }
                    environments[Path.GetFileNameWithoutExtension(file)] = vars;
                }
        return (new Loaded(requests, [], [], environments, notesOfEnvs.Distinct().ToList(), "Bruno collection"), null);
    }

    /// <summary>What a request inherits from the nearest <c>folder.bru</c> above it, then <c>collection.bru</c>: their
    /// headers (collection first) and the first authorisation one of them sets.</summary>
    private static (List<KeyValuePair<string, string>>? Headers, (string Kind, Dictionary<string, string> Blocks)? Auth) Inherited(
        string folder, string file, string? root, List<string> notes)
    {
        var chain = new List<string>();
        for (var dir = Path.GetDirectoryName(file); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            chain.Add(Path.Combine(dir, "folder.bru"));
            if (PathComparer.Default.Equals(Path.TrimEndingDirectorySeparator(dir), Path.TrimEndingDirectorySeparator(folder))) break;
        }
        chain.Add(Path.Combine(folder, "collection.bru"));
        List<KeyValuePair<string, string>>? headers = null;
        (string, Dictionary<string, string>)? auth = null;
        foreach (var settings in Enumerable.Reverse(chain))   // the collection first, the nearest folder last
        {
            if (ReadOptional(settings, root, notes) is not { } text) continue;
            var blocks = BruBlocks(text);
            if (blocks.TryGetValue("headers", out var hs)) (headers ??= []).AddRange(Pairs(hs));
            if (blocks.TryGetValue("auth", out var a) && Pairs(a).TryGetValue("mode", out var mode) && mode is not ("inherit" or "none"))
                auth = (mode, blocks);
        }
        return (headers, auth);
    }

    /// <summary>The blocks of a .bru file, <c>name {</c> … <c>}</c> (or <c>[</c> … <c>]</c>), their content dedented.</summary>
    private static Dictionary<string, string> BruBlocks(string text)
    {
        var blocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var head = BruBlockStart().Match(lines[i]);
            if (!head.Success) continue;
            var close = head.Groups[2].Value == "{" ? "}" : "]";
            var content = new StringBuilder();
            for (i++; i < lines.Length && lines[i].TrimEnd() != close; i++)
                content.Append(lines[i].StartsWith("  ", StringComparison.Ordinal) ? lines[i][2..] : lines[i]).Append('\n');
            blocks[head.Groups[1].Value] = content.ToString().TrimEnd();
        }
        return blocks;
    }

    [GeneratedRegex(@"^([\w:-]+)\s*([{\[])\s*$")]
    private static partial Regex BruBlockStart();

    /// <summary>The <c>key: value</c> lines of a block; a key written <c>~key</c> is turned off and left out.</summary>
    private static Dictionary<string, string> Pairs(string block)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in block.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || line.TrimStart().StartsWith('~')) continue;
            pairs[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return pairs;
    }

    private static string ValueText(JsonElement e) =>
        e.TryGetProperty("value", out var v) ? v.ValueKind == JsonValueKind.String ? v.AsText() ?? "" : v.GetRawText() : "";
}
