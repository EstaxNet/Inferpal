namespace Inferpal.Services.Tools;

/// <summary>The first names of a list in a sentence, then how many more: a note does not become the list it qualifies,
/// and a list cut without its count reads as the whole of it.</summary>
internal static class NamedFew
{
    /// <summary>The first <paramref name="shown"/> of <paramref name="names"/>, comma-separated, then "and N more".</summary>
    internal static string Of(IReadOnlyList<string> names, int shown = 5) =>
        string.Join(", ", names.Take(shown)) + (names.Count > shown ? $" and {names.Count - shown} more" : "");
}
