using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Inferpal.Services.Execution;

/// <summary>How a call changed a file of the work tree.</summary>
internal enum WorkTreeChangeKind { Modified, Created, Deleted }

/// <summary>One file a call changed, with its content from before the call (<c>null</c> for a file it created).</summary>
internal sealed record WorkTreeChange(string Path, WorkTreeChangeKind Kind, byte[]? Before);

/// <summary>Why something a call may have changed is not recorded.</summary>
internal enum UncoveredReason
{
    /// <summary>The workspace is not in a git work tree (or there is no workspace).</summary>
    NoRepository,
    /// <summary>More changed or untracked files, or more of their content, than a reading copies.</summary>
    TooManyFiles,
    /// <summary>The repository took too long to read; reading is off for it in this session.</summary>
    TooSlow,
    /// <summary>git could not read the repository (the cause goes to <c>/diagnostics</c>).</summary>
    ReadFailed,
    /// <summary>A job started in the background: what it changes after it starts is not watched.</summary>
    Background,
    /// <summary>One file larger than <see cref="WorkTreeWatch.MaxFileBytes"/> before the call.</summary>
    FileTooLarge,
    /// <summary>One file stored with Git LFS: its content from before is not on the machine as such.</summary>
    LfsFile,
    /// <summary>One symbolic link.</summary>
    SymbolicLink,
    /// <summary>One file that could not be read.</summary>
    FileUnreadable,
    /// <summary>One file of the repository outside the workspace: not the workspace's to give back.</summary>
    OutsideWorkspace,
    /// <summary>One file that appeared when the call changed an ignore rule: maybe there before, maybe the call's.</summary>
    UnknownOrigin,
    /// <summary>One clean file whose bytes before the call git cannot give exactly (its line endings, say).</summary>
    BeforeUnknown,
    /// <summary>One file with unsaved changes in the editor, saved during the call: the person's, not the call's.</summary>
    SavedByEditor,
}

/// <summary>Something a call may have changed and that is not recorded; <see cref="File"/> when it is one file.</summary>
internal sealed record UncoveredNote(UncoveredReason Reason, string? File = null);

/// <summary>One stage-0 index entry: mode, object, and the size git last saw the file at in the work tree.</summary>
internal readonly record struct IndexEntry(string Mode, string Object, long WorkTreeSize);

/// <summary>
/// The state of the workspace's git work tree at one moment, enough to tell afterwards what changed and what it held:
/// the index (each tracked file's object), and the bytes of every file that differs from it (modified or untracked).
/// </summary>
/// <remarks>
/// ⚠ Nothing here writes to the repository: no <c>git add</c>, no object written, and <c>--no-optional-locks</c> so that
/// reading the status does not refresh the user's index either. A clean file needs no copy: its content is the index
/// object, already in git.
/// </remarks>
internal sealed class WorkTreeReading
{
    /// <summary>The git work tree; <c>null</c> when <see cref="Uncovered"/> says why there is none.</summary>
    public string? Root { get; init; }

    /// <summary>The workspace the call works in: only what changes under it is recorded.</summary>
    public string? Workspace { get; init; }

    /// <summary>Why this reading cannot tell what changes; <c>null</c> when it can.</summary>
    public UncoveredReason? Uncovered { get; init; }

    /// <summary>Stage-0 index entries by path as git writes it.</summary>
    public Dictionary<string, IndexEntry> Index { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Paths git reports as changed or untracked.</summary>
    public HashSet<string> Dirty { get; init; } = new(StringComparer.Ordinal);

    /// <summary>The bytes of each dirty path; <c>null</c> for a dirty path that does not exist (deleted).</summary>
    public Dictionary<string, byte[]?> Copies { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Dirty paths whose content could not be kept, with why, and their size and write time then: said only if
    /// they change, not at every call.</summary>
    public Dictionary<string, (UncoveredReason Reason, long Length, DateTime WrittenUtc)> NotKept { get; init; } =
        new(StringComparer.Ordinal);

    /// <summary>Files the editor held unsaved changes for when the reading was taken (full paths).</summary>
    public IReadOnlySet<string> Unsaved { get; init; } = new HashSet<string>(PathComparer.Default);

    /// <summary>The write time of the repository's own exclude file (<c>info/exclude</c>), to tell if it changed.</summary>
    public DateTime ExcludeWrittenUtc { get; init; }

    /// <summary>When the reading started: a file "new" since, but written before it, may not be the call's creation.</summary>
    public DateTime StartedUtc { get; init; }

    public static WorkTreeReading Without(UncoveredReason reason) => new() { Uncovered = reason, StartedUtc = DateTime.UtcNow };
}

/// <summary>
/// Reads the workspace's git work tree before and after a call, and tells which files the call changed — what a command,
/// a test run, a user's shell tool or an MCP tool writes without going through <see cref="FileHistoryService"/>.
/// </summary>
internal static class WorkTreeWatch
{
    /// <summary>Beyond this many changed or untracked files, before or after a call, the call is not covered: a partial
    /// record would give back half a checkout.</summary>
    internal const int MaxDirtyFiles = 2_000;

    /// <summary>Beyond this much content to copy, the call is not covered.</summary>
    internal const long MaxCopiedBytes = 128L * 1024 * 1024;

    /// <summary>A file whose content from before the call is larger than this is not kept (that file is not covered).</summary>
    internal static long MaxFileBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>Beyond this many clean files to fetch from git after a call, the call is not covered (all or nothing).</summary>
    internal static int MaxFetchedFiles { get; set; } = 300;

    /// <summary>A reading slower than this counts against the repository (<see cref="SlowStrikes"/>).</summary>
    internal static TimeSpan SlowReading { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Slow readings in a row after which the repository is no longer read in this session.</summary>
    internal const int SlowStrikes = 2;

    // Slow readings in a row, per repository. ⚠ One slow reading is a cold cache as often as a slow repository, and a git
    // that never answers is slow too: every call there would wait for it twice.
    private static readonly ConcurrentDictionary<string, int> Strikes = new(PathComparer.Default);

    // git could not start at all (not installed, not on PATH): every reading would fail the same way, after a process start.
    private static volatile bool _gitMissing;

    private static readonly byte[] LfsPointer = Encoding.ASCII.GetBytes("version https://git-lfs.github.com/spec/v1");

    /// <summary>Reads the work tree holding <paramref name="workspaceRoot"/>. Never throws but for <paramref name="ct"/>.</summary>
    /// <param name="unsaved">Files the editor holds unsaved changes for now: if one is saved during the call (a debug
    /// start saves them, an autosave does), the change is the person's.</param>
    public static async Task<WorkTreeReading> ReadAsync(string? workspaceRoot, IReadOnlyCollection<string>? unsaved,
                                                        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot) || GitProcess.WorkTreeOf(workspaceRoot) is not { } root)
            return WorkTreeReading.Without(UncoveredReason.NoRepository);
        if (_gitMissing) return WorkTreeReading.Without(UncoveredReason.ReadFailed);
        if (Strikes.TryGetValue(root, out var strikes) && strikes >= SlowStrikes)
            return WorkTreeReading.Without(UncoveredReason.TooSlow);

        var started = DateTime.UtcNow;
        var clock   = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var listing = await ListAsync(root, ct);
            if (listing is null) return WorkTreeReading.Without(UncoveredReason.ReadFailed);
            var (index, dirty) = listing.Value;
            if (dirty.Count > MaxDirtyFiles) return WorkTreeReading.Without(UncoveredReason.TooManyFiles);

            var reading = new WorkTreeReading
            {
                Root = root, Workspace = Path.GetFullPath(workspaceRoot), Index = index, Dirty = dirty, StartedUtc = started,
                ExcludeWrittenUtc = await ExcludeWrittenAsync(root, ct),
                Unsaved = new HashSet<string>((unsaved ?? []).Select(Path.GetFullPath), PathComparer.Default),
            };
            long copied = 0;
            foreach (var path in dirty)
            {
                var full = FullPath(root, path);
                // ⚠ A name that is not UTF-8 (a legacy file name on Linux) comes back with U+FFFD: it names no file, and
                // its absence would read as a deletion that /undo-run "restores" under a garbled name.
                if (path.Contains('�'))
                {
                    reading.NotKept[path] = (UncoveredReason.FileUnreadable, -1, default);
                    continue;
                }
                var (length, written) = Stat(full);
                switch (Probe(full))
                {
                    case Kind.Absent:
                        reading.Copies[path] = null;
                        break;
                    case Kind.Link:
                        reading.NotKept[path] = (UncoveredReason.SymbolicLink, length, written);
                        break;
                    case Kind.TooLarge:
                        reading.NotKept[path] = (UncoveredReason.FileTooLarge, length, written);
                        break;
                    case Kind.Directory:
                        break;   // a nested repository git lists as one entry: never recorded, and /undo-run says so
                    default:
                        try
                        {
                            var bytes = await File.ReadAllBytesAsync(full, ct);
                            copied += bytes.Length;
                            if (copied > MaxCopiedBytes) return WorkTreeReading.Without(UncoveredReason.TooManyFiles);
                            reading.Copies[path] = bytes;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            reading.NotKept[path] = (UncoveredReason.FileUnreadable, length, written);
                        }
                        break;
                }
            }
            return reading;
        }
        finally
        {
            Count(root, clock.Elapsed);
        }
    }

    /// <summary>Counts a reading against the repository when it was slow, and forgives it when it was not.</summary>
    private static void Count(string root, TimeSpan took)
    {
        if (took <= SlowReading)
        {
            Strikes.TryRemove(root, out _);
            return;
        }
        if (Strikes.AddOrUpdate(root, 1, (_, n) => n + 1) == SlowStrikes)
            Diagnostics.Record("WorkTreeWatch",
                $"Reading the git work tree {root} took {took.TotalSeconds:0.0}s, {SlowStrikes} times in a row: what commands "
                + "change there is no longer recorded for /undo-run in this session.");
    }

    /// <summary>
    /// What changed since <paramref name="before"/>, with the content each changed file held then; and what could not
    /// be told. All or nothing past the caps. Never throws but for <paramref name="ct"/>.
    /// </summary>
    public static async Task<(IReadOnlyList<WorkTreeChange> Changes, IReadOnlyList<UncoveredNote> Notes)> CompareAsync(
        WorkTreeReading before, CancellationToken ct)
    {
        if (before.Uncovered is not null || before.Root is not { } root)
            return ([], [new UncoveredNote(before.Uncovered ?? UncoveredReason.NoRepository)]);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        (Dictionary<string, IndexEntry> Index, HashSet<string> Dirty)? listing;
        try { listing = await ListAsync(root, ct); }
        finally { Count(root, clock.Elapsed); }   // a reading like the one before: slow twice in a row turns watching off
        if (listing is null) return ([], [new UncoveredNote(UncoveredReason.ReadFailed)]);
        var (index, dirty) = listing.Value;

        // The paths that may have changed: dirty before or after, and every index entry that moved — a checkout or a
        // commit leaves the tree clean and the index changed.
        var candidates = new SortedSet<string>(before.Dirty, StringComparer.Ordinal);
        candidates.UnionWith(dirty);
        foreach (var (path, entry) in index)
            if (!before.Index.TryGetValue(path, out var was) || (was.Mode, was.Object) != (entry.Mode, entry.Object))
                candidates.Add(path);
        foreach (var path in before.Index.Keys)
            if (!index.ContainsKey(path)) candidates.Add(path);
        if (candidates.Count > MaxDirtyFiles) return ([], [new UncoveredNote(UncoveredReason.TooManyFiles)]);

        // ⚠ A file that is new to git's listings may have been there all along, ignored: the call changed an ignore rule.
        // Only then is "written before the call" a sign of it — a .gitignore whose CONTENT changed during the call, not
        // one merely dirty. Otherwise a file the call made by renaming or copying, which keeps its dates (`git mv`, `mv`,
        // a copy on Windows), would pass for an old one and be left behind by /undo-run.
        var ignoreRulesChanged = candidates.Any(p => IsIgnoreFile(p) && ChangedDuringTheCall(root, p, before, dirty, index))
                                 || await ExcludeWrittenAsync(root, ct) != before.ExcludeWrittenUtc;

        var notes = new List<UncoveredNote>();
        var plans = new List<(string Full, Kind Now, byte[]? Was, IndexEntry? Fetch)>();
        // The paths of the reading before, by the shared rule for "same file?": built once, when a path is new to git.
        Dictionary<string, string>? beforeByFile = null;
        string? SameFileBefore(string path)
        {
            if (beforeByFile is null)
            {
                beforeByFile = new Dictionary<string, string>(PathComparer.Default);
                foreach (var known in before.Index.Keys.Concat(before.Copies.Keys)) beforeByFile.TryAdd(known, known);
            }
            return beforeByFile.TryGetValue(path, out var twin) && twin != path ? twin : null;
        }
        foreach (var path in candidates)
        {
            var full = FullPath(root, path);
            if (path.Contains('�'))
            {
                notes.Add(new UncoveredNote(UncoveredReason.FileUnreadable, full));
                continue;
            }
            var now = Probe(full);
            if (now is Kind.Directory) continue;   // a nested repository: never recorded, and /undo-run says so

            if (before.NotKept.TryGetValue(path, out var notKept))
            {
                // Said when it changed, and only then: a large untracked file would otherwise be named after every call.
                if (notKept.Length < 0 || Stat(full) != (notKept.Length, notKept.WrittenUtc))
                    notes.Add(new UncoveredNote(notKept.Reason, full));
                continue;
            }

            if (before.Copies.TryGetValue(path, out var copy))
            {
                plans.Add((full, now, copy, null));
            }
            else if (before.Index.TryGetValue(path, out var entry))
            {
                if (entry.Mode == "120000") { notes.Add(new UncoveredNote(UncoveredReason.SymbolicLink, full)); continue; }
                if (entry.Mode is not ("100644" or "100755")) continue;   // a submodule: never recorded, said by /undo-run
                plans.Add((full, now, null, entry));
            }
            else if (SameFileBefore(path) is { } twin)
            {
                // ⚠ The same file under another case (`git mv Foo.cs foo.cs`) where the file system folds case: read as
                // created, the undo would delete the file that was there before the run. Its bytes from before stand in.
                if (before.Copies.TryGetValue(twin, out var twinCopy)) { plans.Add((full, now, twinCopy, null)); continue; }
                var twinEntry = before.Index[twin];
                if (twinEntry.Mode == "120000") { notes.Add(new UncoveredNote(UncoveredReason.SymbolicLink, full)); continue; }
                if (twinEntry.Mode is not ("100644" or "100755")) continue;   // a submodule: as under its own name
                plans.Add((full, now, null, twinEntry));
            }
            else
            {
                // Neither changed nor tracked before: absent — unless git was ignoring it and the call changed the rule.
                if (now is not Kind.Absent && ignoreRulesChanged && WrittenBefore(full, before.StartedUtc))
                {
                    notes.Add(new UncoveredNote(UncoveredReason.UnknownOrigin, full));
                    continue;
                }
                plans.Add((full, now, null, null));
            }
        }
        // All or nothing: half a checkout given back is worse than none, and said.
        if (plans.Count(p => p.Fetch is not null) > MaxFetchedFiles) return ([], [new UncoveredNote(UncoveredReason.TooManyFiles)]);

        var changes = new List<WorkTreeChange>();
        foreach (var (full, now, copied, fetch) in plans)
        {
            // What the file held before the call.
            var was = copied;
            if (fetch is { } entry)
            {
                var path = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                var (bytes, why) = await CheckedOutAsync(root, path, entry, ct);
                if (bytes is null) { notes.Add(new UncoveredNote(why, full)); continue; }
                was = bytes;
            }
            var existedBefore = fetch is not null || copied is not null;

            // A link is never given back: writing the old bytes would write through it, to its target.
            if (now is Kind.Link) { notes.Add(new UncoveredNote(UncoveredReason.SymbolicLink, full)); continue; }

            WorkTreeChangeKind kind;
            if (!existedBefore)
            {
                if (now is Kind.Absent) continue;
                kind = WorkTreeChangeKind.Created;   // its bytes are not needed: an undo deletes it
            }
            else if (now is Kind.Absent)
            {
                kind = WorkTreeChangeKind.Deleted;
            }
            else if (now is Kind.TooLarge)
            {
                kind = WorkTreeChangeKind.Modified;   // larger than any content kept from before: it changed
            }
            else
            {
                try
                {
                    var current = await File.ReadAllBytesAsync(full, ct);
                    if (was!.AsSpan().SequenceEqual(current)) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    notes.Add(new UncoveredNote(UncoveredReason.FileUnreadable, full));
                    continue;
                }
                kind = WorkTreeChangeKind.Modified;
            }

            // ⚠ Only the workspace's: in a repository above it, another program's edits elsewhere are not the call's to
            // give back. And a file the editor held unsaved changes for was saved by the editor during the call (a debug
            // start saves them): the person's work, not the call's — said, not recorded.
            if (!Under(before.Workspace, full)) { notes.Add(new UncoveredNote(UncoveredReason.OutsideWorkspace, full)); continue; }
            if (before.Unsaved.Contains(full))   { notes.Add(new UncoveredNote(UncoveredReason.SavedByEditor, full)); continue; }
            changes.Add(new WorkTreeChange(full, kind, kind == WorkTreeChangeKind.Created ? null : was));
        }
        return (changes, notes);
    }

    private static bool IsIgnoreFile(string path) =>
        path == ".gitignore" || path.EndsWith("/.gitignore", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="path"/> changed between the reading and now, without reading git's objects.</summary>
    private static bool ChangedDuringTheCall(string root, string path, WorkTreeReading before,
                                             HashSet<string> dirtyNow, Dictionary<string, IndexEntry> indexNow)
    {
        var full = FullPath(root, path);
        if (before.Copies.TryGetValue(path, out var copy))
        {
            try { return copy is null ? File.Exists(full) : !File.Exists(full) || !copy.AsSpan().SequenceEqual(File.ReadAllBytes(full)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
        // Clean before: changed if it is listed now, or its index entry moved (unknown counts as changed).
        return dirtyNow.Contains(path)
               || !before.Index.TryGetValue(path, out var was)
               || !indexNow.TryGetValue(path, out var now)
               || (was.Mode, was.Object) != (now.Mode, now.Object);
    }

    /// <summary>The index (stage 0, with each file's work-tree size) and the dirty paths; <c>null</c> when git could
    /// not read them, the cause gone to <c>/diagnostics</c>.</summary>
    private static async Task<(Dictionary<string, IndexEntry> Index, HashSet<string> Dirty)?> ListAsync(
        string root, CancellationToken ct)
    {
        var status = await GitProcess.CaptureBytesAsync(
            ["--no-optional-locks", "status", "--porcelain", "-z", "--untracked-files=all", "--no-renames",
             "--ignore-submodules=all"],
            root, ct, maxBytes: 64L * 1024 * 1024);
        if (status.Output is null) return Failed(root, status);
        var files = await GitProcess.CaptureBytesAsync(
            ["--no-optional-locks", "ls-files", "-s", "--debug", "-z"], root, ct, maxBytes: 512L * 1024 * 1024);
        if (files.Output is null) return Failed(root, files);

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Split(status.Output))
            if (entry.Length > 3) dirty.Add(entry[3..]);   // "XY path"

        // "<mode> <object> <stage>\t<path>\0" then "  ctime: …\n … size: N\tflags: F\n" and the next entry.
        var index = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        string? header = null;
        foreach (var segment in Split(files.Output, keepEmpty: true))
        {
            var debug = segment;
            if (header is not null)
            {
                var flags = segment.IndexOf("flags:", StringComparison.Ordinal);
                var end   = flags < 0 ? -1 : segment.IndexOf('\n', flags);
                debug     = end < 0 ? segment : segment[..end];
                var size  = SizeOf(debug);
                Add(index, header, size);
                header = end < 0 ? null : segment[(end + 1)..];
            }
            else
            {
                header = segment;
            }
        }
        return (index, dirty);
    }

    private static (Dictionary<string, IndexEntry>, HashSet<string>)? Failed(string root, GitBytes result)
    {
        if (result.NotStarted) _gitMissing = true;
        Diagnostics.RecordOnce("WorkTreeWatch",
            $"git could not read {root}: {result.Failure}. What commands change there is not recorded for /undo-run.",
            root + "|" + result.Failure);
        return null;
    }

    private static void Add(Dictionary<string, IndexEntry> index, string header, long size)
    {
        var tab = header.IndexOf('\t');
        if (tab < 0) return;
        var meta = header[..tab].Split(' ');
        if (meta.Length != 3 || meta[2] != "0") return;   // a conflict's stages: the file is dirty, its bytes copied
        index[header[(tab + 1)..]] = new IndexEntry(meta[0], meta[1], size);
    }

    private static long SizeOf(string debug)
    {
        var at = debug.IndexOf("size:", StringComparison.Ordinal);
        if (at < 0) return -1;
        var digits = debug[(at + 5)..].TrimStart();
        var n = 0;
        while (n < digits.Length && char.IsAsciiDigit(digits[n])) n++;
        return long.TryParse(digits[..n], System.Globalization.NumberStyles.None,
                             System.Globalization.CultureInfo.InvariantCulture, out var size) ? size : -1;
    }

    private static IEnumerable<string> Split(byte[] output, bool keepEmpty = false)
    {
        var start = 0;
        for (var i = 0; i <= output.Length; i++)
        {
            if (i < output.Length && output[i] != 0) continue;
            if (i > start || (keepEmpty && i < output.Length)) yield return Encoding.UTF8.GetString(output, start, i - start);
            start = i + 1;
        }
    }

    /// <summary>
    /// The bytes a clean file held before the call: what git writes at a checkout (filters, line endings), or the object
    /// as stored — the one whose size is the file's size the index last saw. Neither: not known, and said.
    /// </summary>
    /// <remarks>⚠ A file checked out with LF stays clean after <c>core.autocrlf</c> is turned on, and a checkout now would
    /// write CRLF: taken without the size, an undo would rewrite every line ending of a file git calls unchanged.</remarks>
    private static async Task<(byte[]? Bytes, UncoveredReason Why)> CheckedOutAsync(
        string root, string path, IndexEntry entry, CancellationToken ct)
    {
        var lfs = new Dictionary<string, string> { ["GIT_LFS_SKIP_SMUDGE"] = "1" };
        var filtered = await GitProcess.CaptureBytesAsync(
            ["--no-optional-locks", "cat-file", "--filters", "--path=" + path, entry.Object], root, ct, MaxFileBytes, lfs);
        if (filtered.Overflow) return (null, UncoveredReason.FileTooLarge);
        if (filtered.Output is { } bytes)
        {
            if (bytes.AsSpan().StartsWith(LfsPointer)) return (null, UncoveredReason.LfsFile);
            if (bytes.Length == entry.WorkTreeSize) return (bytes, default);
        }
        var stored = await GitProcess.CaptureBytesAsync(
            ["--no-optional-locks", "cat-file", "blob", entry.Object], root, ct, MaxFileBytes);
        if (stored.Output is { } raw && raw.Length == entry.WorkTreeSize) return (raw, default);
        return (null, filtered.Output is null && stored.Output is null ? UncoveredReason.FileUnreadable : UncoveredReason.BeforeUnknown);
    }

    private enum Kind { Absent, File, Directory, Link, TooLarge }

    private static Kind Probe(string full)
    {
        try
        {
            var info = new FileInfo(full);
            if (info.Exists)
            {
                // ⚠ A link by its target, not by the reparse flag: cloud placeholders (OneDrive) and deduplicated files
                // carry the flag too, and taken for links nothing in such a folder would ever be recorded.
                if (info.LinkTarget is not null) return Kind.Link;
                return info.Length > MaxFileBytes ? Kind.TooLarge : Kind.File;
            }
            return Directory.Exists(full) ? Kind.Directory : Kind.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Kind.Absent;
        }
    }

    /// <summary>
    /// Whether a file that was neither tracked nor listed before existed before the call — written, or created, before
    /// it started: then git was ignoring it, and it may not be the call's creation.
    /// </summary>
    /// <remarks>⚠ Taken for a creation, /undo-run would delete it. Unknown counts as "existed": never deleted.</remarks>
    private static bool WrittenBefore(string full, DateTime startedUtc)
    {
        var slack = startedUtc - TimeSpan.FromSeconds(2);
        try { return File.GetLastWriteTimeUtc(full) < slack || File.GetCreationTimeUtc(full) < slack; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>The write time of the repository's <c>info/exclude</c>, wherever git keeps it; default when none.</summary>
    private static async Task<DateTime> ExcludeWrittenAsync(string root, CancellationToken ct)
    {
        try
        {
            return await GitProcess.GitPathAsync(root, "info/exclude", ct) is { } exclude && File.Exists(exclude)
                ? File.GetLastWriteTimeUtc(exclude)
                : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }

    private static (long Length, DateTime WrittenUtc) Stat(string full)
    {
        try
        {
            var info = new FileInfo(full);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-2, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (-2, default); }
    }

    private static bool Under(string? workspace, string full)
    {
        if (string.IsNullOrEmpty(workspace)) return false;
        var root = Path.TrimEndingDirectorySeparator(workspace);
        return full.StartsWith(root + Path.DirectorySeparatorChar, PathComparer.Comparison);
    }

    private static string FullPath(string root, string gitPath) =>
        Path.GetFullPath(Path.Combine(root, gitPath.Replace('/', Path.DirectorySeparatorChar)));
}

/// <summary>
/// The watch of one tool call: the reading taken before it, read again once a person has approved it, and what the
/// call itself says it cannot cover (a background job). Current for the call's asynchronous flow.
/// </summary>
internal sealed class WorkTreeBracket
{
    private static readonly AsyncLocal<WorkTreeBracket?> _current = new();

    private readonly string? _workspaceRoot;
    private readonly IReadOnlyCollection<string>? _unsaved;
    private readonly List<UncoveredNote> _notes = [];
    private bool _closed;

    private WorkTreeBracket(string? workspaceRoot, IReadOnlyCollection<string>? unsaved, WorkTreeReading before)
    {
        _workspaceRoot = workspaceRoot;
        _unsaved = unsaved;
        Before = before;
    }

    /// <summary>The reading the call is compared against.</summary>
    public WorkTreeReading Before { get; private set; }

    /// <summary>What the call said it cannot cover.</summary>
    public IReadOnlyList<UncoveredNote> Notes => _notes;

    /// <summary>The call was refused — by the person or by a rule, or stopped at its card — and ran nothing: there is
    /// nothing to compare.</summary>
    public bool Refused { get; private set; }

    /// <summary>The watch of the call running in this flow, if it is watched and not over.</summary>
    public static WorkTreeBracket? Current => _current.Value is { _closed: false } bracket ? bracket : null;

    /// <summary>Reads the work tree. The caller makes the watch current with <see cref="Enter"/>.</summary>
    /// <param name="unsaved">The files the editor holds unsaved changes for (see <see cref="WorkTreeWatch.ReadAsync"/>).</param>
    public static async Task<WorkTreeBracket> OpenAsync(string? workspaceRoot, IReadOnlyCollection<string>? unsaved,
                                                        CancellationToken ct) =>
        new(workspaceRoot, unsaved, await WorkTreeWatch.ReadAsync(workspaceRoot, unsaved, ct));

    /// <summary>Makes <paramref name="bracket"/> the watch of the calling flow, for what that flow awaits next.</summary>
    /// <remarks>⚠ Synchronous on purpose: an <see cref="AsyncLocal{T}"/> set inside an <c>async</c> method is restored
    /// when that method returns, so set inside <see cref="OpenAsync"/> it would not reach the tool — no approval would read
    /// the work tree again, no background job could say it is not watched.</remarks>
    public static void Enter(WorkTreeBracket bracket) => _current.Value = bracket;

    /// <summary>
    /// After a person approved the call: what changed while the card waited — their own edits, saved — is not the
    /// call's, so the comparison starts from now.
    /// </summary>
    /// <remarks>⚠ Refused until the reading is in: stopped halfway, the comparison would start from before the card and
    /// count the person's saves as the call's.</remarks>
    public async Task ReadAgainAsync(CancellationToken ct)
    {
        Refused = true;
        var reading = await WorkTreeWatch.ReadAsync(_workspaceRoot, _unsaved, ct);
        if (_closed) return;
        Before = reading;
        Refused = false;
    }

    /// <summary>Notes, for the call in this flow, something it cannot cover.</summary>
    public static void NoteUncovered(UncoveredReason reason) => Current?._notes.Add(new UncoveredNote(reason));

    /// <summary>Marks the call in this flow as refused (<see cref="Refused"/>).</summary>
    public static void NoteRefused()
    {
        if (Current is { } bracket) bracket.Refused = true;
    }

    /// <summary>
    /// Ends the watch once what the call changed is recorded: its reading — the bytes of every dirty file — is let go.
    /// </summary>
    /// <remarks>⚠ A task the call started (a background job, an MCP server's reader) keeps the call's flow, and with it
    /// this watch, for as long as it lives: open, the watch would keep up to <see cref="WorkTreeWatch.MaxCopiedBytes"/>
    /// alive for hours, and an approval asked from that task would read the work tree again for a call long over.</remarks>
    public void Close()
    {
        _closed = true;
        Before = WorkTreeReading.Without(UncoveredReason.ReadFailed);
    }
}
