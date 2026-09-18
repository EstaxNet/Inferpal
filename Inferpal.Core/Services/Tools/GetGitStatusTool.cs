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

    public string Name => "get_git_status";

    public string Description =>
        "Returns the state of the git repository: current branch, status of tracked/untracked files, " +
        "last 20 commits, local branches, and a diff summary of uncommitted changes. " +
        "Set include_diff=true to also get the full diff of uncommitted changes (can be large). " +
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
            }
        },
        required = Array.Empty<string>(),
    };

    public async Task<string> ExecuteAsync(JsonElement args, CancellationToken ct)
    {
        var startPath    = args.Str("path");
        var includeDiff  = args.Bool("include_diff", false);

        // Same confinement contract as every other path-taking tool: this was the one tool that
        // took the model's raw path and would read the git status of any repository on the
        // machine (pre-1.6.0 architecture review, §1.10). Read-only, but outside the advertised boundary.
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
        // and this repository opens. Without it the four sections below each answered "" and were
        // rendered as (empty) / (no commits) / (no branches) / (nothing to diff) — a complete,
        // fabricated report of a pristine repository. Measured on a folder holding an empty `.git`,
        // which is what a partial clone, a dubious-ownership refusal or a half-deleted worktree
        // looks like from out here: `status` exits 128 with everything on stderr, and this tool
        // kept stdout alone.
        if (!status.Ok)
            return $"Repository root: {root}\n\n{Strings.GitCommandFailed("status", status.Detail)}";

        var sb = new StringBuilder();
        sb.AppendLine($"Repository root: {root}");
        sb.AppendLine();

        sb.AppendLine("=== git status ===");
        sb.AppendLine(status.Or("(empty)"));
        sb.AppendLine();

        // ── log ───────────────────────────────────────────────────────────────
        var log = await GitAsync("log --oneline -20", root, ct);
        sb.AppendLine("=== git log --oneline -20 ===");
        // ⚠ Deliberately NOT gated: measured, a repository with no commits yet answers 128 here
        // (and to `diff HEAD` below) because HEAD does not exist, and "(no commits)" is exactly
        // right for it. What makes that safe is the gate above — a repository git will not open
        // never reaches this line.
        sb.AppendLine(log.Or("(no commits)"));
        sb.AppendLine();

        // ── branches ─────────────────────────────────────────────────────────
        var branches = await GitAsync("branch -a", root, ct);
        sb.AppendLine("=== git branch -a ===");
        // Gated: unlike `log`, this one answers 0 on a repository without commits.
        sb.AppendLine(branches.OrFailure("branch -a", "(no branches)"));
        sb.AppendLine();

        // ── diff stat ─────────────────────────────────────────────────────────
        var diffStat = await GitAsync("diff --stat HEAD", root, ct);
        if (!diffStat.Ok || diffStat.Output.Length == 0)
            diffStat = await GitAsync("diff --stat", root, ct);   // fallback: no commits yet

        sb.AppendLine("=== diff summary (vs HEAD) ===");
        // The fallback is what covers the missing HEAD, so a failure of BOTH attempts is a real one.
        sb.AppendLine(diffStat.OrFailure("diff --stat", "(nothing to diff)"));

        // ── full diff (optional) ──────────────────────────────────────────────
        if (includeDiff)
        {
            sb.AppendLine();
            var diff = await GitAsync("diff HEAD", root, ct);
            if (!diff.Ok || diff.Output.Length == 0)
                diff = await GitAsync("diff", root, ct);

            sb.AppendLine("=== git diff HEAD ===");
            if (!diff.Ok)
            {
                sb.AppendLine(Strings.GitCommandFailed("diff", diff.Detail));
            }
            else if (diff.Output.Length == 0)
            {
                sb.AppendLine("(no diff)");
            }
            else if (diff.Output.Length > MaxDiffChars)
            {
                sb.AppendLine(diff.Output[..MaxDiffChars]);
                sb.AppendLine($"... [truncated — {diff.Output.Length - MaxDiffChars} more characters]");
            }
            else
            {
                sb.AppendLine(diff.Output);
            }
        }

        return sb.ToString().TrimEnd();
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
    /// and git writes advice and warnings to stderr. This used to be a third private copy of the
    /// process plumbing — env vars, encodings, timeout — and it had drifted: it never drained
    /// stderr, so a repository chatty enough to fill that buffer deadlocked git (it blocks writing,
    /// never closes stdout, and the read of stdout never returns) until the 15 s budget expired,
    /// after which the catch-all reported "no changes". That is the exact defect
    /// <see cref="GitProcess"/> was fixed for on 2026-08-03; the copy kept it. Found by the review
    /// of 2026-08-07.
    /// </remarks>
    /// <remarks>
    /// This used to be a third private copy of the process plumbing — env vars, encodings, timeout —
    /// and it had drifted: it never drained stderr, so a repository chatty enough to fill that
    /// buffer deadlocked git (it blocks writing, never closes stdout, and the read of stdout never
    /// returns) until the 15 s budget expired, after which the catch-all reported "no changes".
    /// That is the exact defect <see cref="GitProcess"/> was fixed for on 2026-08-03; the copy kept
    /// it. Found by the review of 2026-08-07.
    /// </remarks>
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
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
