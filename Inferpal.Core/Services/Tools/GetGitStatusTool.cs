using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetGitStatusTool : ITool
{
    private readonly IEditorSurface _editor;

    public GetGitStatusTool(IEditorSurface editor) => _editor = editor;

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
                description = "Path to any file or directory inside the repository (optional, defaults to cwd)."
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
        var startPath    = args.TryGetProperty("path",         out var p) ? p.GetString() : null;
        var includeDiff  = args.TryGetProperty("include_diff", out var d) && d.GetBoolean();

        var root = (startPath is not null ? FindGitRoot(startPath) : null)
                ?? FindGitRootFromOpenFiles()
                ?? FindGitRoot(Directory.GetCurrentDirectory());

        if (root is null)
            return Strings.GitNotRepo;

        var sb = new StringBuilder();
        sb.AppendLine($"Repository root: {root}");
        sb.AppendLine();

        // ── status ────────────────────────────────────────────────────────────
        var status = await GitAsync("status", root, ct);
        sb.AppendLine("=== git status ===");
        sb.AppendLine(string.IsNullOrEmpty(status) ? "(empty)" : status);
        sb.AppendLine();

        // ── log ───────────────────────────────────────────────────────────────
        var log = await GitAsync("log --oneline -20", root, ct);
        sb.AppendLine("=== git log --oneline -20 ===");
        sb.AppendLine(string.IsNullOrEmpty(log) ? "(no commits)" : log);
        sb.AppendLine();

        // ── branches ─────────────────────────────────────────────────────────
        var branches = await GitAsync("branch -a", root, ct);
        sb.AppendLine("=== git branch -a ===");
        sb.AppendLine(string.IsNullOrEmpty(branches) ? "(no branches)" : branches);
        sb.AppendLine();

        // ── diff stat ─────────────────────────────────────────────────────────
        var diffStat = await GitAsync("diff --stat HEAD", root, ct);
        if (string.IsNullOrEmpty(diffStat))
            diffStat = await GitAsync("diff --stat", root, ct);   // fallback: no commits yet

        sb.AppendLine("=== diff summary (vs HEAD) ===");
        sb.AppendLine(string.IsNullOrEmpty(diffStat) ? "(nothing to diff)" : diffStat);

        // ── full diff (optional) ──────────────────────────────────────────────
        if (includeDiff)
        {
            sb.AppendLine();
            var diff = await GitAsync("diff HEAD", root, ct);
            if (string.IsNullOrEmpty(diff))
                diff = await GitAsync("diff", root, ct);

            sb.AppendLine("=== git diff HEAD ===");
            if (string.IsNullOrEmpty(diff))
            {
                sb.AppendLine("(no diff)");
            }
            else if (diff.Length > MaxDiffChars)
            {
                sb.AppendLine(diff[..MaxDiffChars]);
                sb.AppendLine($"... [truncated — {diff.Length - MaxDiffChars} more characters]");
            }
            else
            {
                sb.AppendLine(diff);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>stdout of <c>git &lt;arguments&gt;</c>, empty when git could not answer.</summary>
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
    private static async Task<string> GitAsync(string arguments, string workDir, CancellationToken ct) =>
        (await GitProcess.CaptureAsync(arguments, workDir, ct)).Stdout.Trim();

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
