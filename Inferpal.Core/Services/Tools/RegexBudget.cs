namespace Inferpal.Services.Tools;

/// <summary>
/// Match budget for the regexes the analysis tools run over source files.
/// </summary>
/// <remarks>
/// <para>
/// These patterns parse whatever happens to be in the workspace — including generated, minified or
/// concatenated files where a single "line" is megabytes long. Several of them combine optional
/// groups with lazy quantifiers, which is exactly the shape that degenerates into catastrophic
/// backtracking; the authors bounded some quantifiers by hand (<c>{0,512}</c>) but a bound is not
/// a guarantee. Without a timeout the failure mode is the worst one available: the tool call never
/// returns and the agent turn hangs with no error.
/// </para>
/// <para>
/// Two seconds is generous for a per-file parse and short enough that a pathological file costs a
/// hiccup rather than a wedged session. Same tactic as <c>FetchUrlTool</c>, which already bounds its
/// HTML regexes because it parses attacker-controlled input.
/// </para>
/// </remarks>
internal static class RegexBudget
{
    /// <summary>Per-match ceiling for source-scanning regexes.</summary>
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(2);
}
