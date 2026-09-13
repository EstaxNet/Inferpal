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
            var enabled = !line.StartsWith('#');
            var body    = enabled ? line : line.TrimStart('#').Trim();
            var eq      = body.IndexOf('=');
            var name    = eq > 0 ? body[..eq].Trim() : string.Empty;
            var value   = eq > 0 ? body[(eq + 1)..].Trim() : string.Empty;
            if (name.Length == 0 || value.Length == 0) { kept.Add(line); continue; }
            entries.Add(new Entry(enabled, name, value));
        }
        unparsed = kept;
        return entries;
    }

    /// <summary>The setting text: one line per entry, then the lines that were not entries.</summary>
    public static string Render(IEnumerable<Entry> entries, IReadOnlyList<string> unparsed) =>
        string.Join("\n", entries.Select(e => (e.Enabled ? "" : "#") + e.Name + "=" + e.Value).Concat(unparsed));
}
