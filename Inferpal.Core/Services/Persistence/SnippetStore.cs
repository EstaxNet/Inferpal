using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inferpal.Localization;

namespace Inferpal.Services.Persistence;

internal record Snippet(
    string Id,
    string Language,
    string Code,
    string CreatedAt);

internal static class SnippetStore
{
    private const int MaxSnippets = 100;

    private static readonly AppDataJsonFile<List<Snippet>> _file = new("snippets.json", "SnippetStore", preserveUnreadable: true);

    // Overridden in tests to avoid writing to %APPDATA%.
    internal static string? _fileOverride
    {
        get => _file.PathOverride;
        set => _file.PathOverride = value;
    }

    // Save, Delete and Clear return false when nothing was written: a caller that announces the change
    // must check it.
    public static async Task<bool> SaveAsync(string language, string code, CancellationToken ct)
    {
        var snippets = await LoadAllAsync(ct);
        snippets.Add(new Snippet(
            Guid.NewGuid().ToString("N")[..8],
            language,
            code,
            DateTime.Now.ToString("s")));

        if (snippets.Count > MaxSnippets)
            snippets.RemoveAt(0);

        return await _file.SaveAsync(snippets, ct);
    }

    public static Task<List<Snippet>> LoadAllAsync(CancellationToken ct) =>
        _file.LoadAsync([], ct: ct);

    /// <returns><c>true</c> when written, or when the index names no snippet (nothing to write).</returns>
    public static async Task<bool> DeleteAsync(int index, CancellationToken ct)
    {
        var snippets = await LoadAllAsync(ct);
        if (index < 0 || index >= snippets.Count) return true;
        snippets.RemoveAt(index);
        return await _file.SaveAsync(snippets, ct);
    }

    public static Task<bool> ClearAsync(CancellationToken ct) => _file.SaveAsync([], ct);

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
            // By id, not position: a position shifts after the first delete, and the next command copied
            // from this same listing would target another snippet.
            var target = string.IsNullOrWhiteSpace(s.Id) ? (n + 1).ToString() : s.Id;
            sb.AppendLine($"  {Strings.SnippetsSavedAt(s.CreatedAt)} — `/snippets copy {target}` • `/snippets delete {target}`");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }
}
