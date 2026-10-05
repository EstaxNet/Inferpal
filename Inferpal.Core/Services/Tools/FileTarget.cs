using System.IO;

namespace Inferpal.Services.Tools;

/// <summary>
/// What a tool that writes a FILE answers when its path names a directory.
/// </summary>
/// <remarks>
/// ⚠ Called before the approval prompt, by every tool that writes a file path. Without it <c>write_file</c> had the
/// human approve a write "to a folder", then answered "Access … is denied. The arguments are not the cause … continue
/// without this tool" — false, since the path IS the cause, and the one advice that makes the model give up writing;
/// <c>apply_diff</c>, <c>delete_file</c> and <c>restore_file</c> answered "file not found" for a path that exists.
/// Model-facing and corrective: not localized, like the other refusals of an argument.
/// </remarks>
internal static class FileTarget
{
    /// <summary>
    /// The refusal for replacing an existing, non-empty file the model has not read in the current run; <c>null</c>
    /// otherwise, and always outside a run (nobody tracks reads there).
    /// </summary>
    /// <remarks>
    /// ⚠ A whole-file write is the one edit that does not have to quote what it changes: <c>apply_diff</c> must match
    /// its <c>old_content</c>, <c>write_file</c> replaces everything. Without this, a model asked to change a file it
    /// has not opened writes the file from its idea of one: what it did not know was there is gone, and the project
    /// may well still build. Refused before the approval prompt, with the gesture that works.
    /// <para>
    /// ⚠ Read means EVERY line: a long file comes back a page at a time, and a first page is not the file. A file read
    /// in part is named with the line to read on from.
    /// </para>
    /// </remarks>
    public static string? UnreadRefusal(FileHistoryService history, string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0 || history.WasRead(path) != false) return null;
        return history.PartialRead(path) is { } part
            ? $"'{path}' has only been read in part in this run ({part.Seen} of {part.Total} lines): write_file "
            + "replaces the whole file, including every line you have not seen. Read on with read_file "
            + $"(start_line={part.FirstUnseen}), or change only part of it with apply_diff."
            : $"'{path}' already exists and has not been read in this run: write_file replaces the whole file, "
            + "including every line you have not seen. Read it with read_file first, or change only part of it "
            + "with apply_diff.";
    }

    /// <summary>The refusal for <paramref name="path"/> when it is a directory, else <c>null</c>.</summary>
    public static string? DirectoryRefusal(string path) =>
        Directory.Exists(path)
            ? $"'{path}' is a directory, not a file — give the path of a file (inside it, for a new one)."
            : null;

    /// <summary>
    /// The refusal for writing <paramref name="path"/> while the editor holds unsaved changes to it; <c>null</c>
    /// otherwise, and always when the editor mirrors no buffer (<paramref name="overlay"/> null: Visual Studio).
    /// </summary>
    /// <remarks>
    /// ⚠ read_file shows the unsaved buffer, and every writing tool reads and writes the DISK: the model quoted a line
    /// it had just read and was told "old_content not found", and an edit that did apply landed under a dirty buffer
    /// that the user's next save overwrote. Writing the buffer to disk instead would save the user's changes without
    /// asking — so the file is named, and saving it stays the user's gesture.
    /// </remarks>
    public static string? UnsavedRefusal(Editor.OpenDocumentOverlay? overlay, string path) =>
        overlay is not null && overlay.TryGetUnsaved(path, out _)
            ? $"'{path}' has unsaved changes in the editor. read_file shows them, but this tool works on the file on " +
              "disk, which does not have them: the edit would miss them, or the user's next save would undo it. " +
              "Ask the user to save the file, then try again."
            : null;

    /// <summary>
    /// The refusal for writing <paramref name="content"/> to <paramref name="path"/> when the file's own encoding — a
    /// legacy code page, kept on rewrite — cannot hold one of its characters; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// ⚠ Called before the approval prompt too: written anyway, the character becomes "?" or a look-alike behind a
    /// prompt that showed it. Converting the file to UTF-8 instead would change every other accented byte in it —
    /// the user's decision, so it is named, never done.
    /// </remarks>
    public static string? EncodingRefusal(string path, string content)
    {
        if (!File.Exists(path)) return null;
        var encoding = TextFileEncoding.Detect(path);
        return TextFileEncoding.FirstUnrepresentable(encoding, content) is { } bad
            ? CannotHold(path, encoding, bad.Character, bad.Line)
            : null;
    }

    internal static string CannotHold(string path, System.Text.Encoding encoding, string character, int line) =>
        $"'{path}' is saved in {encoding.WebName} (a legacy code page, no byte order mark), which has no " +
        $"'{character}' (U+{char.ConvertToUtf32(character, 0):X4}, line {line}): written, it would become another " +
        $"character. Nothing was written. Keep to characters {encoding.WebName} holds, or ask the user to convert " +
        "the file to UTF-8.";
}
