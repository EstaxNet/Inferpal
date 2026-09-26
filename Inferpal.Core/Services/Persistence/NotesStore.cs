using System.IO;

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

    /// <summary>
    /// Appends one note, in the file's own encoding. Returns <c>null</c> when it was written, or the character the
    /// file's legacy code page cannot hold and that code page's name — then nothing was written.
    /// </summary>
    /// <remarks>
    /// ⚠ The file is written BY HAND too, and an older editor saves it in the machine's legacy code page. Appended in
    /// UTF-8, a note makes the file valid in neither encoding: read back in the code page — the user's bytes are not
    /// UTF-8 — the new note reaches the system prompt as "dÃ©cision". And a character that code page lacks would be
    /// written as "?" or a look-alike: refused by name; converting the file is the user's decision.
    /// </remarks>
    public static async Task<(string Character, string Encoding)?> AppendAsync(
        string projectRoot, string text, DateTime now, CancellationToken ct)
    {
        var path = NotesPath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = FormatLine(text, now);
        if (!File.Exists(path))
        {
            await Tools.SafeFileWriter.WritePreservingAsync(path, line, ct);
            return null;
        }

        var encoding = Tools.TextFileEncoding.Detect(path);
        if (Tools.TextFileEncoding.FirstUnrepresentable(encoding, line) is { } bad)
            return (bad.Character, encoding.WebName);

        var existing = await Tools.TextFileEncoding.ReadTextAsync(path, ct);
        var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
        await Tools.SafeFileWriter.WritePreservingAsync(path, existing + separator + line, ct);
        return null;
    }

    /// <summary>
    /// Trimmed notes content; null when the file doesn't exist, empty when it exists
    /// but holds nothing — the two states get different <c>/notes</c> messages.
    /// </summary>
    public static async Task<string?> ReadAsync(string projectRoot, CancellationToken ct)
    {
        var path = NotesPath(projectRoot);
        if (!File.Exists(path)) return null;
        return (await Tools.TextFileEncoding.ReadTextAsync(path, ct)).Trim();
    }

    public static void Clear(string projectRoot)
    {
        var path = NotesPath(projectRoot);
        if (File.Exists(path)) File.Delete(path);
    }
}
