using System.IO;
using System.Text;

namespace Inferpal.Services.Persistence;

/// <summary>
/// Project notes (<c>/note</c>, <c>/notes</c>): a timestamped markdown bullet list in
/// <c>.inferpal/notes.md</c>, injected into the system prompt by
/// <see cref="SystemPromptBuilder"/>.
/// </summary>
internal static class NotesStore
{
    public static string NotesPath(string projectRoot) =>
        Path.Combine(projectRoot, ".inferpal", "notes.md");

    /// <summary>One note as a markdown bullet: <c>- [yyyy-MM-dd HH:mm] text</c>.</summary>
    public static string FormatLine(string text, DateTime now) =>
        $"- [{now:yyyy-MM-dd HH:mm}] {text}\n";

    public static async Task AppendAsync(
        string projectRoot, string text, DateTime now, CancellationToken ct)
    {
        var path = NotesPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, FormatLine(text, now), Encoding.UTF8, ct);
    }

    /// <summary>
    /// Trimmed notes content; null when the file doesn't exist, empty when it exists
    /// but holds nothing — the two states get different <c>/notes</c> messages.
    /// </summary>
    public static async Task<string?> ReadAsync(string projectRoot, CancellationToken ct)
    {
        var path = NotesPath(projectRoot);
        if (!File.Exists(path)) return null;
        return (await File.ReadAllTextAsync(path, ct)).Trim();
    }

    public static void Clear(string projectRoot)
    {
        var path = NotesPath(projectRoot);
        if (File.Exists(path)) File.Delete(path);
    }
}
