using System.IO;
using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// Execution logic for <c>/undo-run [list]</c> — reverts every file touched by the most recent
/// agent run (restoring edits, deleting creations), or lists this session's tracked runs.
/// </summary>
/// <remarks>
/// Extracted from the two front-ends, which carried byte-identical copies differing only in how
/// they emitted the answer and how they relativised paths. Same pattern as
/// <see cref="ReplayCommandHandler"/>, but async: the revert itself is I/O.
/// </remarks>
internal static class UndoRunCommandHandler
{
    /// <param name="history">Session change tracker (<c>ToolRegistry.History</c>).</param>
    /// <param name="parts">Tokenised command; <c>parts[1] == "list"</c> switches to listing.</param>
    /// <param name="root">Project root used to shorten paths; may be null or empty.</param>
    public static async Task<string> HandleAsync(
        FileHistoryService history, string[] parts, string? root, CancellationToken ct)
    {
        var runs = history.Runs;

        if (parts.Length >= 2 && parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            var withChanges = runs.Where(r => r.FileCount > 0).ToList();
            if (withChanges.Count == 0) return Strings.UndoRunNone;

            var list = new StringBuilder();
            list.AppendLine(Strings.UndoRunListHeader(withChanges.Count));
            foreach (var r in withChanges)
                list.AppendLine($"- {r.StartedAt:HH:mm:ss} — {r.FileCount} file(s)");
            return list.ToString().TrimEnd();
        }

        var run = runs.FirstOrDefault(r => r.FileCount > 0);
        if (run is null) return Strings.UndoRunNone;

        var result = await history.UndoRunAsync(run, ct);

        var sb = new StringBuilder();
        sb.AppendLine(Strings.UndoRunResult(result.Restored.Count, result.Deleted.Count));
        foreach (var f in result.Restored) sb.AppendLine($"  ↩ {Relativise(f, root)}");
        foreach (var f in result.Deleted)  sb.AppendLine($"  🗑 {Relativise(f, root)}");
        foreach (var f in result.Failed)   sb.AppendLine($"  ⚠ {Relativise(f, root)}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Shortens a path under <paramref name="root"/>; anything else passes through.</summary>
    private static string Relativise(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return rel.StartsWith("..", StringComparison.Ordinal) ? path : rel;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow($"UndoRunCommandHandler.Relativise({path})", ex);
            return path;
        }
    }
}
