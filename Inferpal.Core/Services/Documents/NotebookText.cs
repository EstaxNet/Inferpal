using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Documents;

/// <summary>
/// The text of a Jupyter notebook (<c>.ipynb</c>): its cells in order — Markdown as written, code in a fenced block in the
/// notebook's language, each code cell's outputs after it (text, results, errors) — instead of the JSON around them, whose
/// escaped lines and base64 images fill a model's window with noise.
/// </summary>
internal static class NotebookText
{
    /// <summary>One output kept at most: a cell that printed a log keeps its beginning and says what it left out.</summary>
    internal const int MaxOutputChars = 4_000;

    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            return new(string.Empty, 0, 0, DocumentProblem.Unreadable);
        var language = Language(root);
        var text = new StringBuilder();
        var count = 0;
        var stoppedAt = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            count++;
            var type = cell.TryGetProperty("cell_type", out var t) ? t.GetString() : null;
            var source = Joined(cell, "source");
            if (type == "code")
            {
                if (cell.TryGetProperty("execution_count", out var n) && n.ValueKind == JsonValueKind.Number)
                    text.Append("In [").Append(n.GetInt32().ToString(CultureInfo.InvariantCulture)).Append("]:\n");
                text.Append("```").Append(language).Append('\n').Append(source.TrimEnd()).Append("\n```\n");
                if (cell.TryGetProperty("outputs", out var outputs) && outputs.ValueKind == JsonValueKind.Array)
                    foreach (var output in outputs.EnumerateArray())
                        if (Output(output) is { Length: > 0 } shown) text.Append("Output:\n").Append(shown.TrimEnd()).Append('\n');
            }
            else if (source.Trim().Length > 0) text.Append(source.TrimEnd()).Append('\n');
            text.Append('\n');
            if (text.Length > PdfText.MaxChars) { stoppedAt = count; break; }
        }
        var read = text.ToString().Trim();
        if (read.Length == 0) return new(string.Empty, 0, 0, DocumentProblem.NoText);
        return new(read.Length > PdfText.MaxChars ? read[..PdfText.MaxChars] : read, 0, 0, DocumentProblem.None, stoppedAt);
    }

    private static string Language(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var meta) || meta.ValueKind != JsonValueKind.Object) return string.Empty;
        if (meta.TryGetProperty("language_info", out var info) && info.ValueKind == JsonValueKind.Object
            && info.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) return name.GetString() ?? string.Empty;
        if (meta.TryGetProperty("kernelspec", out var spec) && spec.ValueKind == JsonValueKind.Object
            && spec.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String) return lang.GetString() ?? string.Empty;
        return string.Empty;
    }

    /// <summary>What an output shows as text: a stream, a result's plain text, an error's name and message; an image is
    /// named, never its bytes.</summary>
    internal static string Output(JsonElement output)
    {
        var kind = output.TryGetProperty("output_type", out var k) ? k.GetString() : null;
        string shown;
        if (kind == "stream") shown = Joined(output, "text");
        else if (kind == "error")
            shown = (output.TryGetProperty("ename", out var e) ? e.GetString() : "Error") + ": " +
                    (output.TryGetProperty("evalue", out var v) ? v.GetString() : string.Empty);
        else if (output.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            shown = data.TryGetProperty("text/plain", out _) ? Joined(data, "text/plain") : string.Empty;
            if (data.EnumerateObject().Any(p => p.Name.StartsWith("image/", StringComparison.Ordinal)))
                shown = "[an image, not included]" + (shown.Length > 0 ? "\n" + shown : string.Empty);
        }
        else shown = string.Empty;
        return shown.Length > MaxOutputChars
            ? shown[..MaxOutputChars] + string.Create(CultureInfo.InvariantCulture, $"\n[output cut: {shown.Length - MaxOutputChars:N0} more characters not included]")
            : shown;
    }

    /// <summary>A notebook string: one string, or an array of lines (each ending with its own line break).</summary>
    private static string Joined(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var value)) return string.Empty;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
        if (value.ValueKind != JsonValueKind.Array) return string.Empty;
        var joined = new StringBuilder();
        foreach (var part in value.EnumerateArray())
            if (part.ValueKind == JsonValueKind.String) joined.Append(part.GetString());
        return joined.ToString();
    }
}
