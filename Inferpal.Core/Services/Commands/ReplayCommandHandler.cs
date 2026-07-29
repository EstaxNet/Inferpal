using System.IO;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/replay [n]</c> — renders the post-mortem timeline of an agent run:
/// every tool invocation (name, target, duration, error flag) followed by the files it touched.
/// <c>n</c> is 1-based among the runs that actually called tools, most recent first (same session
/// memory as <c>/undo-run</c>). Pure and synchronous → unit-testable. Same pattern as
/// <see cref="DiagnosticsCommandHandler"/>.
/// </summary>
internal static class ReplayCommandHandler
{
    /// <summary>Handles a <c>/replay</c> invocation and returns the markdown to display.</summary>
    /// <param name="runs">Tracked runs, most recent first (<see cref="FileHistoryService.Runs"/>).</param>
    /// <param name="parts">Tokenised command (<c>parts[1]</c> = optional 1-based run index).</param>
    /// <param name="root">Project root used to relativise paths; may be empty.</param>
    public static string Handle(IReadOnlyList<HistoryRun> runs, string[] parts, string? root)
    {
        var index = 1;
        if (parts.Length >= 2 && (!int.TryParse(parts[1], out index) || index < 1))
            return Strings.SlashUsage("/replay [n]");

        var replayable = runs.Where(r => r.ToolCallCount > 0).ToList();
        if (index > replayable.Count) return Strings.ReplayNone;

        var run   = replayable[index - 1];
        var calls = run.ToolCalls;

        var sb = new StringBuilder();
        sb.AppendLine(Strings.ReplayHeader(run.StartedAt.ToString("HH:mm:ss"), calls.Count, run.FileCount));

        foreach (var c in calls)
        {
            sb.Append('\n').Append(c.Seq).Append(". `").Append(c.Tool).Append('`');
            if (!string.IsNullOrEmpty(c.Subject))
                sb.Append(' ').Append(Relativise(c.Subject, root));
            sb.Append(" *(").Append(FormatDuration(c.DurationMs)).Append(")*");
            if (c.Error) sb.Append(" ⚠");
        }

        if (run.FileCount > 0)
        {
            sb.Append("\n\n").Append(Strings.ReplayFilesHeader);
            foreach (var change in run.Changes)
                sb.Append(change.SnapshotPath is null ? "\n  🆕 " : "\n  ✏ ")
                  .Append(Relativise(change.OriginalPath, root));
        }

        return sb.ToString();
    }

    /// <summary>Shortens an absolute path under <paramref name="root"/> for display;
    /// anything else (commands, queries, outside paths) passes through unchanged.</summary>
    private static string Relativise(string subject, string? root)
    {
        if (string.IsNullOrEmpty(root)) return subject;
        try
        {
            if (!Path.IsPathRooted(subject)) return subject;
            var rel = Path.GetRelativePath(root, subject);
            return rel.StartsWith("..", StringComparison.Ordinal) ? subject : rel;
        }
        catch { return subject; }
    }

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";
}
