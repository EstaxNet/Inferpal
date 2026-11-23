using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Tools;

/// <summary>
/// GraphQL for <c>http_request</c>: the body a document becomes, whether a request may change something (a
/// <c>mutation</c> or a <c>subscription</c>), and the schema an introspection answers, summarized.
/// </summary>
/// <remarks>⚠ "May change something" is judged on every way a request can carry an operation — the document given, every
/// string of a JSON body (escapes undone by the parse, a JSON written inside a string read again), a body that is not JSON
/// once percent-decoded, the URL's query — and fails closed: a GraphQL request whose text is not there (a persisted or
/// stored document named by a hash or an id), a body in another charset than UTF-8 or ASCII, a multipart body, or an
/// escape left after decoding counts as one. A body that merely mentions the word costs a card, never a pass. Not seen: a
/// server whose query fields change data, or a route that runs a stored mutation under a plain address — as a GET with
/// side effects is not.</remarks>
internal static class GraphQlRequest
{
    // Whole words, either case: a server is free to read its own keywords loosely, and a card is cheaper than a guess.
    private static readonly Regex Changing = new(@"(?<![_0-9A-Za-z])(mutation|subscription)(?![_0-9A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>The JSON body of a GraphQL request over HTTP.</summary>
    internal static string Body(string document, JsonElement? variables, string? operationName)
    {
        var body = new Dictionary<string, object?> { ["query"] = document };
        if (variables is { ValueKind: JsonValueKind.Object } v) body["variables"] = v;
        if (!string.IsNullOrWhiteSpace(operationName)) body["operationName"] = operationName;
        return JsonSerializer.Serialize(body);
    }

    // What names a stored GraphQL operation instead of sending its text: a hash or an id.
    private static readonly string[] StoredCarriers = ["operations", "extensions", "documentId", "doc_id", "queryId"];

    // Also a stored operation's name — but only in a request that is GraphQL by its other fields: a REST body or address
    // has an id far too often for an id alone to mean anything.
    private static readonly string[] StoredWhenGraphQl = ["id", "hash"];

    // What makes a JSON object a GraphQL request beyond its carriers.
    private static readonly string[] GraphQlFields = ["variables", "operationName"];

    // An escape a decoding left: a server that decodes once more reads what this check did not (m in JSON, %u006d in a
    // form a .NET Framework server decodes).
    private static readonly Regex LeftEscape = new(@"\\u[0-9A-Fa-f]{4}|%u[0-9A-Fa-f]{4}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexBudget.Default);

    private const string Stored = "it names a stored GraphQL document instead of sending its text, so the operation it runs cannot be read";

    /// <summary>Why the request may change something on the server, or <c>null</c> when nothing it carries says so.</summary>
    /// <param name="contentType">The Content-Type the request is sent with, or <c>null</c>.</param>
    internal static string? MayChange(Uri uri, string? body, string? contentType)
    {
        var query = Uri.UnescapeDataString(uri.Query.Replace('+', ' '));
        if (Says(query)) return "its address names a mutation or a subscription";
        if (StoredIn(System.Web.HttpUtility.ParseQueryString(uri.Query), uri.AbsolutePath)) return Stored;
        if (string.IsNullOrEmpty(body)) return null;
        if (contentType is not null)
        {
            // Read by the parser that sends it: "charset =utf-7" is sent as charset=utf-7.
            if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var parsed))
                return "its Content-Type cannot be read, so neither can how the server reads its body";
            if (parsed.MediaType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
                return "its body is multipart, whose parts are not read here";
            var charset = parsed.CharSet?.Trim('"', ' ').ToLowerInvariant();
            if (!string.IsNullOrEmpty(charset) && charset is not ("utf-8" or "utf8" or "us-ascii" or "ascii"))
                return $"its body is declared in {charset}, which the server reads otherwise than this check";
        }
        // A BOM a server strips before parsing; whitespace a parser skips.
        var trimmed = body.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return Walk(doc.RootElement, request: true);
            }
            // Not JSON to this parser — a GraphQL document ({ orders { id } }), or JSON a lenient parser reads (comments,
            // trailing commas, single quotes): read as text below, where an escape left or a control character fails closed.
            catch (JsonException) { }
        }
        // NULs between letters are UTF-16 a parser may guess at; other control characters, a decoding this check skips.
        if (body.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n')))
            return "its body holds control characters, so how the server reads it cannot be told";
        string decoded;
        try { decoded = Uri.UnescapeDataString(body.Replace('+', ' ')); }
        catch (UriFormatException) { decoded = body; }
        if (Says(body) || Says(decoded)) return "its body names a mutation or a subscription";
        if (RegexBudget.IsMatch(LeftEscape, body) || RegexBudget.IsMatch(LeftEscape, decoded))
            return "its body holds an escape left after decoding, which a server may decode again";
        // A form body (query=…, documentId=…): the same carriers as an address.
        return StoredIn(System.Web.HttpUtility.ParseQueryString(body), uri.AbsolutePath) ? Stored : null;
    }

    /// <summary>Whether form fields (an address's or a form body's) name a stored operation with no text beside it. Names
    /// are read in any case, as the server's binder reads them.</summary>
    private static bool StoredIn(System.Collections.Specialized.NameValueCollection fields, string path)
    {
        if (!string.IsNullOrWhiteSpace(fields["query"])) return false;
        if (StoredCarriers.Any(c => fields[c] is not null)) return true;
        var graphQl = path.Contains("graphql", StringComparison.OrdinalIgnoreCase) || GraphQlFields.Any(c => fields[c] is not null)
                      || fields["query"] is not null;
        return graphQl && StoredWhenGraphQl.Any(c => fields[c] is not null);
    }

    private static bool Says(string text) => text.Length > 0 && RegexBudget.IsMatch(Changing, text);

    /// <summary>A property of <paramref name="e"/> read in any case, as a server's binder may.</summary>
    private static bool Has(JsonElement e, string name, out JsonElement value)
    {
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        value = default;
        return false;
    }

    /// <summary>Whether a body is a GraphQL request \u2014 a JSON object, or a batch of them, carrying its document as
    /// <c>query</c> text \u2014 so that its answer is read as one.</summary>
    internal static bool IsGraphQlBody(string? body)
    {
        var trimmed = body?.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (string.IsNullOrEmpty(trimmed) || !(trimmed.StartsWith('{') || trimmed.StartsWith('['))) return false;
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var first = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
            return first.ValueKind == JsonValueKind.Object && first.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }

    /// <param name="request">Whether <paramref name="e"/> is where a GraphQL request object stands \u2014 the body, an item of a
    /// batch, a JSON written inside a string \u2014 and not a value under it (variables, a REST field named extensions).</param>
    private static string? Walk(JsonElement e, bool request)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.ToString();
                if (Says(s)) return "its body names a mutation or a subscription";
                if (RegexBudget.IsMatch(LeftEscape, s)) return "its body holds an escape left after decoding, which a server may decode again";
                // A JSON document written as a string inside the body: read it too. A GraphQL document starting with a
                // brace is not JSON: already judged by its words above.
                var inner = s.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                if (inner.StartsWith('{') || inner.StartsWith('['))
                    try { using var doc = JsonDocument.Parse(inner); return Walk(doc.RootElement, request: true); }
                    catch (JsonException) { }
                return null;
            case JsonValueKind.Object:
                // A GraphQL request is a plain query only when its text is there: a persisted query (a hash) or a stored
                // document (an id) runs an operation nobody here can read. An empty query is no text: servers look the
                // stored one up.
                var text = Has(e, "query", out var q) && q.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(q.ToString());
                if (request && !text)
                {
                    if (StoredCarriers.Any(c => Has(e, c, out _))) return Stored;
                    var graphQl = Has(e, "query", out _) || GraphQlFields.Any(c => Has(e, c, out _));
                    if (graphQl && StoredWhenGraphQl.Any(c => Has(e, c, out _))) return Stored;
                }
                foreach (var p in e.EnumerateObject())
                {
                    if (Says(p.Name)) return "its body names a mutation or a subscription";
                    if (Walk(p.Value, request: false) is { } why) return why;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    if (Walk(item, request) is { } why) return why;   // a batch: each item a request
                return null;
            default:
                return null;
        }
    }

    /// <summary>The introspection query: the schema's root types and every named type with its fields, arguments, inputs
    /// and values — types read four levels deep (lists and non-nulls).</summary>
    internal const string IntrospectionQuery =
        "query IntrospectionQuery { __schema { queryType { name } mutationType { name } subscriptionType { name } " +
        "types { kind name description fields(includeDeprecated: true) { name description isDeprecated args { name type { ...T } defaultValue } type { ...T } } " +
        "inputFields { name type { ...T } defaultValue } enumValues(includeDeprecated: true) { name isDeprecated } } } } " +
        "fragment T on __Type { kind name ofType { kind name ofType { kind name ofType { kind name ofType { kind name } } } } }";

    private const int DescriptionChars = 160, MaxArgs = 20;

    // A GraphQL name: anything else a server returns as a name is not written as one.
    private static readonly Regex Name = new("^[_A-Za-z][_0-9A-Za-z]{0,127}$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>A property's text as the server wrote it, or <c>null</c> when it is missing or not a string: an answer
    /// of another shape is read as far as it goes, never thrown on.</summary>
    private static string? Text(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.ToString() : null;

    private static string Named(JsonElement e, string property) =>
        Text(e, property) is { } s && RegexBudget.IsMatch(Name, s) ? s : "?";

    /// <summary><c>[Order!]!</c> from an introspected type reference.</summary>
    internal static string TypeOf(JsonElement t)
    {
        if (t.ValueKind != JsonValueKind.Object) return "?";
        var kind = Text(t, "kind");
        var inner = t.TryGetProperty("ofType", out var o) && o.ValueKind == JsonValueKind.Object ? o : default;
        return kind switch
        {
            "NON_NULL" => TypeOf(inner) + "!",
            "LIST" => "[" + TypeOf(inner) + "]",
            _ => Named(t, "name"),
        };
    }

    private static string Description(JsonElement e)
    {
        if (Text(e, "description") is not { } d) return string.Empty;
        var flat = string.Join(' ', d.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        flat = new string(flat.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        if (flat.Length == 0) return string.Empty;
        return " — " + (flat.Length > DescriptionChars ? flat[..(DescriptionChars - 1)] + "…" : flat);
    }

    private static string Field(JsonElement f)
    {
        var sb = new StringBuilder(Named(f, "name"));
        if (f.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0)
        {
            var all = args.EnumerateArray().ToList();
            sb.Append('(').AppendJoin(", ", all.Take(MaxArgs).Select(a => Named(a, "name") + ": " + TypeOf(a.ValueKind == JsonValueKind.Object && a.TryGetProperty("type", out var at) ? at : default)))
              .Append(all.Count > MaxArgs ? $", … {all.Count - MaxArgs} more" : "").Append(')');
        }
        sb.Append(": ").Append(TypeOf(f.TryGetProperty("type", out var t) ? t : default));
        if (f.TryGetProperty("isDeprecated", out var dep) && dep.ValueKind == JsonValueKind.True) sb.Append(" (deprecated)");
        return sb.Append(Description(f)).ToString();
    }

    /// <summary>The schema an introspection answered, within <paramref name="budget"/> characters: the root operations
    /// first (mutations and subscriptions said to be asked on a card), then the other types; <c>null</c> when the answer
    /// holds no schema (introspection off, an error).</summary>
    internal static string? Schema(string answer, int budget)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(answer); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("__schema", out var schema) || schema.ValueKind != JsonValueKind.Object
                || !schema.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
                return null;
            string? Root(string p) => schema.TryGetProperty(p, out var r) && r.ValueKind == JsonValueKind.Object ? Named(r, "name") : null;
            var roots = new[] { ("Queries", Root("queryType")), ("Mutations (each sent on a card)", Root("mutationType")),
                                ("Subscriptions (each sent on a card)", Root("subscriptionType")) };
            var all = types.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object).ToList();
            var byName = all.GroupBy(t => Named(t, "name")).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var rootNames = roots.Select(r => r.Item2).Where(n => n is not null).ToHashSet(StringComparer.Ordinal);

            var sb = new StringBuilder($"GraphQL schema ({all.Count} types). Root operations first, then the other types; built-in types (__*) and scalars left out.\n");
            var listed = 0;
            var others = all.Where(t => Named(t, "name") is var n && !n.StartsWith("__", StringComparison.Ordinal) && !rootNames.Contains(n)
                                        && Text(t, "kind") is "OBJECT" or "INPUT_OBJECT" or "ENUM" or "INTERFACE" or "UNION")
                            .OrderBy(t => Named(t, "name"), StringComparer.Ordinal).ToList();
            foreach (var (title, name) in roots)
            {
                if (name is null || !byName.TryGetValue(name, out var type)) continue;
                var fields = type.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array ? fs.EnumerateArray().ToList() : [];
                sb.Append($"\n## {title} — {name}, {fields.Count}\n");
                var told = 0;
                foreach (var f in fields)
                {
                    if (told > 0 && sb.Length > budget) break;
                    sb.Append("- ").Append(Field(f)).Append('\n');
                    told++;
                }
                if (told < fields.Count) sb.Append($"… {fields.Count - told} more field(s), not listed: the page is full.\n");
            }
            sb.Append($"\n## Types ({others.Count})\n");
            foreach (var t in others)
            {
                if (sb.Length > budget) break;
                var kind = Text(t, "kind")!;
                sb.Append($"- {kind.ToLowerInvariant().Replace('_', ' ')} {Named(t, "name")}");
                IEnumerable<string> members = kind switch
                {
                    "ENUM" => t.TryGetProperty("enumValues", out var ev) && ev.ValueKind == JsonValueKind.Array ? ev.EnumerateArray().Select(v => Named(v, "name")) : [],
                    "INPUT_OBJECT" => t.TryGetProperty("inputFields", out var inf) && inf.ValueKind == JsonValueKind.Array
                        ? inf.EnumerateArray().Select(f => Named(f, "name") + ": " + TypeOf(f.TryGetProperty("type", out var ft) ? ft : default)) : [],
                    _ => t.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array
                        ? fs.EnumerateArray().Select(f => Named(f, "name") + ": " + TypeOf(f.TryGetProperty("type", out var ft) ? ft : default)) : [],
                };
                var list = members.ToList();
                sb.Append(list.Count > 0 ? " { " + string.Join(", ", list.Take(30)) + (list.Count > 30 ? $", … {list.Count - 30} more" : "") + " }" : "").Append('\n');
                listed++;
            }
            if (listed < others.Count) sb.Append($"… {others.Count - listed} more type(s), not listed: the page is full.\n");
            return sb.ToString();
        }
    }

    /// <summary>A line on a GraphQL answer that carries <c>errors</c>: a server answers them under HTTP 200.</summary>
    internal static string? Errors(string answer)
    {
        try
        {
            using var doc = JsonDocument.Parse(answer);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return null;
            var first = Text(errors[0], "message") ?? "";
            first = string.Join(' ', first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            first = first.TrimEnd('.');
            if (first.Length > 200) first = first[..199] + "…";
            return $"(GraphQL: the answer carries {errors.GetArrayLength()} error(s), whatever its HTTP status{(first.Length > 0 ? " — first: " + first : "")}.)";
        }
        catch (JsonException) { return null; }
    }
}
