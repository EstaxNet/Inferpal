namespace Inferpal.Services;

/// <summary>
/// Match budget for every regex in this repository that meets input the process did not write.
/// </summary>
/// <remarks>
/// <para>
/// It started as the budget of the analysis tools, and it sat under <c>Services\Tools</c> for
/// that reason — but the callers had already spread to <c>Bench</c>, <c>Docs</c>, <c>Execution</c>
/// and <c>Persistence</c> before anyone moved it, which is the usual sign that a type is named
/// after where it was born rather than what it is. It lives at the root of <c>Services</c> now, in
/// the enclosing namespace, so every file in the Core sees it without a using.
/// </para>
/// <para>
/// These patterns parse whatever happens to be in the workspace — including generated, minified or
/// concatenated files where a single "line" is megabytes long — the open web, the output of a
/// child process, and the output of the model itself. Several of them combine optional
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
