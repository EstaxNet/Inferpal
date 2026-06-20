namespace Inferpal.Services;

/// <summary>
/// Pure formatter for the settings editable-list count badges (MCP servers, pinned files, custom
/// slash commands, custom agent tools). Extracted from <c>InferpalSettingsData</c> so it is
/// unit-testable without VS.
/// </summary>
internal static class ListCountBadge
{
    /// <summary>
    /// Badge text for a list of <paramref name="total"/> items of which <paramref name="enabled"/> are
    /// enabled: empty when the list is empty, the plain total when all are enabled, otherwise
    /// <c>"{enabled} / {total}"</c>.
    /// </summary>
    public static string Format(int total, int enabled) =>
        total == 0 ? string.Empty : (enabled == total ? total.ToString() : $"{enabled} / {total}");
}
