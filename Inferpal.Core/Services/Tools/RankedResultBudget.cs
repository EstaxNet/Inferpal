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

    /// <summary>The most of one result's body shown: past it, cut and said.</summary>
    internal const int ResultBodyChars = 900;

    /// <summary>One result's body as shown, cut past <see cref="ResultBodyChars"/>.</summary>
    internal static string Shown(string content) =>
        content.Length > ResultBodyChars ? SafeTruncate.Truncate(content, ResultBodyChars) + "\n…(truncated)" : content;

    /// <summary>
    /// The ranked results laid out: in rank order, each <c>Entry</c> (its body) while the bodies fit
    /// <see cref="BodyChars"/> — the first one always —, then from the first that does not, every result by its
    /// <c>Location</c> line. <c>FirstByLocation</c>: the 1-based rank of the first listed by location, 0 when none is.
    /// </summary>
    internal static (string Bodies, string Locations, int FirstByLocation) Lay(IReadOnlyList<(string Entry, string Location)> ranked)
    {
        var bodies = new System.Text.StringBuilder();
        var locations = new System.Text.StringBuilder();
        var firstByLocation = 0;
        for (var i = 0; i < ranked.Count; i++)
        {
            var (entry, location) = ranked[i];
            if (firstByLocation == 0 && (i == 0 || bodies.Length + entry.Length <= BodyChars))
                bodies.Append(entry);
            else
            {
                if (firstByLocation == 0) firstByLocation = i + 1;
                locations.AppendLine(location);
            }
        }
        return (bodies.ToString(), locations.ToString(), firstByLocation);
    }

    /// <summary>The line above the results when some are listed by location only.</summary>
    internal static string Note(int firstByLocation, int total, string remedy) =>
        $"(results {firstByLocation}–{total} are listed by location only, at the end: with their bodies this answer " +
        $"would not fit the context — {remedy})";
}
