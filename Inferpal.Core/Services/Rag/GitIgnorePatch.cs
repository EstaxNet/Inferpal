namespace Inferpal.Services.Rag;

/// <summary>
/// What Inferpal adds to a repository's <c>.gitignore</c>: its file-history snapshots, nothing else.
/// </summary>
/// <remarks>
/// Most of <c>.inferpal/</c> is meant to be committed — the team overlays (<c>permissions.json</c>,
/// <c>validators.json</c>, <c>project.json</c>), <c>rules/</c>, <c>checks/</c>, <c>prompts/</c>,
/// <c>plans/</c>, and the files the system prompt re-injects for everyone on the repository. Only
/// <c>history/</c> is local data: copies of whatever the agent overwrote, secrets included. Ignoring the
/// whole folder made every overlay created afterwards silently uncommittable.
/// A rule the user wrote is never rewritten; only the block earlier versions appended, recognised by
/// its header, is narrowed.
/// </remarks>
internal static class GitIgnorePatch
{
    internal const string Header       = "# Inferpal AI assistant";
    internal const string HistoryEntry = ".inferpal/history/";

    /// <summary>The patched content, or <c>null</c> when <paramref name="existing"/> needs no change.</summary>
    internal static string? Apply(string existing)
    {
        var nl    = existing.Contains("\r\n") ? "\r\n" : existing.Contains('\n') ? "\n" : Environment.NewLine;
        var lines = existing.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // The block earlier versions appended ignored the whole folder: narrow it in place.
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].Trim() == Header && lines[i + 1].Trim() == ".inferpal/")
            {
                lines[i + 1] = HistoryEntry;
                return string.Join(nl, lines);
            }
        }

        if (lines.Any(Covers)) return null;

        var separator = existing.Length == 0      ? string.Empty
                      : existing.EndsWith('\n')   ? nl
                      :                             nl + nl;
        return existing + separator + Header + nl + HistoryEntry + nl;
    }

    // Already ignored: the history folder itself, or the whole folder by the user's own choice.
    private static bool Covers(string line)
    {
        var rule = line.Trim().TrimStart('/').TrimEnd('/');
        return rule is ".inferpal" or ".inferpal/history";
    }
}
