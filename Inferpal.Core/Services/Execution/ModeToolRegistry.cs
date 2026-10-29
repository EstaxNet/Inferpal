using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and keeps only the tools a mode keeps (<see cref="ModeTools"/>): what is not
/// offered is not declared, and a call written as text to it is refused. An edit outside the mode's pattern (Roo's
/// <c>fileRegex</c>) is refused here, BEFORE the inner registry asks for approval.
/// </summary>
/// <remarks>
/// ⚠ Only narrows: every call that passes goes through the inner registry, its approval card, deny lists and turned-off
/// tools included. Same two halves as <see cref="PlanModeToolRegistry"/> — the definitions keep the tools out of the
/// model's view, the execute guard catches a call written as text that bypasses them.
/// </remarks>
internal sealed class ModeToolRegistry(IToolRegistry inner, ChatMode mode, string? root) : IToolRegistry
{
    private readonly ModeTools _tools = mode.Tools ?? throw new ArgumentException("A mode that keeps every tool needs no wrapper.", nameof(mode));

    public IReadOnlyList<ToolDefinition> Definitions =>
        inner.Definitions.Where(d => _tools.Allows(d.Function.Name)).ToList();

    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();

    public int? WritesInRun => inner.WritesInRun;

    // A tool the mode refuses gets the mode's refusal, never "turn it back on": turned on, the mode still refuses it.
    public string? TurnedOffRefusal(string name) => _tools.Allows(name) ? inner.TurnedOffRefusal(name) : null;

    /// <summary>The inner registry's names this mode keeps, tools turned off included — so a call written as text to one
    /// is refused with its cause rather than shown as the answer.</summary>
    public IEnumerable<string> CallableNames => inner.CallableNames.Where(_tools.Allows);

    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct)
    {
        if (!_tools.Allows(name))
            return Task.FromResult(
                $"Error: tool '{name}' is not part of the '{mode.Name}' mode. Do not retry it; do what the mode allows, " +
                "or tell the user what this step would need.");
        if (_tools.EditPattern is { } pattern && ModeTools.IsBuiltIn(name) && ModeTools.Families["edit"].Contains(name)
            && OutsidePattern(name, args, pattern) is { } refusal)
            return Task.FromResult(refusal);
        return inner.ExecuteAsync(name, args, ct);
    }

    /// <summary>Why this edit is refused under the mode's pattern; <c>null</c> when every file it writes matches.</summary>
    private string? OutsidePattern(string name, JsonElement args, System.Text.RegularExpressions.Regex pattern)
    {
        var meaning = _tools.EditPatternDescription is { Length: > 0 } d ? $" ({d})" : string.Empty;
        var paths = ModeTools.EditedPaths(name, args);
        // The active document, a rename across files, a path missing or unreadable: no file can be checked.
        if (paths is null)
            return $"Error: '{name}' is not available in the '{mode.Name}' mode, whose edits are limited to files matching " +
                   $"{_tools.EditPatternText}{meaning}: this call does not name every file it writes. Nothing was changed.";
        foreach (var path in paths)
        {
            // ⚠ Judged on the file the tool WRITES: resolved as it resolves it, and a pattern past its budget is no match.
            var relative = RelativeToRoot(path);
            bool matches;
            try { matches = relative is not null && RegexBudget.IsMatch(pattern, relative); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { matches = false; }
            if (!matches)
                return $"Error: not written: {relative ?? path}. The '{mode.Name}' mode edits only files matching " +
                       $"{_tools.EditPatternText}{meaning}. Nothing was changed; do not retry it — say what this file would need.";
        }
        return null;
    }

    /// <summary>A path as Roo matches it — relative to the workspace, with <c>/</c> — resolved as the writing tools resolve
    /// it (<see cref="Tools.PathSanitizer.Sanitize(string?, string?)"/>); <c>null</c> when it cannot be resolved, or lies
    /// outside the workspace (the tool refuses it anyway).</summary>
    private string? RelativeToRoot(string path)
    {
        try
        {
            var full = Tools.PathSanitizer.Sanitize(path, root);
            if (string.IsNullOrEmpty(root)) return full.Replace('\\', '/');
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) ? null : relative;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
