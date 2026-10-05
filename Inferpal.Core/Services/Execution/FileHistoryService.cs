using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Creates timestamped backups of files before they are modified by the agent,
/// and restores them on demand via the <c>restore_file</c> tool or <c>/restore</c> slash command.
/// </summary>
/// <remarks>
/// Backups are stored in <c>.inferpal/history/</c> at the git repository root
/// (falls back to the file's directory when no git root is found).
/// Snapshot filename format: <c>yyyy-MM-dd_HH-mm-ss-fff_&lt;pathHash8&gt;_&lt;originalFilename&gt;.bak</c>.
/// The 8-hex-char hash of the <em>full</em> path disambiguates same-named files: on the bare file
/// name, <c>restore_file</c> on <c>A\Config.cs</c> silently restores the content of a more recently
/// touched <c>B\Config.cs</c>, and homonyms prune each other's retention slots. Snapshots written
/// before the hash are no longer found by name-matching — deliberate: that matching is the bug —
/// but stay on disk and remain restorable via <c>/undo-run</c>, which keeps exact snapshot paths.
/// </remarks>
internal class FileHistoryService
{
    // Snapshot filename: "yyyy-MM-dd_HH-mm-ss-fff_<pathHash8>_<originalFilename>"
    // 23 timestamp chars + 1 underscore separator = 24 characters before the hash.
    private const int TimestampPrefixLength = 24;

    /// <summary>First 8 hex chars of the SHA-256 of the normalized full path — the per-file
    /// identity that survives homonyms in other directories.</summary>
    internal static string PathHash(string filePath)
    {
        var normalized = Path.GetFullPath(filePath).ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes.AsSpan(0, 4)).ToLowerInvariant();
    }

    /// <summary>The per-file suffix a snapshot name must carry after its timestamp prefix.</summary>
    private static string SnapshotSuffix(string filePath) =>
        $"{PathHash(filePath)}_{Path.GetFileName(filePath)}";

    /// <summary>The extension every snapshot name ends with, after the original file name.</summary>
    /// <remarks>
    /// ⚠ The history folder lives INSIDE the project tree (the git root, or the file's own folder), and a
    /// snapshot that ends like its original is picked up by every tool that finds files by their name:
    /// jest, vitest and <c>node --test</c> (Node 20) run a backup of <c>sum.test.js</c> as a test — red,
    /// its relative imports no longer resolving and its assertions stale — so every test file the agent
    /// edits would add a failing suite to <c>npm test</c>, <c>run_tests</c> and <c>/tdd</c>. No runner,
    /// compiler or linter globs <c>*.bak</c>.
    /// </remarks>
    internal const string SnapshotExtension = ".bak";

    /// <summary>Whether <paramref name="snapshotPath"/> is a snapshot carrying <paramref name="suffix"/> —
    /// in the current format, or in the one before <see cref="SnapshotExtension"/>, still on users' disks.</summary>
    private static bool MatchesSuffix(string snapshotPath, string suffix)
    {
        var bn = Path.GetFileName(snapshotPath);
        if (bn.Length <= TimestampPrefixLength) return false;
        var rest = bn.AsSpan(TimestampPrefixLength);
        return rest.Equals(suffix + SnapshotExtension, StringComparison.OrdinalIgnoreCase)
            || rest.Equals(suffix, StringComparison.OrdinalIgnoreCase);
    }

    // History folders whose snapshots from before SnapshotExtension were renamed in this process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> RenamedFolders =
        new(PathComparer.Default);

    /// <summary>
    /// Gives the <see cref="SnapshotExtension"/> to snapshots written before it, once per folder and process.
    /// </summary>
    /// <remarks>Without it, a backup of a test file taken by an earlier version keeps failing the user's
    /// <c>npm test</c> until twenty newer snapshots of that same file prune it. The folder is Inferpal's
    /// own, and every name in it that starts with a date is a snapshot; renaming keeps it restorable
    /// (<see cref="MatchesSuffix"/> reads both forms). Best-effort: a failed rename leaves the file as it was.</remarks>
    private static void RenameOlderSnapshots(string historyDir)
    {
        if (!RenamedFolders.TryAdd(Path.GetFullPath(historyDir), 0)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(historyDir).ToList())
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(SnapshotExtension, StringComparison.OrdinalIgnoreCase) || !StartsWithDate(name)) continue;
                try { File.Move(file, file + SnapshotExtension); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diagnostics.Swallow("FileHistoryService.RenameOlderSnapshots", ex);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.RenameOlderSnapshots", ex);
        }
    }

    /// <summary>The ignore file the history folder carries, so git never lists a snapshot.</summary>
    internal const string GitIgnoreContent =
        "# Inferpal file history: local copies of files the assistant changed. Never committed.\n*\n";

    /// <summary>Makes git ignore <paramref name="historyDir"/> from inside it, whatever the repository looks like.</summary>
    /// <remarks>
    /// ⚠ A snapshot is a copy of whatever was overwritten, secrets included, and the history sits at the git
    /// ROOT. The line indexing adds to the WORKSPACE root's <c>.gitignore</c> only exists when that root holds
    /// the <c>.git</c> and indexing runs: a solution below the repository root, or RAG off, and every snapshot
    /// is an untracked file — what "Stage All" commits. A rule inside the folder travels with it (<c>*</c>
    /// ignores the rule itself too). Best-effort: the snapshot is still taken if this cannot be written.
    /// </remarks>
    private static void IgnoreInGit(string historyDir)
    {
        var path = Path.Combine(historyDir, ".gitignore");
        if (File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(GitIgnoreContent));
        }
        catch (IOException) when (File.Exists(path)) { }   // written meanwhile by another snapshot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.IgnoreInGit", ex);
        }
    }

    /// <summary><c>yyyy-MM-dd_</c> — how every snapshot name, in every format, begins.</summary>
    private static bool StartsWithDate(string name) =>
        name.Length > 11
        && char.IsAsciiDigit(name[0]) && char.IsAsciiDigit(name[1]) && char.IsAsciiDigit(name[2]) && char.IsAsciiDigit(name[3])
        && name[4] == '-' && char.IsAsciiDigit(name[5]) && char.IsAsciiDigit(name[6])
        && name[7] == '-' && char.IsAsciiDigit(name[8]) && char.IsAsciiDigit(name[9])
        && name[10] == '_';

    // Cap on retained snapshots per original file. Older ones are pruned after each
    // new snapshot so .inferpal/history/ cannot grow without bound.
    private const int MaxSnapshotsPerFile = 20;

    /// <summary>Clock the snapshot names are stamped from; tests freeze it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    internal async Task<string> SnapshotAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var historyDir = HistoryDirOf(filePath);
            Directory.CreateDirectory(historyDir);
            IgnoreInGit(historyDir);
            RenameOlderSnapshots(historyDir);

            // UTC + invariant. Local time repeats an hour every autumn, and the name is not just a
            // label: it is what the lookup below sorts on. A Buddhist or Umm al-Qura default calendar
            // (th-TH, ar-SA) would also rewrite the year outright.
            var stamp  = UtcNow();
            var suffix = SnapshotSuffix(filePath);
            var bytes  = await File.ReadAllBytesAsync(filePath, ct);

            // The name is CLAIMED, never assumed free: two snapshots of one file in the same
            // millisecond shared a name and the second silently replaced the first — and /undo-run
            // snapshots the current state right before restoring, so it replaced the very content
            // it was about to put back. CreateNew claims the name atomically (across processes too);
            // a taken name moves one millisecond later, so the later snapshot still sorts as the
            // more recent one and the name keeps the format MatchesSuffix reads. Only a failed
            // claim is retried — a write that fails after the claim is a real error.
            string snapPath;
            for (var attempt = 0; ; attempt++)
            {
                var timestamp = stamp.AddMilliseconds(attempt).ToString("yyyy-MM-dd_HH-mm-ss-fff",
                                                                         System.Globalization.CultureInfo.InvariantCulture);
                snapPath = Path.Combine(historyDir, $"{timestamp}_{suffix}{SnapshotExtension}");

                FileStream stream;
                try { stream = new FileStream(snapPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException) when (attempt < 1000 && File.Exists(snapPath)) { continue; }

                await using (stream) await stream.WriteAsync(bytes, ct);
                break;
            }

            PruneOldSnapshots(historyDir, suffix);
            RecordInRun(filePath, snapPath);   // for /undo-run (no-op when no run is active)
            return snapPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A failed snapshot means the write that follows has NO safety net. Swallowing it
            // without a trace made the file silently vanish from the /undo-run perimeter while
            // the tool description still promised "snapshotted": trace
            // it, and record the file in the run as snapshot-failed so UndoRunAsync reports it
            // as Failed instead of not knowing it was ever touched.
            Diagnostics.Swallow($"FileHistoryService.Snapshot({filePath})", ex);
            lock (_runLock) _currentRun?.RecordFirst(filePath, snapshot: null, snapshotFailed: true);
            return string.Empty;
        }
    }

    /// <summary>
    /// Backs up a file about to be replaced or deleted. <c>Saved</c> is false only when the file
    /// exists and no backup could be written.
    /// </summary>
    /// <remarks>⚠ The change must then NOT happen: every editing tool promises that <c>restore_file</c>
    /// and <c>/undo-run</c> bring the previous version back, and a failed snapshot leaves nothing to
    /// bring back — a deletion is then permanent. Same rule as <c>/undo-run</c>, which refuses to
    /// delete what it could not save.</remarks>
    internal async Task<(bool Saved, string Snapshot)> BackUpBeforeChangeAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            // ⚠ Nothing to back up, and that is precisely the moment we know the write that follows
            // CREATES this file — so /undo-run can delete it. A file created during a run has no
            // snapshot by construction (SnapshotAsync returns before recording anything when the
            // file is absent), so NoteCreated is its ONLY way into the undo perimeter, and it used
            // to have a single caller: write_file. update_memory creates .inferpal/memory.md on its
            // first call, so undoing the run gave every other file back and left that one — the one
            // re-injected into the system prompt of every later session. Said here, the property
            // holds for all eight callers instead of being a list to remember.
            NoteCreated(filePath);
            return (true, string.Empty);
        }
        var snapshot = await SnapshotAsync(filePath, ct);
        return (snapshot.Length > 0, snapshot);
    }

    /// <summary>The model-facing refusal when <see cref="BackUpBeforeChangeAsync"/> could not save a backup.</summary>
    internal static string BackupFailedMessage(string filePath) =>
        $"Error: no backup of {filePath} could be saved (details in /diagnostics), so it was NOT changed — "
        + "restore_file could not have undone it. Check that the .inferpal/history folder is writable, then retry.";

    /// <summary>
    /// Deletes the oldest snapshots carrying <paramref name="suffix"/> (path hash + file name)
    /// beyond <see cref="MaxSnapshotsPerFile"/>. Best-effort — never throws.
    /// </summary>
    private static void PruneOldSnapshots(string historyDir, string suffix)
    {
        try
        {
            // Same ordering as the lookup, and for the same reason: pruning by name would delete
            // the NEWEST snapshots for an hour every autumn.
            var snaps = Directory.EnumerateFiles(historyDir)
                .Where(f => MatchesSuffix(f, suffix))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(f => f)
                .Skip(MaxSnapshotsPerFile)
                .ToList();

            foreach (var old in snaps)
                try { File.Delete(old); } catch { }
        }
        catch { /* best-effort retention — never break the write path */ }
    }

    internal string? FindMostRecentSnapshot(string originalPath)
    {
        var suffix = SnapshotSuffix(originalPath);

        // Ordered by WRITE TIME, not by name. The name is written by this class, and trusting it
        // as a sort key makes "which snapshot is the most recent?" — the question that decides what
        // restore_file puts back — depend on the local clock: at the autumn fall-back an hour of
        // snapshots sorts before older ones. It also survives a folder holding both local-named and
        // UTC-named files after an upgrade.
        return HistoryDirsToRead(originalPath).SelectMany(Directory.EnumerateFiles)
            .Where(f => MatchesSuffix(f, suffix))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(f => f)
            .FirstOrDefault();
    }

    /// <summary>
    /// <c>true</c> when <paramref name="snapshotPath"/> is a snapshot of <paramref name="originalPath"/>
    /// kept in that file's own history folder.
    /// </summary>
    /// <remarks>The history lives at the git root, which can sit above the solution root the workspace
    /// is confined to — and every write hands the model that very snapshot path. A snapshot of THIS
    /// file (its path hash is in the name) is safe to restore wherever that root is.</remarks>
    internal static bool IsSnapshotOf(string snapshotPath, string originalPath)
    {
        try
        {
            var full = Path.GetFullPath(snapshotPath);
            // ⚠ A path IDENTITY, and the one that gates a sandbox exemption: a `true` here is what
            // lets `restore_file` accept a source ABOVE the workspace root. Folding case on a volume
            // that does not fold makes two different directories one, so a history folder differing
            // only by case would carry the exemption. The shared rule answers "same file?" once.
            var history = Path.GetFullPath(GetHistoryDir(originalPath));
            return string.Equals(Path.GetDirectoryName(full), history, PathComparer.Comparison)
                   && MatchesSuffix(full, SnapshotSuffix(originalPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Snapshots a restore took of the state it was about to replace (in memory, per session).
    private readonly HashSet<string> _restoreSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _restoreLock = new();

    /// <summary>Records that <paramref name="snapshotPath"/> was taken by a restore, right before it.</summary>
    internal void MarkTakenByRestore(string snapshotPath)
    {
        lock (_restoreLock) _restoreSnapshots.Add(snapshotPath);
    }

    /// <summary>
    /// The snapshot a restore without an explicit snapshot puts back: the most recent one that is
    /// neither a snapshot a restore took of itself nor identical to the current content.
    /// </summary>
    /// <remarks>⚠ Plain "most recent" made a second <c>restore_file</c> put back the state the first one
    /// had just undone — the snapshot the first restore took before overwriting — instead of stepping
    /// back once more.</remarks>
    internal async Task<string?> FindRestoreCandidateAsync(string originalPath, CancellationToken ct)
    {
        var current = File.Exists(originalPath) ? await File.ReadAllBytesAsync(originalPath, ct) : null;
        var suffix  = SnapshotSuffix(originalPath);
        var ordered = HistoryDirsToRead(originalPath).SelectMany(Directory.EnumerateFiles)
            .Where(f => MatchesSuffix(f, suffix))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(f => f)
            .ToList();

        foreach (var candidate in ordered)
        {
            lock (_restoreLock)
                if (_restoreSnapshots.Contains(candidate)) continue;
            if (current is not null
                && new FileInfo(candidate).Length == current.Length
                && (await File.ReadAllBytesAsync(candidate, ct)).AsSpan().SequenceEqual(current))
                continue;   // restoring identical content changes nothing
            return candidate;
        }
        return null;
    }

    internal async Task RestoreAsync(string snapPath, string targetPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(snapPath, ct);
        await File.WriteAllBytesAsync(targetPath, bytes, ct);
    }

    // ── Run grouping (for /undo-run) ─────────────────────────────────────────────
    // In-memory only: undo-run is meant to revert the agent run you just watched, in the same VS
    // session. Cross-session recovery is still served by the persisted per-file snapshots + /restore.

    private const int MaxRetainedRuns = 15;
    private readonly List<HistoryRun> _runs = [];   // chronological; newest last
    private HistoryRun? _currentRun;
    private readonly object _runLock = new();

    /// <summary>Starts a new change-tracking run; subsequent snapshots/creations attach to it.</summary>
    /// <param name="alreadyRead">
    /// A file the run starts with as read — for a write whose content a person reviewed as a diff (a <c>/task</c>
    /// proposal applied through the ordinary prompt), which is not a blind rewrite. See <see cref="WasRead"/>.
    /// </param>
    internal string BeginRun(string? alreadyRead = null)
    {
        // UTC, and invariant formatting. Local time repeats an hour every autumn, so two runs can
        // be handed the same identifier and the lexicographic order of identifiers lies for that
        // hour — SessionManager names its files the same way. The identifier is shown to nobody:
        // it keys /undo-run.
        var run = new HistoryRun(DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff",
                                                          System.Globalization.CultureInfo.InvariantCulture));
        lock (_runLock)
        {
            if (alreadyRead is not null) run.NoteRead(alreadyRead);
            _runs.Add(run);
            if (_runs.Count > MaxRetainedRuns) _runs.RemoveRange(0, _runs.Count - MaxRetainedRuns);
            _currentRun = run;
        }
        return run.Id;
    }

    /// <summary>
    /// How many files the current run has changed — every write goes through <see cref="BackUpBeforeChangeAsync"/>,
    /// which enters the file in the run; <c>null</c> when no run is active.
    /// </summary>
    internal int? CurrentRunFileCount
    {
        get { lock (_runLock) return _currentRun?.FileCount; }
    }

    /// <summary>Records that the model has seen the content of <paramref name="filePath"/> in the current run.</summary>
    internal void NoteRead(string filePath)
    {
        lock (_runLock) _currentRun?.NoteRead(filePath);
    }

    /// <summary>
    /// Records that the model has seen lines <paramref name="first"/>–<paramref name="last"/> of the
    /// <paramref name="total"/> of <paramref name="filePath"/>: the file counts as read once its pages cover it.
    /// </summary>
    internal void NoteReadLines(string filePath, int first, int last, int total)
    {
        lock (_runLock) _currentRun?.NoteReadLines(filePath, first, last, total);
    }

    /// <summary>
    /// Of a file the model has read only in part in the current run, how many lines it has seen and the first one it has
    /// not; <c>null</c> when it has read all of it, none of it, or no run is active.
    /// </summary>
    internal (int Seen, int Total, int FirstUnseen)? PartialRead(string filePath)
    {
        lock (_runLock) return _currentRun?.PartialRead(filePath);
    }

    /// <summary>
    /// Whether the model has seen <paramref name="filePath"/> in the current run; <c>null</c> when no run is active.
    /// </summary>
    /// <remarks>
    /// Per run, because a run is what the model still has in front of it: only the question and the answer outlive
    /// one, so a file read in an earlier turn is no longer in its context. <c>null</c> is not "no": outside a run
    /// (a code action, a tool a slash command calls) nobody is tracking reads, and a guard must not refuse on that.
    /// </remarks>
    internal bool? WasRead(string filePath)
    {
        lock (_runLock) return _currentRun?.WasRead(filePath);
    }

    /// <summary>
    /// Closes the current run: what is written afterwards belongs to no run.
    /// </summary>
    /// <remarks>
    /// ⚠ Without it, <c>_currentRun</c> stayed the last OPEN run forever, and the contract written
    /// two lines below — "No-op when no run is active" — was only true before the session's first
    /// agent run. Consequence: a write made AFTER the run (a <c>/restore</c>, a tool launched by a
    /// slash command) attached to it, so <c>/undo-run</c> reverted it along with the run, and
    /// <c>/replay</c> listed it in the journal of a run where it never happened. The run stays in
    /// the list: only the "current" one is closed.
    /// </remarks>
    internal void EndRun()
    {
        lock (_runLock) _currentRun = null;
    }

    /// <summary>
    /// <see cref="BeginRun"/> + <see cref="EndRun"/> bound to a scope, for callers whose exits are
    /// scattered (<c>/tdd</c> returns from eight places inside its loop). Callers that already own
    /// a <c>finally</c> call <c>EndRun</c> directly.
    /// </summary>
    internal IDisposable BeginRunScope()
    {
        BeginRun();
        return new RunScope(this);
    }

    private sealed class RunScope(FileHistoryService owner) : IDisposable
    {
        public void Dispose() => owner.EndRun();
    }

    /// <summary>Records that a file was created this run (no prior content → undo deletes it).</summary>
    internal void NoteCreated(string filePath)
    {
        lock (_runLock)
        {
            _currentRun?.RecordFirst(filePath, snapshot: null);
            _currentRun?.CountWrite();
        }
    }

    /// <summary>
    /// How many writes the current run has made, each counted as its backup is taken — every one, where
    /// <see cref="CurrentRunFileCount"/> counts each file once; <c>null</c> when no run is active.
    /// </summary>
    internal int? CurrentRunWriteCount
    {
        get { lock (_runLock) return _currentRun?.WriteCount; }
    }

    /// <summary>Appends a tool invocation to the current run's journal (for <c>/replay</c>).
    /// No-op when no run is active (e.g. code actions, tools disabled).</summary>
    internal void RecordToolCall(string tool, string? subject, long durationMs, bool error)
    {
        lock (_runLock) _currentRun?.RecordToolCall(tool, subject, durationMs, error);
    }

    private void RecordInRun(string filePath, string snapPath)
    {
        lock (_runLock)
        {
            _currentRun?.RecordFirst(filePath, snapPath);
            _currentRun?.CountWrite();
        }
    }

    /// <summary>All tracked runs, most recent first.</summary>
    internal IReadOnlyList<HistoryRun> Runs
    {
        get { lock (_runLock) return Enumerable.Reverse(_runs).ToList(); }
    }

    /// <summary>
    /// Reverts every file changed during <paramref name="run"/> to its pre-run state: restores the
    /// first snapshot taken that run, or deletes files that were created during it.
    /// </summary>
    internal async Task<RunUndoResult> UndoRunAsync(HistoryRun run, CancellationToken ct)
    {
        var restored = new List<string>();
        var deleted  = new List<string>();
        var failed   = new List<string>();
        var savedFirst = 0;

        foreach (var change in run.Changes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (change.SnapshotFailed)
                {
                    // The file was modified but its pre-run content was never captured: undoing
                    // cannot restore it, and deleting it (the created-file path below) would
                    // destroy data. Report it as failed so the user knows this one is manual.
                    failed.Add(change.OriginalPath);
                }
                else if (change.SnapshotPath is null)
                {
                    // ⚠ The CURRENT state is captured first, and that is the half that was missing.
                    // Undoing a run writes with no approval prompt — the only write path of the
                    // product that does — and what the run wrote may have been edited since: by
                    // hand, by a later run, by a merge. Without this line a file CREATED by the run
                    // and then filled in by the user was deleted without a trace, and a modified
                    // file was overwritten by a state older than the user's own corrections. The
                    // refusal two branches above already said it, for the other case.
                    if (await SnapshotAsync(change.OriginalPath, ct) is { Length: > 0 }) savedFirst++;
                    if (File.Exists(change.OriginalPath)) { File.Delete(change.OriginalPath); deleted.Add(change.OriginalPath); }
                }
                else if (File.Exists(change.SnapshotPath))
                {
                    if (await SnapshotAsync(change.OriginalPath, ct) is { Length: > 0 }) savedFirst++;
                    await RestoreAsync(change.SnapshotPath, change.OriginalPath, ct);
                    restored.Add(change.OriginalPath);
                }
                else
                {
                    failed.Add(change.OriginalPath);   // snapshot pruned/missing
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { failed.Add(change.OriginalPath); }
        }

        lock (_runLock) run.MarkUndone();
        return new RunUndoResult(restored, deleted, failed, savedFirst);
    }

    /// <summary>
    /// The workspace root: where the history of a file outside any git repository lives. Set by the tool registry;
    /// <c>null</c> (a bare service) keeps the file's own folder.
    /// </summary>
    /// <remarks>
    /// ⚠ Without it a project that is not a git repository got one <c>.inferpal/history/</c> per folder an edit
    /// touched — and <c>restore_file</c>'s description names one <c>.inferpal/history/</c>: asked to restore from the
    /// history, a model looked at the workspace root, found nothing, and answered that no backup existed (measured,
    /// Devstral).
    /// </remarks>
    internal Func<string?>? WorkspaceRoot { get; set; }

    /// <summary>Where a new snapshot of <paramref name="filePath"/> goes.</summary>
    private string HistoryDirOf(string filePath) => GetHistoryDir(filePath, WorkspaceRoot?.Invoke());

    /// <summary>Where snapshots of <paramref name="filePath"/> are looked for: where they go now, then the folder an
    /// older version put them in (the file's own folder, outside git), so a restore still finds them.</summary>
    private IEnumerable<string> HistoryDirsToRead(string filePath)
    {
        var now = HistoryDirOf(filePath);
        if (Directory.Exists(now)) yield return now;
        var legacy = GetHistoryDir(filePath);
        if (!string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(now), PathComparer.Comparison) && Directory.Exists(legacy))
            yield return legacy;
    }

    /// <summary>The git root's <c>.inferpal/history</c>; outside git, the workspace root's when the file is under
    /// <paramref name="workspaceRoot"/>; else the file's own folder.</summary>
    internal static string GetHistoryDir(string filePath, string? workspaceRoot = null)
    {
        var full     = Path.GetFullPath(filePath);
        var startDir = Path.GetDirectoryName(full) ?? ".";
        if (FindGitRoot(startDir) is { } git) return Path.Combine(git, ".inferpal", "history");
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            if (full.StartsWith(root + Path.DirectorySeparatorChar, PathComparer.Comparison))
                return Path.Combine(root, ".inferpal", "history");
        }
        return Path.Combine(startDir, ".inferpal", "history");
    }

    private static string? FindGitRoot(string startDir) => GitProcess.WorkTreeOf(startDir);
}

/// <summary>One file touched during a run. <see cref="SnapshotPath"/> is <c>null</c> when the file
/// was <em>created</em> during the run (no prior content — undo deletes it) — unless
/// <see cref="SnapshotFailed"/> is set: the file existed but its snapshot could not be taken, so
/// undo can neither restore nor delete it and reports it as failed.</summary>
internal sealed record RunChange(string OriginalPath, string? SnapshotPath, bool SnapshotFailed = false);

/// <summary>One tool invocation in a run's journal (for <c>/replay</c>). <see cref="Subject"/> is the
/// best-effort target extracted from the arguments (path, command, query…), <c>null</c> when none.
/// <see cref="Error"/> is <c>true</c> only for tool exceptions, not for in-band refusals.</summary>
internal sealed record ToolCallRecord(int Seq, string Tool, string? Subject, long DurationMs, bool Error);

/// <summary>Outcome of <see cref="FileHistoryService.UndoRunAsync"/>.</summary>
/// <param name="SavedFirst">Files whose current state was snapshotted before being reverted.</param>
internal sealed record RunUndoResult(List<string> Restored, List<string> Deleted, List<string> Failed,
                                     int SavedFirst = 0);

/// <summary>The lines of one file the model has seen, as merged ranges.</summary>
internal sealed class LinesSeen(int total)
{
    private readonly List<(int First, int Last)> _ranges = [];

    public int Total { get; } = total;

    /// <summary>How many lines have been seen.</summary>
    public int Count => _ranges.Sum(r => r.Last - r.First + 1);

    /// <summary>The first line not seen yet; <c>null</c> once every line has been.</summary>
    public int? FirstUnseen
    {
        get
        {
            var next = 1;
            foreach (var (first, last) in _ranges)
            {
                if (first > next) return next;
                next = Math.Max(next, last + 1);
            }
            return next <= Total ? next : null;
        }
    }

    public void Add(int first, int last)
    {
        _ranges.Add((first, Math.Min(last, Total)));
        _ranges.Sort();
        // Merge what overlaps or touches, so the ranges stay few and ordered.
        for (var i = _ranges.Count - 1; i > 0; i--)
        {
            if (_ranges[i].First > _ranges[i - 1].Last + 1) continue;
            _ranges[i - 1] = (_ranges[i - 1].First, Math.Max(_ranges[i - 1].Last, _ranges[i].Last));
            _ranges.RemoveAt(i);
        }
    }
}

/// <summary>
/// A change-tracking run: the set of files first touched between one <see cref="FileHistoryService.BeginRun"/>
/// and the next. Keeps only the <em>first</em> change per file so undo reverts to the pre-run state
/// even when a file was edited several times.
/// </summary>
internal sealed class HistoryRun
{
    public string Id { get; }
    public DateTime StartedAt { get; }

    // ⚠ PathComparer: two files that differ only in case are two files under Linux, and folding
    // them here meant the second never entered the run — so /undo-run could no longer give it back.
    private readonly Dictionary<string, RunChange> _firstByPath = new(PathComparer.Default);

    // Tool-call journal (for /replay). Guarded by its own lock: read-only tool batches execute in
    // parallel (ShouldRunParallel), so records can arrive concurrently — and /replay may read while
    // a later run is still writing.
    private readonly List<ToolCallRecord> _toolCalls = [];
    private readonly object _toolCallLock = new();

    // Files the model has read during this run (see FileHistoryService.WasRead). Mutated under
    // FileHistoryService's run lock, like _firstByPath.
    private readonly HashSet<string> _read = new(PathComparer.Default);

    public HistoryRun(string id) { Id = id; StartedAt = DateTime.Now; }

    /// <summary>
    /// The run has been undone. ⚠ It stays the most recent run with changes, so without this a second <c>/undo-run</c> — or
    /// a second click on the still-visible Undo button — undoes it AGAIN: it overwrites whatever has been edited since with
    /// the pre-run state, and answers "last run undone".
    /// </summary>
    public bool Undone { get; private set; }

    public void MarkUndone() => Undone = true;

    // Of a file read in part (a page, a range): the lines seen so far, promoted to _read once they cover the file.
    private readonly Dictionary<string, LinesSeen> _partial = new(PathComparer.Default);

    public void NoteRead(string path)
    {
        _read.Add(path);
        _partial.Remove(path);
    }

    public void NoteReadLines(string path, int first, int last, int total)
    {
        if (first < 1 || last < first || _read.Contains(path)) return;
        if (first == 1 && last >= total) { NoteRead(path); return; }

        // Another line count: the file changed between two reads, and what was seen of the old one no longer counts.
        if (!_partial.TryGetValue(path, out var seen) || seen.Total != total)
            _partial[path] = seen = new LinesSeen(total);
        seen.Add(first, last);
        if (seen.FirstUnseen is null) NoteRead(path);
    }

    public (int Seen, int Total, int FirstUnseen)? PartialRead(string path) =>
        !_read.Contains(path) && _partial.TryGetValue(path, out var seen) && seen.FirstUnseen is { } next
            ? (seen.Count, seen.Total, next)
            : null;

    public bool WasRead(string path) => _read.Contains(path);

    public void RecordFirst(string originalPath, string? snapshot, bool snapshotFailed = false)
    {
        if (!_firstByPath.ContainsKey(originalPath))
            _firstByPath[originalPath] = new RunChange(originalPath, snapshot, snapshotFailed);
    }

    public void RecordToolCall(string tool, string? subject, long durationMs, bool error)
    {
        lock (_toolCallLock)
            _toolCalls.Add(new ToolCallRecord(_toolCalls.Count + 1, tool, subject, durationMs, error));
    }

    public IReadOnlyCollection<RunChange> Changes => _firstByPath.Values;
    public int FileCount => _firstByPath.Count;

    // Every write, where _firstByPath keeps each file once. Mutated under FileHistoryService's run lock.
    public int WriteCount { get; private set; }
    public void CountWrite() => WriteCount++;

    /// <summary>Snapshot copy — safe to enumerate while the run is still recording.</summary>
    public IReadOnlyList<ToolCallRecord> ToolCalls
    {
        get { lock (_toolCallLock) return _toolCalls.ToList(); }
    }

    public int ToolCallCount
    {
        get { lock (_toolCallLock) return _toolCalls.Count; }
    }
}
