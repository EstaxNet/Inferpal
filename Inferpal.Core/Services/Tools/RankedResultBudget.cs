namespace Inferpal.Services.Tools;

/// <summary>
/// How much of a ranked list of results (codebase or documentation search) goes out with its bodies.
/// </summary>
/// <remarks>
/// ⚠ The loop cuts a tool result longer than its context cap in the MIDDLE, keeping the head and the end. For a list
/// ranked by relevance that drops the middle ranks and keeps the lowest: ten 900-character excerpts are over the cap.
/// So the bodies stop where the cap would cut, the results after that are named by location — still in rank order —
/// and the answer says so above them.
/// </remarks>
internal static class RankedResultBudget
{
    /// <summary>What the bodies may take: the context cap, less room for the headers, notes and locations.</summary>
    internal const int BodyChars = Agent.AgentOrchestrator.MaxToolResultCharsInContext - 1_500;

    /// <summary>The line above the results when some are listed by location only.</summary>
    internal static string Note(int firstByLocation, int total, string remedy) =>
        $"(results {firstByLocation}–{total} are listed by location only, at the end: with their bodies this answer " +
        $"would not fit the context — {remedy})";
}
