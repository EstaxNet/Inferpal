using System.Text.RegularExpressions;
using Inferpal.Localization;

namespace Inferpal.Services.Commands;

/// <summary>
/// What <c>/check &lt;revision&gt;</c> reviews instead of the working tree: a range, a branch or one commit, as the
/// diff git prints for it and the line that names it above the findings.
/// </summary>
/// <param name="DiffArgs">The arguments of the <c>git diff</c> that prints the change: two object names, never a name
/// the person wrote.</param>
/// <param name="Shown">The change as the person reads it in the answer (<c>main...HEAD</c>, a short hash).</param>
/// <param name="Label">The lines above the findings that say what was reviewed.</param>
/// <param name="IsCommit">One commit, not a range: a diff cut at the cap cannot be reviewed in smaller pieces.</param>
/// <param name="Commits">A range's commits as <c>git log</c> reads them, by object names resolved here (<c>a..b</c>) —
/// never the name the person typed, which a fetch could move between two calls; <c>null</c> for one commit.</param>
internal sealed record CheckTarget(string DiffArgs, string Shown, string Label, bool IsCommit, string? Commits = null)
{
    /// <summary>What a range is called by the command that reads it — <c>/check</c> reviews it, <c>/pr</c> describes it.</summary>
    /// <param name="Label">The line above the answer: the range as shown, and its commit count.</param>
    /// <param name="Empty">A range that holds no commit.</param>
    /// <param name="NoBase">Two histories with no commit in common.</param>
    internal sealed record RangeWords(Func<string, int, string> Label, Func<string, string> Empty, Func<string, string> NoBase);

    private static readonly RangeWords CheckWords =
        new(Strings.CheckReviewedRange, Strings.CheckRangeEmpty, Strings.CheckNoCommonBase);

    /// <summary>A revision as git writes one: never a leading <c>-</c> (an option), never a space or a quote (a second
    /// argument). The text is the person's and goes into a git command line: without this, <c>--output=file</c> makes
    /// <c>git diff</c> write a file.</summary>
    /// <remarks>⚠ This is what keeps an option out of every command below, not <c>--end-of-options</c>: git before
    /// 2.24 reads that flag as a revision, and no revision would ever resolve.</remarks>
    private static readonly Regex Revision = new(@"^(?!-)[A-Za-z0-9._/~^@{}+-]{1,200}$",
        RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex ObjectName = new("^[0-9a-f]{40,64}$", RegexOptions.CultureInvariant, RegexBudget.Default);

    private static readonly Regex HashPrefix = new("^[0-9a-fA-F]{4,39}$", RegexOptions.CultureInvariant, RegexBudget.Default);

    /// <summary>Whether <paramref name="text"/> can name a revision or a range at all — read before any git call.</summary>
    public static bool IsRevisionText(string text) =>
        Sides(text) is { } sides ? sides.All(s => s.Length == 0 || Revision.IsMatch(s)) : Revision.IsMatch(text);

    /// <summary>The two sides of <c>A..B</c> or <c>A...B</c>; <c>null</c> for a single revision.</summary>
    private static string[]? Sides(string text)
    {
        var at = text.IndexOf("..", StringComparison.Ordinal);
        if (at < 0) return null;
        var width = text.AsSpan(at).StartsWith("...") ? 3 : 2;
        string[] sides = [text[..at], text[(at + width)..]];
        // A third dot pair is not a range git reads ("a..b..c").
        return sides.Any(s => s.Contains("..", StringComparison.Ordinal)) ? ["-", "-"] : sides;
    }

    /// <summary>
    /// Reads <paramref name="text"/> as a git revision and says what reviewing it means, or why there is nothing to
    /// review; <c>(null, null)</c> when it names no revision (the caller's refusal names both meanings).
    /// </summary>
    /// <remarks>
    /// <para>⚠ <c>A..B</c> is read as <c>git log</c> reads it — the commits B has and A has not, what a pull request
    /// shows — so its diff starts at the common base: <c>git diff A..B</c> compares the two tips and shows, reversed,
    /// everything A received since the branches parted, a review of code nobody wrote in B.</para>
    /// <para>⚠ Every git call is read through <see cref="GitProcess.FailureNote"/>: a git that refused or was stopped at
    /// its budget is not "no such revision". And what is reviewed is a diff between two object names resolved here — git
    /// warns on stderr, which the runner appends to the output, when a <c>...</c> diff meets several common bases.</para>
    /// <para>⚠ A name two refs share (a branch and a tag), or a hash prefix several objects share, is refused naming
    /// them: git would pick one silently, and the review would be about another change than the one asked for.</para>
    /// </remarks>
    /// <param name="words">What a range is called; <c>null</c>: <c>/check</c>'s.</param>
    public static async Task<(CheckTarget? Target, string? Nothing)> ResolveAsync(string text, GitRunner git, CancellationToken ct,
        RangeWords? words = null)
    {
        words ??= CheckWords;
        if (!IsRevisionText(text)) return (null, null);
        if (Sides(text) is { } sides)
        {
            var from = sides[0].Length == 0 ? "HEAD" : sides[0];
            var to   = sides[1].Length == 0 ? "HEAD" : sides[1];
            var (fromSha, fromNothing) = await CommitOrWhyAsync(from, git, ct);
            if (fromSha is null) return (null, fromNothing);
            var (toSha, toNothing) = await CommitOrWhyAsync(to, git, ct);
            if (toSha is null) return (null, toNothing);
            var shown = $"{from}...{to}";
            var range = await BetweenAsync(fromSha, toSha, shown, n => words.Label(shown, n), words.NoBase, git, ct);
            return range.Empty ? (null, words.Empty(shown)) : (range.Target, range.Nothing);
        }

        var (sha, nothing) = await CommitOrWhyAsync(text, git, ct);
        if (sha is null) return (null, nothing);

        // A branch is what it adds to HEAD; HEAD itself, a hash, a tag is the commit it names.
        var (fullName, nameFailed) = await FullNameAsync(text, git, ct);
        if (nameFailed is not null) return (null, nameFailed);
        if (IsBranch(text, fullName))
        {
            var branch = await BetweenAsync("HEAD", sha, $"HEAD...{text}", n => Strings.CheckReviewedBranch(text, n),
                                            words.NoBase, git, ct);
            return branch.Empty
                ? (null, Strings.CheckBranchAddsNothing(text, await DefaultBaseAsync(fullName!, git, ct) ?? "<base>"))
                : (branch.Target, branch.Nothing);
        }

        // ⚠ The parents come from the commit object itself, never from `log`: in a shallow clone `log` shows the commit
        // at the edge without the parent it has, and `log.showSignature` puts a signature check before the format line.
        var shortSha = sha[..8];
        var read = $"cat-file commit {sha}";
        var readRun = await git(read, ct);
        if (GitProcess.FailureNote(read, readRun) is { } readFailed) return (null, readFailed);
        var (parents, subject) = ReadCommit(readRun.Output);

        string before;
        if (parents.Count > 0)
        {
            // A merge is reviewed against its first parent: what it brought to its branch.
            var (parent, parentNothing) = await CommitOfAsync(parents[0], git, ct);
            if (parentNothing is not null) return (null, parentNothing);
            // At the edge of a shallow clone the commit names a parent that was not fetched: compared with the empty
            // tree instead, the whole snapshot would be reviewed under the name of one commit.
            if (parent is null) return (null, Strings.CheckShallowBoundary(shortSha));
            before = parent;
        }
        else
        {
            // A root commit has no parent: it brought everything, compared with the empty tree.
            const string format = "rev-parse --show-object-format";
            var formatRun = await git(format, ct);
            if (GitProcess.FailureNote(format, formatRun) is { } formatFailed) return (null, formatFailed);
            before = GitProcess.EmptyTree(formatRun.Output);
        }
        return (new CheckTarget($"diff {before} {sha} --", shortSha, Strings.CheckReviewedCommit(shortSha, subject), true), null);
    }

    /// <summary>
    /// When a check's name is also a revision: the spelling that reviews the revision instead (its full ref name,
    /// <c>refs/heads/x</c> or <c>refs/tags/x</c>, which no check's name matches; else its object name); <c>null</c>
    /// when the name is no revision, or git could not tell.
    /// </summary>
    public static async Task<string?> RevisionSpellingAsync(string text, GitRunner git, CancellationToken ct)
    {
        if (!IsRevisionText(text) || Sides(text) is not null) return null;
        var (sha, failed) = await CommitOfAsync(text, git, ct);
        if (sha is null || failed is not null) return null;
        var (fullName, nameFailed) = await FullNameAsync(text, git, ct);
        return nameFailed is null && fullName is not null ? fullName : sha;
    }

    /// <summary>The parents and the subject of a raw commit object (<c>cat-file commit</c>): headers up to the first
    /// empty line — a signature header's own lines start with a space — then the message.</summary>
    private static (List<string> Parents, string Subject) ReadCommit(string raw)
    {
        var parents = new List<string>();
        var lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var at = 0;
        for (; at < lines.Count && lines[at].Length > 0; at++)
            if (lines[at].StartsWith("parent ", StringComparison.Ordinal)) parents.Add(lines[at]["parent ".Length..].Trim());
        var subject = lines.Skip(at).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return (parents, subject);
    }

    /// <summary>
    /// The change from where <paramref name="from"/> and <paramref name="to"/> parted to <paramref name="to"/>, with
    /// the commits it holds; <c>Empty</c> when it holds none.
    /// </summary>
    private static async Task<(CheckTarget? Target, string? Nothing, bool Empty)> BetweenAsync(
        string from, string to, string shown, Func<int, string> label, Func<string, string> noBase, GitRunner git,
        CancellationToken ct)
    {
        var (count, countFailed) = await CountAsync($"{from}..{to}", git, ct);
        if (countFailed is not null) return (null, countFailed, false);
        if (count == 0) return (null, null, true);

        var command = $"merge-base --all {from} {to}";
        var run = await git(command, ct);
        // 1 and nothing: two histories without a commit in common, so no base to start the change from.
        if (run.ExitCode == 1 && string.IsNullOrWhiteSpace(run.Output)) return (null, noBase(shown), false);
        if (GitProcess.FailureNote(command, run) is { } failed) return (null, failed, false);
        var bases = run.Output.Split('\n').Select(l => l.Trim()).Where(l => ObjectName.IsMatch(l)).ToList();
        if (bases.Count == 0)
            return (null, Strings.GitCommandFailed(command, GitProcess.Detail(run.Output, run.ExitCode)), false);

        // ⚠ After a criss-cross merge there are several bases, and the diff from the first shows what the other side
        // already has: said, never left to a warning only git's stderr carried.
        var text = label(count);
        if (bases.Count > 1) text += "\n\n" + Strings.CheckSeveralBases(bases.Count, bases[0][..8]);
        return (new CheckTarget($"diff {bases[0]} {to} --", shown, text, false, $"{from}..{to}"), null, false);
    }

    /// <summary>The commit <paramref name="revision"/> names, or why it names none: a git that could not answer, a
    /// name two refs share, a hash prefix several objects share; <c>(null, null)</c> when it simply names nothing.</summary>
    internal static async Task<(string? Sha, string? Nothing)> CommitOrWhyAsync(string revision, GitRunner git, CancellationToken ct)
    {
        var (sha, failed) = await CommitOfAsync(revision, git, ct);
        if (failed is not null) return (null, failed);
        if (sha is null) return (null, await AmbiguousHashAsync(revision, git, ct));
        return await AmbiguousRefAsync(revision, git, ct) is { } ambiguous ? (null, ambiguous) : (sha, null);
    }

    /// <summary>The commit <paramref name="revision"/> names; <c>(null, null)</c> when it names none, the failure when git
    /// could not answer.</summary>
    private static async Task<(string? Sha, string? Failed)> CommitOfAsync(string revision, GitRunner git, CancellationToken ct)
    {
        var command = $"rev-parse --verify --quiet {revision}^{{commit}}";
        var run = await git(command, ct);
        // --verify --quiet answers 1 and nothing for a name that resolves to no commit: the one exit that is an answer.
        if (run.ExitCode == 1 && string.IsNullOrWhiteSpace(run.Output)) return (null, null);
        if (GitProcess.FailureNote(command, run) is { } failed) return (null, failed);
        var sha = GitProcess.FirstLine(run.Output);
        return ObjectName.IsMatch(sha)
            ? (sha, null)
            : (null, Strings.GitCommandFailed(command, GitProcess.Detail(run.Output, run.ExitCode)));
    }

    /// <summary>When <paramref name="name"/> is a plain name more than one ref answers to (git's own lookup order:
    /// <c>refs/</c>, tags, branches, remotes), the refusal that names them; <c>null</c> otherwise.</summary>
    private static async Task<string?> AmbiguousRefAsync(string name, GitRunner git, CancellationToken ct)
    {
        if (name == "@" || name.AsSpan().IndexOfAny("~^{}") >= 0 || ObjectName.IsMatch(name)) return null;
        string[] candidates = [$"refs/{name}", $"refs/tags/{name}", $"refs/heads/{name}", $"refs/remotes/{name}",
                               $"refs/remotes/{name}/HEAD"];
        if (name.StartsWith("refs/", StringComparison.Ordinal)) candidates = [name, .. candidates];
        var command = "for-each-ref --format=%(refname) " + string.Join(" ", candidates);
        var run = await git(command, ct);
        if (GitProcess.FailureNote(command, run) is { } failed) return failed;
        // A pattern also matches the refs below it (refs/heads/x/y): only the exact names count.
        var found = run.Output.Split('\n').Select(l => l.Trim()).Where(l => candidates.Contains(l)).Distinct().ToList();
        return found.Count > 1 ? Strings.CheckAmbiguousRef(name, string.Join(", ", found.Select(f => $"`{f}`"))) : null;
    }

    /// <summary>When <paramref name="text"/> is the beginning of several object names, the refusal that says so;
    /// <c>null</c> otherwise (git then resolves no commit by that name).</summary>
    private static async Task<string?> AmbiguousHashAsync(string text, GitRunner git, CancellationToken ct)
    {
        if (!HashPrefix.IsMatch(text)) return null;
        var command = $"rev-parse --disambiguate={text}";
        var run = await git(command, ct);
        if (GitProcess.FailureNote(command, run) is { } failed) return failed;
        var objects = run.Output.Split('\n').Count(l => ObjectName.IsMatch(l.Trim()));
        return objects > 1 ? Strings.CheckAmbiguousHash(text, objects) : null;
    }

    /// <summary>The full ref name git resolves <paramref name="revision"/> to (<c>refs/heads/x</c>,
    /// <c>refs/remotes/origin/HEAD</c> for <c>origin</c>, <c>refs/tags/x</c>); <c>null</c> for a hash or an expression
    /// (<c>HEAD~2</c>).</summary>
    private static async Task<(string? FullName, string? Failed)> FullNameAsync(string revision, GitRunner git, CancellationToken ct)
    {
        var command = $"rev-parse --symbolic-full-name {revision}";
        var run = await git(command, ct);
        if (GitProcess.FailureNote(command, run) is { } failed) return (null, failed);
        var name = run.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("refs/", StringComparison.Ordinal));
        return (name, null);
    }

    /// <summary>Whether the revision is a branch, local or remote. <c>HEAD</c> and <c>@</c> resolve to the branch they
    /// are on, and what that branch brings to itself is always nothing: they name the commit — <c>head</c> too, which a
    /// file system that folds case resolves as <c>HEAD</c>.</summary>
    private static bool IsBranch(string text, string? fullName) =>
        !text.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && text != "@"
        && fullName is not null
        && (fullName.StartsWith("refs/heads/", StringComparison.Ordinal)
            || fullName.StartsWith("refs/remotes/", StringComparison.Ordinal));

    /// <summary>The commits <c>git rev-list --count</c> finds in <paramref name="range"/>, or why it could not count.</summary>
    private static async Task<(int Count, string? Failed)> CountAsync(string range, GitRunner git, CancellationToken ct)
    {
        var command = $"rev-list --count {range}";
        var run = await git(command, ct);
        if (GitProcess.FailureNote(command, run) is { } failed) return (-1, failed);
        return int.TryParse(GitProcess.FirstLine(run.Output), out var n)
            ? (n, null)
            : (-1, Strings.GitCommandFailed(command, GitProcess.Detail(run.Output, run.ExitCode)));
    }

    /// <summary>
    /// The branch <paramref name="branchFullName"/> is compared with when none is named: the remote's default branch,
    /// else a local <c>main</c> or <c>master</c> that is not this one; <c>null</c> when none is found — a git that could
    /// not answer finds none too, the caller then naming the base to type.
    /// </summary>
    /// <param name="branchFullName">The branch itself (<c>refs/heads/x</c>), never its own base; <c>null</c> when HEAD
    /// is on no branch.</param>
    internal static async Task<string?> DefaultBaseAsync(string? branchFullName, GitRunner git, CancellationToken ct)
    {
        var remote = await git("symbolic-ref --quiet --short refs/remotes/origin/HEAD", ct);
        // ⚠ Not the branch's own copy on the remote (on main, origin/main is no base: it is main itself), and not a
        // default branch pruned since (the symbolic ref outlives the branch it points to).
        if (remote.ExitCode == 0 && GitProcess.FirstLine(remote.Output) is { Length: > 0 } remoteHead
            && "refs/remotes/" + remoteHead != branchFullName && Revision.IsMatch(remoteHead)
            && "refs/heads/" + remoteHead[(remoteHead.IndexOf('/') + 1)..] != branchFullName
            && (await git($"rev-parse --verify --quiet refs/remotes/{remoteHead}", ct)).ExitCode == 0)
            return remoteHead;
        foreach (var name in (string[])["main", "master"])
        {
            if ("refs/heads/" + name == branchFullName) continue;
            if ((await git($"rev-parse --verify --quiet refs/heads/{name}", ct)).ExitCode == 0) return name;
        }
        return null;
    }
}
