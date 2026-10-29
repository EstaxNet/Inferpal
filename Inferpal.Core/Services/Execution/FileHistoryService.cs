using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// Creates timestamped backups of files before they are modified by the agent,
/// and restores them on demand via the <c>restore_file</c> tool or <c>/restore</c> slash command.
/// </summary>
/// <remarks>
/// Backups are stored in <c>.inferpal/history/</c> at the git repository root (or at the workspace root, else in the
/// file's directory, when no git root is found) — or, with <see cref="OutsideProject"/>, in a folder of the user's own
/// application data, one per repository, and nothing is written into the project (<see cref="UserHistoryDir"/>).
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
        // The original name keeps its case in the suffix: on Linux, xt_DSCP.c and xt_dscp.c are two files with one hash.
        return rest.Equals(suffix + SnapshotExtension, PathComparer.Comparison)
            || rest.Equals(suffix, PathComparer.Comparison);
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
    /// ⚠ A snapshot is a copy of whatever was overwritten, secrets included, and the history sits at the git ROOT: without
    /// this rule every snapshot is an untracked file — what "Stage All" commits. It is the only one: the repository's own
    /// <c>.gitignore</c> is the user's file, and Inferpal never writes it. A rule inside the folder travels with it
    /// (<c>*</c> ignores the rule itself too), whatever the layout — a solution below the repository root, indexing off.
    /// Written in a history kept outside the project too: a home folder kept in git (dotfiles) would list those copies.
    /// Best-effort: the snapshot is still taken if this cannot be written.
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

    /// <summary>
    /// Gives the history folder that already exists for <paramref name="workspaceRoot"/> — at its git root, else at the
    /// root itself — the ignore rule it may not carry yet (<see cref="IgnoreInGit"/>). Called when a workspace opens;
    /// creates no folder. Best-effort.
    /// </summary>
    /// <remarks>
    /// ⚠ The rule is written with a snapshot, and a history whose last snapshot is older than the rule has none: kept out
    /// of git by a line of the repository's <c>.gitignore</c> alone, its copies — secrets included — are untracked files
    /// the moment that line is removed, until the agent's next write. Inferpal never writes the repository's file, so the
    /// workspace's own history is made to protect itself as soon as the workspace opens, whichever setting is chosen: the
    /// copies already in the project stay there either way. A repository nested below a root that is not one gets its
    /// rule at its next snapshot; finding every such folder would mean walking the whole workspace at each open.
    /// </remarks>
    internal static void IgnoreExistingHistory(string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot)) return;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var dir  = Path.Combine(AnchorOfFolder(root, root), ".inferpal", "history");
            if (Directory.Exists(dir)) IgnoreInGit(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Diagnostics.Swallow("FileHistoryService.IgnoreExistingHistory", ex);
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

    /// <param name="pruneLater">When given, the old snapshots of the file are pruned by <see cref="PruneBatch"/> once the
    /// batch is written, not after this one (see <see cref="PruneBatch"/>).</param>
    internal async Task<string> SnapshotAsync(string filePath, CancellationToken ct,
                                              ICollection<(string Dir, string Suffix)>? pruneLater = null)
    {
        if (!File.Exists(filePath)) return string.Empty;
        return await SnapshotOfAsync(filePath, () => File.ReadAllBytesAsync(filePath, ct), ct, pruneLater);
    }

    /// <summary>
    /// A snapshot of <paramref name="filePath"/> made of <paramref name="before"/> — the content it held before a call
    /// changed it without a backup of its own (a command: see <see cref="RecordWatchedCallAsync"/>) — recorded in the run
    /// like any other. Empty when it could not be written, and the file is then recorded as failed.
    /// </summary>
    internal Task<string> SnapshotBytesAsync(string filePath, byte[] before, CancellationToken ct,
                                             ICollection<(string Dir, string Suffix)>? pruneLater = null) =>
        SnapshotOfAsync(filePath, () => Task.FromResult(before), ct, pruneLater);

    private async Task<string> SnapshotOfAsync(string filePath, Func<Task<byte[]>> content, CancellationToken ct,
                                               ICollection<(string Dir, string Suffix)>? pruneLater = null)
    {
        try
        {
            var (historyDir, owner) = WhereNewSnapshotsGo(filePath);
            Directory.CreateDirectory(historyDir);
            IgnoreInGit(historyDir);
            if (owner is not null) NameTheOwner(historyDir, owner);
            RenameOlderSnapshots(historyDir);

            // UTC + invariant. Local time repeats an hour every autumn, and the name is not just a
            // label: it is what the lookup below sorts on. A Buddhist or Umm al-Qura default calendar
            // (th-TH, ar-SA) would also rewrite the year outright.
            var stamp  = UtcNow();
            var suffix = SnapshotSuffix(filePath);
            var bytes  = await content();

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

                try { await using (stream) await stream.WriteAsync(bytes, ct); }
                catch
                {
                    // ⚠ A claimed name holding less than the file — a full disk, a Stop mid-write — is the backup
                    // restore_file would pick as the newest: given back before the error goes on.
                    try { File.Delete(snapPath); } catch { }
                    throw;
                }
                break;
            }

            RecordInRun(filePath, snapPath);   // for /undo-run (no-op when no run is active)
            // After the run records it: the list published for the other processes then holds this backup too.
            if (pruneLater is null) PruneOldSnapshots(historyDir, [suffix]);
            else pruneLater.Add((historyDir, suffix));
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
            HistoryRun? run;
            lock (_runLock)
            {
                run = Run;
                run?.RecordFirst(filePath, snapshot: null, snapshotFailed: true);
            }
            PersistAsItGrows(run);   // kept on disk too: after a restart the undo still names it as one it cannot give back
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
        bool firstInRun;
        lock (_runLock) firstInRun = Run is { } run && !run.Has(filePath);

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
            Pending(new PendingWrite(filePath, Snapshot: null, Failed: false, firstInRun));
            return (true, string.Empty);
        }
        var snapshot = await SnapshotAsync(filePath, ct);
        Pending(new PendingWrite(filePath, snapshot.Length > 0 ? snapshot : null, Failed: snapshot.Length == 0, firstInRun));
        return (snapshot.Length > 0, snapshot);
    }

    /// <summary>A backup taken for a write of the tool call in flight, checked when the call ends.</summary>
    internal sealed record PendingWrite(string Path, string? Snapshot, bool Failed, bool FirstInRun);

    // The backups of the tool call in flight: async-local, so a call's own awaits keep its list and a parallel batch of
    // reads (which never writes) has none of its own.
    private static readonly AsyncLocal<List<PendingWrite>?> _callWrites = new();

    private static void Pending(PendingWrite write) => _callWrites.Value?.Add(write);

    /// <summary>
    /// Brackets one tool call (<see cref="ToolRegistry.ExecuteAsync"/>): when it ends, each backup it took is checked
    /// against the file, and a write that did not LAND leaves the run as if never attempted.
    /// </summary>
    /// <remarks>
    /// ⚠ A file enters the run when its backup is taken — before the write. A read-only file (a TFVC or Perforce
    /// checkout), a batch rolled back, a backup that failed (the write is then refused) or the same content written back
    /// left it counted: "edited 1" on the result bar with Undo offered, no "edits without effect" notice, the plan step
    /// marked done, a failed last check said to "a turn that changed files". Checked here, once, for every tool.
    /// </remarks>
    internal CallScope TrackCall()
    {
        var writes = new List<PendingWrite>();
        _callWrites.Value = writes;
        return new CallScope(this, writes);
    }

    /// <summary>One tool call's own backups (<see cref="TrackCall"/>).</summary>
    internal sealed class CallScope(FileHistoryService owner, List<PendingWrite> writes) : IDisposable
    {
        /// <summary>The files the call backed up itself: a watch of the work tree does not record them a second time.</summary>
        public IReadOnlyCollection<string> Paths => writes.Select(w => w.Path).ToList();

        public void Dispose()
        {
            _callWrites.Value = null;
            HistoryRun? forgotten = null;
            foreach (var write in writes)
            {
                if (Landed(write)) continue;
                lock (owner._runLock)
                {
                    // A failed backup was entered in the run without being counted as a write.
                    if (!write.Failed) owner.Run?.UncountWrite();
                    if (write.FirstInRun && owner.Run is { } run) (forgotten = run).Forget(write.Path);
                }
            }
            // Out of its file too: kept there, the write that did not land is one an undo would "give back".
            owner.PersistAsItGrows(forgotten);
        }
    }

    /// <summary>Whether a backed-up write changed the file. Unknown (unreadable) counts as landed, as before.</summary>
    private static bool Landed(PendingWrite write)
    {
        try
        {
            if (write.Failed) return false;                          // refused: a change with no backup is never made
            if (write.Snapshot is null) return File.Exists(write.Path); // a creation
            if (!File.Exists(write.Path)) return true;               // a deletion
            return !File.ReadAllBytes(write.Path).AsSpan().SequenceEqual(File.ReadAllBytes(write.Snapshot));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("FileHistoryService.Landed", ex);
            return true;
        }
    }

    /// <summary>The model-facing refusal when <see cref="BackUpBeforeChangeAsync"/> could not save a backup.</summary>
    /// <remarks>It names the folder the backup was meant for: where the history lives depends on a setting
    /// (<see cref="OutsideProject"/>), and a remedy naming the other one sends the reader to a folder nobody writes.</remarks>
    internal string BackupFailedMessage(string filePath)
    {
        const string NotChanged = "so it was NOT changed — restore_file could not have undone it.";
        string where;
        try { where = WhereNewSnapshotsGo(filePath).Dir; }
        catch (IOException) when (OutsideProject?.Invoke() == true)
        {
            // No folder to make writable: the remedy is the setting.
            return $"Error: no backup of {filePath} could be saved, {NotChanged} Inferpal is set to keep its backups in the "
                + "user's own folder, and this account has no application data folder to keep them in: setting historyStorage "
                + "back to project (Backups of changed files → In the project, in Inferpal's settings) lets the change go ahead.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { where = "the file-history folder"; }
        return $"Error: no backup of {filePath} could be saved (details in /diagnostics), {NotChanged} "
            + $"Check that {where} is writable, then retry.";
    }

    /// <summary>
    /// Deletes, for each of <paramref name="suffixes"/> (path hash + file name), the oldest snapshots carrying it
    /// beyond <see cref="MaxSnapshotsPerFile"/>. Best-effort — never throws.
    /// </summary>
    /// <remarks>
    /// ⚠ A snapshot a retained run still refers to is never deleted: it is the version <c>/undo-run</c> puts back. Counted
    /// like the others, a run that wrote one file twenty times — five <c>/tdd</c> rounds — lost its pre-run backup to its own
    /// later snapshots, and the undo reported the file as failed.
    /// </remarks>
    private void PruneOldSnapshots(string historyDir, IReadOnlyCollection<string> suffixes)
    {
        try
        {
            HashSet<string> held;
            HashSet<string> own;
            lock (_runLock)
            {
                held = new HashSet<string>(_runs.Append(_currentRun).OfType<HistoryRun>().SelectMany(HeldCopies),
                                           PathComparer.Default);
                own = _runs.Select(r => r.Id).ToHashSet();
            }
            // ⚠ The runs kept on disk hold their copies too: pruned on the live processes' runs alone, the pre-run backup
            // of a run made before a restart goes with its file's twentieth snapshot, and its undo fails.
            if (RunKey() is { } key)
                held.UnionWith(DiskRuns(own, key).SelectMany(HeldCopies));
            // ⚠ The history folder is the workspace's, shared by Visual Studio and VS Code, and the runs live in each
            // process's memory. Pruned on this process's runs alone, a pre-run backup another editor's run still holds
            // was deleted by this one's twentieth snapshot of the file, and that editor's /undo-run could no longer put
            // the file back. Each process publishes what it holds; the others honour it.
            PublishHeld(historyDir, held);
            held.UnionWith(HeldByOthers(historyDir));

            // The folder listed once, each snapshot under the suffixes its name can carry (MatchesSuffix reads both forms).
            var bySuffix = new Dictionary<string, List<string>>(PathComparer.Default);
            foreach (var file in Directory.EnumerateFiles(historyDir))
            {
                var name = Path.GetFileName(file);
                if (name.Length <= TimestampPrefixLength) continue;
                var rest = name[TimestampPrefixLength..];
                Add(rest, file);
                if (rest.EndsWith(SnapshotExtension, StringComparison.OrdinalIgnoreCase)) Add(rest[..^SnapshotExtension.Length], file);
            }
            foreach (var suffix in suffixes)
            {
                if (!bySuffix.TryGetValue(suffix, out var files)) continue;
                // Same ordering as the lookup, and for the same reason: pruning by name would delete
                // the NEWEST snapshots for an hour every autumn.
                var snaps = files.Distinct(PathComparer.Default)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ThenByDescending(f => f)
                    .Skip(MaxSnapshotsPerFile)
                    .Where(f => !held.Contains(f))
                    .ToList();
                foreach (var old in snaps)
                    try { File.Delete(old); } catch { }
            }

            void Add(string key, string file)
            {
                if (!bySuffix.TryGetValue(key, out var list)) bySuffix[key] = list = [];
                list.Add(file);
            }
        }
        catch { /* best-effort retention — never break the write path */ }
    }

    /// <summary>The copies a run needs: its pre-run backups, and what its undos replaced (what a redo puts back).</summary>
    private static IEnumerable<string> HeldCopies(HistoryRun run) =>
        run.Changes.Select(c => c.SnapshotPath).OfType<string>().Concat(run.RedoSources.Values)
           .Concat(run.Pieces.Values.Select(p => p.RunCopy));

    /// <summary>Prunes, once per history folder, the old snapshots of the files a batch backed up.</summary>
    /// <remarks>⚠ One snapshot at a time, each pruning lists the folder and rewrites the list of held snapshots: a checkout
    /// that changed two thousand files, recorded or undone, wrote that list two thousand times.</remarks>
    private void PruneBatch(IEnumerable<(string Dir, string Suffix)> pruneLater)
    {
        foreach (var folder in pruneLater.GroupBy(p => p.Dir, PathComparer.Default))
            PruneOldSnapshots(folder.Key, [.. folder.Select(p => p.Suffix).Distinct(PathComparer.Default)]);
    }

    /// <summary>The snapshots a process's runs hold, one file per service instance: <c>.held-&lt;pid&gt;-&lt;id&gt;</c>.</summary>
    private const string HeldPrefix = ".held-";

    private readonly string _instanceId = Guid.NewGuid().ToString("N")[..8];

    private string HeldFile(string historyDir) =>
        Path.Combine(historyDir, $"{HeldPrefix}{Environment.ProcessId}-{_instanceId}");

    /// <summary>Writes the snapshots of <paramref name="historyDir"/> this instance's runs hold, for the other processes.</summary>
    private void PublishHeld(string historyDir, IEnumerable<string> held)
    {
        try
        {
            var here = held.Where(p => PathComparer.SameDirectory(Path.GetDirectoryName(p) ?? "", historyDir))
                           .Select(Path.GetFileName);
            AtomicFile.WriteAllText(HeldFile(historyDir), string.Join("\n", here));
        }
        catch (Exception ex) { Diagnostics.Swallow("FileHistoryService.PublishHeld", ex); }
    }

    /// <summary>
    /// The snapshots other live instances hold in <paramref name="historyDir"/>. A list left by a process that has ended
    /// holds nothing and is removed; one whose process cannot be asked about is honoured.
    /// </summary>
    private IEnumerable<string> HeldByOthers(string historyDir)
    {
        var own = HeldFile(historyDir);
        var held = new List<string>();
        foreach (var list in Directory.EnumerateFiles(historyDir, HeldPrefix + "*"))
        {
            if (PathComparer.Default.Equals(list, own)) continue;
            var name = Path.GetFileName(list)[HeldPrefix.Length..];
            var dash = name.IndexOf('-');
            if (dash <= 0 || !int.TryParse(name[..dash], System.Globalization.NumberStyles.None,
                                           System.Globalization.CultureInfo.InvariantCulture, out var pid))
                continue;
            if (!IsAlive(pid))
            {
                try { File.Delete(list); } catch { }
                continue;
            }
            try
            {
                held.AddRange(File.ReadAllLines(list).Where(l => l.Length > 0).Select(l => Path.Combine(historyDir, l)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Swallow("FileHistoryService.HeldByOthers", ex);
            }
        }
        return held;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (Exception) { return true; }                    // cannot be asked: its list is honoured
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
    /// kept in one of that file's own history folders (<see cref="HistoryDirsOf"/>).
    /// </summary>
    /// <remarks>The history lives at the git root, which can sit above the solution root the workspace
    /// is confined to, or outside the project altogether (<see cref="OutsideProject"/>) — and every write hands the
    /// model that very snapshot path. A snapshot of THIS file (its path hash is in the name) is safe to restore
    /// wherever that folder is.</remarks>
    internal bool IsSnapshotOf(string snapshotPath, string originalPath)
    {
        try
        {
            var full = Path.GetFullPath(snapshotPath);
            // ⚠ A path IDENTITY, and the one that gates a sandbox exemption: a `true` here is what
            // lets `restore_file` accept a source outside the workspace root. Folding case on a volume
            // that does not fold makes two different directories one, so a history folder differing
            // only by case would carry the exemption. The shared rule answers "same file?" once.
            return HistoryDirsOf(originalPath).Any(history =>
                       string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(history), PathComparer.Comparison))
                   && MatchesSuffix(full, SnapshotSuffix(originalPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // Snapshots a restore took of the state it was about to replace (in memory, per session).
    private readonly HashSet<string> _restoreSnapshots = new(PathComparer.Default);
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

    /// <summary>
    /// Puts <paramref name="snapPath"/>'s content back over <paramref name="targetPath"/>, after backing up what the target
    /// holds now; <c>false</c>, and nothing written, when that backup could not be saved.
    /// </summary>
    /// <remarks>
    /// ⚠ The source is READ before the backup: the backup goes through the per-file cap, and when the source is the oldest
    /// of the snapshots kept, taking the backup deleted it — the restore failed and the version just approved was gone.
    /// </remarks>
    internal async Task<(bool Saved, string PreRestore)> RestoreWithBackupAsync(
        string snapPath, string targetPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(snapPath, ct);
        var (saved, preRestore) = await BackUpBeforeChangeAsync(targetPath, ct);
        if (!saved) return (false, string.Empty);
        // Past the backup, a Stop asked now must not leave the file half written: the write ends.
        await File.WriteAllBytesAsync(targetPath, bytes, CancellationToken.None);
        return (true, preRestore);
    }

    // ── Run grouping (for /undo-run) ─────────────────────────────────────────────
    // Held in memory while the process lives, and each run that changed or missed something is also kept on disk beside
    // the workspace's snapshots (RunManifest): /undo-run and a result bar's Undo outlive a restart of the editor.

    internal const int MaxRetainedRuns = 15;
    private readonly List<HistoryRun> _runs = [];   // chronological; newest last
    private HistoryRun? _currentRun;
    private readonly object _runLock = new();

    // ⚠ The run a call belongs to: none for a call made on behalf of a background task (OutsideRun). A /task runs while
    // a chat turn is open, through the same tools: its reads entered the CHAT's run — lifting write_file's "not read in
    // this run" guard for the chat's model, counted on its result bar, listed by /replay — and in proposal mode its own
    // writes were refused only while a turn happened to be open.
    private readonly AsyncLocal<bool> _outsideRun = new();
    private HistoryRun? Run => _outsideRun.Value ? null : _currentRun;

    /// <summary>Until the returned scope is disposed, the calls of this asynchronous flow belong to no run.</summary>
    internal IDisposable OutsideRun()
    {
        var previous = _outsideRun.Value;
        _outsideRun.Value = true;
        return new Scope(() => _outsideRun.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

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
        // it keys /undo-run, and names the run's file on disk — hence the instance: two editors that open a run in the
        // same millisecond would otherwise share one.
        var run = new HistoryRun(DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss-fff",
                                                          System.Globalization.CultureInfo.InvariantCulture)
                                 + "_" + _instanceId + "-" + Interlocked.Increment(ref _runSequence).ToString(
                                     "D4", System.Globalization.CultureInfo.InvariantCulture));
        lock (_runLock)
        {
            if (alreadyRead is not null) run.NoteRead(alreadyRead);
            _runs.Add(run);
            // ⚠ A run begins with every question asked with tools on, so the cap counts questions: dropped oldest first,
            // fifteen questions that changed nothing pushed out the run that did, and /undo-run answered "no run with
            // file changes" about an edit made an hour before. What changed nothing goes first, then what only read
            // (/replay's), and a run that changed files only when the cap holds nothing else.
            while (_runs.Count > MaxRetainedRuns)
            {
                var drop = _runs.FindIndex(r => r != run && r.FileCount == 0 && r.ToolCallCount == 0);
                if (drop < 0) drop = _runs.FindIndex(r => r != run && r.FileCount == 0);
                if (drop < 0) drop = 0;
                if (_runs[drop].ToolCallCount > 0) _forgottenRuns++;
                _runs.RemoveAt(drop);
            }
            _currentRun = run;
        }
        return run.Id;
    }

    private int _forgottenRuns;

    // Two runs begun in the same millisecond still get two ids, and two files.
    private int _runSequence;

    /// <summary>Runs that called tools and are no longer kept (past <see cref="MaxRetainedRuns"/>): what /replay can
    /// no longer show, and says.</summary>
    internal int ForgottenRuns
    {
        get { lock (_runLock) return _forgottenRuns; }
    }

    /// <summary>
    /// How many files the current run has changed — every write goes through <see cref="BackUpBeforeChangeAsync"/>,
    /// which enters the file in the run; <c>null</c> when no run is active.
    /// </summary>
    internal int? CurrentRunFileCount
    {
        get { lock (_runLock) return Run?.FileCount; }
    }

    /// <summary>Records that the model has seen the content of <paramref name="filePath"/> in the current run.</summary>
    internal void NoteRead(string filePath)
    {
        lock (_runLock) Run?.NoteRead(filePath);
    }

    /// <summary>Records that <c>read_file</c> read <paramref name="filePath"/> in the current run — a page of it counts,
    /// a write does not (see <see cref="HistoryRun.FilesOpened"/>).</summary>
    internal void NoteOpened(string filePath)
    {
        lock (_runLock) Run?.NoteOpened(filePath);
    }

    /// <summary>
    /// Records that the model has seen lines <paramref name="first"/>–<paramref name="last"/> of the
    /// <paramref name="total"/> of <paramref name="filePath"/>: the file counts as read once its pages cover it.
    /// </summary>
    internal void NoteReadLines(string filePath, int first, int last, int total)
    {
        lock (_runLock) Run?.NoteReadLines(filePath, first, last, total);
    }

    /// <summary>
    /// Of a file the model has read only in part in the current run, how many lines it has seen and the first one it has
    /// not; <c>null</c> when it has read all of it, none of it, or no run is active.
    /// </summary>
    internal (int Seen, int Total, int FirstUnseen)? PartialRead(string filePath)
    {
        lock (_runLock) return Run?.PartialRead(filePath);
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
        lock (_runLock) return Run?.WasRead(filePath);
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
        HistoryRun? ended;
        lock (_runLock)
        {
            ended = _currentRun;
            _currentRun = null;
        }
        if (ended is null) return;
        Seal(ended);   // what each file holds now: a file that differs later was changed after the run
        lock (_runLock)
            // Every file given back while it ran (/test coverage sets its tests aside mid-run): nothing of it is left, and
            // left "not undone" it would be the run /undo-run takes — answering "undone" while restoring nothing.
            if (ended.Changes.Count > 0 && ended.Changes.All(c => ended.IsGivenBack(c.OriginalPath))) ended.MarkUndone();
        Persist(ended, open: false);
    }

    // ── Runs kept on disk (RunManifest) ──────────────────────────────────────────

    // One writer of run files at a time in this instance: a state serialized before another and written after it would
    // put an older run back on disk (an undo's marks, say).
    private readonly object _persistGate = new();

    private byte[]? _runKey;
    private bool _runKeyRead;

    /// <summary>This account's run key (<see cref="RunManifest.KeyIn"/>), read once; <c>null</c>: runs stay in memory.</summary>
    private byte[]? RunKey()
    {
        lock (_persistGate)
        {
            if (!_runKeyRead) (_runKey, _runKeyRead) = (RunManifest.KeyIn(UserHistoryRoot), true);
            return _runKey;
        }
    }

    /// <summary>
    /// Writes <paramref name="run"/>'s file while the run holds a file change and a workspace is known — <paramref name="open"/>
    /// while this process still records into it —, and removes the file once the run holds none: a write that did not land
    /// leaves no run behind for an undo to "give back". Best-effort: the run stays undoable in this process either way.
    /// </summary>
    private void Persist(HistoryRun run, bool open)
    {
        if (RunKey() is not { } key) return;
        lock (_persistGate)
        {
            try
            {
                if (run.ManifestPath is { } existing) MergeFrom(run, existing, key);   // what the other editor marked stays
                string text;
                int files;
                lock (_runLock)
                {
                    files = run.FileCount;
                    // A run read from disk keeps who recorded it, and stays open if it was: a Keep in its review must
                    // not make a run cut short by a killed editor read as closed, nor one still recording elsewhere.
                    text  = files == 0 ? string.Empty
                          : RunManifest.Serialize(run, open || run.OpenElsewhere || run.CutShort, key, run.RecordedBy);
                }
                if (files == 0)
                {
                    if (run.ManifestPath is { } gone) File.Delete(gone);
                    run.ManifestPath = null;
                }
                else if (run.ManifestPath is { } path) AtomicFile.WriteAllText(path, text);
                else if (RunsDirToWrite() is { } dir)
                {
                    Directory.CreateDirectory(dir);
                    IgnoreInGit(Path.GetDirectoryName(dir)!);
                    path = Path.Combine(dir, RunManifest.FileName(run));
                    AtomicFile.WriteAllText(path, text);
                    run.ManifestPath = path;
                    TrimRuns(key);
                }
                lock (_runLock) run.PersistedFileCount = files;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Diagnostics.Swallow("FileHistoryService.Persist", ex);
            }
        }
    }

    /// <summary>
    /// Writes the run as its files change, so that an editor killed mid-run leaves a run to undo: at each change up to two
    /// hundred files, then every fifty — a checkout that changes thousands rewrites the file a few dozen times, and a run
    /// cut short past two hundred is said to be (<see cref="HistoryRun.CutShort"/>).
    /// </summary>
    private void PersistAsItGrows(HistoryRun? run)
    {
        if (run is null) return;
        int files, persisted;
        lock (_runLock) (files, persisted) = (run.FileCount, run.PersistedFileCount);
        if (files != persisted && (files <= 200 || files % 50 == 0)) Persist(run, open: true);
    }

    /// <summary>Brings into <paramref name="run"/> the marks another process wrote in its file (undone, given back).</summary>
    private void MergeFrom(HistoryRun run, string file, byte[] key)
    {
        try
        {
            if (!File.Exists(file)) return;
            var text = File.ReadAllText(file);
            lock (_runLock) RunManifest.MergeMarks(run, text, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.MergeFrom", ex);
        }
    }

    /// <summary>
    /// Keeps the newest <see cref="MaxRetainedRuns"/> signed runs of the workspace, in both runs folders: only runs that
    /// changed files are written, so the newest are the ones to keep (ids begin with their start, UTC). A file that is not
    /// one of this account's runs is never deleted.
    /// </summary>
    private void TrimRuns(byte[] key)
    {
        foreach (var old in RunFiles().Where(f => ReadRunFile(f, key) is not null)
                                      .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
                                      .Skip(MaxRetainedRuns))
            try { File.Delete(old); } catch { }
    }

    /// <summary>The anchor of the workspace root — its git work tree, else the root itself — or <c>null</c> without one.</summary>
    private string? WorkspaceAnchor()
    {
        if (WorkspaceRoot?.Invoke() is not { Length: > 0 } root) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return AnchorOfFolder(full, full);
    }

    /// <summary>Where this instance writes its runs: beside the workspace's new snapshots.</summary>
    private string? RunsDirToWrite() => WorkspaceAnchor() is { } anchor
        ? Path.Combine(OutsideProject?.Invoke() == true ? UserHistoryDir(anchor) : Path.Combine(anchor, ".inferpal", "history"),
                       RunManifest.Folder)
        : null;

    /// <summary>Every run file of the workspace: in the project and outside it, whichever setting wrote.</summary>
    private List<string> RunFiles()
    {
        var files = new List<string>();
        try
        {
            if (WorkspaceAnchor() is not { } anchor) return files;
            var dirs = new List<string> { Path.Combine(anchor, ".inferpal", "history", RunManifest.Folder) };
            try { dirs.Add(Path.Combine(UserHistoryDir(anchor), RunManifest.Folder)); }
            catch (IOException) { }   // no user folder: the project's still is one
            foreach (var dir in dirs.Where(Directory.Exists).Distinct(PathComparer.Default))
                files.AddRange(Directory.EnumerateFiles(dir, "*" + RunManifest.Extension));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Diagnostics.Swallow("FileHistoryService.RunFiles", ex);
        }
        return files;
    }

    private sealed record CachedRun(DateTime Written, long Length, RunManifest.Reading? Reading);

    // Run files parsed, by path: read again only when written since — a run of a checkout names thousands of files, each
    // checked against the workspace.
    private readonly Dictionary<string, CachedRun> _runFileCache = new(PathComparer.Default);

    /// <summary>
    /// The signed run in <paramref name="file"/>, every path it names under the workspace's anchor and every copy one of
    /// its file's own; <c>null</c> otherwise, said once per version of the file.
    /// </summary>
    private RunManifest.Reading? ReadRunFile(string file, byte[] key)
    {
        var info = new FileInfo(file);
        if (!info.Exists) return null;
        lock (_runFileCache)
            if (_runFileCache.TryGetValue(file, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Length == info.Length)
                return hit.Reading;

        RunManifest.Reading? reading = null;
        string? refusal;
        try
        {
            var anchor = WorkspaceAnchor();
            reading = RunManifest.Parse(File.ReadAllText(file), key,
                                        (path, copy) => anchor is not null && Tools.PathSanitizer.IsUnderRoot(path, anchor)
                                                        && (copy is null || IsSnapshotOf(copy, path)),
                                        out refusal);
            if (reading is not null && reading.Run.Id != Path.GetFileNameWithoutExtension(file))
                (reading, refusal) = (null, "its id is not its file's name");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = "could not be read: " + ex.Message;
        }
        if (reading is null)
            Diagnostics.RecordOnce("FileHistoryService.Runs", $"A run kept on disk was left out ({file}): {refusal}",
                                   file + "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        else
        {
            reading.Run.ManifestPath       = file;
            reading.Run.PersistedFileCount = reading.Run.FileCount;
        }
        lock (_runFileCache) _runFileCache[file] = new CachedRun(info.LastWriteTimeUtc, info.Length, reading);
        return reading;
    }

    /// <summary>
    /// The workspace's runs kept on disk whose ids are not in <paramref name="own"/> — written by an earlier process, or by
    /// the other editor. One still recorded into by a live process is marked so (<see cref="HistoryRun.OpenElsewhere"/>).
    /// </summary>
    private List<HistoryRun> DiskRuns(IReadOnlySet<string> own, byte[] key)
    {
        var runs = new List<HistoryRun>();
        foreach (var file in RunFiles())
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (own.Contains(id) || runs.Any(r => r.Id == id) || ReadRunFile(file, key) is not { } reading) continue;
            // shortcut: a process id reused after a crash reads as alive, and the run waits until that process ends.
            reading.Run.OpenElsewhere = reading.Open && IsAlive(reading.Pid);
            reading.Run.CutShort      = reading.Open && !reading.Run.OpenElsewhere;
            reading.Run.RecordedBy    = reading.Pid;
            runs.Add(reading.Run);
        }
        return runs;
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
        HistoryRun? run;
        lock (_runLock)
        {
            run = Run;
            run?.RecordFirst(filePath, snapshot: null);
            run?.CountWrite();
        }
        PersistAsItGrows(run);
    }

    /// <summary>
    /// How many writes the current run has made, each counted as its backup is taken — every one, where
    /// <see cref="CurrentRunFileCount"/> counts each file once; <c>null</c> when no run is active.
    /// </summary>
    internal int? CurrentRunWriteCount
    {
        get { lock (_runLock) return Run?.WriteCount; }
    }

    /// <summary>Appends a tool invocation to the current run's journal (for <c>/replay</c>).
    /// No-op when no run is active (e.g. code actions, tools disabled).</summary>
    internal void RecordToolCall(string tool, string? subject, long durationMs, bool error)
    {
        lock (_runLock) Run?.RecordToolCall(tool, subject, durationMs, error);
    }

    private void RecordInRun(string filePath, string snapPath)
    {
        HistoryRun? run;
        lock (_runLock)
        {
            run = Run;
            run?.RecordFirst(filePath, snapPath);
            run?.CountWrite();
        }
        PersistAsItGrows(run);
    }

    /// <summary>Whether a run is tracking this flow's calls — no run, no watch (a code action, a slash command, a task).</summary>
    internal bool IsTracking
    {
        get { lock (_runLock) return Run is not null; }
    }

    /// <summary>
    /// Records in the run what a watched call (<see cref="WorkTreeBracket"/>) changed in the work tree without backing it
    /// up — a command, a test run, an MCP tool —, as if each change were one of its writes; and what it could not cover.
    /// </summary>
    /// <param name="ownWrites">Files the call backed up itself (its own writes): already in the run.</param>
    /// <param name="call">The call as <c>/undo-run</c> names it: the tool and its subject.</param>
    /// <remarks>
    /// ⚠ The first change of a file in the run stays the one an undo goes back to (<see cref="HistoryRun.RecordFirst"/>):
    /// a file a command changes after a <c>write_file</c> of the run is restored to what it held before the run, and a
    /// file the run created and a command then changed is still deleted.
    /// </remarks>
    internal async Task RecordWatchedCallAsync(WorkTreeBracket bracket, IReadOnlyCollection<string> ownWrites, string call,
                                               CancellationToken ct)
    {
        // Refused at its card or by a rule, the call ran nothing: what changed meanwhile is someone else's.
        if (bracket.Refused) return;

        IReadOnlyList<WorkTreeChange> changes;
        IReadOnlyList<UncoveredNote> notes;
        try { (changes, notes) = await WorkTreeWatch.CompareAsync(bracket.Before, ct); }
        catch (OperationCanceledException)
        {
            // Out of time: nothing is recorded, and that is said rather than left to look like a complete undo.
            (changes, notes) = ([], [new UncoveredNote(UncoveredReason.ReadFailed)]);
        }
        var own = new HashSet<string>(ownWrites, PathComparer.Default);
        lock (_runLock)
        {
            if (Run is not { } run) return;
            run.CountWatchedCall();
            foreach (var note in notes.Concat(bracket.Notes)) run.NoteUncovered(call, note);
        }
        // ⚠ The bytes are in hand: written without the budget, every change found is recorded. Stopped halfway, the loop
        // left part of a `git stash` out of the run with nothing said, and a backup half written.
        var pruneLater = new List<(string Dir, string Suffix)>();
        try
        {
            foreach (var change in changes)
            {
                if (own.Contains(change.Path)) continue;
                bool known;
                lock (_runLock)
                {
                    if (Run is not { } run) return;
                    known = run.Has(change.Path);
                    if (known) run.CountWrite();
                }
                if (known) continue;
                if (change.Before is null) NoteCreated(change.Path);
                else await SnapshotBytesAsync(change.Path, change.Before, CancellationToken.None, pruneLater);
            }
        }
        finally
        {
            PruneBatch(pruneLater);
        }
    }

    /// <summary>All tracked runs, most recent first: this instance's, and the workspace's kept on disk — by an earlier
    /// process or by the other editor, read again at each call (a later run of the other editor holds an undo back).</summary>
    internal IReadOnlyList<HistoryRun> Runs
    {
        get
        {
            List<HistoryRun> own;
            lock (_runLock) own = Enumerable.Reverse(_runs).ToList();
            if (RunKey() is not { } key) return own;
            // Undone in the other editor is undone here: this instance's copy of a run would undo it a second time.
            foreach (var run in own)
                if (run.ManifestPath is { } file) MergeFrom(run, file, key);
            var disk = DiskRuns(own.Select(r => r.Id).ToHashSet(), key);
            // By id, which begins with the start in UTC: local times repeat an hour every autumn.
            return disk.Count == 0 ? own : own.Concat(disk).OrderByDescending(r => r.Id, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Reverts every file changed during <paramref name="run"/> to its pre-run state: restores the first snapshot taken
    /// that run, or deletes files that were created during it — save the files the person kept, and those changed since
    /// the run ended, which the undo would overwrite: both are left as they are, and named in the result.
    /// </summary>
    internal async Task<RunUndoResult> UndoRunAsync(HistoryRun run, CancellationToken ct)
    {
        // A retry after a failure: the files given back by an earlier undo are not touched again.
        List<RunChange> pending;
        List<string> kept;
        lock (_runLock)
        {
            pending = run.Changes.Where(c => !run.IsGivenBack(c.OriginalPath) && !run.IsKept(c.OriginalPath)).ToList();
            // Kept, then undone by hand from the review: given back, so not "left as it is".
            kept    = run.Changes.Select(c => c.OriginalPath).Where(p => run.IsKept(p) && !run.IsGivenBack(p)).ToList();
        }
        var later = TouchedLater(run);
        var changedSince = pending.Where(c => ChangedSinceRun(run, c.OriginalPath, later)).Select(c => c.OriginalPath).ToList();
        pending.RemoveAll(c => changedSince.Contains(c.OriginalPath, PathComparer.Default));
        // A file with changes the person kept: every other change given back, those left as the run made them.
        List<RunChange> partly;
        lock (_runLock) partly = pending.Where(c => run.PiecesOf(c.OriginalPath) is { Kept.Count: > 0 }).ToList();
        pending.RemoveAll(partly.Contains);

        var result = await UndoChangesAsync(run, pending, ct);
        var piecesKept = new List<string>();
        var savedFirst = 0;
        foreach (var change in partly)
        {
            var (outcome, _) = await ApplyPiecesAsync(run, change, (pieces, undone, keep) =>
            {
                var before = undone.Count;
                undone.UnionWith(Enumerable.Range(0, pieces.Pieces.Count).Where(i => !keep.Contains(i)));
                return undone.Count > before ? PieceOutcome.Undone : PieceOutcome.Unchanged;
            }, ct);
            if (outcome == PieceOutcome.ChangedSince) changedSince.Add(change.OriginalPath);
            else if (outcome is PieceOutcome.Undone or PieceOutcome.Unchanged)
            {
                piecesKept.Add(change.OriginalPath);
                // Written, and saved first: counted with the files put back, and named apart for what it keeps.
                if (outcome == PieceOutcome.Undone)
                {
                    result.Restored.Add(change.OriginalPath);
                    savedFirst++;
                }
            }
            else result.Failed.Add(change.OriginalPath);
        }
        lock (_runLock)
            if (result.Failed.Count == 0) run.MarkUndone();
        Persist(run, open: false);   // undone stays undone after a restart, and a retry gives back only what failed
        return result with { ChangedSince = changedSince, Kept = kept, PiecesKept = piecesKept,
                             SavedFirst = result.SavedFirst + savedFirst };
    }

    /// <summary>Puts <paramref name="path"/> back as it was before <paramref name="run"/>: one file of the run's review.</summary>
    internal async Task<FileUndoOutcome> UndoFileAsync(HistoryRun run, string path, CancellationToken ct)
    {
        if (ChangeOf(run, path) is not { } change) return FileUndoOutcome.NotInRun;
        lock (_runLock)
            if (run.IsGivenBack(change.OriginalPath)) return FileUndoOutcome.AlreadyUndone;
        if (ChangedSinceRun(run, change.OriginalPath, TouchedLater(run))) return FileUndoOutcome.ChangedSince;
        var result = await UndoChangesAsync(run, [change], ct);
        bool open;
        lock (_runLock)
        {
            // ⚠ Not while the run still records (/test coverage sets a test aside mid-run): marked undone, the files it
            // writes next would be refused by /undo-run as already given back.
            open = ReferenceEquals(run, _currentRun);
            // Every file given back one by one is the whole run undone: an earlier run's undo is no longer held back.
            if (!open && run.Changes.All(c => run.IsGivenBack(c.OriginalPath))) run.MarkUndone();
        }
        Persist(run, open);
        return result.Failed.Count > 0 ? FileUndoOutcome.Failed
             : result.Restored.Count > 0 ? FileUndoOutcome.Restored
             : result.Deleted.Count > 0 ? FileUndoOutcome.Deleted
             : FileUndoOutcome.AlreadyUndone;   // created by the run and already gone: nothing was saved, nothing to redo
    }

    /// <summary>
    /// Gives <paramref name="path"/> back the version an undo replaced (<see cref="HistoryRun.RedoSourceOf"/>): an undo,
    /// undone. What the file holds then is saved first, as for any write without an approval card.
    /// </summary>
    internal async Task<FileUndoOutcome> RedoFileAsync(HistoryRun run, string path, CancellationToken ct)
    {
        if (ChangeOf(run, path) is not { } change) return FileUndoOutcome.NotInRun;
        string? copy;
        bool piecesUndone;
        lock (_runLock)
        {
            copy = run.IsGivenBack(change.OriginalPath) ? run.RedoSourceOf(change.OriginalPath) : null;
            piecesUndone = !run.IsGivenBack(change.OriginalPath) && run.PiecesOf(change.OriginalPath) is { Undone.Count: > 0 };
        }
        // Changes given back one by one (or by an undo of the run that left kept ones): each given the run's version again.
        if (piecesUndone)
            return await ApplyPiecesAsync(run, change, (_, undone, _) =>
            {
                undone.Clear();
                return PieceOutcome.Redone;
            }, ct) switch
            {
                (PieceOutcome.Redone, _)       => FileUndoOutcome.Redone,
                (PieceOutcome.ChangedSince, _) => FileUndoOutcome.ChangedSinceUndo,
                // The run's copy the changes are cut from is gone: nothing left to redo from.
                (PieceOutcome.NoCopy, _)       => FileUndoOutcome.NothingToRedo,
                _                              => FileUndoOutcome.Failed,
            };
        if (copy is null) return FileUndoOutcome.NothingToRedo;
        // ⚠ The redo writes the run's version over what the undo left: changed since, that change would be lost.
        if (!AsTheUndoLeft(change)) return FileUndoOutcome.ChangedSinceUndo;
        var pruneLater = new List<(string Dir, string Suffix)>();
        try
        {
            if (!File.Exists(copy))
            {
                Diagnostics.Record("FileHistoryService.RedoFile", $"the copy to redo from is gone: {copy}");
                return FileUndoOutcome.Failed;
            }
            var bytes = await File.ReadAllBytesAsync(copy, ct);
            if (File.Exists(change.OriginalPath) && await SnapshotAsync(change.OriginalPath, ct, pruneLater) is not { Length: > 0 })
                return FileUndoOutcome.Failed;   // no backup, no overwrite
            if (Path.GetDirectoryName(change.OriginalPath) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            // Backed up: a stop asked now must not leave the file half written.
            await File.WriteAllBytesAsync(change.OriginalPath, bytes, CancellationToken.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.RedoFile", ex);
            return FileUndoOutcome.Failed;
        }
        finally
        {
            PruneBatch(pruneLater);
        }
        lock (_runLock)
        {
            run.NoteRedone(change.OriginalPath);
            run.ReopenUndo();
        }
        Persist(run, open: false);
        return FileUndoOutcome.Redone;
    }

    /// <summary>The person keeps <paramref name="path"/> as the run left it: Undo run leaves it alone.</summary>
    internal FileUndoOutcome KeepFile(HistoryRun run, string path)
    {
        if (ChangeOf(run, path) is not { } change) return FileUndoOutcome.NotInRun;
        lock (_runLock) run.NoteKept(change.OriginalPath);
        Persist(run, open: false);
        return FileUndoOutcome.Kept;
    }

    /// <summary>
    /// One change of <paramref name="path"/> (<see cref="RunPiece.Index"/>) given back, given the run's version again, or
    /// kept: the file is rewritten as <see cref="RunPieces.Compose"/> of its marks, what it held saved first.
    /// </summary>
    internal async Task<(PieceOutcome Outcome, RunPieces.Whole Why)> PieceActionAsync(
        HistoryRun run, string path, int index, PieceAction action, CancellationToken ct)
    {
        if (ChangeOf(run, path) is not { } change) return (PieceOutcome.NotInRun, RunPieces.Whole.None);
        lock (_runLock)
            if (run.IsGivenBack(change.OriginalPath)) return (PieceOutcome.GivenBackWhole, RunPieces.Whole.None);
        return await ApplyPiecesAsync(run, change, (pieces, undone, kept) =>
        {
            if (index < 0 || index >= pieces.Pieces.Count) return PieceOutcome.NoSuchPiece;
            switch (action)
            {
                case PieceAction.Undo:
                    if (!undone.Add(index)) return PieceOutcome.Unchanged;
                    kept.Remove(index);
                    return PieceOutcome.Undone;
                case PieceAction.Redo:
                    return undone.Remove(index) ? PieceOutcome.Redone : PieceOutcome.Unchanged;
                default:
                    // Given back, it holds the state before the run: "kept as the run made it" would be false.
                    if (undone.Contains(index)) return PieceOutcome.KeepGivenBack;
                    return kept.Add(index) ? PieceOutcome.Kept : PieceOutcome.Unchanged;
            }
        }, ct);
    }

    // One piece gesture at a time in this process: two would read the same marks, and the second write drop the first.
    private readonly SemaphoreSlim _pieceGate = new(1, 1);

    /// <summary>
    /// Cuts the file into its changes, lets <paramref name="decide"/> change which are given back and kept, and writes what
    /// they compose — after checking the file holds what its marks compose now, and saving it.
    /// </summary>
    private async Task<(PieceOutcome Outcome, RunPieces.Whole Why)> ApplyPiecesAsync(
        HistoryRun run, RunChange change, Func<RunPieces, HashSet<int>, HashSet<int>, PieceOutcome> decide, CancellationToken ct)
    {
        var path = change.OriginalPath;
        // Created by the run: one change by nature, given back whole.
        if (change.SnapshotPath is null && !change.SnapshotFailed) return (PieceOutcome.WholeOnly, RunPieces.Whole.Created);
        if (change.SnapshotFailed || !File.Exists(change.SnapshotPath)) return (PieceOutcome.NoCopy, RunPieces.Whole.None);
        await _pieceGate.WaitAsync(ct);
        var pruneLater = new List<(string Dir, string Suffix)>();
        try
        {
            // ⚠ Judged first, then the marks read: Runs brings in what the other editor marked on disk, its end hash with
            // its marks — marks read before would cut the changes from a file that is no longer the run's version.
            if (ChangedSinceRun(run, path, TouchedLater(run))) return (PieceOutcome.ChangedSince, RunPieces.Whole.None);
            // Not changed since, and gone: the run deleted it.
            if (!File.Exists(path)) return (PieceOutcome.WholeOnly, RunPieces.Whole.Created);
            PieceMarks? marks;
            lock (_runLock) marks = run.PiecesOf(path);
            if (marks is not null && !File.Exists(marks.RunCopy)) return (PieceOutcome.NoCopy, RunPieces.Whole.None);
            var now = await File.ReadAllBytesAsync(path, ct);
            var runVersion = marks is null ? now : await File.ReadAllBytesAsync(marks.RunCopy, ct);
            if (RunPieces.Of(await File.ReadAllBytesAsync(change.SnapshotPath!, ct), runVersion, out var whole) is not { } pieces)
                return (PieceOutcome.WholeOnly, whole);
            var undone = (marks?.Undone ?? []).ToHashSet();
            var kept   = (marks?.Kept ?? []).ToHashSet();
            // ⚠ The file must hold what its marks compose: else it changed since — by hand, or by the other editor's
            // gesture not read yet — and the write would lose that change.
            // shortcut: two editors making a first gesture on the same file in the same moment both write generation 1, and
            // the one that loses on disk is refused here until a restart; serialize across processes if that is ever seen.
            if (!pieces.Compose(undone).AsSpan().SequenceEqual(now)) return (PieceOutcome.ChangedSince, RunPieces.Whole.None);
            var was = undone.ToHashSet();
            var outcome = decide(pieces, undone, kept);
            if (outcome is not (PieceOutcome.Undone or PieceOutcome.Redone or PieceOutcome.Kept)) return (outcome, RunPieces.Whole.None);
            var write = !undone.SetEquals(was);
            var runCopy = marks?.RunCopy;
            if (runCopy is null || write)
            {
                // The bytes just checked: the run's version the changes are cut from on the first gesture, and the backup
                // of a write — no backup, no write. Read again, a save in between would make the copy another file.
                if (await SnapshotBytesAsync(path, now, ct, pruneLater) is not { Length: > 0 } saved)
                    return (PieceOutcome.Failed, RunPieces.Whole.None);
                runCopy ??= saved;
            }
            var bytes = pieces.Compose(undone);
            // Backed up: a stop asked now must not leave the file half written.
            if (write) await File.WriteAllBytesAsync(path, bytes, CancellationToken.None);
            bool open;
            lock (_runLock)
            {
                run.NotePieces(path, new PieceMarks((marks?.Generation ?? 0) + 1, runCopy, [.. undone.Order()],
                                                    [.. kept.Order()], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))));
                // A change given the run's version again: the run is no longer wholly undone.
                if (outcome == PieceOutcome.Redone) run.ReopenUndo();
                open = ReferenceEquals(run, _currentRun);
            }
            Persist(run, open);
            return (outcome, RunPieces.Whole.None);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.ApplyPieces", ex);
            return (PieceOutcome.Failed, RunPieces.Whole.None);
        }
        finally
        {
            PruneBatch(pruneLater);
            _pieceGate.Release();
        }
    }

    private RunChange? ChangeOf(HistoryRun run, string path)
    {
        lock (_runLock) return run.Changes.FirstOrDefault(c => PathComparer.Default.Equals(c.OriginalPath, path));
    }

    /// <summary>
    /// Whether <paramref name="path"/> holds something else than when <paramref name="run"/> ended — changed since by the
    /// person, or by a later run (<paramref name="touchedLater"/>, from <see cref="TouchedLater"/>).
    /// </summary>
    /// <remarks>⚠ A run whose end was never noted (cut short) has no hash to compare: a later run that changed the file
    /// still holds its undo back, or the state before this run would overwrite what that run wrote.</remarks>
    internal bool ChangedSinceRun(HistoryRun run, string path, IReadOnlySet<string> touchedLater)
    {
        if (touchedLater.Contains(path)) return true;
        string? after;
        lock (_runLock) after = run.AfterOf(path);
        if (after is null || ContentHash(path) is not { } now || now == after) return false;
        // Already back as it was before the run (a created file deleted by hand): giving it back loses nothing.
        return ChangeOf(run, path) is not { } change || !AsTheUndoLeft(change);
    }

    /// <summary>The files the runs after <paramref name="run"/> changed and have not given back.</summary>
    internal IReadOnlySet<string> TouchedLater(HistoryRun run)
    {
        var later = new HashSet<string>(PathComparer.Default);
        foreach (var other in Runs.Where(r => string.CompareOrdinal(r.Id, run.Id) > 0))
            lock (_runLock)
                foreach (var change in other.Changes)
                    if (!other.IsGivenBack(change.OriginalPath)) later.Add(change.OriginalPath);
        return later;
    }

    /// <summary>Whether a file holds its state from before the run — what an undo leaves: the copy taken then, or no
    /// file for one the run created.</summary>
    internal static bool AsTheUndoLeft(RunChange change) => change.SnapshotPath is { } before
        ? ContentHash(change.OriginalPath) is { Length: > 0 } now && now == ContentHash(before)
        : !change.SnapshotFailed && !File.Exists(change.OriginalPath);

    /// <summary>The SHA-256 of <paramref name="path"/>'s bytes, "" for no file, <c>null</c> when it cannot be read.</summary>
    internal static string? ContentHash(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        // Called where nothing may throw (the end of a turn): any path the system refuses is "cannot be read".
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>What a review of <paramref name="run"/> reads, taken at once under the lock its marks are written under.</summary>
    internal RunMarks MarksOf(HistoryRun run)
    {
        lock (_runLock)
            return new RunMarks(
                [.. run.Changes],
                run.Changes.Select(c => c.OriginalPath).Where(run.IsGivenBack).ToHashSet(PathComparer.Default),
                run.Changes.Select(c => c.OriginalPath).Where(run.IsKept).ToHashSet(PathComparer.Default),
                run.Changes.Where(c => run.RedoSourceOf(c.OriginalPath) is { } copy && File.Exists(copy))
                           .Select(c => c.OriginalPath).ToHashSet(PathComparer.Default),
                run.Pieces.ToDictionary(p => p.Key, p => p.Value, PathComparer.Default));
    }

    /// <summary>Notes what each file of <paramref name="run"/> holds as it ends (<see cref="HistoryRun.AfterOf"/>).</summary>
    private void Seal(HistoryRun run)
    {
        List<string> paths;
        lock (_runLock) paths = run.Changes.Select(c => c.OriginalPath).ToList();
        var hashes = paths.Select(p => (Path: p, Hash: ContentHash(p))).ToList();
        lock (_runLock)
            foreach (var (path, hash) in hashes)
                if (hash is not null) run.NoteAfter(path, hash);
    }

    /// <summary>
    /// What both undos share: each change given back — restored from its first snapshot, or deleted when the run created
    /// it —, what it replaces saved first and kept for a redo.
    /// </summary>
    private async Task<RunUndoResult> UndoChangesAsync(HistoryRun run, List<RunChange> pending, CancellationToken ct)
    {
        var restored = new List<string>();
        var deleted  = new List<string>();
        var failed   = new List<string>();
        var savedFirst = 0;
        var redo = new Dictionary<string, string>(PathComparer.Default);

        var pruneLater = new List<(string Dir, string Suffix)>();
        try
        {
            foreach (var change in pending)
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
                        // ⚠ No backup, no deletion: the file may hold work done since the run, and nothing could bring it back.
                        string? saved = null;
                        if (File.Exists(change.OriginalPath)
                            && (saved = await SnapshotAsync(change.OriginalPath, ct, pruneLater)) is not { Length: > 0 })
                        {
                            failed.Add(change.OriginalPath);
                            continue;
                        }
                        // ⚠ The CURRENT state is captured first, and that is the half that was missing.
                        // Undoing a run writes with no approval prompt — the only write path of the
                        // product that does — and what the run wrote may have been edited since: by
                        // hand, by a later run, by a merge. Without this line a file CREATED by the run
                        // and then filled in by the user was deleted without a trace, and a modified
                        // file was overwritten by a state older than the user's own corrections. The
                        // refusal two branches above already said it, for the other case.
                        if (File.Exists(change.OriginalPath))
                        {
                            savedFirst++;
                            File.Delete(change.OriginalPath);
                            deleted.Add(change.OriginalPath);
                            redo[change.OriginalPath] = saved!;
                        }
                    }
                    else if (File.Exists(change.SnapshotPath))
                    {
                        // Read before the backup of the current state, which goes through the per-file cap.
                        var bytes = await File.ReadAllBytesAsync(change.SnapshotPath, ct);
                        if (File.Exists(change.OriginalPath))
                        {
                            if (await SnapshotAsync(change.OriginalPath, ct, pruneLater) is not { Length: > 0 } saved)
                            {
                                failed.Add(change.OriginalPath);   // no backup, no overwrite
                                continue;
                            }
                            savedFirst++;
                            redo[change.OriginalPath] = saved;
                        }
                        // A command deletes a folder with its files (`rm -rf`): giving a file back gives its folder back.
                        if (Path.GetDirectoryName(change.OriginalPath) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
                        // Backed up: a stop asked now must not leave the file half written.
                        await File.WriteAllBytesAsync(change.OriginalPath, bytes, CancellationToken.None);
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
        }
        finally
        {
            PruneBatch(pruneLater);
        }

        lock (_runLock)
        {
            // Given back: restored, deleted, or a created file already gone — everything that did not fail.
            foreach (var change in pending.Where(c => !failed.Contains(c.OriginalPath, PathComparer.Default)))
                run.NoteGivenBack(change.OriginalPath);
            foreach (var (path, copy) in redo) run.NoteRedoSource(path, copy);
        }
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

    /// <summary>
    /// Whether new snapshots go to the user's own application data instead of the project (<c>historyStorage: user</c>).
    /// Set by the tool registry, which reads the configuration at each call; <c>null</c> (a bare service) keeps them in
    /// the project.
    /// </summary>
    internal Func<bool>? OutsideProject { get; set; }

    /// <summary>The folder the histories kept outside the project live under, one sub-folder per repository; tests point
    /// it elsewhere.</summary>
    internal string UserHistoryRoot { get; set; } = DefaultUserHistoryRoot;

    /// <summary><c>Inferpal/history</c> in the user's LOCAL application data.</summary>
    /// <remarks>⚠ Local, not roaming: a snapshot is a copy of whatever was overwritten, secrets included, and a roaming
    /// profile would carry every one of them to each machine the person signs in to.</remarks>
    internal static string DefaultUserHistoryRoot => DefaultRoot.Value;

    private static readonly Lazy<string> DefaultRoot = new(() =>
        Path.Combine(UserFolders.Of(Environment.SpecialFolder.LocalApplicationData), "Inferpal", "history"));

    /// <summary>Whether <paramref name="fullPath"/> is in, or is, the folder the histories kept outside the project live
    /// under (<see cref="DefaultUserHistoryRoot"/>).</summary>
    /// <remarks>⚠ What keeps those copies unread by the model's tools when a workspace holds the user's application data —
    /// one opened on the home folder: in the project the history sits under <c>.inferpal</c>, a folder no walk enters, and
    /// outside it nothing marked it. Asked for every folder a walk meets: the root is read once.</remarks>
    internal static bool InUserHistoryRoot(string fullPath)
    {
        var root = DefaultUserHistoryRoot;
        if (!Path.IsPathFullyQualified(root)) return false;
        var path = Path.TrimEndingDirectorySeparator(fullPath);
        return string.Equals(path, root, PathComparer.Comparison)
               || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparer.Comparison)
               || path.StartsWith(root + Path.AltDirectorySeparatorChar, PathComparer.Comparison);
    }

    /// <summary>
    /// Where a new snapshot of <paramref name="filePath"/> goes, and — when that folder is outside the project — the
    /// folder its history belongs to (<see cref="NameTheOwner"/>).
    /// </summary>
    private (string Dir, string? Owner) WhereNewSnapshotsGo(string filePath)
    {
        var anchor = AnchorOf(filePath, WorkspaceRoot?.Invoke());
        return OutsideProject?.Invoke() == true
            ? (UserHistoryDir(anchor), anchor)
            : (Path.Combine(anchor, ".inferpal", "history"), null);
    }

    /// <summary>
    /// Every folder a snapshot of <paramref name="filePath"/> can be in, the one new snapshots go to first: the project's,
    /// the one outside it — either setting may have written —, and the one found without the workspace root (outside git,
    /// the file's own folder), which may still hold backups.
    /// </summary>
    /// <remarks>⚠ Both settings' folders, whichever is chosen now: switching would otherwise leave every backup made
    /// before out of <c>/restore</c> and <c>restore_file</c>, and nothing is ever moved from one to the other.</remarks>
    private IEnumerable<string> HistoryDirsOf(string filePath)
    {
        var anchor  = AnchorOf(filePath, WorkspaceRoot?.Invoke());
        var project = Path.Combine(anchor, ".inferpal", "history");
        string? user;
        try { user = UserHistoryDir(anchor); }
        catch (IOException) { user = null; }   // no user folder to read from: the project's still is one

        string?[] dirs = OutsideProject?.Invoke() == true ? [user, project] : [project, user];
        return dirs.Append(GetHistoryDir(filePath))
                   .OfType<string>()
                   .Select(Path.GetFullPath)
                   .Distinct(PathComparer.Default);
    }

    /// <summary>The history folders of <paramref name="filePath"/> that exist (<see cref="HistoryDirsOf"/>).</summary>
    private IEnumerable<string> HistoryDirsToRead(string filePath) => HistoryDirsOf(filePath).Where(Directory.Exists);

    /// <summary>The git root's <c>.inferpal/history</c>; outside git, the workspace root's when the file is under
    /// <paramref name="workspaceRoot"/>; else the file's own folder.</summary>
    internal static string GetHistoryDir(string filePath, string? workspaceRoot = null) =>
        Path.Combine(AnchorOf(filePath, workspaceRoot), ".inferpal", "history");

    /// <summary>The folder a file's history belongs to: its git work tree; outside git, <paramref name="workspaceRoot"/>
    /// when the file is under it; else the file's own folder. One history per anchor, wherever it is kept.</summary>
    private static string AnchorOf(string filePath, string? workspaceRoot)
    {
        var full = Path.GetFullPath(filePath);
        return AnchorOfFolder(Path.GetDirectoryName(full) ?? ".", workspaceRoot);
    }

    /// <summary>The same rule for a folder: its git work tree; outside git, <paramref name="workspaceRoot"/> when the
    /// folder is it or under it; else the folder itself. The one place the rule is written.</summary>
    private static string AnchorOfFolder(string folder, string? workspaceRoot)
    {
        if (FindGitRoot(folder) is { } git) return git;
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
            var dir  = Path.TrimEndingDirectorySeparator(folder);
            if (string.Equals(dir, root, PathComparer.Comparison)
                || dir.StartsWith(root + Path.DirectorySeparatorChar, PathComparer.Comparison))
                return root;
        }
        return folder;
    }

    /// <summary>
    /// The folder, under <see cref="UserHistoryRoot"/>, of the history of <paramref name="anchor"/> kept outside the
    /// project: named after the anchor's folder so a person can tell whose it is, and told apart by a hash of its path.
    /// </summary>
    /// <remarks>
    /// ⚠ The hash follows the shared rule for "same folder?" (<see cref="PathComparer"/>): folded where the file system
    /// folds case, never under Linux, where two repositories that differ only by case are two histories — merged, each
    /// would restore the other's files and prune the other's backups. Visual Studio and VS Code on one repository land
    /// on the same folder, as they share the project's one — links and junctions resolved first, so a repository reached
    /// through one is still one history (a <c>subst</c> drive, or a mapped drive and its UNC path, are not resolved).
    /// ⚠ Throws when there is no absolute folder to put it under (no application data for this account): a relative one
    /// would resolve against the process's working directory — the project, often — and the setting is the promise that
    /// nothing is written there. The snapshot then fails, and the write it guards is refused.
    /// </remarks>
    internal string UserHistoryDir(string anchor)
    {
        if (!Path.IsPathFullyQualified(UserHistoryRoot))
            throw new IOException($"the history is set to be kept outside the project, and this account has no application "
                                  + $"data folder to keep it in (\"{UserHistoryRoot}\")");
        var full  = Path.TrimEndingDirectorySeparator(Tools.PathSanitizer.Resolved(Path.GetFullPath(anchor)));
        var folds = PathComparer.Comparison != StringComparison.Ordinal;
        var hash  = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(folds ? full.ToUpperInvariant() : full)).AsSpan(0, 6)).ToLowerInvariant();
        // ⚠ The name folds too: with the hash alone folded, "C:\Dev\App" and "c:\dev\app" — two spellings one editor and
        // the other hand over for one folder — name two histories.
        var name  = Path.GetFileName(full);
        if (folds) name = name.ToLowerInvariant();
        if (name.Length > 40) name = name[..(char.IsHighSurrogate(name[39]) ? 39 : 40)];   // never half a character
        return Path.Combine(UserHistoryRoot, name.Length == 0 ? hash : $"{name}-{hash}");
    }

    /// <summary>The file, in a history kept outside the project, that names the folder it belongs to.</summary>
    internal const string OwnerFileName = "workspace.txt";

    /// <summary>Writes, once, which folder a history kept outside the project belongs to — for a person cleaning up
    /// histories of repositories that no longer exist. Best-effort: the snapshot does not depend on it.</summary>
    /// <remarks>Never taken for a snapshot: every snapshot name starts with a date, and the lookups and the pruning read
    /// nothing else.</remarks>
    private static void NameTheOwner(string historyDir, string owner)
    {
        var path = Path.Combine(historyDir, OwnerFileName);
        if (File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(System.Text.Encoding.UTF8.GetBytes(owner + "\n"));
        }
        catch (IOException) when (File.Exists(path)) { }   // written meanwhile by another snapshot
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("FileHistoryService.NameTheOwner", ex);
        }
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
/// <param name="ChangedSince">Files left as they are: changed after the run ended, so the undo would lose that change.</param>
/// <param name="Kept">Files left as they are: the person kept them.</param>
/// <param name="PiecesKept">Files given back but for the changes the person kept.</param>
internal sealed record RunUndoResult(List<string> Restored, List<string> Deleted, List<string> Failed,
                                     int SavedFirst = 0, List<string>? ChangedSince = null, List<string>? Kept = null,
                                     List<string>? PiecesKept = null);

/// <summary>What became of one file of a run's review (<see cref="FileHistoryService.UndoFileAsync"/> and the others).</summary>
internal enum FileUndoOutcome
{
    Restored, Deleted, Redone, Kept, ChangedSince, ChangedSinceUndo, AlreadyUndone, NothingToRedo, NotInRun, Failed,
}

/// <summary>A run's files and marks as one review reads them (<see cref="FileHistoryService.MarksOf"/>).</summary>
/// <param name="Redoable">Files given back whose copy to redo from is still on disk.</param>
/// <param name="Pieces">Files whose changes were kept or given back one by one.</param>
internal sealed record RunMarks(
    IReadOnlyList<RunChange> Changes, IReadOnlySet<string> GivenBack, IReadOnlySet<string> Kept, IReadOnlySet<string> Redoable,
    IReadOnlyDictionary<string, PieceMarks> Pieces);

/// <summary>The changes of one file of a run kept or given back one by one (<see cref="RunPieces"/>).</summary>
/// <param name="Generation">Grows with each gesture: the newer marks win when two editors share the run.</param>
/// <param name="RunCopy">The file as the run left it, copied at the first gesture: the changes are cut from it.</param>
/// <param name="Undone">The changes given back, by <see cref="RunPiece.Index"/>.</param>
/// <param name="Kept">The changes the person kept: an undo of the run leaves them.</param>
/// <param name="After">The hash of what the file holds after the last gesture.</param>
internal sealed record PieceMarks(int Generation, string RunCopy, IReadOnlyList<int> Undone, IReadOnlyList<int> Kept, string After);

/// <summary>What a gesture on one change of a file did (<see cref="FileHistoryService.PieceActionAsync"/>).</summary>
internal enum PieceOutcome
{
    Undone, Redone, Kept,
    /// <summary>The change already stood that way: nothing written.</summary>
    Unchanged,
    /// <summary>Created, deleted or not text, or its changes cannot be given back exactly: the file goes whole.</summary>
    WholeOnly,
    /// <summary>The file was given back whole: Redo first.</summary>
    GivenBackWhole,
    /// <summary>The file holds something else than the run and its gestures left: a write would lose that change.</summary>
    ChangedSince,
    /// <summary>Keep asked for a change given back: it holds the state before the run, not the run's.</summary>
    KeepGivenBack,
    NoSuchPiece, NoCopy, NotInRun, Failed,
}

internal enum PieceAction { Undo, Redo, Keep }

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

    public HistoryRun(string id, DateTime? startedAt = null) { Id = id; StartedAt = startedAt ?? DateTime.Now; }

    /// <summary>The file that keeps the run on disk (<see cref="RunManifest"/>), once it has been written or read.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>The <see cref="FileCount"/> its file last held. Mutated under FileHistoryService's run lock.</summary>
    public int PersistedFileCount { get; set; } = -1;

    /// <summary>Read from disk while another live process still records into it: undoing it would revert files under
    /// that process's agent.</summary>
    public bool OpenElsewhere { get; set; }

    /// <summary>Read from disk, left open by a process that has ended (an editor closed or killed mid-run): what it
    /// changed after its last write to disk may be missing.</summary>
    public bool CutShort { get; set; }

    /// <summary>Read from disk: the process that recorded it, kept when this one writes the run again; <c>null</c> for
    /// this process's own runs.</summary>
    public int? RecordedBy { get; set; }

    /// <summary>
    /// The run has been undone. ⚠ It stays the most recent run with changes, so without this a second <c>/undo-run</c> — or
    /// a second click on the still-visible Undo button — undoes it AGAIN: it overwrites whatever has been edited since with
    /// the pre-run state, and answers "last run undone".
    /// </summary>
    public bool Undone => _undoneGeneration % 2 == 1;

    // ⚠ The undo marks are generations that only grow — odd: undone, even: not, or again after a redo —, so the editors
    // that share a run merge them by keeping the larger (RunManifest.MergeMarks): a flag that a redo clears could not be
    // merged without the other editor's older "undone" putting it back.
    private int _undoneGeneration;

    public int UndoneGeneration => _undoneGeneration;

    public void MarkUndone() { if (!Undone) _undoneGeneration++; }

    /// <summary>A file of the run redone: the run is no longer wholly undone.</summary>
    public void ReopenUndo() { if (Undone) _undoneGeneration++; }

    public void MergeUndoneGeneration(int generation) => _undoneGeneration = Math.Max(_undoneGeneration, generation);

    // Files an undo has given back, by generation. ⚠ A run is undone only once all of them are: marked undone after a
    // failure (a file held by another program, say), the run answered "already undone" to the retry — the failed files
    // never came back. And a retry tries those only: never again a file given back, which may have been edited since.
    private readonly Dictionary<string, int> _fileGenerations = new(PathComparer.Default);

    public bool IsGivenBack(string path) => _fileGenerations.TryGetValue(path, out var g) && g % 2 == 1;

    public void NoteGivenBack(string path) { if (!IsGivenBack(path)) _fileGenerations[path] = Generation(path) + 1; }

    /// <summary>A file given back, then given the run's version again.</summary>
    public void NoteRedone(string path) { if (IsGivenBack(path)) _fileGenerations[path] = Generation(path) + 1; }

    private int Generation(string path) => _fileGenerations.TryGetValue(path, out var g) ? g : 0;

    public IReadOnlyDictionary<string, int> FileGenerations => _fileGenerations;

    /// <summary>Keeps the larger generation of <paramref name="path"/>; <c>true</c> when the other was newer.</summary>
    public bool MergeFileGeneration(string path, int generation)
    {
        if (generation <= Generation(path)) return false;
        _fileGenerations[path] = generation;
        return true;
    }

    // What each file held when the run ended (FileHistoryService.ContentHash, "" for none): a file whose content differs
    // now was changed after the run — by the person, by another run —, and putting back the state before the run would
    // lose that change.
    private readonly Dictionary<string, string> _after = new(PathComparer.Default);

    public string? AfterOf(string path) => _after.TryGetValue(path, out var hash) ? hash : null;

    public void NoteAfter(string path, string hash) => _after[path] = hash;

    public IReadOnlyDictionary<string, string> AfterHashes => _after;

    // Files the person kept: Undo run leaves them as they are.
    private readonly HashSet<string> _kept = new(PathComparer.Default);

    public bool IsKept(string path) => _kept.Contains(path);

    public void NoteKept(string path) => _kept.Add(path);

    public IReadOnlyCollection<string> Kept => _kept;

    // The copy an undo took of the version it replaced: what a redo puts back.
    private readonly Dictionary<string, string> _redoSources = new(PathComparer.Default);

    public string? RedoSourceOf(string path) => _redoSources.TryGetValue(path, out var copy) ? copy : null;

    public void NoteRedoSource(string path, string copy) => _redoSources[path] = copy;

    public IReadOnlyDictionary<string, string> RedoSources => _redoSources;

    // Files whose changes were kept or given back one by one: what each holds is RunPieces.Compose of its marks.
    private readonly Dictionary<string, PieceMarks> _pieces = new(PathComparer.Default);

    public PieceMarks? PiecesOf(string path) => _pieces.TryGetValue(path, out var marks) ? marks : null;

    /// <summary>A piece gesture landed: the file now holds <see cref="PieceMarks.After"/>, the end a later gesture or an
    /// undo is judged against.</summary>
    public void NotePieces(string path, PieceMarks marks)
    {
        _pieces[path] = marks;
        _after[path] = marks.After;
    }

    /// <summary>Takes <paramref name="other"/> when it is newer: the marks of both editors only grow by generation.</summary>
    public void MergePieces(string path, PieceMarks other)
    {
        if (other.Generation > (PiecesOf(path)?.Generation ?? 0)) NotePieces(path, other);
    }

    public IReadOnlyDictionary<string, PieceMarks> Pieces => _pieces;

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

    // Files read_file read in this run, a page being enough: what the run line counts. Distinct from _read, which a
    // write_file also feeds (the model knows what it wrote) and which waits for every line.
    private readonly HashSet<string> _opened = new(PathComparer.Default);

    public void NoteOpened(string path) => _opened.Add(path);

    /// <summary>How many files <c>read_file</c> read in this run — each once, however many pages or reads.</summary>
    public int FilesOpened => _opened.Count;

    public bool Has(string path) => _firstByPath.ContainsKey(path);

    /// <summary>A file whose only write in the run did not land: out of the run, as if never attempted.</summary>
    public void Forget(string path) => _firstByPath.Remove(path);

    public void RecordFirst(string originalPath, string? snapshot, bool snapshotFailed = false)
    {
        if (!_firstByPath.ContainsKey(originalPath))
            _firstByPath[originalPath] = new RunChange(originalPath, snapshot, snapshotFailed);
        // ⚠ Given back mid-run, then written again by the same run: it holds the run's version, which /undo-run must
        // give back too — still marked given back, it would be skipped.
        else NoteRedone(originalPath);
    }

    public void RecordToolCall(string tool, string? subject, long durationMs, bool error)
    {
        lock (_toolCallLock)
            _toolCalls.Add(new ToolCallRecord(_toolCalls.Count + 1, tool, subject, durationMs, error));
    }

    public IReadOnlyCollection<RunChange> Changes => _firstByPath.Values;
    public int FileCount => _firstByPath.Count;

    // What watched calls could not record (WorkTreeWatch). Mutated under FileHistoryService's run lock.
    private readonly List<(string Call, UncoveredNote Note)> _uncovered = [];

    /// <summary>What the run's calls may have changed without its being recorded, and why — for <c>/undo-run</c> to say.</summary>
    public IReadOnlyList<(string Call, UncoveredNote Note)> Uncovered => _uncovered.ToList();

    public void NoteUncovered(string call, UncoveredNote note)
    {
        // One file is named once per run, by the first call that left it out: a file nobody can read would otherwise be
        // named again under every call.
        if (note.File is not null ? _uncovered.Any(u => u.Note == note) : _uncovered.Contains((call, note))) return;
        _uncovered.Add((call, note));
    }

    /// <summary>How many calls of the run were watched and ran — commands, tests, MCP tools: what makes <c>/undo-run</c>
    /// say that files git ignores are never recorded.</summary>
    public int WatchedCalls { get; private set; }

    public void CountWatchedCall() => WatchedCalls++;

    /// <summary>The count a run read from disk was written with.</summary>
    public void RestoreWatchedCalls(int count) => WatchedCalls = Math.Max(0, count);

    // Every write, where _firstByPath keeps each file once. Mutated under FileHistoryService's run lock.
    public int WriteCount { get; private set; }
    public void CountWrite() => WriteCount++;
    public void UncountWrite() => WriteCount = Math.Max(0, WriteCount - 1);

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
