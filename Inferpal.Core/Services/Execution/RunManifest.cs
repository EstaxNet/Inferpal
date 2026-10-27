using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inferpal.Services.Execution;

/// <summary>
/// A run as it is kept on disk, one file per run beside the workspace's snapshots: what lets <c>/undo-run</c> and a result
/// bar's Undo work after the editor, the host or an ACP thread restarted (<see cref="FileHistoryService.Runs"/>).
/// </summary>
/// <remarks>
/// ⚠ Read without trust. Undoing a run is the only write the product makes without an approval card, and under
/// <c>historyStorage: project</c> these files live in the repository: one a clone brought, or one edited in place, would
/// have files rewritten with what the repository supplies. Each file is signed with a random key that never leaves the
/// user's local application data (<see cref="KeyIn"/>): a run the key does not sign is never read, and the caller still
/// checks every path a signed one names.
/// </remarks>
internal static class RunManifest
{
    /// <summary>The folder of a history folder the runs are kept in.</summary>
    internal const string Folder = "runs";

    internal const string Extension = ".json";

    /// <summary>How a run file begins, before its signature: a format a later version can tell from this one.</summary>
    private const string Header = "inferpal-run 1 ";

    private sealed record ChangeDto(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("snapshot")] string? Snapshot,
        [property: JsonPropertyName("failed")] bool Failed);

    private sealed record NoteDto(
        [property: JsonPropertyName("call")] string? Call,
        [property: JsonPropertyName("reason")] string? Reason,
        [property: JsonPropertyName("file")] string? File);

    private sealed record PieceDto(
        [property: JsonPropertyName("generation")] int Generation,
        [property: JsonPropertyName("runCopy")] string? RunCopy,
        [property: JsonPropertyName("undone")] List<int>? Undone,
        [property: JsonPropertyName("kept")] List<int>? Kept,
        [property: JsonPropertyName("after")] string? After);

    // "undone" and "givenBack" are the marks of the first runs written: read as generation 1, never written again.
    private sealed record Dto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("started")] DateTime Started,
        [property: JsonPropertyName("open")] bool Open,
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("watched")] int Watched,
        [property: JsonPropertyName("changes")] List<ChangeDto>? Changes,
        [property: JsonPropertyName("undoneGeneration")] int UndoneGeneration,
        [property: JsonPropertyName("generations")] Dictionary<string, int>? Generations,
        [property: JsonPropertyName("after")] Dictionary<string, string>? After,
        [property: JsonPropertyName("kept")] List<string>? Kept,
        [property: JsonPropertyName("redo")] Dictionary<string, string>? Redo,
        [property: JsonPropertyName("uncovered")] List<NoteDto>? Uncovered,
        [property: JsonPropertyName("undone"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Undone = false,
        [property: JsonPropertyName("givenBack"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? GivenBack = null,
        [property: JsonPropertyName("pieces"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, PieceDto>? Pieces = null);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// The key that signs this account's runs, kept in <paramref name="folder"/> (the user's local history root) and made
    /// on first use; <c>null</c> when it can neither be read nor made — runs are then not kept on disk.
    /// </summary>
    internal static byte[]? KeyIn(string folder)
    {
        var path = System.IO.Path.Combine(folder, ".run-key");
        try
        {
            if (File.Exists(path)) return ReadKey(path);
            Directory.CreateDirectory(folder);
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(key);
                return key;
            }
            catch (IOException) when (File.Exists(path)) { return ReadKey(path); }   // made meanwhile
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("RunManifest.KeyIn", ex);
            return null;
        }
    }

    /// <summary>The key at <paramref name="path"/>. ⚠ The other editor may be making it this very moment — held open, or
    /// not yet written — so it is read again for a moment before it counts as unreadable: its caller keeps the answer
    /// for the life of the process, and without a key no run is kept on disk.</summary>
    private static byte[]? ReadKey(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var key = File.ReadAllBytes(path);
                if (key.Length == 32) return key;
                if (attempt >= KeyReadAttempts) return null;
            }
            catch (IOException) when (attempt < KeyReadAttempts) { }
            Thread.Sleep(20);
        }
    }

    private const int KeyReadAttempts = 50;

    private static string Sign(string json, byte[] key) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(json)));

    /// <summary>The file name of <paramref name="run"/> in a runs folder.</summary>
    internal static string FileName(HistoryRun run) => run.Id + Extension;

    /// <summary>
    /// <paramref name="run"/> as it is written, signed with <paramref name="key"/>; <paramref name="open"/> while its
    /// process still records into it. Call under the run lock: the run may still be recording.
    /// </summary>
    internal static string Serialize(HistoryRun run, bool open, byte[] key, int? pid = null)
    {
        var json = JsonSerializer.Serialize(new Dto(
            run.Id, run.StartedAt.ToUniversalTime(), open, pid ?? Environment.ProcessId, run.WatchedCalls,
            run.Changes.Select(c => new ChangeDto(c.OriginalPath, c.SnapshotPath, c.SnapshotFailed)).ToList(),
            run.UndoneGeneration,
            run.FileGenerations.ToDictionary(p => p.Key, p => p.Value),
            run.AfterHashes.ToDictionary(p => p.Key, p => p.Value),
            run.Kept.ToList(),
            run.RedoSources.ToDictionary(p => p.Key, p => p.Value),
            run.Uncovered.Select(u => new NoteDto(u.Call, u.Note.Reason.ToString(), u.Note.File)).ToList(),
            Pieces: run.Pieces.Count == 0 ? null : run.Pieces.ToDictionary(
                p => p.Key, p => new PieceDto(p.Value.Generation, p.Value.RunCopy, [.. p.Value.Undone], [.. p.Value.Kept], p.Value.After))),
            Options);
        return Header + Sign(json, key) + "\n" + json;
    }

    /// <summary>The run a file describes, once its signature holds; <c>null</c> with the reason otherwise.</summary>
    private static Dto? Read(string text, byte[] key, out string? refusal)
    {
        var newline = text.IndexOf('\n');
        if (!text.StartsWith(Header, StringComparison.Ordinal) || newline < 0)
        {
            refusal = "not a run file of this version";
            return null;
        }
        var json = text[(newline + 1)..];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(text[Header.Length..newline].Trim()),
                                                     Encoding.ASCII.GetBytes(Sign(json, key))))
        {
            refusal = "not signed by this account's key (written elsewhere, or edited)";
            return null;
        }
        try { refusal = null; return JsonSerializer.Deserialize<Dto>(json, Options); }
        catch (JsonException ex) { refusal = "unreadable: " + ex.Message; return null; }
    }

    /// <summary>What a signed run file says: the run, whether its process was still recording, and which process.</summary>
    internal sealed record Reading(HistoryRun Run, bool Open, int Pid);

    /// <summary>
    /// The run <paramref name="text"/> describes, or <c>null</c> with the reason it is not read: unsigned or edited, or
    /// naming a file and copy <paramref name="accepts"/> refuses.
    /// </summary>
    internal static Reading? Parse(string text, byte[] key, Func<string, string?, bool> accepts, out string? refusal)
    {
        if (Read(text, key, out refusal) is not { Id.Length: > 0, Changes: not null } dto)
        {
            refusal ??= "unreadable: no id or no changes";
            return null;
        }
        foreach (var change in dto.Changes)
            if (change.Path is not { Length: > 0 } path || !System.IO.Path.IsPathFullyQualified(path)
                || change.Snapshot is { } snap && !System.IO.Path.IsPathFullyQualified(snap)
                || !accepts(path, change.Snapshot))
            {
                refusal = $"names a file or a copy outside this workspace's history: {change.Path}";
                return null;
            }
        // A redo writes its copy over the file, without an approval card either: the same checks.
        foreach (var (path, copy) in dto.Redo ?? [])
            if (!dto.Changes.Any(c => PathComparer.Default.Equals(c.Path, path))
                || !System.IO.Path.IsPathFullyQualified(copy) || !accepts(path, copy))
            {
                refusal = $"names a copy to redo outside this workspace's history: {path}";
                return null;
            }
        // A change given back or redone writes from the run's copy too.
        foreach (var (path, piece) in dto.Pieces ?? [])
            if (!dto.Changes.Any(c => PathComparer.Default.Equals(c.Path, path))
                || piece.RunCopy is not { } copy || !System.IO.Path.IsPathFullyQualified(copy) || !accepts(path, copy))
            {
                refusal = $"names a copy of a run's changes outside this workspace's history: {path}";
                return null;
            }

        var run = new HistoryRun(dto.Id, dto.Started.ToLocalTime());
        foreach (var change in dto.Changes) run.RecordFirst(change.Path!, change.Snapshot, change.Failed);
        run.RestoreWatchedCalls(dto.Watched);
        foreach (var (path, hash) in dto.After ?? []) run.NoteAfter(path, hash);
        foreach (var path in dto.Kept ?? []) run.NoteKept(path);
        foreach (var note in dto.Uncovered ?? [])
            if (note.Call is not null && Enum.TryParse<UncoveredReason>(note.Reason, out var reason))
                run.NoteUncovered(note.Call, new UncoveredNote(reason, note.File));
        MergeMarks(run, dto);
        return new Reading(run, dto.Open, dto.Pid);
    }

    /// <summary>
    /// Brings into <paramref name="run"/> what another process marked in its file — undone, given back. Both only grow:
    /// without this, the editor that made a run undoes it a second time after the other editor has, over what was edited
    /// since, and its next write clears the other's marks.
    /// </summary>
    internal static void MergeMarks(HistoryRun run, string text, byte[] key)
    {
        if (Read(text, key, out _) is { Id: { } id } dto && id == run.Id) MergeMarks(run, dto);
    }

    private static void MergeMarks(HistoryRun run, Dto dto)
    {
        foreach (var path in dto.GivenBack ?? []) run.MergeFileGeneration(path, 1);
        foreach (var (path, generation) in dto.Generations ?? [])
            // The newer state of a file brings its copy to redo with it.
            if (run.MergeFileGeneration(path, generation) && dto.Redo?.GetValueOrDefault(path) is { } copy)
                run.NoteRedoSource(path, copy);
        foreach (var (path, copy) in dto.Redo ?? [])
            if (run.RedoSourceOf(path) is null) run.NoteRedoSource(path, copy);
        foreach (var path in dto.Kept ?? []) run.NoteKept(path);
        foreach (var (path, piece) in dto.Pieces ?? [])
            if (piece is { RunCopy: { } copy, After: { } after })
                run.MergePieces(path, new PieceMarks(piece.Generation, copy, piece.Undone ?? [], piece.Kept ?? [], after));
        run.MergeUndoneGeneration(Math.Max(dto.UndoneGeneration, dto.Undone ? 1 : 0));
    }
}
