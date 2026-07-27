using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Creates timestamped backups of files before they are modified by the agent,
/// and restores them on demand via the <c>restore_file</c> tool or <c>/restore</c> slash command.
/// </summary>
/// <remarks>
/// Backups are stored in <c>.inferpal/history/</c> at the git repository root
/// (falls back to the file's directory when no git root is found).
/// Snapshot filename format: <c>yyyy-MM-dd_HH-mm-ss-fff_&lt;originalFilename&gt;</c>.
/// </remarks>
internal class FileHistoryService
{
    // Snapshot filename: "yyyy-MM-dd_HH-mm-ss-fff_<originalFilename>"
    // 23 timestamp chars + 1 underscore separator = 24 characters total.
    private const int TimestampPrefixLength = 24;

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

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            var fileName  = Path.GetFileName(filePath);
            var snapPath  = Path.Combine(historyDir, $"{timestamp}_{fileName}");

            var bytes = await File.ReadAllBytesAsync(filePath, ct);
            await File.WriteAllBytesAsync(snapPath, bytes, ct);

            PruneOldSnapshots(historyDir, fileName);
            RecordInRun(filePath, snapPath);   // for /undo-run (no-op when no run is active)
            return snapPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Deletes the oldest snapshots of <paramref name="fileName"/> beyond
    /// <see cref="MaxSnapshotsPerFile"/>. Best-effort — never throws.
    /// </summary>
    private static void PruneOldSnapshots(string historyDir, string fileName)
    {
        try
        {
            var snaps = Directory.EnumerateFiles(historyDir)
                .Where(f =>
                {
                    var bn = Path.GetFileName(f);
                    return bn.Length > TimestampPrefixLength &&
                           bn[TimestampPrefixLength..].Equals(fileName, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(f => f) // timestamp prefix sorts lexically == chronologically
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

        var fileName = Path.GetFileName(originalPath);

        return Directory.EnumerateFiles(historyDir)
            .Where(f =>
            {
                var bn = Path.GetFileName(f);
                return bn.Length > TimestampPrefixLength &&
                       bn[TimestampPrefixLength..].Equals(fileName, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(f => f)
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
        var run = new HistoryRun(DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff"));
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
                if (change.SnapshotPath is null)
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
/// was <em>created</em> during the run (no prior content — undo deletes it).</summary>
internal sealed record RunChange(string OriginalPath, string? SnapshotPath);

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

    public HistoryRun(string id) { Id = id; StartedAt = DateTime.Now; }

    public void RecordFirst(string originalPath, string? snapshot)
    {
        if (!_firstByPath.ContainsKey(originalPath))
            _firstByPath[originalPath] = new RunChange(originalPath, snapshot);
    }

    public IReadOnlyCollection<RunChange> Changes => _firstByPath.Values;
    public int FileCount => _firstByPath.Count;
}
