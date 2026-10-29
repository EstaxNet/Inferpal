using System.Collections.Concurrent;
using System.IO;
using Inferpal.Localization;

namespace Inferpal.Services.Governance;

/// <summary>
/// The files a workspace keeps out of the AI's reach — <c>.aiignore</c>, <c>.cursorignore</c> and the other tools'
/// ignore files, and Inferpal's own <c>.inferpalignore</c> — judged for any path by one reader, for everything that reads.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ An exclusion is always SAID — to the model (a refused call, a note above a walk's result) and to the person (a
/// notice, a line of a review). A repository may narrow what the tools read; it may never do it in silence: a model
/// that is not told concludes the file does not exist, and a review that is not told passes over what it never read.
/// </para>
/// <para>
/// The files are read at the ROOT of a declared workspace only, named by the setting
/// <see cref="Config.InferpalConfig.IgnoreFiles"/>; several files: a path one of them excludes is excluded. A path is
/// judged by every declared root holding it, any exclusion winning — one process serves several workspaces (ACP threads,
/// nested solutions, the folders of a VS Code window). Read again
/// when a file changes, looked at no more than every <see cref="RecheckEvery"/>.
/// </para>
/// </remarks>
internal static class ContentExclusions
{
    /// <summary>What excludes a path: the file, the line and the pattern.</summary>
    internal sealed record Exclusion(string File, int Line, string Pattern)
    {
        /// <summary>The exclusion as a note names it: the ignore file, the line and the pattern — or the backups.</summary>
        public string Named => Line == 0 ? "Inferpal's backups (unread while files are excluded)" : $"{File} line {Line} (`{Pattern}`)";

        /// <summary>For the model (English, as every message that corrects its call).</summary>
        public string ForModel(string shown) => Line == 0
            ? $"Error: {shown} is a backup copy kept by Inferpal: while this workspace excludes files from AI tools, its "
              + "backups are not read (one may hold an excluded file). restore_file restores a file from its backup."
            : $"Error: {shown} is excluded from AI tools by {File}, line {Line} (`{Pattern}`): its content is neither read "
              + "nor changed. Do not try another way to read it; tell the person if it is needed.";
    }

    /// <summary>The ignore files read when the setting is unset: the setting's own default — ours first, then the other
    /// tools'. One source, the configuration's initializer, which the documentation is checked against.</summary>
    internal static readonly string DefaultFiles = new Config.InferpalConfig().IgnoreFiles;

    /// <summary>The largest ignore file read; beyond, the file is skipped and said.</summary>
    internal const int MaxFileBytes = 256 * 1024;

    /// <summary>How often a root's files are looked at again for a change.</summary>
    internal static readonly TimeSpan RecheckEvery = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<string, RootRules> Roots = new(PathComparer.Default);

    /// <summary>Whether <paramref name="fileName"/> names an ignore file: one of the default names, or one a declared
    /// workspace's setting names — the setting is the list, never the defaults alone.</summary>
    internal static bool IsIgnoreFileName(string fileName) =>
        Split(DefaultFiles).Contains(fileName, StringComparer.OrdinalIgnoreCase)
        || Roots.Values.Any(r => r.Names().Contains(fileName, StringComparer.OrdinalIgnoreCase));

    private static string[] Split(string setting) =>
        setting.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Declares <paramref name="root"/> a workspace whose ignore files apply below it; <paramref name="files"/> is read at
    /// each recheck, so a settings change follows without a new declaration.
    /// </summary>
    internal static void Declare(string? root, Func<string?> files)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return; }
        // Declared again (a workspace set once more, a settings save): read again at the next question.
        Roots.AddOrUpdate(full, r => new RootRules(r, files), (_, existing) =>
        {
            existing.Files = files;
            existing.ReadAgain();
            return existing;
        });
    }

    /// <summary>What excludes <paramref name="path"/>; <c>null</c> when nothing does, or no declared root holds it.</summary>
    internal static Exclusion? Of(string? path, bool isFolder = false)
    {
        if (string.IsNullOrWhiteSpace(path) || Roots.IsEmpty) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }

        // ⚠ Inferpal's backups hold copies of workspace files — of excluded ones too (a command that changed one, a backup
        // older than the rule) — under a name that keeps only a hash of the original path: no rule can judge them, so
        // while any workspace excludes something they are not read. The history may sit above the root (the git root).
        // ⚠ The histories kept in the user's folder are never read, rules or not: they hold copies of OTHER repositories'
        // files, which a workspace opened on the home folder would otherwise read through them.
        if (FileHistoryService.InUserHistoryRoot(full)) return History;
        if (InHistory(full) && AnyRules()) return History;
        // ⚠ EVERY declared root that holds the path judges it, the closest first: a workspace nested in another (a solution
        // in a subfolder opened before the outer one, two ACP threads) has no ignore file of its own, and judged by it
        // alone a file the outer workspace excludes was read. A root is never undeclared: what it excludes stays out.
        foreach (var rules in Roots.Values.Where(r => Under(r.Root, full)).OrderByDescending(r => r.Root.Length))
            if (rules.Judge(full, isFolder) is { } why) return why;
        return null;
    }

    /// <summary>What keeps Inferpal's backups unread (<see cref="InHistory"/>).</summary>
    internal static readonly Exclusion History = new(".inferpal/history", 0, "*");

    /// <summary>In a history folder: the project's, or the one kept in the user's own folder.</summary>
    private static bool InHistory(string full)
    {
        var at = full.Replace('\\', '/').IndexOf("/.inferpal/history/", StringComparison.OrdinalIgnoreCase);
        return at >= 0 || FileHistoryService.InUserHistoryRoot(full);
    }

    /// <summary>The longest declared root holding <paramref name="full"/>, or <c>null</c>.</summary>
    private static RootRules? Owner(string full)
    {
        RootRules? owner = null;
        foreach (var (root, rules) in Roots)
            if (Under(root, full) && (owner is null || root.Length > owner.Root.Length)) owner = rules;
        return owner;
    }

    private static bool Under(string root, string full) =>
        full.StartsWith(root + Path.DirectorySeparatorChar, PathComparer.Comparison)
        || (Path.DirectorySeparatorChar != '/' && full.StartsWith(root + "/", PathComparer.Comparison));

    // ── the walk's tally: what a walk skipped during one tool call, said above its result ──

    private static readonly AsyncLocal<Tally?> _tally = new();

    /// <summary>What walks skipped during one call: the files, and the exclusions that kept them out.</summary>
    internal sealed class Tally
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Exclusion> _files = new(PathComparer.Default);

        internal void Add(string file, Exclusion why)
        {
            lock (_gate) _files.TryAdd(file, why);
        }

        /// <summary>How many files were skipped.</summary>
        internal int Count
        {
            get { lock (_gate) return _files.Count; }
        }

        /// <summary>The files skipped that <paramref name="keep"/> keeps — a walk counts every file, a reader its own kind.</summary>
        internal Tally Only(Func<string, bool> keep)
        {
            var only = new Tally();
            lock (_gate)
                foreach (var (file, why) in _files)
                    if (keep(file)) only.Add(file, why);
            return only;
        }

        /// <summary>The rules that skipped them, as a note names them.</summary>
        internal string Rules()
        {
            List<Exclusion> why;
            lock (_gate) why = [.. _files.Values.Distinct()];
            return string.Join(", ", why.Take(3).Select(w => w.Named))
                   + (why.Count > 3 ? $" and {why.Count - 3} more rule(s)" : string.Empty);
        }

        /// <summary>The note above the result; empty when nothing was skipped.</summary>
        internal string Note() =>
            Count == 0
                ? string.Empty
                : $"Note: {Count} file(s) excluded from AI tools were skipped, by {Rules()}. Their content was not read; "
                  + "they exist.";

        /// <summary>The same for the person (translated, naming the ignore files only); empty when nothing was skipped.</summary>
        internal string ForPerson()
        {
            List<string> files;
            lock (_gate) files = [.. _files.Values.Select(w => w.File).Distinct(StringComparer.Ordinal)];
            return files.Count == 0 ? string.Empty : Strings.ExcludedLeftOut(Count, string.Join(", ", files));
        }
    }

    /// <summary>
    /// The notice for the person when <paramref name="path"/> is excluded — named relative to <paramref name="root"/>,
    /// or to the workspace that excludes it when <c>null</c> — or <c>null</c> when it is not: what an attachment, a code
    /// action or a pinned file says in place of the content.
    /// </summary>
    internal static string? NoticeFor(string? path, string? root)
    {
        if (Of(path) is not { } why) return null;
        root ??= Owner(Path.GetFullPath(path!))?.Root;
        return why.Line == 0
            ? Strings.BackupNotSent(Shown(path!, root))
            : Strings.ExcludedFromAi(Shown(path!, root), why.File, why.Line, why.Pattern);
    }

    /// <summary><paramref name="path"/> as a message names it: relative to <paramref name="root"/>, with forward
    /// slashes, when under it; whole otherwise.</summary>
    internal static string Shown(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        try
        {
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)
                ? path
                : relative.Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return path; }
    }

    /// <summary>Starts counting what walks skip in this flow — one tool call. Synchronous, for the flow to keep it.</summary>
    internal static Tally BeginTally() => _tally.Value = new Tally();

    /// <summary>Runs <paramref name="walk"/> counting what it skips, the flow's own tally left as it was.</summary>
    internal static T Counting<T>(Func<T> walk, out Tally tally)
    {
        var previous = _tally.Value;
        tally = _tally.Value = new Tally();
        try { return walk(); }
        finally { _tally.Value = previous; }
    }

    /// <summary>
    /// Whether a walk skips <paramref name="file"/>: excluded, it is counted for the note of the call in this flow.
    /// </summary>
    internal static bool SkipInWalk(string file)
    {
        // A file that is itself a link is judged where it leads too (the walk never descends a linked folder).
        if ((Of(file) ?? (AnyRules() && IsLink(file) ? OfLinked(file) : null)) is not { } why) return false;
        _tally.Value?.Add(file, why);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of the ignore files of a declared root — and if so, that root reads its
    /// files again at the next question instead of waiting for <see cref="RecheckEvery"/>: whoever reacts to the change
    /// (the index re-reading the workspace) must see the new rules.
    /// </summary>
    internal static bool Changed(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Roots.IsEmpty) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        var folder = Path.GetDirectoryName(full);
        if (folder is null || !Roots.TryGetValue(Path.TrimEndingDirectorySeparator(folder), out var rules)) return false;
        if (!rules.Names().Contains(Path.GetFileName(full), StringComparer.OrdinalIgnoreCase)) return false;
        rules.ReadAgain();
        return true;
    }

    /// <summary>Whether any declared workspace excludes anything — what spares the readers that must ask git for a path.</summary>
    internal static bool AnyRules() => Roots.Values.Any(r => r.HasRules());

    /// <summary>The work tree a diff's paths are relative to (what <see cref="WithoutExcludedFiles"/> takes), asked of git
    /// only while some workspace excludes something; <c>null</c> otherwise, or when git cannot say.</summary>
    internal static async Task<string?> DiffWorkTreeAsync(GitRunner git, CancellationToken ct) =>
        AnyRules() && await git("rev-parse --show-toplevel", ct) is { ExitCode: 0 } top ? top.Output.Trim() : null;

    /// <summary>What excludes the file or folder <paramref name="full"/> leads to through a link on its way; <c>null</c> when
    /// nothing does, or no link is on the way.</summary>
    /// <remarks>⚠ Judged on its own spelling alone, <c>apps/web/secrets → ../../secrets</c> reads what <c>/secrets/</c>
    /// excludes.</remarks>
    internal static Exclusion? OfLinked(string full, bool isFolder = false)
    {
        if (Roots.IsEmpty || !AnyRules()) return null;
        var resolved = Tools.PathSanitizer.Resolved(full);
        if (resolved.Length == 0 || string.Equals(resolved, Path.TrimEndingDirectorySeparator(full), PathComparer.Comparison)) return null;
        List<(string Root, string Resolved)>? roots = null;
        return OfThroughLinks(full, ref roots, isFolder);
    }

    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// What excludes <paramref name="full"/> once the links are resolved on both sides, judged in the spelling of each
    /// declared root that holds it then. ⚠ git names a work tree with its links resolved (macOS's <c>/var</c> is
    /// <c>/private/var</c>, a junction, a linked home folder) while a root keeps the spelling the workspace was opened by:
    /// compared as written, a diff's path is under no root, and what the ignore files exclude reaches the model.
    /// </summary>
    /// <param name="full">A path that no root holds as written.</param>
    /// <param name="roots">The roots with their links resolved: computed on the first call, then reused.</param>
    private static Exclusion? OfThroughLinks(string full, ref List<(string Root, string Resolved)>? roots, bool isFolder = false)
    {
        // A root of the file system resolves to an empty string and holds everything: never one to re-spell into.
        roots ??= Roots.Keys.Select(r => (r, Tools.PathSanitizer.Resolved(r))).Where(r => r.Item2.Length > 0).ToList();
        var resolved = Tools.PathSanitizer.Resolved(full);
        foreach (var (root, resolvedRoot) in roots)
            if (Under(resolvedRoot, resolved) && Of(Path.Join(root, Path.GetRelativePath(resolvedRoot, resolved)), isFolder) is { } why)
                return why;
        return null;
    }

    /// <summary>
    /// <paramref name="diff"/> — git's unified diff, paths relative to <paramref name="workTree"/> — without the sections
    /// of the files the workspace's ignore files exclude, each dropped file counted in <paramref name="dropped"/>.
    /// </summary>
    /// <remarks>
    /// A section is dropped when ANY path it names is excluded (a rename from or to one): the lines of an excluded file
    /// are its content. What comes before the first section (a heading, a status) is kept.
    /// </remarks>
    internal static string WithoutExcludedFiles(string diff, string? workTree, Tally dropped)
    {
        if (string.IsNullOrEmpty(diff) || string.IsNullOrEmpty(workTree) || Roots.IsEmpty) return diff;
        var starts = new List<int>();
        for (var at = 0; at < diff.Length; at = diff.IndexOf('\n', at) is var nl and >= 0 ? nl + 1 : diff.Length)
            if (SectionHeader(diff, at) is not null) starts.Add(at);
        if (starts.Count == 0) return diff;

        var kept = new System.Text.StringBuilder(diff.Length);
        kept.Append(diff, 0, starts[0]);
        List<(string Root, string Resolved)>? resolvedRoots = null;
        for (var i = 0; i < starts.Count; i++)
        {
            var end     = i + 1 < starts.Count ? starts[i + 1] : diff.Length;
            var section = diff[starts[i]..end];
            Exclusion? why = null;
            string? excludedPath = null;
            foreach (var path in SectionPaths(section))
            {
                string full;
                try { full = Path.GetFullPath(Path.Combine(workTree, path)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if ((Of(full) ?? OfThroughLinks(full, ref resolvedRoots)) is { } found) { (why, excludedPath) = (found, full); break; }
            }
            if (why is not null) dropped.Add(excludedPath!, why);
            else kept.Append(section);
        }
        return kept.ToString();
    }

    /// <summary>The headers that open a file's section: two-sided, and the combined ones of a path in conflict.</summary>
    private static readonly string[] SectionHeaders = ["diff --git ", "diff --cc ", "diff --combined "];

    private static string? SectionHeader(string diff, int at) =>
        SectionHeaders.FirstOrDefault(h => string.CompareOrdinal(diff, at, h, 0, h.Length) == 0);

    /// <summary>The lines of a section's header that name a path.</summary>
    private static readonly string[] NameLines = ["--- ", "+++ ", "rename from ", "rename to ", "copy from ", "copy to "];

    /// <summary>
    /// Every path a diff section names — its header's, its <c>---</c>/<c>+++</c> sides, a rename's or a copy's — each with
    /// and without the side prefix git may write (<c>a/</c> <c>b/</c> by default, <c>i/</c> <c>w/</c> <c>c/</c> <c>o/</c>
    /// under <c>diff.mnemonicPrefix</c>, none under <c>--no-prefix</c>): a candidate that names no excluded file costs
    /// nothing, one missed sends a file's content.
    /// </summary>
    private static IEnumerable<string> SectionPaths(string section)
    {
        var lines = section.Split('\n');
        var first = lines[0].TrimEnd('\r');
        var header = first[SectionHeader(first, 0)!.Length..];
        if (!first.StartsWith("diff --git ", StringComparison.Ordinal))
            foreach (var c in Candidates(Unquoted(header))) yield return c;   // combined: the one path, as is
        // "a/X b/X": the two halves are the same path when nothing was renamed — the one split a space cannot fool.
        else if (header.Length % 2 == 1 && header[header.Length / 2] == ' ')
            foreach (var half in new[] { header[..(header.Length / 2)], header[(header.Length / 2 + 1)..] })
                foreach (var c in Candidates(half)) yield return c;
        foreach (var raw in lines.Skip(1))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal)) yield break;   // the hunks: content, no more names
            foreach (var prefix in NameLines)
            {
                if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = Unquoted(line[prefix.Length..].TrimEnd('\t'));
                if (name != "/dev/null")
                    foreach (var c in Candidates(name)) yield return c;
                break;
            }
        }
    }

    /// <summary>The path as written, and without a one-letter side prefix when it has one.</summary>
    private static IEnumerable<string> Candidates(string name)
    {
        if (name.Length > 2 && name[1] == '/' && "abiwco".Contains(name[0])) yield return name[2..];
        yield return name;
    }

    /// <summary>A path git quoted (<c>"a/x\"y"</c>) as the path; any other as is.</summary>
    private static string Unquoted(string name)
    {
        if (name.Length < 2 || name[0] != '"' || name[^1] != '"') return name;
        var sb = new System.Text.StringBuilder(name.Length);
        for (var i = 1; i < name.Length - 1; i++)
        {
            if (name[i] == '\\' && i + 1 < name.Length - 1)
            {
                var next = name[++i];
                sb.Append(next switch { 't' => '\t', 'n' => '\n', _ => next });
            }
            else sb.Append(name[i]);
        }
        return sb.ToString();
    }

    /// <summary>Forgets every declared root — for tests that declare their own.</summary>
    internal static void ForgetForTests(string root)
    {
        try { Roots.TryRemove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), out _); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    /// <summary>The rules of one root, read again when one of its files changes.</summary>
    private sealed class RootRules(string root, Func<string?> files)
    {
        internal string Root { get; } = root;
        internal Func<string?> Files { get; set; } = files;

        private readonly object _gate = new();
        private string _stamp = string.Empty;
        private DateTime _checkedUtc = DateTime.MinValue;

        /// <summary>The rules read, and the verdicts on folders computed from THEM: swapped together, a verdict computed
        /// from the old rules while the new ones are read can never enter the new cache.</summary>
        private sealed record Snapshot(IReadOnlyList<(string Name, GitIgnoreRules Rules)> Read,
                                       ConcurrentDictionary<string, Exclusion?> Folders);

        private Snapshot _snapshot = new([], new(PathComparer.Default));

        internal bool HasRules() => Current().Read.Any(r => r.Rules.Rules.Count > 0);

        /// <summary>The ignore files' names, as the setting gives them now.</summary>
        internal IReadOnlyList<string> Names() => Names(Files());

        /// <summary>The next question reads the files again, whenever they were last looked at.</summary>
        internal void ReadAgain()
        {
            lock (_gate) _checkedUtc = DateTime.MinValue;
        }

        internal Exclusion? Judge(string full, bool isFolder)
        {
            var (read, folders) = Current();
            if (read.Count == 0) return null;
            var relative = Path.GetRelativePath(Root, full).Replace('\\', '/');
            // ⚠ What is written FOR the agent is never kept from it: Inferpal's own folder, the repository's instruction
            // files and skills — a `*.md` exclusion would otherwise silence AGENTS.md, the file meant to steer the agent.
            var segments = relative.Split('/');
            if (segments[0] == ".inferpal"
                || Prompting.RepoInstructionFormats.AnyEndsAt(segments) || Prompting.RepoSkills.IsInASkill(segments))
                return null;
            // The folders on the way first, remembered: a walk asks about thousands of files in the same few folders.
            var slash = relative.IndexOf('/');
            while (slash > 0)
            {
                var folder = relative[..slash];
                if (folders.GetOrAdd(folder, f => Decide(read, f, isFolder: true)) is { } byFolder) return byFolder;
                slash = relative.IndexOf('/', slash + 1);
            }
            return Decide(read, relative, isFolder);
        }

        private static Exclusion? Decide(IReadOnlyList<(string Name, GitIgnoreRules Rules)> read, string relative, bool isFolder)
        {
            foreach (var (name, rules) in read)
                if (rules.Last(relative, isFolder) is { Excluded: true } verdict)
                    return new Exclusion(name, verdict.Rule.Line, verdict.Rule.Pattern);
            return null;
        }

        private Snapshot Current()
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _checkedUtc < RecheckEvery) return _snapshot;
                _checkedUtc = DateTime.UtcNow;
                var names = Names(Files());
                var stamp = string.Join("|", names.Select(n => n + ":" + Stamp(Path.Combine(Root, n))));
                if (stamp == _stamp) return _snapshot;
                _stamp = stamp;
                IReadOnlyList<(string, GitIgnoreRules)> read =
                    [.. names.Select(n => (n, Read(Path.Combine(Root, n), n))).Where(r => r.Item2 is not null)
                             .Select(r => (r.n, r.Item2!))];
                return _snapshot = new Snapshot(read, new(PathComparer.Default));
            }
        }

        private static IReadOnlyList<string> Names(string? setting) =>
            [.. (setting ?? DefaultFiles).Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                          .Where(n => n.Length > 0 && n.IndexOfAny(['/', '\\']) < 0)
                                          .Distinct(StringComparer.OrdinalIgnoreCase)];

        private static string Stamp(string file)
        {
            try
            {
                var info = new FileInfo(file);
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "?"; }
        }

        private static GitIgnoreRules? Read(string file, string name)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) return null;
                if (info.Length > MaxFileBytes)
                {
                    Diagnostics.RecordOnce("ContentExclusions",
                        $"{name} is larger than {MaxFileBytes / 1024} KB and was not read: its exclusions do not apply.",
                        file + "|big");
                    return null;
                }
                var text = Tools.TextFileEncoding.ReadText(file);
                Diagnostics.Forget("ContentExclusions", file + "|unreadable");
                return GitIgnoreRules.Parse(name, text, ignoreCase: PathComparer.Comparison == StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diagnostics.RecordOnce("ContentExclusions",
                    $"{name} could not be read ({ex.GetType().Name}): its exclusions do not apply until it can be.",
                    file + "|unreadable");
                return null;
            }
        }
    }
}
