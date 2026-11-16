using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

/// <summary>
/// Reads the history of the code, read-only: the commits that touched a file or folder, who last changed each line of a
/// range (<c>git blame</c>), or one commit shown with its message and diff.
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
            max_count  = new { type = "integer", description = $"Commits listed, {MinCount}-{MaxCount} (default {DefaultCount})." },
        },
        required = Array.Empty<string>(),
    };

    internal const int DefaultCount = 20, MinCount = 1, MaxCount = 100;

    /// <summary>The most lines one blame reads: past it, the call names a narrower range.</summary>
    internal const int MaxBlameLines = 200;

    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Colour off: a user's <c>color.ui=always</c> would put escape codes in what the model reads.</summary>
    private static readonly string[] NoColour = ["-c", "color.ui=never"];

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
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return $"Error: '{target}' is outside the repository {repo}.";
        if (relative == ".") relative = string.Empty;

        if (args.Str("commit") is { Length: > 0 } commit)
            return await ShowAsync(repo, commit.Trim(), relative, ct);
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
        List<string> command = [.. NoColour, "log", "--date=short", "--format=%h %ad %an%x09%s", $"-n{count + 1}"];
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
        var (text, failure) = await RunAsync([.. NoColour, "blame", "--no-textconv", "--date=short", "-L", $"{start},{end}", "HEAD", "--", relative], repo, ct);
        if (failure is not null) return ClampedArgument.Above(notice, Strings.GitCommandFailed("blame", failure));
        var lines = text.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var fit = ToolOutputBudget.LinesThatFit(lines);
        var report = $"git blame {relative} as last committed, lines {start}-{end} (hash, author, date, line, content; ^ marks the first commit; uncommitted changes are not shown):\n"
                   + string.Join('\n', lines.Take(fit))
                   + (fit < lines.Count ? $"\n… {lines.Count - fit} more line(s) did not fit; blame a narrower range." : "")
                   + "\nShow a commit with commit=<hash>.";
        return ClampedArgument.Above(notice, report);
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
            .. NoColour, "-c", "diff.noprefix=false", "-c", "diff.mnemonicPrefix=false", "-c", "diff.relative=false",
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

    private static async Task<(string Text, string? Failure)> RunAsync(IReadOnlyList<string> command, string repo, CancellationToken ct)
    {
        var r = await GitProcess.CaptureBytesAsync(command, repo, ct, MaxBytes);
        if (r.Output is null)
            return (string.Empty, r.Overflow ? $"the answer is larger than {MaxBytes / (1024 * 1024)} MB — ask for a narrower path or range" : r.Failure);
        var text = GitProcess.DecodeCaptured(Encoding.Latin1.GetString(r.Output), workingTree: GitProcess.WorkingTreeEncoding(repo));
        return (text, null);
    }
}
