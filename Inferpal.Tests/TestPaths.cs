namespace Inferpal.Tests;

/// <summary>
/// Platform-neutral fixture paths (§23). Many pure-logic tests fake a file system with
/// Windows-style literals (<c>C:\repo\src</c>); the logic under test walks them with
/// <c>System.IO.Path</c>, which on Linux does not treat <c>\</c> as a separator — the walk
/// silently goes nowhere and the test fails for a reason that has nothing to do with the code.
/// </summary>
internal static class TestPaths
{
    /// <summary>
    /// Maps a Windows-style fixture path to the platform: unchanged on Windows,
    /// <c>C:\repo\src</c> → <c>/c/repo/src</c> elsewhere (the drive letter keeps two roots
    /// like <c>C:</c> and <c>D:</c> distinct).
    /// </summary>
    public static string P(string winPath) => OperatingSystem.IsWindows()
        ? winPath
        : "/" + char.ToLowerInvariant(winPath[0]) + winPath[2..].Replace('\\', '/');
}
