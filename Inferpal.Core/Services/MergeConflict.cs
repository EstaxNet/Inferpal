using System.Text;

namespace Inferpal.Services;

/// <summary>
/// A file git left in conflict — a merge, a rebase, a cherry-pick, a revert or a stash stopped on it — read for what resolving
/// it takes: the operation, each side's last commit on the file, and the common base of every block, which git's markers do
/// not show.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The base is read from the index's three stages, merged again in a folder of its own (<c>git merge-file --diff3</c>): the
/// person's file is never rewritten. It comes as a header ABOVE the file, whose text stays the one on disk: shown inside the
/// blocks, the base lines would be quoted into an edit that finds no such lines in the file.
/// </para>
/// <para>
/// Markers in a file git does not hold in conflict (a merge tool's samples, a document about conflicts) are content: no
/// header. The header is extra knowledge: a failure building it leaves the file read without it, never the read refused.
/// </para>
/// </remarks>
internal static class MergeConflict
{
    /// <summary>What a refused edit says, in whatever a tool wraps it.</summary>
    internal const string RefusalMarker = "Refused: this edit leaves git's conflict markers wrong in ";

    /// <summary>What a refusal that lets the same edit through when it is sent again adds (within a run only).</summary>
    internal const string PassOffered =
        " If the file is meant to hold these lines (a merge tool's samples, a document about conflicts), send the same edit "
        + "again and it will be written.";

    /// <summary>The most base text the header carries: past it, the remaining blocks are named with the command that shows them.</summary>
    internal const int MaxBaseChars = 2_500;

    /// <summary>The most the three stages may weigh together to be merged again: their merge must come back whole within what
    /// a capture keeps (<see cref="ChildProcess.MaxCapturedChars"/>), never cut in the middle of a block.</summary>
    private const int MaxStagesBytes = ChildProcess.MaxCapturedChars / 2;

    /// <summary>The conflict blocks <paramref name="text"/> opens: lines that begin with seven <c>&lt;</c> then a space or the
    /// line's end, as git writes them.</summary>
    internal static int Blocks(string text) => Lines(text).Count(l => IsMarker(l, '<'));

    /// <summary>
    /// Why an edit from <paramref name="original"/> (<c>null</c>: a new file) to <paramref name="proposed"/> leaves git's
    /// markers wrong, or <c>null</c>. Wrong is a marker the file did not have, or half a block (its <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>
    /// taken out and its <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> left, or the reverse) where the file's blocks were whole. Resolving
    /// the blocks one edit at a time is not refused, nor an edit that keeps a file's own markers as they were.
    /// </summary>
    internal static string? WrongMarkers(string? original, string proposed, string shown)
    {
        var (opens, closes, separators) = Counts(proposed);
        var (wereOpen, wereClosed, wereSeparators) = original is null ? (0, 0, 0) : Counts(original);
        var added = opens > wereOpen || closes > wereClosed;
        var halved = opens != closes && wereOpen == wereClosed;
        // A ======= line beyond one per block that was not there: both markers dropped, both sides kept.
        var stray = wereOpen > 0 && separators - opens > wereSeparators - wereOpen;
        return added || halved || stray
            ? $"{RefusalMarker}{shown}: a resolved block keeps none of its lines <<<<<<<, ======= and >>>>>>>, and a block left "
              + "for later keeps all three. Nothing was written: send the edit without them, or with the whole block."
            : null;
    }

    private static (int Opens, int Closes, int Separators) Counts(string text)
    {
        int opens = 0, closes = 0, separators = 0;
        foreach (var line in Lines(text))
        {
            if (IsMarker(line, '<')) opens++;
            else if (IsMarker(line, '>')) closes++;
            else if (line == "=======") separators++;
        }
        return (opens, closes, separators);
    }

    /// <summary>
    /// The header that goes above <paramref name="content"/> (the file at <paramref name="path"/> as read), or <c>null</c>: the
    /// text opens no conflict block, git does not hold the file in conflict, or the header could not be built (traced).
    /// </summary>
    internal static async Task<string?> HeaderAsync(string path, string content, CancellationToken ct)
    {
        if (Blocks(content) == 0) return null;
        try
        {
            return await BuildAsync(path, content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Diagnostics.Swallow("MergeConflict.Header", ex);
            return null;
        }
    }

    private static async Task<string?> BuildAsync(string path, string content, CancellationToken ct)
    {
        if (Path.GetDirectoryName(path) is not { } folder || GitProcess.WorkTreeOf(folder) is not { } tree) return null;
        var asked = Path.GetRelativePath(tree, path).Replace('\\', '/');

        // Every unmerged entry, "<mode> <object> <stage>\t<path>\0" per stage, and this file's among them as the file system
        // compares names: git matches a pathspec case-sensitively, and a path written in another case on Windows found none.
        var listed = await GitProcess.CaptureBytesAsync(["ls-files", "-u", "-z"], tree, ct, maxBytes: 1024 * 1024);
        if (listed.Output is not { Length: > 0 } entries) return null;
        var mine = Encoding.UTF8.GetString(entries).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                           .Select(e => e.Split('\t', 2) is [var head, var name]
                                            ? (Stage: head.Split(' ').LastOrDefault() ?? "", Name: name)
                                            : (Stage: "", Name: ""))
                           .Where(e => PathComparer.Default.Equals(e.Name, asked)).ToList();
        if (mine.Count == 0) return null;
        var relative = mine[0].Name;
        var stages = mine.Select(e => e.Stage).ToHashSet(StringComparer.Ordinal);

        var gitDir = await GitTextAsync(["rev-parse", "--absolute-git-dir"], tree, ct);
        var (operation, ours, theirs, theirsRef) = Operation(gitDir);
        var oursCommit   = await LastCommitAsync("HEAD", relative, tree, ct);
        var theirsCommit = theirsRef is null ? null : await LastCommitAsync(theirsRef, relative, tree, ct);

        var disk = DiskBlocks(content);
        var sb = new StringBuilder();
        sb.Append('[').Append(Path.GetFileName(path)).Append(" is in conflict: ").Append(operation).Append(" stopped on it. ")
          .Append(disk.Count).Append(disk.Count == 1 ? " conflicting block" : " conflicting blocks")
          .Append(". Its markers show each block's two sides; the common base, which they do not show, is below.\n");
        sb.Append(" ours (<<<<<<<) = ").Append(ours).Append(oursCommit is { Length: > 0 } o ? $", last changed this file in {o}" : "").Append('\n');
        sb.Append(" theirs (>>>>>>>) = ").Append(theirs).Append(theirsCommit is { Length: > 0 } t ? $", last changed it in {t}" : "").Append('\n');

        if (disk.Any(b => b.HasBase))
            sb.Append(" The base of each block is in the file, after its ||||||| line.\n");
        else if (!stages.Contains("1"))
            sb.Append(" There is no common base: both sides added this file.\n");
        else if (await BasesAsync(tree, relative, ct) is not { } merged)
            sb.Append($" The base could not be read here: `git show :1:{relative}` prints it.\n");
        else
            AppendBases(sb, disk, merged, relative);

        sb.Append("Resolve every block and remove its markers. Staging the file (git add) and going on with the operation are the "
                  + "person's steps.]\n\n");
        return sb.ToString();
    }

    /// <summary>The base of each block of the file: the merged block that holds its two sides, the nearest by where its ours
    /// side sits in the ours version.</summary>
    /// <remarks>⚠ Not the first that holds them: git's default style splits one conflict into several blocks where shared
    /// lines separate them, and a block of closing braces is held by many. Both outputs count the ours version's lines the
    /// same way (the lines outside blocks and each block's ours side), so a position is comparable; after a block was
    /// resolved by hand the positions drift, and the nearest still wins.</remarks>
    private static void AppendBases(StringBuilder sb, List<DiskBlock> disk, List<MergedBlock> merged, string relative)
    {
        var shownChars = 0;
        for (var i = 0; i < disk.Count; i++)
        {
            var block = disk[i];
            var at = -1;
            for (var m = 0; m < merged.Count; m++)
                if (Holds(merged[m].Ours, block.Ours) && Holds(merged[m].Theirs, block.Theirs)
                    && (at < 0 || Distance(merged[m], block) < Distance(merged[at], block))) at = m;
            sb.Append(" Block ").Append(i + 1).Append(", lines ").Append(block.First).Append('-').Append(block.Last);
            if (at < 0)
            {
                sb.Append($": its base could not be matched to git's merge of the three versions — `git show :1:{relative}` prints it.\n");
                continue;
            }
            var basis = merged[at].Base;
            if (basis.Trim().Length == 0)
            {
                sb.Append(": no base lines — both sides added these lines.\n");
                continue;
            }
            if (shownChars + basis.Length > MaxBaseChars)
            {
                sb.Append($": base not shown, past {MaxBaseChars} characters of bases — `git show :1:{relative}` prints it.\n");
                continue;
            }
            shownChars += basis.Length;
            sb.Append(", base:\n");
            foreach (var line in Lines(basis)) sb.Append("    ").Append(line).Append('\n');
        }
    }

    /// <summary>Whether a merged side holds a disk side as whole lines. ⚠ Not equal: git's default style moves the lines both
    /// sides share out of a block's edges, which the diff3 style cannot do (the base differs there), so the merged side holds
    /// the disk's with those lines around it — an empty disk side too, which the position then places.</summary>
    private static bool Holds(string merged, string disk) =>
        disk.Length == 0 || ("\n" + merged + "\n").Contains("\n" + disk + "\n", StringComparison.Ordinal);

    /// <summary>How far a disk block's ours side is from a merged block's, in lines of the ours version (0 inside it).</summary>
    private static int Distance(MergedBlock merged, DiskBlock disk) =>
        disk.OursAt < merged.OursAt ? merged.OursAt - disk.OursAt
        : disk.OursAt > merged.OursAt + merged.OursLines ? disk.OursAt - merged.OursAt - merged.OursLines
        : 0;

    /// <summary>What git is in the middle of, from its own files: how the operation and each side are named, and the ref of
    /// the side being brought in (<c>null</c> when nothing names it: a stash applied, a checkout -m).</summary>
    private static (string Operation, string Ours, string Theirs, string? TheirsRef) Operation(string? gitDir)
    {
        bool Has(string name) => gitDir is not null && (File.Exists(Path.Combine(gitDir, name)) || Directory.Exists(Path.Combine(gitDir, name)));
        if (Has("MERGE_HEAD"))
        {
            var message = gitDir is null ? null : FirstLine(Path.Combine(gitDir, "MERGE_MSG"));
            return (message is { Length: > 0 } m ? $"a merge (\"{m}\")" : "a merge",
                    "HEAD, the branch merged into", "MERGE_HEAD, the branch being merged", "MERGE_HEAD");
        }
        // `git am -3` keeps its patches where a rebase keeps its own.
        if (Has(Path.Combine("rebase-apply", "applying")))
            return ("git am (a patch being applied)", "HEAD, the current branch", "the patch being applied", null);
        // ⚠ In a rebase the sides are the other way round from a merge: HEAD is the new base, the commit replayed is theirs.
        if (Has("rebase-merge") || Has("rebase-apply"))
            return ("a rebase", "HEAD, the new base with the commits replayed so far", "the commit being replayed",
                    Has("REBASE_HEAD") ? "REBASE_HEAD" : null);
        if (Has("CHERRY_PICK_HEAD"))
            return ("a cherry-pick", "HEAD, the current branch", "CHERRY_PICK_HEAD, the commit being picked", "CHERRY_PICK_HEAD");
        if (Has("REVERT_HEAD"))
            return ("a revert", "HEAD, the current branch", "the inverse of REVERT_HEAD, the commit being reverted", "REVERT_HEAD");
        if (Has("SQUASH_MSG"))
            return ("a squash merge", "HEAD, the current branch", "the branch being squashed in", null);
        return ("an operation that left it unmerged (a stash applied, a checkout -m)", "HEAD, the current branch",
                "the changes being applied", null);
    }

    private static string? FirstLine(string file)
    {
        try { return File.Exists(file) ? File.ReadLines(file).FirstOrDefault()?.Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The last commit of <paramref name="rev"/> that touched the file, "hash subject", read like every log Inferpal
    /// reads (<see cref="GitProcess.LogShaped"/>: no colour, no gpg lines).</summary>
    private static Task<string?> LastCommitAsync(string rev, string relative, string tree, CancellationToken ct) =>
        GitTextAsync([.. GitProcess.LogShapedArgs, "--literal-pathspecs", "log", "-1", "--format=%h %s", rev, "--", relative], tree, ct);

    /// <summary>The three stages merged again in a folder of their own, with the base of every block; <c>null</c> when a
    /// stage cannot be read, or the three weigh too much to come back whole.</summary>
    private static async Task<List<MergedBlock>?> BasesAsync(string tree, string relative, CancellationToken ct)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        long weight = 0;
        foreach (var (stage, name) in new[] { ("1", "base"), ("2", "ours"), ("3", "theirs") })
        {
            var blob = await GitProcess.CaptureBytesAsync(["show", $":{stage}:{relative}"], tree, ct, MaxStagesBytes);
            if (blob.Output is not { } bytes || (weight += bytes.Length) > MaxStagesBytes) return null;
            texts[name] = string.Join('\n', Lines(Tools.TextFileEncoding.Decode(bytes)));
        }
        var work = Directory.CreateTempSubdirectory("inferpal-conflict-").FullName;
        try
        {
            foreach (var (name, text) in texts)
                await File.WriteAllTextAsync(Path.Combine(work, name), text, new UTF8Encoding(false), ct);
            // Relative names in the folder: no path of the person's reaches the command line. The exit code is the number
            // of conflicts (at most 127), not a failure; above it, git failed.
            const string Merge = "merge-file -p --diff3 -L ours -L base -L theirs ours base theirs";
            var run = await GitProcess.CaptureAsync(Merge.Replace("--diff3", "--diff3 --diff-algorithm=histogram"), work, ct);
            if (!run.TimedOut && run.ExitCode > 127) run = await GitProcess.CaptureAsync(Merge, work, ct);
            return run.TimedOut || run.ExitCode is < 0 or > 127 || run.Stdout.Length == 0 ? null : MergedBlocks(run.Stdout);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best-effort cleanup of a temporary folder */ }
        }
    }

    /// <param name="OursAt">The line, counted from 0 in the ours version, where the block's ours side starts.</param>
    private sealed record DiskBlock(int First, int Last, string Ours, string Theirs, bool HasBase, int OursAt);

    /// <param name="OursAt">The line, counted from 0 in the ours version, where the block's ours side starts.</param>
    /// <param name="OursLines">How many lines its ours side has.</param>
    private sealed record MergedBlock(string Ours, string Base, string Theirs, int OursAt, int OursLines);

    /// <summary>The blocks of the file as git wrote them on disk, numbered from 1 by their first and last line, as read_file
    /// numbers lines.</summary>
    private static List<DiskBlock> DiskBlocks(string content)
    {
        var lines = Lines(content);
        var blocks = new List<DiskBlock>();
        var oursLine = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsMarker(lines[i], '<')) { oursLine++; continue; }
            int? baseAt = null, separator = null, end = null;
            for (var j = i + 1; j < lines.Count && end is null; j++)
            {
                if (IsMarker(lines[j], '|') && separator is null) baseAt ??= j;
                else if (lines[j] == "=======" && separator is null) separator = j;
                else if (IsMarker(lines[j], '>') && separator is not null) end = j;
                else if (IsMarker(lines[j], '<')) break;   // an unclosed block: the next one starts
            }
            if (separator is not { } sep || end is not { } last) { oursLine++; continue; }
            var oursEnd = baseAt ?? sep;
            blocks.Add(new DiskBlock(i + 1, last + 1, Join(lines, i + 1, oursEnd), Join(lines, sep + 1, last), baseAt is not null, oursLine));
            oursLine += oursEnd - i - 1;
            i = last;
        }
        return blocks;
    }

    /// <summary>The blocks of <c>git merge-file --diff3</c>'s output: each side and the base between them.</summary>
    private static List<MergedBlock> MergedBlocks(string merged)
    {
        var lines = Lines(merged);
        var blocks = new List<MergedBlock>();
        var oursLine = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsMarker(lines[i], '<')) { oursLine++; continue; }
            int baseAt = -1, separator = -1, end = -1;
            for (var j = i + 1; j < lines.Count && end < 0; j++)
            {
                if (baseAt < 0 && IsMarker(lines[j], '|')) baseAt = j;
                else if (baseAt >= 0 && separator < 0 && lines[j] == "=======") separator = j;
                else if (separator >= 0 && IsMarker(lines[j], '>')) end = j;
            }
            if (end < 0) break;
            blocks.Add(new MergedBlock(Join(lines, i + 1, baseAt), Join(lines, baseAt + 1, separator), Join(lines, separator + 1, end),
                                       oursLine, baseAt - i - 1));
            oursLine += baseAt - i - 1;
            i = end;
        }
        return blocks;
    }

    private static string Join(List<string> lines, int from, int to) => string.Join("\n", lines.Skip(from).Take(to - from));

    /// <summary>The lines of a text, cut at <c>\n</c> only (a <c>\r</c> before it dropped), as read_file counts them: a form
    /// feed or a lone <c>\r</c> is inside a line there.</summary>
    private static List<string> Lines(string text) => [.. text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l)];

    /// <summary>A marker line: seven <paramref name="mark"/> characters, then a space or nothing.</summary>
    private static bool IsMarker(string line, char mark) =>
        line.Length >= 7 && line.AsSpan(0, 7).IndexOfAnyExcept(mark) < 0 && (line.Length == 7 || line[7] == ' ');

    private static async Task<string?> GitTextAsync(IReadOnlyList<string> args, string tree, CancellationToken ct) =>
        (await GitProcess.CaptureBytesAsync(args, tree, ct, maxBytes: 64 * 1024)).Output is { } output
            ? Encoding.UTF8.GetString(output).Trim()
            : null;
}
