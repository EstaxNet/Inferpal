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
}
