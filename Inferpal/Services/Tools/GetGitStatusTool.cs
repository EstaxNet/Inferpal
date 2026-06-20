using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Localization;

namespace Inferpal.Services.Tools;

internal class GetGitStatusTool : ITool
{
    private readonly VsContextHolder _contextHolder;

    public GetGitStatusTool(VsContextHolder contextHolder) => _contextHolder = contextHolder;

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

    private static async Task<string> GitAsync(string arguments, string workDir, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "git",
                Arguments              = arguments,
                WorkingDirectory       = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding  = Encoding.UTF8,
            };
            // prevent interactive prompts and ensure English output
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            psi.Environment["LANG"]                = "en_US.UTF-8";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return stdout.Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch { return string.Empty; }
    }

    private string? FindGitRootFromOpenFiles()
    {
        foreach (var p in _contextHolder.GetOpenPaths())
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
