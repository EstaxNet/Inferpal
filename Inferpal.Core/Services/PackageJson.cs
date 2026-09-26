using System.IO;
using System.Text.Json;

namespace Inferpal.Services;

/// <summary>What a Node project's package.json declares, read without trusting its shape.</summary>
internal static class PackageJson
{
    /// <summary>The <c>test</c> script of <paramref name="dir"/>'s package.json; <c>null</c> when there is none to read.</summary>
    internal static string? TestScript(string dir)
    {
        var file = Path.Combine(dir, "package.json");
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                && scripts.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.String
                ? test.GetString()
                : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("PackageJson.TestScript", ex);
            return null;
        }
    }
}
