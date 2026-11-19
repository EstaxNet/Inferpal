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

/// <summary>What applying the kept hunks did: the files written, and the files left with why.</summary>
internal sealed record CopyApplied(IReadOnlyList<string> Written, IReadOnlyList<(string Path, string Why)> Left);

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
        var add = await GitProcess.CaptureBytesAsync(["worktree", "add", "-b", branch, folder, head], root, ct, MaxFileBytes, budget: Budget);
        if (add.Output is null) return (null, "git could not make the copy: " + add.Failure);
        return (new WorkCopy { Repository = root, Folder = folder, Branch = branch, BaseCommit = head }, null);
    }

    /// <summary>What the run changed in the copy against the commit it started from, created and deleted files included.</summary>
    internal async Task<(IReadOnlyList<CopyChange> Changes, string? Problem)> ChangesAsync(CancellationToken ct)
    {
        // Untracked files are changes too: --intent-to-add makes them visible to a diff without staging their content.
        await GitProcess.CaptureBytesAsync(["add", "--intent-to-add", "--all"], Folder, ct, MaxFileBytes);
        var names = await GitProcess.CaptureBytesAsync(["diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", BaseCommit],
                                                       Folder, ct, MaxFileBytes);
        if (names.Output is null) return ([], "the copy's changes could not be read: " + names.Failure);
        var changes = new List<CopyChange>();
        foreach (var path in Encoding.UTF8.GetString(names.Output).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var before = await GitProcess.CaptureBytesAsync(["show", $"{BaseCommit}:{path}"], Folder, ct, MaxFileBytes);
            var file = Path.Combine(Folder, path);
            string? after = File.Exists(file) ? await Tools.TextFileEncoding.ReadTextAsync(file, ct) : null;
            changes.Add(new CopyChange(path.Replace('\\', '/'),
                                       before.Output is { } b ? LineEndings.ToEol(Tools.TextFileEncoding.Decode(b), "\n") : null,
                                       after is null ? null : LineEndings.ToEol(after, "\n")));
        }
        return (changes, null);
    }

    /// <summary>Removes the copy and its branch; what could not be removed is said.</summary>
    internal async Task<string?> DiscardAsync(CancellationToken ct)
    {
        var remove = await GitProcess.CaptureBytesAsync(["worktree", "remove", "--force", Folder], Repository, ct, MaxFileBytes, budget: Budget);
        var branch = await GitProcess.CaptureBytesAsync(["branch", "-D", Branch], Repository, ct, MaxFileBytes);
        // ⚠ git stops at the first file it cannot delete — one a build that has just ended still holds for a moment — and
        // leaves the rest: what it left is removed again for a few seconds, and said if it stays.
        var left = Directory.Exists(Folder) ? await RemoveFolderAsync(Folder, ct) : null;
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
                                                        FileHistoryService history, CancellationToken ct)
    {
        var written = new List<string>();
        var left = new List<(string Path, string Why)>();
        foreach (var change in changes)
        {
            if (!kept.TryGetValue(change.Path, out var hunks) || hunks.Count == 0) continue;
            var target = Path.GetFullPath(Path.Combine(repository, change.Path));
            Tools.PathSanitizer.AssertUnderRoot(target, repository);
            string? raw = File.Exists(target) ? await Tools.TextFileEncoding.ReadTextAsync(target, ct) : null;
            var current = raw is null ? null : LineEndings.ToEol(raw, "\n");
            if (current != change.Before)
            {
                left.Add((change.Path, current is null ? "you deleted it since the copy was made"
                                     : change.Before is null ? "you created a file of that name since the copy was made"
                                     : "you changed it since the copy was made"));
                continue;
            }
            var (saved, _) = await history.BackUpBeforeChangeAsync(target, ct);
            if (!saved)
            {
                left.Add((change.Path, "it could not be backed up first, so it was not written"));
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
        return new(written, left);
    }

    private static async Task<string?> GitText(string[] args, string workDir, CancellationToken ct)
    {
        var run = await GitProcess.CaptureBytesAsync(args, workDir, ct, MaxFileBytes);
        return run.Output is null ? null : Encoding.UTF8.GetString(run.Output).Trim();
    }
}
