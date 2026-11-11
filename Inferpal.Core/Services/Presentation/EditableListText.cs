namespace Inferpal.Services.Presentation;

/// <summary>
/// The <c>name=value</c> lists of the settings window — custom tools, command templates — as the
/// structured editor reads and writes them.
/// </summary>
/// <remarks>
/// The editor rewrites the whole setting from its rows on every add, edit, delete, toggle and save. A
/// line that does not become a row — no <c>=</c>, an empty side — is still text the user typed: it is
/// kept verbatim after the entries rather than deleted before it could be corrected. The parsers that
/// consume these settings ignore such a line and say so in <c>/diagnostics</c>.
/// </remarks>
internal static class EditableListText
{
    /// <param name="Enabled"><c>false</c> for a line prefixed with <c>#</c>.</param>
    internal readonly record struct Entry(bool Enabled, string Name, string Value);

    /// <summary>Splits <paramref name="text"/> into entries and the lines that are not entries.</summary>
    public static IReadOnlyList<Entry> Parse(string? text, out IReadOnlyList<string> unparsed)
    {
        var entries = new List<Entry>();
        var kept    = new List<string>();
        foreach (var line in (text ?? string.Empty)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ParseLine(line) is { } entry) entries.Add(entry);
            else kept.Add(line);
        }
        unparsed = kept;
        return entries;
    }

    /// <summary>One trimmed line as an entry; <c>null</c> when it is none (no <c>=</c>, an empty side).</summary>
    public static Entry? ParseLine(string line) =>
        Config.CollectionMerge.NameValue(line) is { } e ? new Entry(e.Enabled, e.Name, e.Value) : null;

    /// <summary>Whether two texts hold the same entries and other lines, however each line is written.</summary>
    public static bool SameEntries(string? a, string? b) =>
        Render(Parse(a, out var ua), ua) == Render(Parse(b, out var ub), ub);

    /// <summary>The setting text: one line per entry, then the lines that were not entries.</summary>
    public static string Render(IEnumerable<Entry> entries, IReadOnlyList<string> unparsed) =>
        string.Join("\n", entries.Select(e => (e.Enabled ? "" : "#") + e.Name + "=" + e.Value).Concat(unparsed));

    /// <summary>
    /// <paramref name="mine"/> with what the file changed since <paramref name="baseline"/>, entry by entry
    /// (<see cref="Config.CollectionShape.NameValueLines"/>): an entry another editor added is kept, one it changed or
    /// removed — and left as it was here — follows the file; what this copy added, changed or removed wins.
    /// </summary>
    public static string Merge(string? baseline, string? onDisk, string mine) =>
        Config.CollectionMerge.Merge(Config.CollectionShape.NameValueLines, baseline ?? string.Empty, onDisk ?? string.Empty, mine)
        ?? mine;
}
