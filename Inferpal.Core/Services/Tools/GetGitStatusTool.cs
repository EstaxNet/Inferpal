using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetGitStatusTool : ITool
{
    private readonly IEditorSurface _editor;

    public GetGitStatusTool(IEditorSurface editor, Func<string?>? getRoot = null)
    {
        _editor  = editor;
        _getRoot = getRoot ?? (() => null);
    }

    private readonly Func<string?> _getRoot;

    private const int MaxDiffChars = 6000;

    /// <summary>What the diff may take after the sections above it: the whole report must fit one tool result, or the
    /// loop cuts the diff in its middle under its own, unnamed marker.</summary>
    private static int DiffBudget(int reportSoFar) =>
        Math.Clamp(ToolOutputBudget.ListChars - reportSoFar - 400, 1_500, MaxDiffChars);

    public string Name => "get_git_status";

    public string Description =>
        "Returns the state of the git repository: current branch, status of tracked/untracked files, " +
        "last 20 commits, local branches, and a diff summary of uncommitted changes. " +
        "Set include_diff=true to also get the full diff of uncommitted changes (capped; when it is cut, " +
        "diff_path reads the diff of one file or folder). " +
        "Use this to understand what changed, suggest a commit message, or explain a diff.";

    public object Parameters => new
    {
        type = "object",
        properties = new
        {
            path = new
            {
                type        = "string",
                description = "Path to any file or directory inside the repository (optional, defaults to the workspace)."
            },
            include_diff = new
            {
                type        = "boolean",
                description = "If true, includes the full unified diff of uncommitted changes. Default: false."
            },
            diff_path = new
            {
                type        = "string",
                description = "With include_diff: restrict the diff to this file or folder — the way to read the diff " +
                              "of a file the full diff cut off."
            }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var startPath    = args.Str("path");
        var includeDiff  = args.Bool("include_diff", false);
        var diffPath     = args.Str("diff_path");
        if (diffPath is not null)
        {
            var workspace = _getRoot();
            diffPath = PathSanitizer.Sanitize(diffPath, workspace);
            PathSanitizer.AssertUnderRoot(diffPath, workspace);
        }

        // Same confinement contract as every other path-taking tool: an unsanitised path would
        // read the git status of any repository on the machine — read-only, but outside the
        // advertised boundary.
        if (startPath is not null)
        {
            var workspace = _getRoot();
            startPath = PathSanitizer.Sanitize(startPath, workspace);
            PathSanitizer.AssertUnderRoot(startPath, workspace);
        }

        // Without a path, the workspace's repository: under VS the process's current directory is
        // not the workspace, and an open file may belong to another repository. Those two only
        // stand in when no root is known. A path outside any repository is simply not a repository.
        var workspaceRoot = _getRoot();
        var root = startPath is not null
            ? FindGitRoot(startPath)
            : !string.IsNullOrWhiteSpace(workspaceRoot)
                ? FindGitRoot(workspaceRoot)
                : FindGitRootFromOpenFiles() ?? FindGitRoot(Directory.GetCurrentDirectory());

        if (root is null)
            return Strings.GitNotRepo;

        // ── status ────────────────────────────────────────────────────────────
        var status = await GitAsync("status", root, ct);
        // ⚠ THE gate of this tool, and it is `status` because its success is what proves git runs
        // and this repository opens. Without it the four sections below each answer "" and render
        // as (empty) / (no commits) / (no branches) / (nothing to diff) — a complete, fabricated
        // report of a pristine repository. A folder holding an empty `.git` (a partial clone, a
        // dubious-ownership refusal, a half-deleted worktree) exits 128 with everything on stderr,
        // and this tool keeps stdout alone.
        if (!status.Ok)
            return $"Repository root: {root}\n\n{Strings.GitCommandFailed("status", status.Detail)}";

        var sb = new StringBuilder();
        sb.AppendLine($"Repository root: {root}");
        sb.AppendLine();

        sb.AppendLine("=== git status ===");
        sb.AppendLine(Fit(status.Or("(empty)"), StatusChars, "status line(s)", keepLast: false));
        sb.AppendLine();

        // ── log ───────────────────────────────────────────────────────────────
        var log = await GitAsync("log --oneline -20", root, ct);
        sb.AppendLine("=== git log --oneline -20 ===");
        // ⚠ Deliberately NOT gated: a repository with no commits yet answers 128 here (and to
        // `diff HEAD` below) because HEAD does not exist, and "(no commits)" is exactly right for
        // it. What makes that safe is the gate above — a repository git will not open never
        // reaches this line.
        sb.AppendLine(log.Or("(no commits)"));
        sb.AppendLine();

        // ── branches ─────────────────────────────────────────────────────────
        // Most recently committed first: past the budget, the branches left out are the stale ones. The current one
        // (marked `*`) is always kept, wherever the date puts it.
        var branches = await GitAsync("branch -a --sort=-committerdate", root, ct);
        sb.AppendLine("=== git branch -a (most recent first) ===");
        // Gated: unlike `log`, this one answers 0 on a repository without commits.
        var branchText = branches.OrFailure("branch -a", "(no branches)");
        if (branches.Ok && branches.Output.Length > 0)
        {
            var lines   = branches.Output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            var current = lines.FindIndex(l => l.StartsWith('*'));
            if (current > 0) { var line = lines[current]; lines.RemoveAt(current); lines.Insert(0, line); }
            branchText = Fit(string.Join('\n', lines), BranchChars, "branch(es)", keepLast: false);
        }
        sb.AppendLine(branchText);
        sb.AppendLine();

        // ── diff stat ─────────────────────────────────────────────────────────
        // ⚠ No commit yet, there is no HEAD to diff against: the work is compared with the EMPTY TREE. The plain
        // `git diff` it fell back to compares the working tree with the INDEX, so every file added with `git add` was
        // missing — "(nothing to diff)" two lines under a status listing "A  b.txt". Only when HEAD is absent: on a
        // clean repository with commits, the empty tree would list the whole project as added.
        var hasHead  = (await GitAsync("rev-parse --verify --quiet HEAD", root, ct)).Ok;
        var baseline = hasHead ? "HEAD" : await EmptyTreeAsync(root, ct);
        var diffStat = await GitAsync($"diff --stat {baseline}", root, ct);

        sb.AppendLine(hasHead ? "=== diff summary (vs HEAD) ===" : "=== diff summary (no commit yet: vs nothing) ===");
        // The fallback is what covers the missing HEAD, so a failure of BOTH attempts is a real one.
        // Its last line is the total ("N files changed, …"): kept whatever the budget drops.
        sb.AppendLine(diffStat.Ok && diffStat.Output.Length > 0
            ? Fit(diffStat.Output, DiffStatChars, "changed file(s) in this summary", keepLast: true)
            : diffStat.OrFailure("diff --stat", "(nothing to diff)"));

        // ── full diff (optional) ──────────────────────────────────────────────
        if (includeDiff)
        {
            sb.AppendLine();
            var pathSpec = string.Empty;
            if (diffPath is not null)
            {
                var relative = Path.GetRelativePath(root, diffPath).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                    return sb.Append($"Error: diff_path '{diffPath}' is outside the repository {root}.").ToString();
                pathSpec = $" -- \"{relative}\"";
            }

            var diff = await GitAsync($"diff {baseline}{pathSpec}", root, ct);

            var shownBase = hasHead ? "HEAD" : "(no commit yet: vs nothing)";
            sb.AppendLine($"=== git diff {shownBase}{pathSpec} ===");
            if (!diff.Ok)
            {
                sb.AppendLine(Strings.GitCommandFailed("diff", diff.Detail));
            }
            else if (diff.Output.Length == 0)
            {
                sb.AppendLine("(no diff)");
            }
            else
            {
                var (shown, note) = CutDiff(diff.Output, DiffBudget(sb.Length), restricted: diffPath is not null);
                if (note is not null) sb.AppendLine(note);   // above the diff: it qualifies what follows
                sb.AppendLine(shown);
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ⚠ The report reaches the model as ONE tool result, and the loop cuts a longer one in its MIDDLE: with three
    // hundred branches it was 25 000 characters, and the cut fell on the branches and the start of the diff summary —
    // the section that says what changed. Each long section keeps what fits and counts the rest.
    private const int StatusChars   = 1_800;
    private const int BranchChars   = 1_200;
    private const int DiffStatChars = 1_800;

    /// <summary>The lines of <paramref name="text"/> that fit <paramref name="budget"/>, the rest counted — the last
    /// line kept apart when it is a total.</summary>
    internal static string Fit(string text, int budget, string what, bool keepLast)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var last  = keepLast && lines.Count > 1 ? lines[^1] : null;
        if (last is not null) lines.RemoveAt(lines.Count - 1);
        var shown = ToolOutputBudget.LinesThatFit(lines, budget);
        if (shown >= lines.Count) return text;

        var kept = lines.Take(shown).ToList();
        kept.Add($"… +{lines.Count - shown} more {what} not listed");
        if (last is not null) kept.Add(last);
        return string.Join('\n', kept);
    }

    private static readonly System.Text.RegularExpressions.Regex DiffFileHeader = new(
        @"^diff --git a/.+? b/(?<path>.+?)\r?$",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// The diff within <paramref name="max"/> characters, cut on a line end, and the note that says what the cut left
    /// out: the file it cut short and the files it never reached, by name, with the way to read them.
    /// </summary>
    /// <remarks>
    /// ⚠ A cap with no way past it is a wall: the diff is sorted by path, so the files after the cut were never
    /// visible at all, and "truncated — N more characters" left the model to review what it could see as if it
    /// were the change. Reading one file's diff (<c>diff_path</c>) is the way past; a single file's diff still
    /// over the cap leaves the file itself, through read_file.
    /// </remarks>
    internal static (string Shown, string? Note) CutDiff(string diff, int max, bool restricted)
    {
        if (diff.Length <= max) return (diff, null);

        var cut = diff.LastIndexOf('\n', max - 1);
        if (cut <= 0) cut = max;
        var shown = diff[..cut];

        if (restricted)
            return (shown, $"(diff cut at {cut:N0} of {diff.Length:N0} characters — the rest of these changes is not " +
                           "shown: read the file itself with read_file)");

        var headers = DiffFileHeader.Matches(diff).Select(m => (m.Index, Path: m.Groups["path"].Value)).ToList();
        string? cutShort = null;
        var notShown = new List<string>();
        for (var i = 0; i < headers.Count; i++)
        {
            var end = i + 1 < headers.Count ? headers[i + 1].Index : diff.Length;
            if (headers[i].Index >= cut) notShown.Add(headers[i].Path);
            else if (end > cut + 1) cutShort = headers[i].Path;
        }

        const int MaxNamed = 20;
        var note = new StringBuilder($"(diff cut at {cut:N0} of {diff.Length:N0} characters.");
        if (cutShort is not null) note.Append($" Cut short: {cutShort}.");
        if (notShown.Count > 0)
        {
            note.Append($" Not shown at all ({notShown.Count} file(s)): {string.Join(", ", notShown.Take(MaxNamed))}");
            if (notShown.Count > MaxNamed) note.Append($", +{notShown.Count - MaxNamed} more");
            note.Append('.');
        }
        note.Append(" Read one file's diff with include_diff=true and diff_path=<file>.)");
        return (shown, note.ToString());
    }

    /// <param name="Output">git's stdout, trimmed — empty when it wrote none.</param>
    /// <param name="Ok">git ran and exited 0. An empty <paramref name="Output"/> means something
    /// else entirely when this is <c>false</c>.</param>
    /// <param name="Detail">git's own first line, for the message.</param>
    private readonly record struct GitAnswer(string Output, bool Ok, string Detail)
    {
        /// <summary>The output, or <paramref name="empty"/> when git answered nothing.</summary>
        /// <remarks>For a section whose failure is a legitimate state — see <c>log</c>.</remarks>
        public string Or(string empty) => Output.Length == 0 ? empty : Output;

        /// <summary>
        /// The output, the named failure when git refused, <paramref name="empty"/> otherwise.
        /// </summary>
        /// <remarks>
        /// One reader for every section, so that no rendering site has to remember the difference
        /// between "git said nothing" and "git said no".
        /// </remarks>
        public string OrFailure(string command, string empty) =>
            !Ok ? Strings.GitCommandFailed(command, Detail) : Or(empty);
    }

    /// <summary>Runs <c>git &lt;arguments&gt;</c> and keeps <b>whether it answered</b>.</summary>
    /// <remarks>
    /// ⚠ <b>stdout only, deliberately</b>: every caller here parses porcelain output line by line,
    /// and git writes advice and warnings to stderr.
    /// ⚠ And it runs through <see cref="GitProcess"/> rather than its own process plumbing: both
    /// pipes must be drained, or a repository chatty enough to fill the stderr buffer deadlocks git
    /// (it blocks writing, never closes stdout, and the read of stdout never returns) until the
    /// budget expires — after which the catch-all reports "no changes".
    /// </remarks>
    /// <summary>The empty tree of this repository's object format — what a repository with no commit is compared with.
    /// git knows it without storing it; its name depends on the hash (SHA-1, or SHA-256 since git 2.29).</summary>
    private static async Task<string> EmptyTreeAsync(string root, CancellationToken ct) =>
        (await GitAsync("rev-parse --show-object-format", root, ct)).Output.Trim() == "sha256"
            ? "6ef19b41225c5369f1c104d45d8d85efa9b057b53b14b4b9b939dd74decc5321"
            : "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    private static async Task<GitAnswer> GitAsync(string arguments, string workDir, CancellationToken ct)
    {
        var r = await GitProcess.CaptureAsync(arguments, workDir, ct);
        var detail = r.TimedOut
            ? $"no answer within {GitProcess.Timeout.TotalSeconds:0}s"
            : GitProcess.Detail(r.Stderr, r.ExitCode);
        return new GitAnswer(r.Stdout.Trim(), !r.TimedOut && r.ExitCode == 0, detail);
    }

    private string? FindGitRootFromOpenFiles()
    {
        foreach (var p in _editor.GetOpenDocumentPaths())
        {
            var root = FindGitRoot(p);
            if (root is not null) return root;
        }
        return null;
    }

    private static string? FindGitRoot(string startPath)
    {
        var dir = Directory.Exists(startPath) ? startPath : Path.GetDirectoryName(startPath);
        return dir is null ? null : GitProcess.WorkTreeOf(dir);
    }
}
