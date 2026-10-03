namespace Inferpal.Services.Tools;

/// <summary>
/// How much of a line-per-entry listing (files, matches) a tool hands back, so that its answer reaches the model whole.
/// </summary>
/// <remarks>
/// ⚠ The loop cuts a tool result longer than its context cap in the MIDDLE, under a marker that says how much but not
/// what: entries vanished from the middle of a listing still announced as "the first 300 files" or "the first 100
/// matches", and the model concluded those files did not exist, or did not use the symbol. The listing stops where it
/// fits, and the tool says where — with what it did not show, when that can be named.
/// </remarks>
internal static class ToolOutputBudget
{
    /// <summary>What the entries may take: the context cap, less room for the notes that follow them.</summary>
    internal const int ListChars = Agent.AgentOrchestrator.MaxToolResultCharsInContext - 1_000;

    /// <summary>How many of <paramref name="lines"/>, from the first, fit <paramref name="budget"/> — at least one.</summary>
    internal static int LinesThatFit(IReadOnlyList<string> lines, int budget = ListChars)
    {
        var used = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            used += lines[i].Length + 1;
            if (used > budget) return Math.Max(1, i);
        }
        return lines.Count;
    }
}
