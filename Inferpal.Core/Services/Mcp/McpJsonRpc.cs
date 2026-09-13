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
        if (result.TryGetProperty("tools", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in arr.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                var desc = t.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() ?? string.Empty
                    : string.Empty;

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
        var sb = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt))
                    sb.AppendLine(txt.GetString());
                else if (type == "resource" && block.TryGetProperty("resource", out var res)
                         && res.TryGetProperty("text", out var rtxt))
                    sb.AppendLine(rtxt.GetString());
            }
        }

        var text = sb.ToString().TrimEnd();
        var isError = result.TryGetProperty("isError", out var err) && err.ValueKind == JsonValueKind.True;
        if (isError)
            return $"MCP tool '{toolName}' reported an error: {text}";

        return text.Length == 0 ? "(no output)" : text;
    }

    /// <summary>A standalone, detached empty JSON object (<c>{}</c>).</summary>
    public static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
