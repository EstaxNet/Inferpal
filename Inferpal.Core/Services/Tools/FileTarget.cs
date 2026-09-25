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
    /// <summary>The refusal for <paramref name="path"/> when it is a directory, else <c>null</c>.</summary>
    public static string? DirectoryRefusal(string path) =>
        Directory.Exists(path)
            ? $"'{path}' is a directory, not a file — give the path of a file (inside it, for a new one)."
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
