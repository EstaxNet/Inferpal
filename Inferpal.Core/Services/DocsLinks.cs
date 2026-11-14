namespace Inferpal.Services;

/// <summary>
/// Where the documentation is: the <c>docs</c> folder of the public repository, at the tag of the version installed.
/// </summary>
/// <remarks>
/// ⚠ Never <c>master</c> for a released build: the pages there describe what is developed since — a setting, a command
/// this version does not have yet. A version that is not a release number (a development build) reads <c>master</c>.
/// VS Code builds the same address from its own version (<c>vscode/src/docsLinks.ts</c>, held to this one by a test).
/// </remarks>
internal static class DocsLinks
{
    internal const string Repository = "https://github.com/EstaxNet/Inferpal";

    /// <summary>The documentation's index, for this build.</summary>
    public static string Index => For("README.md", InstalledVersion);

    /// <summary>How to write a mode (the mode selector's "Write a mode"), for this build.</summary>
    public static string Modes => For("configuration.md#modes", InstalledVersion);

    /// <summary>The page the model suggestion draws from: which model for which card, and how each was measured.</summary>
    public static string ModelsByCard => For(ModelsByCardPage, InstalledVersion);

    /// <summary>The docs page of <see cref="ModelsByCard"/>, as the settings pages of VS Code and the browser ask for it.</summary>
    internal const string ModelsByCardPage = "models.md#by-graphics-card";

    /// <summary><paramref name="page"/> of the <c>docs</c> folder, for <paramref name="version"/>.</summary>
    internal static string For(string page, string? version) => $"{Repository}/blob/{RefFor(version)}/docs/{page}";

    /// <summary>The tag a release is published under (<c>v1.7.7</c>); <c>master</c> for anything else.</summary>
    internal static string RefFor(string? version)
    {
        var parts = (version ?? string.Empty).Split('.');
        return parts.Length == 3 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? "v" + version : "master";
    }

    /// <summary>This build's version as a release names it (<c>1.7.7</c>).</summary>
    internal static string? InstalledVersion =>
        typeof(DocsLinks).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : null;
}
