using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// The JSON Schema of each tool a request offered, by name — what decides the type of a value the model wrote.
/// </summary>
/// <remarks>
/// One reader for every client and every place a call is read from text: the streamed-arguments watcher, the calls
/// recovered from the content, and those recovered from the reasoning. A schema is serialised once per request.
/// </remarks>
internal static class ToolSchemas
{
    /// <summary>A lookup over <paramref name="defs"/>; <c>null</c> for a tool the request did not offer.</summary>
    public static Func<string, JsonElement?> Of(IReadOnlyList<ToolDefinition>? defs)
    {
        var cache = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        return name => cache.TryGetValue(name, out var known) ? known : cache[name] = Read(defs, name);
    }

    private static JsonElement? Read(IReadOnlyList<ToolDefinition>? defs, string name) =>
        defs?.FirstOrDefault(d => d.Function.Name == name) is { } def
            ? JsonSerializer.SerializeToElement(def.Function.Parameters)
            : null;
}
