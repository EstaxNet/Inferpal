using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inferpal.Services.Mcp;

namespace Inferpal.Services.Documents;

/// <summary>
/// An API description read as what calling it needs: an OpenAPI or Swagger specification (<c>.yaml</c>, <c>.yml</c>,
/// <c>.json</c>, recognised by its root key) as its operations — method, path, parameters, body and answers — then its
/// schemas; a browser's network capture (<c>.har</c>) as its requests — method, address, status, time and bounded bodies.
/// The file stays a text file: asked for lines, each is the text it holds (as a test report, <see cref="TestReportText"/>).
/// </summary>
/// <remarks>
/// ⚠ A summary, not a protection: a capture's headers and cookies are left out of it and what looks like a credential in
/// an address or a body is masked (<see cref="SecretRedactor"/>, best effort), but the file holds everything as captured,
/// and its lines are the person's own. Nothing here is said to make a capture safe.
/// ⚠ Never throws for a file of an unexpected shape: such a file is read as its lines (<c>null</c>).
/// </remarks>
internal static class ApiText
{
    /// <summary>The largest file read this way: past it, its text is paged as it is (a tree of it costs several times its
    /// size).</summary>
    private const int MaxChars = 8_000_000;

    /// <summary>How much of a request's or a response's body a capture shows.</summary>
    internal const int BodyChars = 300;

    /// <summary>How long an address, a title or a summary may be in one line.</summary>
    private const int LineChars = 300;

    /// <summary>How many properties of a schema are named.</summary>
    private const int PropertiesShown = 12;

    /// <summary>The operations of the specification, or the requests of the capture, <paramref name="text"/> read from
    /// <paramref name="name"/>, within <paramref name="budget"/> characters; <c>null</c> when it is neither, or not of the
    /// shape this reads (its lines are read instead).</summary>
    internal static string? Summarize(string name, string text, int budget, CancellationToken ct = default)
    {
        if (text.Length > MaxChars) return null;
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".har" or ".json" or ".yaml" or ".yml")) return null;
        // Its root key first, before any parse: a JSON or YAML file of the workspace is rarely a specification.
        var head = text.Length > 2048 ? text[..2048] : text;
        if (extension != ".har" && !head.Contains("openapi", StringComparison.Ordinal) && !head.Contains("swagger", StringComparison.Ordinal))
            return null;
        try
        {
            if (extension == ".har") return Read(text, json: true) is JsonObject har ? Har(name, har, budget, ct) : null;
            return Read(text, json: extension == ".json") is JsonObject spec && (spec["openapi"] ?? spec["swagger"]) is JsonValue
                ? OpenApi(name, spec, budget, ct)
                : null;
        }
        // ⚠ A node of another kind than the specification says (a string where an object goes, a duplicate key): the file
        // is read as its lines, never a failed read.
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or JsonException
                                       or KeyNotFoundException or InvalidCastException)
        {
            Diagnostics.Swallow("ApiText.Summarize", ex);
            return null;
        }
    }

    private static JsonNode? Read(string text, bool json) =>
        json
            ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            : MiniYaml.Parse(text);

    // ── OpenAPI / Swagger ──

    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private static string OpenApi(string name, JsonObject spec, int budget, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var version = spec["openapi"] is JsonValue o ? o.ToString() : (spec["swagger"] as JsonValue)?.ToString() ?? "";
        var title = Clip(Str(Obj(spec["info"])?["title"]));
        var operations = new List<string>();
        var elsewhere = 0;
        if (Obj(spec["paths"]) is { } paths)
            foreach (var (path, item) in paths)
            {
                ct.ThrowIfCancellationRequested();
                if (Obj(item) is not { } ops) continue;
                // A path item kept in another file ($ref './users.yaml'): counted and said, never read as "no operation".
                if (Str(ops["$ref"]) is { Length: > 0 } outside && !outside.StartsWith('#')) { elsewhere++; continue; }
                var resolved = Resolve(spec, ops) ?? ops;
                var shared = resolved["parameters"] as JsonArray;
                foreach (var method in Methods)
                    if (Obj(resolved[method]) is { } op) operations.Add(Operation(spec, method, path, op, shared));
            }
        var schemas = Obj(Obj(spec["components"])?["schemas"]) ?? Obj(spec["definitions"]);

        var sb = new StringBuilder();
        sb.Append(inv, $"[API specification {name} (OpenAPI {version}){(title.Length > 0 ? ": " + title : "")} — {operations.Count} operation(s)");
        if (schemas is not null) sb.Append(inv, $", {schemas.Count} schema(s)");
        sb.Append(". This is what calling it needs; read its text with start_line=1.]\n");
        if (elsewhere > 0)
            sb.Append(inv, $"{elsewhere} path(s) are described in other files ($ref) and not read here: read those files.\n");
        var servers = (spec["servers"] as JsonArray)?.Select(s => Clip(Str(Obj(s)?["url"]))).Where(u => u.Length > 0).ToList() ?? [];
        if (servers.Count == 0 && Str(spec["host"]) is { Length: > 0 } host) servers.Add(Clip(host + Str(spec["basePath"])));
        if (servers.Count > 0) sb.Append("Servers: ").Append(string.Join(", ", servers)).Append('\n');

        var shown = Fill(sb, "\n## Operations\n", operations, budget);
        if (shown < operations.Count)
        {
            sb.Append(inv, $"\n{operations.Count - shown} more operation(s) not shown here: search_in_files finds a path in the text.");
            if (schemas is { Count: > 0 }) sb.Append(inv, $" Its {schemas.Count} schema(s) are not shown either: search_in_files finds one by name.");
            sb.Append('\n');
        }
        else if (schemas is not null)
        {
            var listed = schemas.Select(s => Schema(spec, s.Key, s.Value)).ToList();
            var schemaShown = Fill(sb, "\n## Schemas\n", listed, budget);
            if (schemaShown < listed.Count)
                sb.Append(inv, $"\n{listed.Count - schemaShown} more schema(s) not shown here: search_in_files finds one by name.\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Operation(JsonObject spec, string method, string path, JsonObject op, JsonArray? shared)
    {
        var sb = new StringBuilder().Append(method.ToUpperInvariant()).Append(' ').Append(Clip(path));
        var summary = Str(op["summary"]) is { Length: > 0 } s ? s : Str(op["operationId"]);
        if (summary.Length > 0) sb.Append(" — ").Append(Clip(summary));
        // A parameter given by reference (#/components/parameters/Limit, very common) is read where it is defined.
        var parameters = (shared ?? []).Concat(op["parameters"] as JsonArray ?? [])
            .Select(p => Resolve(spec, Obj(p)))
            .OfType<JsonObject>()
            .Select(p => $"{Str(p["name"])} ({Str(p["in"])}{(Str(p["required"]) == "true" ? ", required" : "")}"
                         + $"{(TypeOf(spec, p["schema"] ?? p) is { Length: > 0 } t ? ", " + t : "")})")
            .ToList();
        if (parameters.Count > 0) sb.Append("\n  parameters: ").Append(Clip(string.Join(", ", parameters)));
        // The body: OpenAPI 3's requestBody, by its media types; Swagger 2's "in: body" parameter is listed above.
        if (Obj(Resolve(spec, Obj(op["requestBody"]))?["content"]) is { } content)
            sb.Append("\n  body: ").Append(Clip(string.Join(", ", content.Select(c => $"{c.Key} {TypeOf(spec, Obj(c.Value)?["schema"])}".TrimEnd()))));
        if (Obj(op["responses"]) is { } responses)
            sb.Append("\n  answers: ").Append(Clip(string.Join(", ", responses.Select(r =>
            {
                var answer = Resolve(spec, Obj(r.Value));
                var schema = Obj(answer?["content"])?.Select(c => Obj(c.Value)?["schema"]).FirstOrDefault(x => x is not null) ?? answer?["schema"];
                return TypeOf(spec, schema) is { Length: > 0 } t ? $"{r.Key} {t}" : r.Key;
            }))));
        return sb.ToString();
    }

    private static string Schema(JsonObject spec, string name, JsonNode? node)
    {
        var sb = new StringBuilder(Clip(name));
        if (Obj(node)?["properties"] is JsonObject properties)
        {
            var required = (node!["required"] as JsonArray)?.Select(r => (r as JsonValue)?.ToString()).ToHashSet(StringComparer.Ordinal) ?? [];
            sb.Append(": ").Append(Clip(string.Join(", ", properties.Take(PropertiesShown).Select(p =>
                $"{p.Key}{(required.Contains(p.Key) ? "*" : "")} {TypeOf(spec, p.Value)}".TrimEnd()))));
            if (properties.Count > PropertiesShown) sb.Append(CultureInfo.InvariantCulture, $", … +{properties.Count - PropertiesShown} more");
        }
        else if (TypeOf(spec, node) is { Length: > 0 } type) sb.Append(": ").Append(Clip(type));
        return sb.ToString();
    }

    /// <summary>A schema's type as one word: a reference by its name, an array by its items, a composition by its parts, an
    /// enum by its values.</summary>
    private static string TypeOf(JsonObject spec, JsonNode? schema, int depth = 0)
    {
        if (Obj(schema) is not { } o || depth > 4) return string.Empty;
        if (Str(o["$ref"]) is { Length: > 0 } reference) return reference[(reference.LastIndexOf('/') + 1)..];
        foreach (var composed in new[] { "oneOf", "anyOf", "allOf" })
            if (o[composed] is JsonArray parts)
                return composed + "(" + string.Join(composed == "allOf" ? "&" : "|", parts.Take(6).Select(p => TypeOf(spec, p, depth + 1))) + ")";
        if (Str(o["type"]) == "array") return TypeOf(spec, o["items"], depth + 1) + "[]";
        if (o["enum"] is JsonArray values) return string.Join("|", values.Take(6).Select(v => (v as JsonValue)?.ToString())) + (values.Count > 6 ? "|…" : "");
        var type = Str(o["type"]);
        return Str(o["format"]) is { Length: > 0 } format ? $"{type}({format})" : type;
    }

    /// <summary><paramref name="node"/>, or the definition its local <c>$ref</c> points to (a few hops at most); <c>null</c>
    /// when it points outside the file or nowhere.</summary>
    private static JsonObject? Resolve(JsonObject spec, JsonObject? node)
    {
        for (var hop = 0; hop < 5 && node is not null; hop++)
        {
            if (Str(node["$ref"]) is not { Length: > 0 } reference) return node;
            if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
            JsonNode? at = spec;
            foreach (var part in reference[2..].Split('/'))
                at = Obj(at)?[part.Replace("~1", "/").Replace("~0", "~")];
            node = Obj(at);
        }
        return node;
    }

    // ── HAR ──

    private static string? Har(string name, JsonObject har, int budget, CancellationToken ct)
    {
        if (Obj(har["log"])?["entries"] is not JsonArray entries) return null;
        var inv = CultureInfo.InvariantCulture;
        var requests = entries.Select(Obj).OfType<JsonObject>()
            .Select((e, i) => (Index: i + 1, Entry: e, Status: Num(Obj(e["response"])?["status"]) is { } n && n is > 0 and < 1000 ? (int)n : 0))
            .ToList();
        // No status, or none a server gives (an aborted request is written -1 or 0): unanswered, listed with the failed.
        static bool Failed(int status) => status is 0 or >= 400;
        var failed = requests.Count(r => Failed(r.Status));

        var sb = new StringBuilder();
        sb.Append(inv, $"[Network capture {name} (HAR): {requests.Count} request(s), {failed} failed or unanswered. ");
        sb.Append("Its requests, the failed ones first. A summary, not a protection: headers and cookies are not part of it, and ");
        sb.Append("values that look like credentials are masked as far as recognised, but the file holds everything as captured; ");
        sb.Append("read its JSON with start_line=1.]\n");
        var ordered = requests.Where(r => Failed(r.Status)).Concat(requests.Where(r => !Failed(r.Status)));
        var lines = new List<string>();
        foreach (var r in ordered)
        {
            ct.ThrowIfCancellationRequested();
            lines.Add(Request(r.Index, r.Entry, r.Status));
        }
        var shown = Fill(sb, "\n", lines, budget);
        if (shown < lines.Count)
            sb.Append(inv, $"\n{lines.Count - shown} more request(s) not shown here: search_in_files finds an address in the JSON.\n");
        return sb.ToString().TrimEnd();
    }

    private static string Request(int index, JsonObject entry, int status)
    {
        var inv = CultureInfo.InvariantCulture;
        var request = Obj(entry["request"]);
        var response = Obj(entry["response"]);
        var content = Obj(response?["content"]);
        var sb = new StringBuilder();
        sb.Append(inv, $"{index}. {Clip(Str(request?["method"]))} {Mask(Str(request?["url"]), LineChars)} → ");
        sb.Append(status == 0 ? "no answer" : $"{status} {Clip(Str(response?["statusText"]))}".TrimEnd());
        if (Num(entry["time"]) is { } ms) sb.Append(inv, $" · {Math.Round(ms)} ms");
        if (Str(content?["mimeType"]) is { Length: > 0 } mime) sb.Append(" · ").Append(Clip(mime));
        if (Str(Obj(request?["postData"])?["text"]) is { Length: > 0 } sent) sb.Append("\n  sent: ").Append(Mask(sent, BodyChars));
        // A body the capture encoded (an image, a zip) is named, never shown as its base64.
        if (Str(content?["text"]) is { Length: > 0 } got)
            sb.Append("\n  got: ").Append(Str(content?["encoding"]) == "base64" ? "(encoded content, not shown)" : Mask(got, BodyChars));
        return sb.ToString();
    }

    /// <summary>The start of <paramref name="text"/> on one line, what looks like a credential masked (best effort), and how
    /// long it was when it is cut. Cut BEFORE it is read: a body of megabytes is never split whole for 300 characters.</summary>
    private static string Mask(string text, int max) => OneLine.Masked(text, max);

    // ── Shared ──

    /// <summary>Appends <paramref name="heading"/> then the items that fit in <paramref name="budget"/>, cut between two of
    /// them; the first one shows even alone, cut to the budget. How many were appended.</summary>
    private static int Fill(StringBuilder sb, string heading, IReadOnlyList<string> items, int budget)
    {
        if (items.Count == 0) return 0;
        sb.Append(heading);
        var shown = 0;
        foreach (var item in items)
        {
            var room = budget - 200 - sb.Length;
            if (item.Length > room && shown > 0) break;
            sb.Append(item.Length > room ? item[..Math.Max(0, room)] + " … (cut to fit this read)" : item).Append('\n');
            shown++;
        }
        return shown;
    }

    private static JsonObject? Obj(JsonNode? node) => node as JsonObject;

    private static string Str(JsonNode? node) =>
        node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToString()) : string.Empty;

    private static double? Num(JsonNode? node) =>
        node is JsonValue v && (v.TryGetValue<double>(out var d) || double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            ? d : null;

    /// <summary>One line of at most <see cref="LineChars"/> characters.</summary>
    private static string Clip(string text)
    {
        var start = text.Length > LineChars * 2 ? text[..(LineChars * 2)] : text;
        var line = OneLine.Flat(start);
        return line.Length <= LineChars ? line : line[..LineChars] + "…";
    }
}
