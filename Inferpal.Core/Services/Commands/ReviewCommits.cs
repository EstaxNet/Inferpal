namespace Inferpal.Services.Commands;

/// <summary>A commit offered for review: its full hash (what <c>/check</c> takes), the short one, its subject, its author
/// and when it was made.</summary>
internal sealed record CommitEntry(string Hash, string ShortHash, string Subject, string Author, DateTimeOffset When);

/// <summary>
/// "Review a commit…": the commits a person picks from in Visual Studio's dialog — VS Code's palette reads its own git
/// extension — and what typed text reviews. The pick runs <c>/check</c>, as typing it does.
/// </summary>
internal static class ReviewCommits
{
    /// <summary>How many commits the list shows at once.</summary>
    public const int Shown = 50;

    /// <summary>How far back a search looks: read once and filtered here — what the person types never reaches git's
    /// command line, where a quote in it would be an argument of its own.</summary>
    public const int Searched = 500;

    private const char Field = '\x1f';
    private const char Record = '\x1e';

    /// <summary>The <see cref="Searched"/> most recent commits of the current branch, newest first; the failure git gave,
    /// named, when it gave one (no repository, no commit yet, git missing).</summary>
    public static async Task<(IReadOnlyList<CommitEntry> Commits, string? Failure)> ReadAsync(GitRunner git, CancellationToken ct)
    {
        const string command = "log";
        // log.showSignature off: set, gpg's lines are printed into each record, ahead of its hash — and 500 signature
        // checks can outlast git's budget. As a -c setting, an older git that has no --no-show-signature reads it too.
        var result = await git($"-c log.showSignature=false {command} -n {Searched} --format=%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1e", ct);
        if (GitProcess.FailureNote($"git {command}", result) is { } failure) return ([], failure);
        return (Parse(result.Output), null);
    }

    /// <summary>The commits of <c>git log --format=%H%x1f%h%x1f%s%x1f%an%x1f%aI%x1e</c>; a record that does not read is
    /// left out rather than shown wrong.</summary>
    internal static List<CommitEntry> Parse(string output)
    {
        var commits = new List<CommitEntry>();
        foreach (var record in output.Split(Record, StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.Trim('\r', '\n').Split(Field);
            if (f.Length < 5 || f[0].Length == 0) continue;
            if (!DateTimeOffset.TryParse(f[4], System.Globalization.CultureInfo.InvariantCulture,
                                         System.Globalization.DateTimeStyles.None, out var when)) continue;
            commits.Add(new CommitEntry(f[0], f[1], f[2], f[3], when));
        }
        return commits;
    }

    /// <summary>The commits a typed text finds — a hash that starts with it, a subject that holds it —, newest first, at
    /// most <see cref="Shown"/>; with nothing typed, the most recent.</summary>
    public static IReadOnlyList<CommitEntry> Filter(IReadOnlyList<CommitEntry> commits, string? typed)
    {
        var text = typed?.Trim() ?? string.Empty;
        return (text.Length == 0 ? commits
                : commits.Where(c => c.Hash.StartsWith(text, StringComparison.OrdinalIgnoreCase)
                                     || c.Subject.Contains(text, StringComparison.OrdinalIgnoreCase)))
               .Take(Shown).ToList();
    }

    /// <summary>
    /// What the typed text reviews when it is chosen itself — a branch, a tag, a range, an older commit: <c>/check
    /// &lt;text&gt;</c>, which judges it. None when nothing is typed; when the text holds a space, which no revision does
    /// (it is words of a subject, searched); for <c>init</c>, which writes an example check rather than review one —
    /// the rule of VS Code's picker (<c>typedRevision</c>) —; or for text <c>/check</c> would refuse as a revision
    /// (<see cref="CheckTarget.IsRevisionText"/>: <c>Cart:</c>, a quote), which then searches the subjects only.
    /// </summary>
    public static string? TypedRevision(string? typed)
    {
        var text = typed?.Trim() ?? string.Empty;
        return text.Length == 0 || text.Any(char.IsWhiteSpace) || text.Equals("init", StringComparison.OrdinalIgnoreCase)
               || !CheckTarget.IsRevisionText(text)
            ? null : text;
    }
}
