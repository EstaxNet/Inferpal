using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Mcp;

/// <summary>
/// Transport-agnostic parsing of MCP JSON-RPC payloads, shared by the stdio and HTTP clients so the
/// <c>tools/list</c> and <c>tools/call</c> result shapes are interpreted identically on both.
/// </summary>
internal static class McpJsonRpc
{
    /// <summary>
    /// Reads a message's JSON-RPC id as this client issues them (numbers), tolerating a server that
    /// echoes it as a numeric string.
    /// </summary>
    /// <remarks>⚠ ValueKind first: <c>TryGetInt64</c> THROWS on a non-number — a string or null id killed
    /// the stdio read loop (every pending call failed, reconnect) and failed the HTTP call.</remarks>
    internal static bool TryReadId(JsonElement message, out long id)
    {
        id = 0;
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var el)) return false;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out id),
            JsonValueKind.String => long.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer,
                                                  System.Globalization.CultureInfo.InvariantCulture, out id),
            _                    => false,
        };
    }

    /// <summary>
    /// <c>true</c> for a message the SERVER initiated — a notification or a request (it carries
    /// <c>method</c>): never the response to one of our calls, even when its id matches one.
    /// </summary>
    internal static bool IsServerMessage(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("method", out _);

    /// <summary>Parses a <c>tools/list</c> result into tool infos. Entries without a name are skipped;
    /// a missing/!object schema falls back to <c>{}</c>. Schemas are cloned to outlive the source document.</summary>
    public static IReadOnlyList<McpToolInfo> ParseTools(JsonElement result)
    {
        var tools = new List<McpToolInfo>();
        // Kinds first: a server can send anything, and TryGetProperty throws on a non-object, GetString on a
        // non-string — one bad entry failed the whole listing, and every tool of the server was lost.
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = StringProperty(t, "name");
                if (string.IsNullOrEmpty(name)) continue;

                var desc = StringProperty(t, "description") ?? string.Empty;

                var schema = t.TryGetProperty("inputSchema", out var s) && s.ValueKind == JsonValueKind.Object
                    ? s.Clone()
                    : EmptyObject();

                tools.Add(new McpToolInfo(name!, desc, schema));
            }
        }
        return tools;
    }

    /// <summary>Flattens a <c>tools/call</c> result's content blocks (text + embedded resource text) into a
    /// single string; an <c>isError</c> result is wrapped in an explanatory message.</summary>
    public static string ExtractCallResult(JsonElement result, string toolName)
    {
        var sb      = new StringBuilder();
        var dropped = new List<string>();
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                // A malformed block is skipped: it failed the whole call, and the text of the others was lost.
                var type = StringProperty(block, "type");
                if (string.IsNullOrEmpty(type)) continue;                    // not even a typed block

                if (type == "text")
                {
                    if (StringProperty(block, "text") is { } txt) sb.AppendLine(txt);
                    continue;                                                // a text block with no text is malformed
                }

                if (type == "resource")
                {
                    if (!block.TryGetProperty("resource", out var res) || res.ValueKind != JsonValueKind.Object)
                        continue;                                            // malformed, not a kind
                    if (StringProperty(res, "text") is { } rtxt) sb.AppendLine(rtxt);
                    else dropped.Add("resource");                            // a blob, or a link with no text
                    continue;
                }

                dropped.Add(type);
            }
        }

        var text = sb.ToString().TrimEnd();
        // Named, and named as NOT an empty result: that is the conclusion the model would otherwise draw.
        var note = dropped.Count == 0
            ? string.Empty
            : $"[the tool returned {dropped.Count} content block(s) Inferpal cannot pass on "
            + $"({string.Join(", ", dropped.Distinct(StringComparer.Ordinal))}); this is NOT an empty result]";

        var isError = result.ValueKind == JsonValueKind.Object
                      && result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
        if (isError)
            return $"MCP tool '{toolName}' reported an error: {Join(text, note)}";

        return text.Length == 0 && note.Length == 0 ? "(no output)" : Join(text, note);
    }

    /// <summary>The two halves on one line each, skipping whichever is empty.</summary>
    private static string Join(string text, string note) =>
        text.Length == 0 ? note : note.Length == 0 ? text : text + "\n" + note;

    /// <summary>
    /// The text of a JSON-RPC <c>error</c> member. Not every server sends the <c>{ "message": … }</c> object: a bare
    /// string threw on <c>TryGetProperty</c>, replacing the server's own words with a .NET message — on stdio, inside
    /// the read loop that serves every pending call.
    /// </summary>
    internal static string ErrorMessage(JsonElement error)
    {
        var text = error.ValueKind == JsonValueKind.String ? error.GetString() : StringProperty(error, "message");
        return string.IsNullOrEmpty(text) ? UnknownError : text;
    }

    /// <summary>What <see cref="ErrorMessage"/> answers when the error carries no text.</summary>
    internal const string UnknownError = "unknown error";

    /// <summary>A string member of an object; null when the element is not an object or the member not a string.</summary>
    /// <summary>The cursor of the next page of a paginated listing; <c>null</c> when this page is the last.</summary>
    public static string? NextCursor(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && StringProperty(result, "nextCursor") is { Length: > 0 } cursor
            ? cursor
            : null;

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A standalone, detached empty JSON object (<c>{}</c>).</summary>
    public static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
