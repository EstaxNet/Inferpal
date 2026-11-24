using System.Globalization;
using Inferpal.Services.Documents;

namespace Inferpal.Services.Tools;

/// <summary>
/// Saves a capture into the workspace as a PNG (<c>save_to</c>, pari 126): the usual write card, the picture on it and the
/// one it replaces beside it; a backup first, so Undo run takes it back; confined to the workspace, and the ignore files
/// hold as for any path a call names (<c>ToolRegistry.ExcludedNamedFile</c>).
/// </summary>
/// <remarks>Asked as <c>write_file</c>: the person's write rules and "Always" hold for it, as for any file the agent
/// writes — it is the one card of the flow.</remarks>
internal static class CaptureSaving
{
    /// <summary>The file <paramref name="saveTo"/> names, or what is wrong with it (said to the model) — checked before any
    /// step plays, so a call never acts for a capture it cannot save.</summary>
    internal static (string? Path, string? Refusal) Target(string? saveTo, string root)
    {
        if (string.IsNullOrWhiteSpace(saveTo)) return (null, null);
        string path;
        try
        {
            path = PathSanitizer.Sanitize(saveTo, root);
            PathSanitizer.AssertUnderRoot(path, root);
        }
        catch (ArgumentException ex) { return (null, $"Error: save_to: {ex.Message}"); }
        if (Directory.Exists(path)) return (null, $"Error: save_to '{saveTo}' is a folder: name a PNG file in it (docs/images/cart.png).");
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return (null, $"Error: save_to '{saveTo}' is not a .png file: the capture is saved as a PNG (docs/images/cart.png).");
        return (path, null);
    }

    /// <summary>
    /// The file <paramref name="saveTo"/> names, or why the call is refused before any step plays: a call never acts for a
    /// capture it cannot save — a bad name (<see cref="Target"/>), no history to back it up, a file the run has not seen
    /// (<see cref="Unseen"/>), one the person's rules refuse writing.
    /// </summary>
    /// <param name="what">What is saved, as the refusals name it: "capture", "picture".</param>
    internal static (string? Path, string? Refusal) BeforeSteps(string? saveTo, string root, FileHistoryService? history,
                                                                IApprovalService approval, string what)
    {
        var (path, refusal) = Target(saveTo, root);
        if (refusal is not null || path is null) return (null, refusal);
        if (history is null) return (null, $"Error: save_to is not available here: the {what} cannot be saved.");
        if (Unseen(history, root, path) is { } unseen) return (null, "Error: save_to: " + unseen + " Nothing was played.");
        if (approval.Refuses("write_file", path))
            return (null, $"Error: save_to: the person's rules refuse writing '{path}'. Nothing was played.");
        return (path, null);
    }

    /// <summary>The first words of every line <see cref="SaveAsync"/> says: the caller tells by them whether the save was
    /// answered at all.</summary>
    internal const string SavedPrefix = "The capture is saved to ", NotSavedPrefix = "The capture was not saved to ";

    /// <summary>Why <paramref name="path"/> is not replaced: an existing file the run has not seen — its picture would go
    /// behind a write "Always" without anyone looking at it, as write_file refuses it; <c>null</c> when it may be. Asked
    /// before any step too: a call never acts for a capture it cannot save.</summary>
    internal static string? Unseen(FileHistoryService history, string root, string path) =>
        File.Exists(path) && FileTarget.UnreadRefusal(history, path) is not null
            ? Path.GetRelativePath(root, path).Replace('\\', '/') + ": that file already exists and has not been looked at in this "
              + "run. Look at it with read_file (it shows the picture), or save to another name."
            : null;

    /// <summary>Asks the write card with the picture on it, then writes <paramref name="png"/> to <paramref name="path"/>,
    /// backed up first; the line said to the model. A failure of the disk is said, never thrown: the page the steps built
    /// stays open.</summary>
    internal static async Task<string> SaveAsync(IApprovalService approval, FileHistoryService history, string root, string path,
                                                 byte[] png, CancellationToken ct)
    {
        var shown = Path.GetRelativePath(root, path).Replace('\\', '/');
        var exists = File.Exists(path);
        // Asked again here: the file may have been created while the steps played.
        if (Unseen(history, root, path) is { } unseen) return NotSavedPrefix + unseen;
        var before = exists ? Stamp(path) : default;
        var pending = Directory.CreateTempSubdirectory("inferpal-capture-").FullName;
        try
        {
            var picture = Path.Combine(pending, "capture.png");
            await File.WriteAllBytesAsync(picture, png, ct);
            // ⚠ No picture, no card: a binary change without one would be drawn as an empty text, a replacement as a creation.
            if (Presentation.RunBinary.Of(exists ? path : null, picture, created: !exists, pictures: true) is not { } binary)
                return NotSavedPrefix + $"{shown}: the capture could not be read back as an image.";
            var diff = new CodeActions.DiffInfo(string.Empty, string.Empty, path) { Binary = binary, Creates = !exists };
            // ⚠ Aside from the call's own watch: asked after the steps, the card would restart it on a yes and drop it on a no —
            // and what the app changed in the repository while the steps played would leave the run, out of Undo run.
            bool approved;
            using (Execution.WorkTreeBracket.Aside())
                approved = await approval.RequestApprovalAsync("write_file", path + "\n" + binary.Label, ct, subject: path, diff: diff);
            if (!approved) return NotSavedPrefix + $"{shown}: the person declined.";
            // What the card showed is what is replaced: a file changed or created while it waited is not written over.
            if (File.Exists(path) != exists || (exists && Stamp(path) != before))
                return NotSavedPrefix + $"{shown}: the file changed while the card waited. Call again to save over it as it is now.";

            var (saved, _) = await history.BackUpBeforeChangeAsync(path, ct);
            if (!saved) return history.BackupFailedMessage(path);
            // Whole or not at all, and never cut by a Stop once begun: a half-written capture is no picture.
            AtomicFile.WriteAllBytes(path, png);
            // Its picture is what the model asked for: saving over it again in this run is no blind write.
            history.NoteRead(path);
            var read = ChatImages.Read(png, Path.GetFileName(path)).Image;
            return read is null
                ? SavedPrefix + $"{shown}."
                : SavedPrefix + string.Create(CultureInfo.InvariantCulture,
                    $"{shown} ({read.Width}×{read.Height}, {png.Length / 1024.0:0.#} KB).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PermissionDeniedException)
        {
            return NotSavedPrefix + $"{shown}: {ex.Message}";
        }
        finally
        {
            try { Directory.Delete(pending, recursive: true); } catch { /* temp is swept */ }
        }
    }

    /// <summary>A file's length and last write: what tells it changed while a card waited.</summary>
    private static (long Length, DateTime Written) Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc) : default;
    }
}
