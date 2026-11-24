using System.Text.Json;

namespace Inferpal.Services;

/// <summary>
/// Reading what a program wrote as JSON — a browser's DevTools, a forge's API, a request collection — never the model's
/// arguments, which <see cref="Tools.ToolArgs"/> reads in their own, laxer dialect (a number written bare is text there).
/// </summary>
internal static class JsonFields
{
    /// <summary>The string property <paramref name="name"/> of <paramref name="e"/>; empty when <paramref name="e"/> is not an
    /// object, or the property is absent or not a string.</summary>
    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
