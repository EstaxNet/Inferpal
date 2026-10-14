namespace Inferpal.Services.Commands;

/// <summary>What <c>/fix-build</c> compiles.</summary>
internal static class FixBuildTarget
{
    /// <summary>
    /// The path typed after the command; else the solution open in the editor; else the first solution of the root, or
    /// its first project.
    /// </summary>
    /// <param name="typed">What follows <c>/fix-build</c>, or <c>null</c>.</param>
    /// <param name="openSolution">The solution the editor has open (<c>ActiveSolutionSignal</c>), or <c>null</c>.</param>
    /// <param name="root">The workspace root, searched when no solution is open.</param>
    /// <remarks>
    /// ⚠ The OPEN solution before the folder's first one: in a folder holding <c>Backend.sln</c> and <c>Frontend.sln</c>,
    /// "Fix with AI" on the build banner of <c>Frontend.sln</c> compiled <c>Backend.sln</c> — first in ordinal order —,
    /// which passed: "✅ Build succeeded", while the solution the banner named still failed. A path given explicitly
    /// carries no note saying what was left unbuilt, so the choice has to be the right one.
    /// </remarks>
    internal static string? Resolve(string? typed, string? openSolution, string root)
    {
        if (!string.IsNullOrWhiteSpace(typed)) return typed.Trim();
        if (!string.IsNullOrEmpty(openSolution)) return openSolution;
        return SolutionFiles.FirstIn(root)
               ?? Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal).FirstOrDefault();
    }
}
