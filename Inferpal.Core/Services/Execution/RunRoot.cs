namespace Inferpal.Services.Execution;

/// <summary>
/// The root a run works in when it is not the workspace's: a copy of the repository (pari 106), entered for the run's
/// whole flow and left when it ends. The tools' root and the approvals' root read it first, so the run reads, writes,
/// builds and asks inside the copy, while the person keeps working in their own folder.
/// </summary>
/// <remarks>
/// ⚠ Flows with the run (<see cref="AsyncLocal{T}"/>): another question asked meanwhile, in another flow, keeps the
/// workspace's root. ⚠ What reaches the person's editor, debugger or index is not offered inside a copy
/// (<c>ITool.ActsOnTheEditorOrIndex</c>): those act on the person's files, never the copy's. ⚠ The copy is a git worktree:
/// it shares the person's git store, so a git command that changes it outside the copy's branch is refused
/// (<see cref="SharedRepositoryChange"/>), and the hooks of their repository run in the copy (their own scripts).
/// </remarks>
internal static class RunRoot
{
    /// <param name="Root">The copy of the folder the person opened: the tools' root.</param>
    /// <param name="Origin">The folder the person opened.</param>
    /// <param name="CopyTop">The copy's top: the whole repository, wherever the person's folder sits in it.</param>
    /// <param name="OriginTop">The person's repository: the top of their git work tree.</param>
    /// <param name="Scope">What this entry's agreements are filed under: one copy folder serves later runs too.</param>
    private sealed record State(string Root, string Origin, string CopyTop, string OriginTop, string Scope);

    private static readonly AsyncLocal<State?> _current = new();

    /// <summary>The folder the current flow's run works in, inside its copy; <c>null</c> for an ordinary run.</summary>
    internal static string? Copy => _current.Value?.Root;

    /// <summary>The person's own folder the run's root copies; <c>null</c> for an ordinary run.</summary>
    internal static string? Origin => _current.Value?.Origin;

    /// <summary>What an "Always" given during this run is filed under — this run's alone: a copy folder freed by a discard
    /// is reused by the next copy, which must not find the agreements of the one before.</summary>
    internal static string? GrantsScope => _current.Value?.Scope;

    /// <summary>
    /// Enters <paramref name="copy"/> for the current flow, at the place of <paramref name="workspace"/> in it (a workspace
    /// opened on a subfolder of its repository works in the same subfolder of the copy); disposing gives the flow back its
    /// previous root.
    /// </summary>
    /// <remarks>⚠ Entered BEFORE the run's system prompt is built: the prompt states the root the tools work in.</remarks>
    internal static IDisposable Enter(WorkCopy copy, string workspace) =>
        Enter(copy.Folder, copy.Repository, workspace);

    /// <summary>The same, from the copy's folder and the repository it copies.</summary>
    internal static IDisposable Enter(string copyTop, string repository, string workspace)
    {
        var top = Path.GetFullPath(copyTop);
        var originTop = Path.GetFullPath(repository);
        var origin = Path.GetFullPath(workspace);
        var inside = Path.GetRelativePath(originTop, origin);
        if (!Holds(originTop, origin))
            throw new ArgumentException($"{workspace} is not in the repository {repository}.", nameof(workspace));
        var root = inside == "." ? top : Path.GetFullPath(Path.Join(top, inside));

        var before = _current.Value;
        // Entered again in the same copy (a run inside a run): the same agreements, and its index kept.
        var again = before is not null && string.Equals(before.CopyTop, top, PathComparer.Comparison);
        // The copy's C# index is built from its own files: one left from an earlier copy in the same folder is old code.
        if (!again) Lsp.CSharpSemanticIndex.Forget(top);
        _current.Value = new State(root, origin, top, originTop, again ? before!.Scope : $"{top}#{Guid.NewGuid():N}");
        return new Leave(before, top);
    }

    /// <summary>
    /// <paramref name="path"/> as the person's own file when it lies in the current copy, else <c>null</c>: a file of the
    /// copy is judged by what the person's repository says of it (its ignore files, uncommitted ones included, never in
    /// the copy).
    /// </summary>
    internal static string? InOrigin(string path) =>
        _current.Value is { } s && Holds(s.CopyTop, path)
            ? Path.Join(s.OriginTop, Path.GetRelativePath(s.CopyTop, path))
            : null;

    /// <summary>Whether <paramref name="command"/>, run in the current copy, names the person's repository (see the
    /// overload); <c>false</c> for an ordinary run.</summary>
    internal static bool ReachesOrigin(string command) =>
        _current.Value is { } s && ReachesOrigin(command, s.CopyTop, s.OriginTop);

    /// <summary>
    /// Whether <paramref name="command"/> names a path in <paramref name="origin"/> that is not in <paramref name="copy"/>:
    /// written to it, or climbing back to it from the copy (<c>..</c>). Judged on the path each spelling reaches, at a
    /// folder boundary — <c>C:\dev\shopfront</c> is not <c>C:\dev\shop</c>, and a copy kept under the person's folder (their
    /// home opened as the workspace) is not their folder.
    /// </summary>
    /// <remarks>⚠ A guard against a slip, not a boundary: a relative path can still climb out, and the card shows the
    /// command.</remarks>
    internal static bool ReachesOrigin(string command, string copy, string origin)
    {
        foreach (var root in new[] { origin, copy })
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);
            foreach (var spelling in new[] { trimmed, trimmed.Replace('\\', '/'), trimmed.Replace('/', '\\') }.Distinct())
            {
                for (var at = command.IndexOf(spelling, PathComparer.Comparison); at >= 0;
                     at = command.IndexOf(spelling, at + 1, PathComparer.Comparison))
                {
                    if (PathAt(command, at) is not { } named) continue;
                    if (Holds(origin, named) && !Holds(copy, named)) return true;
                }
            }
        }
        return false;
    }

    /// <summary>The git subcommands that change the store a worktree shares with the person's repository: its branches,
    /// tags, stash, settings, remotes and objects — beyond the copy's own branch and files.</summary>
    private static readonly HashSet<string> SharedGitChanges = new(StringComparer.OrdinalIgnoreCase)
    {
        "stash", "tag", "config", "gc", "prune", "repack", "maintenance", "remote", "push", "fetch", "pull", "update-ref",
        "symbolic-ref", "reflog", "worktree", "notes", "replace", "filter-branch", "filter-repo", "submodule", "lfs",
    };

    /// <summary>The <c>git branch</c> options that only list.</summary>
    private static readonly HashSet<string> BranchListing = new(StringComparer.Ordinal)
    {
        "-a", "--all", "-r", "--remotes", "-l", "--list", "-v", "-vv", "--verbose", "--show-current", "--contains",
        "--no-contains", "--merged", "--no-merged", "--sort", "--format", "--color", "--no-color", "--column", "--no-column",
    };

    /// <summary>
    /// The git command in <paramref name="command"/> that, run in a copy, would change the person's repository — their
    /// branches, tags, stash, settings — through the store the worktree shares; <c>null</c> when none does or for an
    /// ordinary run. Every reading of the line is judged (steps and pieces): one that hides a step must not hide a refusal.
    /// </summary>
    /// <remarks>⚠ A guard against a slip, not a boundary: an alias, a script or another program can still write there.</remarks>
    internal static string? SharedRepositoryChange(string command)
    {
        if (_current.Value is null) return null;
        IEnumerable<string> readings;
        try { readings = CommandLine.Steps(command).Concat(CommandLine.Pieces(command)); }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { readings = CommandLine.Pieces(command); }
        foreach (var reading in readings)
        {
            var words = (CommandLine.WithoutAssignments(reading) ?? reading)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim('"', '\'', '(', ')', '{', '}'))
                .ToList();
            if (words.Count == 0) continue;
            var program = Path.GetFileNameWithoutExtension(words[0]);
            if (!string.Equals(program, "git", StringComparison.OrdinalIgnoreCase)) continue;
            // The subcommand: past git's own options (-C <dir>, -c <name=value>, --git-dir=…).
            var i = 1;
            while (i < words.Count && words[i].StartsWith('-'))
                i += words[i] is "-C" or "-c" ? 2 : 1;
            if (i >= words.Count) continue;
            var sub = words[i];
            if (SharedGitChanges.Contains(sub)) return $"git {sub}";
            // `git branch` lists; with a name or a change option it writes a ref of the shared store.
            if (string.Equals(sub, "branch", StringComparison.OrdinalIgnoreCase)
                && words.Skip(i + 1).Any(w => !BranchListing.Contains(w.Split('=')[0])))
                return "git branch";
        }
        return null;
    }

    /// <summary>The refusal a tool returns for a command <see cref="ReachesOrigin(string)"/> or
    /// <see cref="SharedRepositoryChange"/> refuses; <c>null</c> when the command may run.</summary>
    internal static string? CommandRefusal(string command)
    {
        if (_current.Value is not { } s) return null;
        if (ReachesOrigin(command))
            return $"Error: this run works in a copy of the repository ({s.Root}); a command that names the user's own "
                 + $"folder ({s.OriginTop}) is refused — nothing ran. Use paths inside the copy.";
        if (SharedRepositoryChange(command) is { } git)
            return $"Error: this run works in a copy of the repository, which shares its git store with the user's own "
                 + $"folder: `{git}` would change their repository (branches, tags, stash, settings) — nothing ran. Commit on "
                 + "the copy's own branch instead.";
        return null;
    }

    /// <summary>The full path written at <paramref name="at"/>: up to the closing quote when it opens one, else up to the
    /// next space or shell operator; <c>null</c> when it is no path.</summary>
    private static string? PathAt(string command, int at)
    {
        var quote = at > 0 && command[at - 1] is '"' or '\'' ? command[at - 1] : (char?)null;
        var end = at;
        while (end < command.Length && (quote is { } q ? command[end] != q : !char.IsWhiteSpace(command[end]) && "\"'`;|&<>,()".IndexOf(command[end]) < 0))
            end++;
        try { return Path.GetFullPath(command[at..end]); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> or below it, at a folder boundary — a drive or
    /// file-system root (<c>D:\</c>, <c>/</c>) included.</summary>
    internal static bool Holds(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var p = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(r, p, PathComparer.Comparison)) return true;
        if (!p.StartsWith(r, PathComparer.Comparison) || p.Length <= r.Length) return false;
        // A root keeps its separator (C:\, /): what follows it is already a name.
        return r[^1] is '\\' or '/' || p[r.Length] is '\\' or '/';
    }

    private sealed class Leave(State? before, string copyTop) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            _current.Value = before;
            // Left for good, not back to the same copy: its index goes with it.
            if (before is null || !string.Equals(before.CopyTop, copyTop, PathComparer.Comparison))
                Lsp.CSharpSemanticIndex.Forget(copyTop);
        }
    }
}
