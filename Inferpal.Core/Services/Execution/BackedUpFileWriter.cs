using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Writes a file that may replace one the user wrote, keeping a way back: the replaced version is
/// backed up first, then the file is written keeping the encoding it already has.
/// </summary>
/// <remarks>
/// ⚠ For writes that no approval prompt covers — a slash command the user typed, whose content
/// comes from the model: <c>/onboard context force</c>, and <c>/test</c> on an existing test file.
/// An existing file that cannot be backed up is not replaced at all, the same rule as
/// <c>/undo-run</c>, which refuses to delete what it could not save. The caller names the command
/// that brings the previous version back (<c>Strings.FilePreviousVersionSaved</c>).
/// </remarks>
internal static class BackedUpFileWriter
{
    /// <param name="Written">False when an existing file was left in place because it could not be backed up.</param>
    /// <param name="Snapshot">The backup of the replaced version, or <c>""</c> when there was nothing to replace.</param>
    internal readonly record struct Outcome(bool Written, string Snapshot);

    internal static async Task<Outcome> WriteAsync(
        string path, string content, FileHistoryService history, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var existed  = File.Exists(path);
        var snapshot = await history.SnapshotAsync(path, ct);
        if (existed && snapshot.Length == 0) return new(false, string.Empty);

        await Inferpal.Services.Tools.SafeFileWriter.WritePreservingAsync(path, content, ct);
        return new(true, snapshot);
    }
}
