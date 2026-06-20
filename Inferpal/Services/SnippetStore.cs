using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Localization;

namespace Inferpal.Services;

internal record Snippet(
    string Id,
    string Language,
    string Code,
    string CreatedAt);

internal static class SnippetStore
{
    private const int MaxSnippets = 100;

    private static readonly string _defaultFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Inferpal", "snippets.json");

    // Overridden in tests to avoid writing to %APPDATA%.
    internal static string? _fileOverride;
    private static string FilePath => _fileOverride ?? _defaultFile;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task SaveAsync(string language, string code, CancellationToken ct)
    {
        var snippets = await LoadAllAsync(ct);
        snippets.Add(new Snippet(
            Guid.NewGuid().ToString("N")[..8],
            language,
            code,
            DateTime.Now.ToString("s")));

        if (snippets.Count > MaxSnippets)
            snippets.RemoveAt(0);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(snippets, _opts), ct);
    }

    public static async Task<List<Snippet>> LoadAllAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            var json = await File.ReadAllTextAsync(FilePath, ct);
            return JsonSerializer.Deserialize<List<Snippet>>(json, _opts) ?? [];
        }
        catch { return []; }
    }

    public static async Task DeleteAsync(int index, CancellationToken ct)
    {
        var snippets = await LoadAllAsync(ct);
        if (index < 0 || index >= snippets.Count) return;
        snippets.RemoveAt(index);
        await File.WriteAllTextAsync(FilePath, JsonSerializer.Serialize(snippets, _opts), ct);
    }

    public static async Task ClearAsync(CancellationToken ct)
    {
        if (File.Exists(FilePath))
            await File.WriteAllTextAsync(FilePath, "[]", ct);
    }

    /// <summary>
    /// The <c>/snippets</c> listing: 1-based index, language, one-line code preview,
    /// save date, and the ready-to-type copy/delete sub-commands.
    /// </summary>
    public static string FormatList(IReadOnlyList<Snippet> snippets)
    {
        var sb = new System.Text.StringBuilder(Strings.SnippetsListHeader + "\n\n");
        for (var n = 0; n < snippets.Count; n++)
        {
            var s    = snippets[n];
            var lang = string.IsNullOrEmpty(s.Language) ? "" : $" ({s.Language})";
            sb.AppendLine($"**#{n + 1}**{lang} — `{ChatTurnPolicy.OneLinePreview(s.Code, 60)}`  ");
            sb.AppendLine($"  {Strings.SnippetsSavedAt(s.CreatedAt)} — `/snippets copy {n + 1}` • `/snippets delete {n + 1}`");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
