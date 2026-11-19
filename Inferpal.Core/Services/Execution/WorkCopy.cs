using System.IO;
using System.Text;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Execution;

/// <summary>A file the run changed in its copy: its text at the start (<c>null</c>: it did not exist) and in the copy
/// (<c>null</c>: deleted), cut into hunks the person keeps or drops one by one (pari 81).</summary>
/// <remarks>⚠ Both texts in LF: git hands the start commit's text as the repository stores it, the copy's file is written with
/// the checkout's line ends (CRLF under Windows) — compared as they are, every line would differ. The file written back takes
/// the line ends of the file it replaces.</remarks>
internal sealed record CopyChange(string Path, string? Before, string? After)
{
    /// <summary>The hunks between the two texts; a created or deleted file is one hunk.</summary>
    public DiffPlan Plan { get; } = InlineDiffPlanner.Plan(Before ?? string.Empty, After ?? string.Empty);
}

/// <summary>Why a file's kept hunks were not written: its text is no longer the one the copy started from, it could not be
/// backed up first, or writing it failed.</summary>
internal enum CopyLeftReason { ChangedSince, DeletedSince, CreatedSince, NotBackedUp, NotWritten, Unsaved, NotInReview }

/// <summary>A file whose kept hunks were not written, and why; <paramref name="Detail"/> is the cause of a failed write.</summary>
internal sealed record CopyLeftFile(string Path, CopyLeftReason Reason, string? Detail = null)
{
    /// <summary>Why, for the person, in their language (a failed write with its cause).</summary>
    public string Why => Reason switch
    {
        CopyLeftReason.DeletedSince => Localization.Strings.CopyLeftDeletedSince,
        CopyLeftReason.CreatedSince => Localization.Strings.CopyLeftCreatedSince,
        CopyLeftReason.NotBackedUp  => Localization.Strings.CopyLeftNotBackedUp,
        CopyLeftReason.NotWritten   => Localization.Strings.CopyLeftWriteFailed(Detail ?? "?"),
        CopyLeftReason.Unsaved      => Localization.Strings.CopyLeftUnsaved,
        CopyLeftReason.NotInReview  => Localization.Strings.CopyLeftNotInReview,
        _                           => Localization.Strings.CopyLeftChangedSince,
    };
}

/// <summary>What applying the kept hunks did: the files written, and the files left with why.</summary>
internal sealed record CopyApplied(IReadOnlyList<string> Written, IReadOnlyList<CopyLeftFile> Left);

/// <summary>
/// A copy of the repository the agent works in (pari 106): a <c>git worktree</c> on a branch of its own, at the commit the
/// repository was at, in Inferpal's folder of the account — outside the repository, so its index, its searches and its
/// watchers never see it. The person's files are not touched until they apply the hunks they keep.
/// </summary>
/// <remarks>
/// ⚠ The copy starts from the last COMMIT: changes the person has not committed are not in it, and are named before the run
/// (<see cref="UncommittedAsync"/>). ⚠ Every git call takes its arguments as a list (a path with a space stays one argument),
/// and reads no program the repository configures (<see cref="GitProcess.CaptureBytesAsync"/>).
/// </remarks>
internal sealed class WorkCopy
{
    /// <summary>The prefix of the copies' branches.</summary>
    internal const string BranchPrefix = "inferpal/";

    /// <summary>The largest file read back from a copy or the start commit for the review.</summary>
    internal const long MaxFileBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    public required string Repository { get; init; }
    public required string Folder { get; init; }
    public required string Branch { get; init; }
    public required string BaseCommit { get; init; }

    /// <summary>Where the copies live, one folder per repository below it.</summary>
    internal static string Home { get; set; } = Rag.IndexHome.FolderOf("copies");

    /// <summary>The record beside a copy's folder (<c>&lt;n&gt;.json</c>): what a later session needs to find it again, and the
    /// process that holds it — a copy is found again only once that process is gone.</summary>
    private sealed record Record(string Repository, string Folder, string Branch, string BaseCommit, int OwnerPid = 0, long OwnerSince = 0);

    private static Record Owned(string repository, string folder, string branch, string head) =>
        new(repository, folder, branch, head, Environment.ProcessId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static string RecordOf(string folder) => folder + ".json";

    /// <summary>
    /// The copies of <paramref name="repository"/> an earlier session left — the editor closed or the host stopped while one
    /// was held: each whose holder is gone and whose folder and branch are still there, oldest first. ⚠ A copy whose holder
    /// lives (another editor, another ACP thread of this process) is never found: it is not theirs to apply or discard.
    /// Never removed here: the person reviews, applies or discards them.
    /// </summary>
    internal static async Task<IReadOnlyList<WorkCopy>> FindAsync(string repository, CancellationToken ct)
    {
        var top = await GitText(["rev-parse", "--show-toplevel"], repository, ct);
        var root = Path.GetFullPath(top ?? repository);
        var dir = Path.Combine(Home, FileHistoryService.FolderNameOf(root));
        if (!Directory.Exists(dir)) return [];
        var found = new List<WorkCopy>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            Record? record;
            try { record = System.Text.Json.JsonSerializer.Deserialize<Record>(await File.ReadAllTextAsync(file, ct)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Diagnostics.Swallow("WorkCopy.Find", ex);
                continue;
            }
            if (record is null || !Directory.Exists(record.Folder)) continue;
            if (record.OwnerPid > 0 && Signals.SignalFile.WriterAlive(record.OwnerPid, record.OwnerSince)) continue;
            if (await GitText(["rev-parse", "--verify", "--quiet", "refs/heads/" + record.Branch], root, ct) is null) continue;
            found.Add(new WorkCopy { Repository = root, Folder = record.Folder, Branch = record.Branch, BaseCommit = record.BaseCommit });
        }
        return found;
    }

    /// <summary>Makes this process the copy's holder (a copy found again is taken up): no other session finds it meanwhile.</summary>
    internal async Task TakeOverAsync(CancellationToken ct)
    {
        try { await File.WriteAllTextAsync(RecordOf(Folder), System.Text.Json.JsonSerializer.Serialize(Owned(Repository, Folder, Branch, BaseCommit)), ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostics.Swallow("WorkCopy.TakeOver", ex); }
    }

    /// <summary>The paths the person changed and has not committed (staged, unstaged, untracked), as git lists them.</summary>
    internal static async Task<IReadOnlyList<string>?> UncommittedAsync(string repository, CancellationToken ct)
    {
        var run = await GitProcess.CaptureBytesAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], repository, ct, MaxFileBytes);
        if (run.Output is null) return null;
        var entries = Encoding.UTF8.GetString(run.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var paths = new List<string>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.Length < 4) continue;
            paths.Add(entry[3..]);
            // A rename lists the old name next: it is the same change.
            if (entry[0] is 'R' or 'C') i++;
        }
        return paths;
    }

    /// <summary>Makes a copy of <paramref name="repository"/> at its HEAD on a new branch; the refusal says why there is none.</summary>
    internal static async Task<(WorkCopy? Copy, string? Refusal)> CreateAsync(string repository, CancellationToken ct)
    {
        var head = await GitText(["rev-parse", "--verify", "HEAD"], repository, ct);
        if (head is null) return (null, "this folder is not a git repository with a commit: a copy starts from the last commit");
        var top = await GitText(["rev-parse", "--show-toplevel"], repository, ct);
        var root = Path.GetFullPath(top ?? repository);

        var branches = await GitText(["branch", "--list", BranchPrefix + "*", "--format=%(refname:short)"], root, ct) ?? "";
        var taken = branches.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).ToHashSet(StringComparer.Ordinal);
        // ⚠ A number whose folder is there is passed over, never removed here: it may be a copy another editor has just made
        // (read after the lists above), or what a discard could not remove — a dead end either way if it stopped this copy.
        var n = 1;
        string folder;
        for (;; n++)
        {
            if (taken.Contains(BranchPrefix + n)) continue;
            folder = Path.Combine(Home, FileHistoryService.FolderNameOf(root), n.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(folder)) break;
        }
        var branch = BranchPrefix + n;
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        // ⚠ The record first, beside the folder (inside, it would be one of the run's changes): a `worktree add` stopped half way
        // leaves a branch and a folder that a record lets a later session find; without one, nothing would reach them.
        try { await File.WriteAllTextAsync(RecordOf(folder), System.Text.Json.JsonSerializer.Serialize(Owned(root, folder, branch, head)), ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "the copy's record could not be written: " + ex.Message);
        }
        var add = await GitProcess.CaptureBytesAsync(["worktree", "add", "-b", branch, folder, head], root, ct, MaxFileBytes, budget: Budget);
        if (add.Output is null)
        {
            if (!Directory.Exists(folder)) try { File.Delete(RecordOf(folder)); } catch { }
            return (null, "git could not make the copy: " + add.Failure);
        }
        return (new WorkCopy { Repository = root, Folder = folder, Branch = branch, BaseCommit = head }, null);
    }

    /// <summary>What the run changed in the copy against the commit it started from, created and deleted files included.</summary>
    internal async Task<(IReadOnlyList<CopyChange> Changes, string? Problem)> ChangesAsync(CancellationToken ct)
    {
        // Untracked files are changes too: --intent-to-add makes them visible to a diff without staging their content. Failed,
        // the files the run created would drop out of the review without a word.
        var intent = await GitProcess.CaptureBytesAsync(["add", "--intent-to-add", "--all"], Folder, ct, MaxFileBytes);
        if (intent.Output is null) return ([], "the files the run created could not be listed: " + intent.Failure);
        var names = await GitProcess.CaptureBytesAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-status", "-z", BaseCommit],
                                                       Folder, ct, MaxFileBytes);
        if (names.Output is null) return ([], "the copy's changes could not be read: " + names.Failure);
        var changes = new List<CopyChange>();
        var unread = new List<string>();
        var binary = new List<string>();
        var entries = Encoding.UTF8.GetString(names.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < entries.Length; i += 2)
        {
            var (status, path) = (entries[i], entries[i + 1]);
            // ⚠ The start's text is read only for a file that was there (git says so): a read that fails is not "absent" —
            // read as created, the review would offer the whole file as one hunk, and the apply would refuse it with a false cause.
            string? before = null;
            if (!status.StartsWith('A'))
            {
                var start = await GitProcess.CaptureBytesAsync(["show", $"{BaseCommit}:{path}"], Folder, ct, MaxFileBytes);
                if (start.Output is null) { unread.Add(path); continue; }
                // ⚠ A binary file is no text to cut into hunks: kept as text, it would be written back corrupt.
                if (Tools.TextFileEncoding.IsBinary(start.Output)) { binary.Add(path); continue; }
                before = LineEndings.ToEol(Tools.TextFileEncoding.Decode(start.Output), "\n");
            }
            var file = Path.Combine(Folder, path);
            string? after = null;
            if (!status.StartsWith('D') && File.Exists(file))
            {
                try
                {
                    if (new FileInfo(file).Length > MaxFileBytes) { unread.Add(path); continue; }
                    var bytes = await File.ReadAllBytesAsync(file, ct);
                    if (Tools.TextFileEncoding.IsBinary(bytes)) { binary.Add(path); continue; }
                    after = await Tools.TextFileEncoding.ReadTextAsync(file, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held by a job the run left running, or gone between the listing and the read: said, never thrown away.
                    Diagnostics.Swallow("WorkCopy.Changes", ex);
                    unread.Add(path);
                    continue;
                }
            }
            changes.Add(new CopyChange(path.Replace('\\', '/'), before, after is null ? null : LineEndings.ToEol(after, "\n")));
        }
        var problems = new List<string>();
        if (unread.Count > 0)
            problems.Add($"{unread.Count} {(unread.Count == 1 ? "file" : "files")} could not be read (too large, held, or git did not "
                         + $"answer) and {(unread.Count == 1 ? "is" : "are")} left out of the review: {string.Join(", ", unread)}");
        if (binary.Count > 0)
            problems.Add($"{binary.Count} binary {(binary.Count == 1 ? "file" : "files")} changed, not offered as text: "
                         + $"{string.Join(", ", binary)} (on the branch {Branch})");
        return (changes, problems.Count == 0 ? null : string.Join("; ", problems));
    }

    /// <summary>Removes the copy and its branch; what could not be removed is said.</summary>
    internal async Task<string?> DiscardAsync(CancellationToken ct)
    {
        var remove = await GitProcess.CaptureBytesAsync(["worktree", "remove", "--force", Folder], Repository, ct, MaxFileBytes, budget: Budget);
        var branch = await GitProcess.CaptureBytesAsync(["branch", "-D", Branch], Repository, ct, MaxFileBytes);
        // ⚠ git stops at the first file it cannot delete — one a build that has just ended still holds for a moment — and
        // leaves the rest: what it left is removed again for a few seconds, and said if it stays.
        var left = Directory.Exists(Folder) ? await RemoveFolderAsync(Folder, ct) : null;
        // Gone with its folder: a later session must not find a copy that is no longer there.
        if (left is null) try { File.Delete(RecordOf(Folder)); } catch { }
        // Removed by hand, or not at all: git's own entry for it goes too, or every later copy at that number is refused
        // ("missing but already registered worktree").
        var prune = remove.Output is null
            ? await GitProcess.CaptureBytesAsync(["worktree", "prune"], Repository, ct, MaxFileBytes)
            : default;
        var problems = new[]
        {
            left is not null ? $"the copy's folder {Folder}: {left}; it can be deleted by hand" : null,
            remove.Output is null && prune.Output is null ? "git's record of the copy: " + (prune.Failure ?? remove.Failure) : null,
            branch.Output is null ? $"the branch {Branch}: " + branch.Failure : null,
        }.Where(p => p is not null);
        return problems.Any() ? "not removed — " + string.Join("; ", problems) : null;
    }

    /// <summary>How long a discard keeps trying to remove a folder a program still holds a file in.</summary>
    internal static TimeSpan RemoveFor { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Removes <paramref name="folder"/>, read-only entries included, trying again for <see cref="RemoveFor"/> while a file in
    /// it is held: <c>null</c> once it is gone, else why it stays.
    /// </summary>
    /// <remarks>⚠ A link inside is removed, never followed: one to the person's repository or to an ancestor would clear the
    /// attributes of files that are not the copy's, or loop.</remarks>
    internal static async Task<string?> RemoveFolderAsync(string folder, CancellationToken ct)
    {
        var until = DateTime.UtcNow + RemoveFor;
        var walk = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        for (;;)
        {
            try
            {
                foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", walk))
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(folder, recursive: true);
                return null;
            }
            catch (DirectoryNotFoundException) { return null; }
            // A file held by another program for a moment (a build that has just ended): tried again until the deadline.
            catch (IOException ex) when (ex is not FileNotFoundException and not PathTooLongException && DateTime.UtcNow < until) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.Swallow("WorkCopy.RemoveFolder", ex);
                return ex is IOException and not FileNotFoundException and not PathTooLongException
                    ? "a program still holds a file in it"
                    : "it could not be deleted (" + ex.Message + ")";
            }
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Writes into the person's files the hunks they kept (<paramref name="kept"/>: by path, the <see cref="DiffHunk.Index"/>es
    /// kept) — pari 81. Call it inside a run of <paramref name="history"/>: each file is backed up before it is written, so
    /// <c>/undo-run</c> gives it back.
    /// </summary>
    /// <remarks>
    /// ⚠ A file whose text is no longer the one the copy started from — the person changed it during the run, or had
    /// uncommitted changes in it — is left as it is and named: its hunks were made against another text, and written over it
    /// they would undo the person's work. ⚠ No backup, no write (<see cref="FileHistoryService.BackUpBeforeChangeAsync"/>).
    /// </remarks>
    internal static async Task<CopyApplied> ApplyAsync(string repository, IReadOnlyList<CopyChange> changes,
                                                        IReadOnlyDictionary<string, IReadOnlyCollection<int>> kept,
                                                        FileHistoryService history, CancellationToken ct,
                                                        Func<string, bool>? unsaved = null)
    {
        var written = new List<string>();
        var left = new List<CopyLeftFile>();
        // A kept path the review did not show is named: dropped in silence, nothing written would read as all written.
        var shown = changes.Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var path in kept.Where(k => k.Value.Count > 0 && !shown.Contains(k.Key)).Select(k => k.Key))
            left.Add(new(path, CopyLeftReason.NotInReview));
        foreach (var change in changes)
        {
            if (!kept.TryGetValue(change.Path, out var hunks) || hunks.Count == 0) continue;
            // ⚠ One file that fails is named, and the others go on: thrown, the report of what was already written was lost.
            try
            {
                var target = Path.GetFullPath(Path.Combine(repository, change.Path));
                Tools.PathSanitizer.AssertUnderRoot(target, repository);
                // ⚠ An unsaved buffer in the editor is the person's text: written under it, their next save would undo the hunk.
                if (unsaved?.Invoke(target) == true)
                {
                    left.Add(new(change.Path, CopyLeftReason.Unsaved));
                    continue;
                }
                string? raw = File.Exists(target) ? await Tools.TextFileEncoding.ReadTextAsync(target, ct) : null;
                var current = raw is null ? null : LineEndings.ToEol(raw, "\n");
                if (current != change.Before)
                {
                    left.Add(new(change.Path, current is null ? CopyLeftReason.DeletedSince
                                            : change.Before is null ? CopyLeftReason.CreatedSince
                                            : CopyLeftReason.ChangedSince));
                    continue;
                }
                var (saved, _) = await history.BackUpBeforeChangeAsync(target, ct);
                if (!saved)
                {
                    left.Add(new(change.Path, CopyLeftReason.NotBackedUp));
                    continue;
                }
                var merged = InlineDiffPlanner.Apply(change.Plan, hunks);
                // Every hunk of a deleted file kept: the file goes.
                if (change.After is null && merged.Length == 0 && hunks.Count == change.Plan.Hunks.Count)
                    File.Delete(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await Tools.SafeFileWriter.WritePreservingAsync(target, LineEndings.ToEol(merged, raw is null ? "\n" : LineEndings.Dominant(raw)), ct);
                }
                written.Add(change.Path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Diagnostics.Swallow("WorkCopy.Apply", ex);
                left.Add(new(change.Path, CopyLeftReason.NotWritten, Diagnostics.RootMessage(ex)));
            }
        }
        return new(written, left);
    }

    private static async Task<string?> GitText(string[] args, string workDir, CancellationToken ct)
    {
        var run = await GitProcess.CaptureBytesAsync(args, workDir, ct, MaxFileBytes);
        return run.Output is null ? null : Encoding.UTF8.GetString(run.Output).Trim();
    }
}
