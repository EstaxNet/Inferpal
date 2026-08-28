namespace Inferpal.Services;

/// <summary>
/// Character-count truncation that never cuts a UTF-16 surrogate pair in half. A chunk with an
/// emoji or astral-plane character exactly at the boundary used to leave a lone surrogate in the
/// prompt — some JSON serializers replace it, others throw (pre-1.6.0 architecture review).
/// </summary>
internal static class SafeTruncate
{
    /// <summary>The first <paramref name="maxChars"/> chars of <paramref name="s"/>, backing off
    /// one char when the cut would split a surrogate pair. No suffix is added — callers own
    /// their own ellipsis convention.</summary>
    public static string Truncate(string s, int maxChars)
    {
        if (s.Length <= maxChars) return s;
        var n = maxChars;
        if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n];
    }
}
