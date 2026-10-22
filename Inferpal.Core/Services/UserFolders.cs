namespace Inferpal.Services;

/// <summary>
/// Where a per-user folder is, whether or not it exists yet: the one way the Core and the in-process half ask.
/// </summary>
/// <remarks>
/// ⚠ <c>Environment.GetFolderPath</c> without an option answers <b>an empty string</b> for a folder that does not
/// exist. On Linux, <c>ApplicationData</c> is <c>$XDG_CONFIG_HOME</c> or <c>~/.config</c>, absent on a fresh account
/// (a new WSL distribution, an SSH remote, a container):
/// <c>Path.Combine("", "Inferpal", …)</c> then resolves against the current directory, which the VS Code extension
/// sets to the workspace root — settings, sessions, indexes and tokens written into the user's repository, and an
/// <c>Inferpal/config.json</c> the repository carries read as the user's own. Every writer creates its folder, so
/// the path is all that is needed. Rule 37 of <c>ConventionCoverageTests</c>.
/// </remarks>
internal static class UserFolders
{
    /// <summary>The folder's path, even before it exists.</summary>
    internal static string Of(Environment.SpecialFolder folder) =>
        Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
}
