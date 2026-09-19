namespace Inferpal.Services;

/// <summary>
/// How two file paths are compared: the one answer, for every site that asks.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Case folding is a property of the FILE SYSTEM, not of the process</b>: Windows and macOS
/// (default APFS, and HFS+ before it) fold case, Linux does not — hence
/// <c>IsLinux() ? Ordinal : OrdinalIgnoreCase</c>. The form written the other way round,
/// <c>IsWindows() ? OrdinalIgnoreCase : Ordinal</c>, says the opposite <b>about macOS</b>, and the
/// divergence is invisible on Windows and on Linux alike: only the third platform sees it.
/// </para>
/// <para>
/// ⚠ <b>What the wrong answer costs, on each side.</b> Comparing case-<i>sensitively</i> on a volume
/// that folds case makes ONE file look like TWO: <c>AssertUnderRoot</c> then refuses a perfectly
/// legitimate write because the root was spelled with another case.
/// Comparing case-<i>insensitively</i> on a volume that does not fold makes TWO files look like ONE,
/// which is worse where it decides a write: <c>apply_edits</c> would apply an edit to one file's
/// content and save it under the other's name.
/// </para>
/// <para>
/// ⚠ <b>Residual risk, named rather than hidden</b>: APFS can be formatted case-sensitive, and a
/// case-insensitive volume can be mounted under Linux. Nothing short of probing the volume can tell,
/// and probing on every comparison is not affordable — so this follows the platform's default and
/// says which way it errs.
/// </para>
/// </remarks>
internal static class PathComparer
{
    /// <summary>Comparer for collections keyed by a file path.</summary>
    public static StringComparer Default =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>The same rule, for <c>string.Equals</c> / <c>StartsWith</c> on a path.</summary>
    public static StringComparison Comparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
