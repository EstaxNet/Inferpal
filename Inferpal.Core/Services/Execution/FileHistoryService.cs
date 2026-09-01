using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Creates timestamped backups of files before they are modified by the agent,
/// and restores them on demand via the <c>restore_file</c> tool or <c>/restore</c> slash command.
/// </summary>
/// <remarks>
/// Backups are stored in <c>.inferpal/history/</c> at the git repository root
/// (falls back to the file's directory when no git root is found).
/// Snapshot filename format: <c>yyyy-MM-dd_HH-mm-ss-fff_&lt;pathHash8&gt;_&lt;originalFilename&gt;</c>.
/// The 8-hex-char hash of the <em>full</em> path disambiguates same-named files: matching on the
/// bare file name let <c>restore_file</c> on <c>A\Config.cs</c> silently restore the content of a
/// more recently touched <c>B\Config.cs</c>, and homonyms pruned each other's retention slots
/// (pre-1.6.0 architecture review, §1.4). Snapshots written before this format are no longer found by
/// name-matching — deliberate: that matching is the bug — but stay on disk and remain restorable
/// via <c>/undo-run</c>, which keeps exact snapshot paths.
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

    private static bool MatchesSuffix(string snapshotPath, string suffix)
    {
        var bn = Path.GetFileName(snapshotPath);
        return bn.Length > TimestampPrefixLength &&
               bn[TimestampPrefixLength..].Equals(suffix, StringComparison.OrdinalIgnoreCase);
    }

    // Cap on retained snapshots per original file. Older ones are pruned after each
    // new snapshot so .inferpal/history/ cannot grow without bound.
    private const int MaxSnapshotsPerFile = 20;

    internal async Task<string> SnapshotAsync(string filePath, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(filePath)) return string.Empty;

            var historyDir = GetHistoryDir(filePath);
            Directory.CreateDirectory(historyDir);

            // UTC + invariant. Local time repeats an hour every autumn, and the name is not just a
            // label: it is what the lookup below sorts on. A Buddhist or Umm al-Qura default calendar
            // (th-TH, ar-SA) would also rewrite the year outright.
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff",
                                                     System.Globalization.CultureInfo.InvariantCulture);
            var suffix    = SnapshotSuffix(filePath);
            var snapPath  = Path.Combine(historyDir, $"{timestamp}_{suffix}");

            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            await File.WriteAllBytesAsync(snapPath, bytes, ct);

            PruneOldSnapshots(historyDir, suffix);
            RecordInRun(filePath, snapPath);   // for /undo-run (no-op when no run is active)
            return snapPath;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A failed snapshot means the write that follows has NO safety net. Swallowing it
            // without a trace made the file silently vanish from the /undo-run perimeter while
            // the tool description still promised "snapshotted" (pre-1.6.0 architecture review, §1.3): trace
            // it, and record the file in the run as snapshot-failed so UndoRunAsync reports it
            // as Failed instead of not knowing it was ever touched.
            Diagnostics.Swallow($"FileHistoryService.Snapshot({filePath})", ex);
            lock (_runLock) _currentRun?.RecordFirst(filePath, snapshot: null, snapshotFailed: true);
            return string.Empty;
        }
    }

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
        var historyDir = GetHistoryDir(originalPath);
        if (!Directory.Exists(historyDir)) return null;

        var suffix = SnapshotSuffix(originalPath);

        // Ordered by WRITE TIME, not by name. The name is written by this class and used to be
        // trusted as a sort key, which made "which snapshot is the most recent?" — the question
        // that decides what restore_file puts back — depend on the local clock: at the autumn
        // fall-back an hour of snapshots sorts before older ones, so the tool restored the older
        // content. It also survives the mix of local-named (pre-fix) and UTC-named files sitting
        // in the same folder after an upgrade.
        return Directory.EnumerateFiles(historyDir)
            .Where(f => MatchesSuffix(f, suffix))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(f => f)
            .FirstOrDefault();
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
    internal string BeginRun()
    {
        // UTC, and invariant formatting. Local time repeats an hour every autumn, so two runs could
        // be handed the same identifier, and the lexicographic order of identifiers lied for that
        // hour — §27.6 fixed exactly this in SessionManager and left this site behind (revue
        // post-1.6.0, item 4.5). The identifier is shown to nobody: it keys /undo-run.
        var run = new HistoryRun(DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff",
                                                          System.Globalization.CultureInfo.InvariantCulture));
        lock (_runLock)
        {
            _runs.Add(run);
            if (_runs.Count > MaxRetainedRuns) _runs.RemoveRange(0, _runs.Count - MaxRetainedRuns);
            _currentRun = run;
        }
        return run.Id;
    }

    /// <summary>Records that a file was created this run (no prior content → undo deletes it).</summary>
    internal void NoteCreated(string filePath)
    {
        lock (_runLock) _currentRun?.RecordFirst(filePath, snapshot: null);
    }

    /// <summary>Appends a tool invocation to the current run's journal (for <c>/replay</c>).
    /// No-op when no run is active (e.g. code actions, tools disabled).</summary>
    internal void RecordToolCall(string tool, string? subject, long durationMs, bool error)
    {
        lock (_runLock) _currentRun?.RecordToolCall(tool, subject, durationMs, error);
    }

    private void RecordInRun(string filePath, string snapPath)
    {
        lock (_runLock) _currentRun?.RecordFirst(filePath, snapPath);
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
                    if (File.Exists(change.OriginalPath)) { File.Delete(change.OriginalPath); deleted.Add(change.OriginalPath); }
                }
                else if (File.Exists(change.SnapshotPath))
                {
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

        return new RunUndoResult(restored, deleted, failed);
    }

    internal static string GetHistoryDir(string filePath)
    {
        var startDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
        var root     = FindGitRoot(startDir) ?? startDir;
        return Path.Combine(root, ".inferpal", "history");
    }

    private static string? FindGitRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
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
internal sealed record RunUndoResult(List<string> Restored, List<string> Deleted, List<string> Failed);

/// <summary>
/// A change-tracking run: the set of files first touched between one <see cref="FileHistoryService.BeginRun"/>
/// and the next. Keeps only the <em>first</em> change per file so undo reverts to the pre-run state
/// even when a file was edited several times.
/// </summary>
internal sealed class HistoryRun
{
    public string Id { get; }
    public DateTime StartedAt { get; }

    private readonly Dictionary<string, RunChange> _firstByPath = new(StringComparer.OrdinalIgnoreCase);

    // Tool-call journal (for /replay). Guarded by its own lock: read-only tool batches execute in
    // parallel (ShouldRunParallel), so records can arrive concurrently — and /replay may read while
    // a later run is still writing.
    private readonly List<ToolCallRecord> _toolCalls = [];
    private readonly object _toolCallLock = new();

    public HistoryRun(string id) { Id = id; StartedAt = DateTime.Now; }

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
