using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Reads the history of the code, read-only: the commits that touched a file or folder, who last changed each line of a
/// range (<c>git blame</c>), the commits that added or removed a text (<c>git log -S</c>, <c>-G</c>), or one commit shown with
/// its message and diff.
/// </summary>
/// <remarks>
/// ⚠ Arguments travel one per element (<see cref="GitProcess.CaptureBytesAsync"/>): a path with a space stays one
/// argument, and a commit the model names is checked before it reaches git — a value starting with <c>-</c> would be read
/// as an option. ⚠ A shown diff runs no program the repository names (<c>--no-textconv</c>, <c>--no-ext-diff</c>; the
/// runner turns <c>core.fsmonitor</c> off) and keeps the <c>a/</c> <c>b/</c> headers whatever the configuration says:
/// the ignore files' filter drops an excluded file's section by them.
/// </remarks>
internal sealed class GitHistoryTool : ITool
{
    private readonly Func<string?> _getRoot;

    public GitHistoryTool(Func<string?> getRoot) => _getRoot = getRoot;

    public string Name => "git_history";

    /// <inheritdoc/>
    public ToolEffect EffectOf(JsonElement args) => ToolEffect.Reads;

    public string Description =>
        "Reads the history of the code. With path: the commits that touched that file or folder (newest first). " +
        "With path and start_line: who last changed each line of that range, and in which commit (git blame). " +
        "With search: the commits that added or removed that text (newest first), each with the lines that did \u2014 when " +
        "code appeared or went, and why. " +
        "With commit: that commit's message and diff. Use it to learn why code is the way it is before changing it.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path       = new { type = "string",  description = "File or folder in the repository (optional: the whole repository)." },
            start_line = new { type = "integer", description = "With a file: first line to blame (1-based)." },
            end_line   = new { type = "integer", description = "With start_line: last line to blame (default: start_line + 20)." },
            commit     = new { type = "string",  description = "A commit to show (hash, or a name such as HEAD~2)." },
            search     = new { type = "string",  description = "Text the commits added or removed (exact, case-sensitive)." },
            regex      = new { type = "boolean", description = "With search: read it as a POSIX regular expression ([0-9], not \\d) matched against the lines changed." },
            skip       = new { type = "integer", description = "With search: matching commits to pass over, to read the next ones." },
            max_count  = new { type = "integer", description = $"Commits listed, {MinCount}-{MaxCount} (default {DefaultCount})." },
        },
        required = Array.Empty<string>(),
    };

    internal const int DefaultCount = 20, MinCount = 1, MaxCount = 100;

    /// <summary>The most lines one blame reads: past it, the call names a narrower range.</summary>
    internal const int MaxBlameLines = 200;

    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Colour off: a user's <c>color.ui=always</c> would put escape codes in what the model reads.</summary>
    private static readonly string[] LogShaped = GitProcess.LogShapedArgs;

    // A revision as git spells one: no space, never an option, nothing a shell would read.
    private static readonly Regex Revision = new(@"^[A-Za-z0-9][A-Za-z0-9._/@{}~^+-]{0,199}$",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private string? _offeredRoot;
    private bool _offered;

    /// <summary>Offered in a git work tree only: a tool that can only say "not a repository" costs every request.</summary>
    public bool IsOffered
    {
        get
        {
            var root = _getRoot();
            if (string.IsNullOrEmpty(root)) return false;
            if (!string.Equals(root, _offeredRoot, StringComparison.Ordinal))
            {
                _offered = GitProcess.WorkTreeOf(root) is not null;
                _offeredRoot = root;
            }
            return _offered;
        }
    }

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var workspace = _getRoot();
        if (string.IsNullOrEmpty(workspace)) throw new NoWorkspaceRootException();

        var target = args.Str("path") is { Length: > 0 } p ? PathSanitizer.Sanitize(p, workspace) : workspace;
        PathSanitizer.AssertUnderRoot(target, workspace);
        var isFile = File.Exists(target);
        var repo = GitProcess.WorkTreeOf(isFile ? Path.GetDirectoryName(target)! : Directory.Exists(target) ? target : workspace);
        if (repo is null) return Strings.GitNotRepo;
        var relative = Path.GetRelativePath(repo, target).Replace('\\', '/');
        if (PathSanitizer.LeavesRoot(relative))
            return $"Error: '{target}' is outside the repository {repo}.";
        if (relative == ".") relative = string.Empty;

        var search = args.Str("search");
        if (search is { Length: 0 } || (search is null && (args.Has("regex") || args.Has("skip"))))
            return "Error: regex and skip go with a search: give the text to search.";
        if (search is not null && (args.Str("commit") is { Length: > 0 } || args.Has("start_line")))
            return "Error: search, commit and start_line are three different reads of the history: give one per call.";
        if (args.Str("commit") is { Length: > 0 } commit)
            return await ShowAsync(repo, commit.Trim(), relative, ct);
        if (search is not null)
            return await SearchAsync(repo, relative, search, args.Bool("regex", false), args, ct);
        if (args.Has("start_line"))
        {
            if (!isFile) return $"Error: start_line needs a file; '{target}' is not one. Call git_history with a file path.";
            return await BlameAsync(repo, relative, args, ct);
        }
        return await LogAsync(repo, relative, isFile, args, ct);
    }

    private static async Task<string> LogAsync(string repo, string relative, bool isFile, JsonElement args, CancellationToken ct)
    {
        var (count, notice) = ClampedArgument.Read(args, "max_count", DefaultCount, MinCount, MaxCount);
        // One more than asked: whether older commits exist is said, never guessed from a full page.
        List<string> command = [.. LogShaped, "log", "--date=short", "--format=%h %ad %an%x09%s", $"-n{count + 1}"];
        if (isFile) command.Add("--follow");
        if (relative.Length > 0) { command.Add("--"); command.Add(relative); }
        var (text, failure) = await RunAsync(command, repo, ct);
        if (failure is not null)
            return ClampedArgument.Above(notice, failure.Contains("does not have any commits yet", StringComparison.Ordinal)
                ? "The repository has no commit yet: there is no history to read."
                : Strings.GitCommandFailed("log", failure));

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        var shownPath = relative.Length > 0 ? relative : "the repository";
        if (lines.Count == 0) return ClampedArgument.Above(notice, $"No commit touches {shownPath}.");
        var more = lines.Count > count;
        if (more) lines.RemoveAt(lines.Count - 1);

        var sb = new StringBuilder($"Commits that touched {shownPath}, newest first (hash date author, then subject):\n");
        var fit = ToolOutputBudget.LinesThatFit(lines);
        sb.AppendJoin('\n', lines.Take(fit));
        if (fit < lines.Count) sb.Append($"\n… {lines.Count - fit} more listed commit(s) did not fit; ask for a narrower path or a smaller max_count.");
        else if (more) sb.Append($"\n(older commits exist: raise max_count up to {MaxCount}, or show one with commit=<hash>)");
        sb.Append("\nShow one with commit=<hash>.");
        return ClampedArgument.Above(notice, sb.ToString());
    }

    private static async Task<string> BlameAsync(string repo, string relative, JsonElement args, CancellationToken ct)
    {
        var (start, notice) = ClampedArgument.Read(args, "start_line", 1, 1, int.MaxValue);
        var end = args.Int("end_line", start + 20);
        if (end < start) end = start;
        if (end - start + 1 > MaxBlameLines)
        {
            notice = (notice is null ? "" : notice + "\n") + $"{ClampedArgument.NotePrefix}the range {start}-{end} is longer than {MaxBlameLines} lines; " +
                     $"the report below covers {start}-{start + MaxBlameLines - 1} — blame the rest in another call.";
            end = start + MaxBlameLines - 1;
        }
        // ⚠ HEAD, never the working tree: blaming the file on disk runs the clean filter its .gitattributes name, and
        // --no-textconv keeps the repository's textconv program from running. Uncommitted lines are therefore not covered.
        // The commits the repository lists as reformatting (.git-blame-ignore-revs) are skipped: a line they only
        // re-indented goes to the commit that wrote it.
        var (revisions, ignoreNote) = IgnoredRevisions(repo);
        List<string> Blame(bool skip) =>
            [.. LogShaped, "blame", "--no-textconv", "--date=short", .. skip ? revisions.SelectMany(r => (string[])["--ignore-rev", r]) : [],
             "-L", $"{start},{end}", "HEAD", "--", relative];
        var (text, failure) = await RunAsync(Blame(revisions.Count > 0), repo, ct);
        var skipNote = revisions.Count > 0
            ? $" The commits {IgnoreRevsFile} lists are skipped: a line they changed is given to the commit before them."
            : ignoreNote;
        // Only a failure about a listed commit is retried without them: a timeout retried would double the wait for nothing.
        if (revisions.Count > 0 && failure is not null && (failure.Contains("to ignore", StringComparison.Ordinal) || failure.Contains("object name", StringComparison.OrdinalIgnoreCase)))
        {
            (text, failure) = await RunAsync(Blame(false), repo, ct);
            skipNote = $" {IgnoreRevsFile} was not applied: it lists a commit this repository does not have.";
        }
        if (failure is not null) return ClampedArgument.Above(notice, Strings.GitCommandFailed("blame", failure));
        var lines = text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var fit = ToolOutputBudget.LinesThatFit(lines);
        var report = $"git blame {relative} as last committed, lines {start}-{end} (hash, author, date, line, content; ^ marks the first commit; uncommitted changes are not shown).{skipNote}\n"
                   + string.Join('\n', lines.Take(fit))
                   + (fit < lines.Count ? $"\n… {lines.Count - fit} more line(s) did not fit; blame a narrower range." : "")
                   + "\nShow a commit with commit=<hash>.";
        return ClampedArgument.Above(notice, report);
    }

    /// <summary>The file a repository lists its reformatting commits in, for blame to skip (GitHub reads it too).</summary>
    internal const string IgnoreRevsFile = ".git-blame-ignore-revs";

    /// <summary>The most commits of <see cref="IgnoreRevsFile"/> passed to git.</summary>
    internal const int MaxIgnoredRevisions = 200;

    // A full or abbreviated commit hash, and nothing else.
    private static readonly Regex CommitHash = new("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>
    /// The commits <see cref="IgnoreRevsFile"/> lists, as arguments for git; none, and why when it is there but not used.
    /// </summary>
    /// <remarks>⚠ Read here, never by git: the file is the repository's. Given to git, a link to a secret came back in its
    /// error message ("invalid object name: API_KEY=…"), and a link to an endless device kept it reading. Only a regular
    /// file the ignore files do not keep out, made of commit hashes and comments, is used — none of its text is ever
    /// shown.</remarks>
    private static (List<string> Revisions, string? Note) IgnoredRevisions(string repo)
    {
        var path = Path.Combine(repo, IgnoreRevsFile);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ([], null);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > 256 * 1024
                || Governance.ContentExclusions.Of(path) is not null)
                return ([], $" {IgnoreRevsFile} was not applied: it is a link, larger than 256 KB, or kept out by the ignore files.");
            var revisions = new List<string>();
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (!RegexBudget.IsMatch(CommitHash, line))
                    return ([], $" {IgnoreRevsFile} was not applied: it holds a line that is no commit hash.");
                if (revisions.Count < MaxIgnoredRevisions) revisions.Add(line);
            }
            return (revisions, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Diagnostics.Swallow("GitHistoryTool.IgnoredRevisions", ex);
            return ([], $" {IgnoreRevsFile} was not applied: it could not be read.");
        }
    }

    /// <summary>The longest the search of the history may take: it reads every commit's changes.</summary>
    internal static TimeSpan SearchBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Per commit, the hunks shown; per hunk, the lines shown (around the first that holds the text); per line, the
    /// characters (a minified line is a file on one line).</summary>
    internal const int HunksPerCommit = 3, LinesPerHunk = 12, MaxLineChars = 160;

    /// <summary>
    /// The commits that added or removed <paramref name="search"/> (<c>git log -S</c>: its number of occurrences changed;
    /// <c>-G</c> with <paramref name="regex"/>: a changed line matches), newest first, each with the lines that did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ First the matching commits with the names of their matching files, every one of them; a file the ignore files keep
    /// out is dropped there, and a commit that matched only in such files with it — never listed, never counted, never said:
    /// git searches every file, and a note that an excluded file matched told whether it held the text (a search could spell
    /// a secret out one character at a time). Then each commit's changes to its other matching files, read one commit at a
    /// time, so that one huge diff costs that commit its lines, never the search.
    /// </para>
    /// <para>Runs no program the repository names (<c>--no-textconv</c>, <c>--no-ext-diff</c>), and never `log.showRoot`'s
    /// absence: the first commit is searched too. Merge commits are not (git's pickaxe skips them).</para>
    /// </remarks>
    private static async Task<string> SearchAsync(string repo, string relative, string search, bool regex, JsonElement args, CancellationToken ct)
    {
        // ⚠ A NUL ends a command line on Windows: every argument after it — the path, the limits, --no-textconv — was dropped.
        if (search.Contains('\0')) return "Error: search holds a NUL character, which git cannot be given.";
        var (count, notice) = ClampedArgument.Read(args, "max_count", DefaultCount, MinCount, MaxCount);
        var (skip, skipNotice) = ClampedArgument.Read(args, "skip", 0, 0, 1_000_000);
        if (skipNotice is not null) notice = notice is null ? skipNotice : notice + "\n" + skipNotice;

        List<string> list =
        [
            .. LogShaped, "-c", "log.showRoot=true", "log", (regex ? "-G" : "-S") + search, "--no-textconv", "--no-ext-diff",
            "--date=short", "--name-only", "--format=%x01%H %h %ad %an%x09%s",
        ];
        if (relative.Length > 0) { list.Add("--"); list.Add(relative); }
        var what = (regex ? "lines matching " : "") + $"\"{search}\"";
        var shownPath = relative.Length > 0 ? relative : "the repository";
        var (text, failure) = await RunAsync(list, repo, ct, SearchBudget);
        if (failure is not null)
            return ClampedArgument.Above(notice, failure.Contains("does not have any commits yet", StringComparison.Ordinal)
                ? "The repository has no commit yet: there is no history to read."
                : Strings.GitCommandFailed("log", failure) + (relative.Length == 0 ? " Narrow the search with path." : ""));

        // A commit starts on a line that opens with \u0001: a byte inside a subject or a file's text is never at a line's start.
        var matches = new List<(string Sha, string Header, List<string> Files)>();
        foreach (var chunk in ("\n" + text.Replace("\r\n", "\n")).Split("\n\u0001", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = chunk.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || lines[0].IndexOf(' ') is not (> 0 and var space)) continue;
            var files = lines.Skip(1).Where(f => Governance.ContentExclusions.Of(Path.Combine(repo, f)) is null).ToList();
            if (files.Count == 0) continue;   // matched only where the model may not look
            matches.Add((lines[0][..space], lines[0][(space + 1)..], files));
        }
        // The same sentence whatever was found: said only when a match was dropped, it told the match.
        var keptOut = Governance.ContentExclusions.AnyRules() ? " Files the ignore files keep out are not searched." : "";
        var page = matches.Skip(skip).Take(count).ToList();
        if (page.Count == 0)
            return ClampedArgument.Above(notice, (skip > 0 && matches.Count > 0
                ? $"Only {matches.Count} commit(s) added or removed {what} in {shownPath}: skip={skip} passes over all of them."
                : $"No commit added or removed {what} in {shownPath}.") + keptOut);

        Regex? pattern = null;
        if (regex)
        {
            try { pattern = new Regex(search, RegexOptions.CultureInvariant, RegexBudget.Default); }
            catch (ArgumentException) { /* git's own syntax: the lines are not singled out, said per commit */ }
        }
        bool Holds(string line)
        {
            if (line.Length < 1 || line[0] is not ('+' or '-')) return false;
            if (!regex) return line.Contains(search, StringComparison.Ordinal);
            if (pattern is null) return false;
            try { return RegexBudget.IsMatch(pattern, line[1..]); }
            catch (RegexMatchTimeoutException) { return false; }
        }

        var sb = new StringBuilder($"Commits that added or removed {what} in {shownPath}, newest first (hash date author, then subject; the lines that did below):\n");
        var shown = 0;
        foreach (var (sha, header, files) in page)
        {
            var block = new StringBuilder(header.TrimEnd()).Append('\n');
            List<string> show =
            [
                .. LogShaped, "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false", "-c", "diff.relative=false",
                "show", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", "--format=", "--patch",
                "--unified=3", sha, "--", .. files,
            ];
            var (diff, tooLarge) = await RunAsync(show, repo, ct);
            var hunks = tooLarge is null ? Hunks(diff) : [];
            var holding = hunks.Where(h => h.Lines.Any(Holds)).ToList();
            if (tooLarge is not null)
                block.Append($"  (its changes could not be read here: {GitProcess.FirstLine(tooLarge)} — show it with commit={sha[..Math.Min(12, sha.Length)]})\n");
            else if (holding.Count == 0)
                block.Append($"  (in {string.Join(", ", files)}; the lines are not singled out — a binary file, a text over several lines, "
                             + "or a pattern only git reads: show the commit)\n");
            foreach (var hunk in holding.Take(HunksPerCommit))
            {
                block.Append("  ").Append(hunk.File).Append(' ').Append(hunk.Header).Append('\n');
                var at = hunk.Lines.FindIndex(Holds);
                var from = Math.Max(0, at - 3);
                foreach (var line in hunk.Lines.Skip(from).Take(LinesPerHunk))
                    block.Append("  ").Append(line.Length > MaxLineChars ? SafeTruncate.Truncate(line, MaxLineChars) + $" … ({line.Length - MaxLineChars} more characters)" : line).Append('\n');
                var left = hunk.Lines.Count - Math.Min(LinesPerHunk, hunk.Lines.Count - from);
                if (left > 0) block.Append($"  … {left} more line(s) of this hunk\n");
            }
            if (holding.Count > HunksPerCommit) block.Append($"  … {holding.Count - HunksPerCommit} more hunk(s) hold it: show the commit\n");
            if (shown > 0 && sb.Length + block.Length > ToolOutputBudget.ListChars) break;
            sb.Append(block);
            shown++;
        }
        var next = skip + shown;
        if (shown < page.Count || matches.Count > skip + page.Count)
            sb.Append($"… {matches.Count - next} more matching commit(s): read them with skip={next}.\n");
        sb.Append("Show one whole with commit=<hash>.").Append(keptOut);
        return ClampedArgument.Above(notice, sb.ToString());
    }

    private sealed record Hunk(string File, string Header, List<string> Lines);

    /// <summary>The hunks of a diff, each with the file it is in (its <c>b/</c> path, <c>a/</c> for a deletion).</summary>
    private static List<Hunk> Hunks(string diff)
    {
        var hunks = new List<Hunk>();
        string? file = null;
        Hunk? current = null;
        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal)) { file = null; current = null; continue; }
            // A name with a space is followed by a tab in these headers.
            if (current is null && (line.StartsWith("--- a/", StringComparison.Ordinal) || line.StartsWith("+++ b/", StringComparison.Ordinal)))
            {
                file = line[6..].TrimEnd('\t');
                continue;
            }
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = new Hunk(file ?? "?", line, []);
                hunks.Add(current);
                continue;
            }
            if (current is not null && line.Length > 0 && line[0] is '+' or '-' or ' ') current.Lines.Add(line);
        }
        return hunks;
    }

    private static async Task<string> ShowAsync(string repo, string commit, string relative, CancellationToken ct)
    {
        if (!RegexBudget.IsMatch(Revision, commit))
            return $"Error: '{commit}' is not a commit name. Give a hash or a name such as HEAD~2 (no spaces, not starting with '-').";
        // ⚠ Resolved to a COMMIT first: `show` also prints a blob or a tree, so `HEAD:.env` (or a blob's hash read off a tree)
        // would print a file the ignore files exclude — no diff header for the exclusion filter to find, no path for the
        // registry to judge.
        var (resolved, notACommit) = await RunAsync(["rev-parse", "--verify", "--quiet", "--end-of-options", commit + "^{commit}"], repo, ct);
        var sha = resolved.Trim();
        if (notACommit is not null || sha.Length == 0 || sha.Contains('\n'))
            return $"Error: '{commit}' names no commit of this repository. Give a commit hash or a name such as HEAD~2.";
        List<string> command =
        [
            .. LogShaped, "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false", "-c", "diff.relative=false",
            "show", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", "--date=short",
            "--format=commit %H%nAuthor: %an%nDate: %ad%n%n%B", "--stat", "--patch", sha,
        ];
        if (relative.Length > 0) { command.Add("--"); command.Add(relative); }
        var (text, failure) = await RunAsync(command, repo, ct);
        if (failure is not null) return Strings.GitCommandFailed("show", failure);

        var dropped = new Governance.ContentExclusions.Tally();
        var readable = Governance.ContentExclusions.WithoutExcludedFiles(text, repo, dropped);
        var note = dropped.Note();
        var budget = Math.Max(1_500, ToolOutputBudget.ListChars - note.Length - 200);
        var (shown, cut) = GetGitStatusTool.CutDiff(readable, budget, restricted: relative.Length > 0);
        var sb = new StringBuilder();
        if (note.Length > 0) sb.AppendLine(note);
        if (cut is not null) sb.AppendLine(cut.Replace("include_diff=true and diff_path=<file>", "commit and path=<file>"));
        sb.Append(shown.TrimEnd());
        return sb.ToString();
    }

    private static async Task<(string Text, string? Failure)> RunAsync(IReadOnlyList<string> command, string repo, CancellationToken ct,
                                                                      TimeSpan? budget = null)
    {
        var r = await GitProcess.CaptureBytesAsync(command, repo, ct, MaxBytes, budget: budget);
        if (r.Output is null)
            return (string.Empty, r.Overflow ? $"the answer is larger than {MaxBytes / (1024 * 1024)} MB — ask for a narrower path or range" : r.Failure);
        var text = GitProcess.DecodeCaptured(Encoding.Latin1.GetString(r.Output), workingTree: GitProcess.WorkingTreeEncoding(repo));
        return (text, null);
    }
}
