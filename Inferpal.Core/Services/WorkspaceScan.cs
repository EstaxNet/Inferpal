using System.IO.Enumeration;

namespace Inferpal.Services;

/// <summary>
/// The one answer to "should a workspace walk descend into this directory?".
/// </summary>
/// <remarks>
/// <para>
/// <b>There were seven of these, and no two agreed.</b> The review of 2026-08-07 found a private
/// <c>IsExcluded</c> in <c>ProjectMapService</c>, <c>NexusIntelligenceTool</c>,
/// <c>AnalyzeImpactTool</c>, <c>TraceDependencyTool</c>, <c>RenameSymbolTool</c>,
/// <c>WorkspaceSymbolScanner</c> and <c>MentionController</c>, each with its own list. The
/// divergences were not academic:
/// </para>
/// <list type="bullet">
///   <item><b><c>rename_symbol</c> — the one that writes</b> — compared with
///         <c>StringComparison.Ordinal</c>, so a path containing <c>\Obj\</c> or
///         <c>\Node_Modules\</c> was not excluded at all on a case-insensitive filesystem; and it
///         did not know about <c>.inferpal</c>, so it could rewrite symbols inside the undo
///         snapshots Inferpal keeps of the user's own files.</item>
///   <item>Only two of the seven excluded <c>.inferpal</c>. The others walked
///         <c>.inferpal/history/</c>, which holds COPIES of source files with the same extensions —
///         a project map and an impact analysis built partly on stale duplicates.</item>
///   <item><c>AnalyzeImpactTool</c> excluded <c>\.git\</c> but not <c>/.git/</c>.</item>
/// </list>
/// <para>
/// <b>The list is the union of what the code base already believed</b>, not a fresh opinion:
/// picking a subset would have changed more behaviour than adopting all of it. It stays a
/// heuristic — a project whose real sources live in <c>build/</c> is misjudged here, and the way
/// to tell Inferpal so is <c>.inferpal/project.json</c>, which extends the index exclusions
/// on top of this list.
/// </para>
/// </remarks>
internal static class WorkspaceScan
{
    /// <summary>
    /// Directory names never worth walking: build output, VCS/IDE metadata, dependency caches, and
    /// Inferpal's own data directory.
    /// </summary>
    public static readonly string[] ExcludedDirNames =
    [
        "obj", "bin", "build", "dist",           // build output
        ".git", ".vs", ".generated",             // VCS / IDE / codegen metadata
        "node_modules", "packages",              // dependency caches
        ".venv", "venv",                         // Python virtual environments (a fresh one: ~400 .py of pip alone)
        ".inferpal",                             // Inferpal's own state, incl. history/ snapshots
    ];

    /// <summary>
    /// <c>true</c> when <paramref name="path"/> lies under one of <see cref="ExcludedDirNames"/>
    /// <b>below</b> <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the part below the root is judged. The folders above it are where the user keeps the
    /// workspace, not build output: judged on the absolute path, a workspace under <c>build/</c>,
    /// <c>dist/</c>, <c>bin/</c> or <c>packages/</c> lost every file to every walker. A path that is
    /// not under <paramref name="root"/>, or no root at all, is judged whole.
    /// </para>
    /// <para>
    /// Case-insensitive and separator-agnostic on purpose: both were forgotten by at least one of
    /// the copies this replaces, and on Windows a case-sensitive comparison is simply wrong.
    /// </para>
    /// </remarks>
    public static bool IsExcludedPath(string path, string? root)
    {
        var below = BelowRoot(path, root);
        foreach (var dir in ExcludedDirNames)
        {
            if (dir.Equals(PackagesDirName, StringComparison.OrdinalIgnoreCase)) continue;
            if (below.Contains($@"\{dir}\", StringComparison.OrdinalIgnoreCase) ||
                below.Contains($"/{dir}/",  StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return UnderNuGetPackages(path, below);
    }

    // `packages` names two different folders: NuGet's packages.config cache (third-party code — the
    // .js of jQuery and Bootstrap in an older ASP.NET solution) and the source tree of a JS/TS
    // monorepo (yarn, pnpm and lerna keep every workspace package there). Skipped by name, a
    // monorepo's own sources vanished from every walker, so it is skipped only when it carries NuGet's
    // marks.
    private const string PackagesDirName = "packages";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NuGetPackagesDirs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <c>true</c> when <paramref name="dir"/> is a NuGet packages folder: a <c>repositories.config</c>,
    /// or a <c>.nupkg</c> in one of its package folders. A folder that cannot be judged (absent,
    /// unreadable) keeps the historical exclusion. Cached per folder: the question is asked for every
    /// file below it.
    /// </summary>
    internal static bool LooksLikeNuGetPackages(string dir) => NuGetPackagesDirs.GetOrAdd(dir, static d =>
    {
        try
        {
            if (File.Exists(Path.Combine(d, "repositories.config"))) return true;
            foreach (var package in Directory.EnumerateDirectories(d).Take(50))
                if (Directory.EnumerateFiles(package, "*.nupkg").Any()) return true;
            return false;
        }
        catch { return true; }
    });

    // Every `packages` segment below the root, judged on the folder it names.
    private static bool UnderNuGetPackages(string path, string below)
    {
        var offset = path.Length - below.Length;
        foreach (var needle in new[] { @"\packages\", "/packages/" })
        {
            for (var i = below.IndexOf(needle, StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = below.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                if (LooksLikeNuGetPackages(path[..(offset + i + needle.Length - 1)])) return true;
            }
        }
        return false;
    }

    // The part of path below root, keeping its leading separator; the whole path when it is not below.
    private static string BelowRoot(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return path;
        var r = root.TrimEnd('\\', '/');
        return path.Length > r.Length
               && path.StartsWith(r, PathComparer.Comparison)
               && path[r.Length] is '\\' or '/'
            ? path[r.Length..]
            : path;
    }

    /// <summary><c>true</c> when a directory should not be descended into, by its own name.</summary>
    /// <remarks>
    /// Separator-agnostic like <see cref="IsExcludedPath"/> — <c>Path.GetFileName</c> alone only
    /// understands the native separator, so on Linux a Windows-style path kept its full
    /// <c>C:\p\node_modules</c> as the "leaf" and was never excluded.
    /// </remarks>
    public static bool IsExcludedDirName(string directoryPath)
    {
        var trimmed = directoryPath.TrimEnd('\\', '/');
        var cut     = trimmed.LastIndexOfAny(['\\', '/']);
        var leaf    = cut < 0 ? trimmed : trimmed[(cut + 1)..];
        return leaf.Equals(PackagesDirName, StringComparison.OrdinalIgnoreCase)
            ? LooksLikeNuGetPackages(trimmed)
            : ExcludedDirNames.Contains(leaf, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file-name pattern a model-supplied argument may enumerate with, or <c>null</c> when it
    /// carries a directory part.
    /// </summary>
    /// <remarks>
    /// ⚠ .NET appends the directory part of a search pattern to the start folder WITHOUT refusing
    /// <c>..</c>: <c>EnumerateFiles(root, "..\*.md")</c> lists the files above the root, while only the
    /// start path goes through <c>PathSanitizer.AssertUnderRoot</c> — <c>search_in_files</c> read, and
    /// <c>rename_symbol</c> rewrote, files outside the workspace. A leading <c>**/</c> is the glob
    /// habit of a recursive walk, which this already is, and is dropped.
    /// </remarks>
    public static string? NormalizeFilePattern(string? pattern)
    {
        var p = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
        while (p.StartsWith("**/", StringComparison.Ordinal) || p.StartsWith("**\\", StringComparison.Ordinal))
            p = p[3..];
        if (p == ".") p = "*";
        return p.Length == 0 || p.IndexOfAny(['\\', '/']) >= 0 || p.Contains("..", StringComparison.Ordinal)
            || p.Contains('\0') || Path.IsPathRooted(p)
            ? null
            : p;
    }

    /// <summary>The model-facing refusal for a pattern <see cref="NormalizeFilePattern"/> rejected.</summary>
    public static string InvalidPatternMessage(string argument, string? pattern) =>
        $"Error: '{argument}' must be a file-name pattern such as *.cs, without a folder or '..' "
        + $"(received: {pattern}). Put the folder in 'path' instead.";

    /// <summary>
    /// Files matching <paramref name="pattern"/> under <paramref name="start"/>, excluded directories
    /// skipped — judged below <paramref name="root"/> when one is given, below <paramref name="start"/>
    /// otherwise. A start that cannot be opened yields nothing; a folder below it that cannot be read
    /// is skipped instead of ending the walk.
    /// </summary>
    /// <remarks>
    /// The enumeration is lazy, so a <c>try</c> around the call never saw what happened deeper: one
    /// unreadable folder (a database volume owned by a container's user, a locked junction in a
    /// Windows profile) threw inside every caller's loop. <see cref="EnumerationOptions.IgnoreInaccessible"/>
    /// skips it at the source; the other options keep what <c>SearchOption.AllDirectories</c> did
    /// (no attribute skipped, Win32 wildcards).
    /// </remarks>
    public static IEnumerable<string> EnumerateFiles(string start, string pattern = "*.cs", string? root = null) =>
        EnumerateFiles(start, pattern, root, out _);

    /// <summary>
    /// The same walk, plus whether it <b>could not even start</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ An empty result has two meanings and they are not the same answer: "nothing matched" and
    /// "we could not look". This funnel collapsed them into <c>catch { return []; }</c> — muted,
    /// against this repository's own rule that a silent <c>catch</c> is for pure cleanup only — and
    /// every scanning tool inherited the confusion: <c>search_in_files</c> answered "no match",
    /// <c>trace_dependency</c> built its index from nothing and reported "Direct dependants (0)"
    /// with <b>no partial-scan warning</b> (<c>ScanCoverage(0, 0)</c> is not partial), and the
    /// indexer indexed nothing. A start directory the process cannot open — a network share, a
    /// protected folder — is all it takes.
    /// </para>
    /// <para>
    /// The walk itself is lazy, so this <c>catch</c> only ever fires while <i>constructing</i> it:
    /// the flag is therefore exact, and a failure during iteration still surfaces as an exception
    /// the caller sees. Callers that turn an empty walk into a sentence for the model use this
    /// overload; the others keep the short one.
    /// </para>
    /// </remarks>
    public static IEnumerable<string> EnumerateFiles(string start, string pattern, string? root, out bool failed)
    {
        failed = false;
        var judgedBelow = string.IsNullOrEmpty(root) ? start : root;
        // Defence in depth: a pattern with a directory part never reaches the walk (see NormalizeFilePattern).
        if (NormalizeFilePattern(pattern) is not { } safePattern) { failed = true; return []; }
        try
        {
            return Walk(start, safePattern).Where(f => !IsExcludedPath(f, judgedBelow));
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("WorkspaceScan.EnumerateFiles", ex);
            failed = true;
            return [];
        }
    }

    /// <summary>
    /// Why a directory of an entry, and which one, describing a hole in what a walk will see.
    /// </summary>
    /// <param name="Folder">The folder, relative to the walk's root.</param>
    /// <param name="Kind">Which of the two reasons applies.</param>
    internal readonly record struct WalkGap(string Folder, WalkGapKind Kind)
    {
        /// <summary>The localized sentence for this gap — one reader for both causes.</summary>
        /// <remarks>
        /// ⚠ The two causes need two sentences and the choice lives HERE, not at each rendering
        /// site: "cannot be listed" sends the reader to a permission or a lock, "is a link, not
        /// followed" sends them somewhere else entirely. Two call sites picking the sentence
        /// themselves is one call site that will pick the wrong one.
        /// </remarks>
        public string Sentence() => Kind == WalkGapKind.NotFollowed
            ? Inferpal.Localization.Strings.ScanFolderNotFollowed(Folder)
            : Inferpal.Localization.Strings.ScanFolderSkipped(Folder);
    }

    /// <summary>The two reasons a folder's files never reach a walk.</summary>
    internal enum WalkGapKind
    {
        /// <summary>The process cannot list the folder (permissions, a lock).</summary>
        Unlistable,

        /// <summary>The folder is a symlink or junction, which the walk does not follow.</summary>
        NotFollowed,
    }

    /// <summary>
    /// The first folder below <paramref name="start"/> whose files a walk will <b>not</b> see, with
    /// the reason — or <c>null</c> when the whole tree is readable and unlinked.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b><see cref="WalkOptions"/> carries <c>IgnoreInaccessible = true</c>, so the walk skips
    /// such a folder in SILENCE and its files are never even enumerated.</b> That is one step worse
    /// than a file it took and could not read: the files are absent from the total, so a scan
    /// coverage built from the walk says "complete". Measured on a two-file workspace with the only
    /// dependant inside an unlistable folder: <c>analyze_impact</c> answered
    /// <c>Direct dependants (0) · Risk: LOW</c> while the funnel itself reported success
    /// (<c>failed = false</c>, which only ever meant "the START directory could not be opened").
    /// The real cases are named by <c>InaccessibleFolderTests</c>: a database volume mounted inside
    /// the repository and owned by a container's user, a locked junction under a Windows profile.
    /// </para>
    /// <para>
    /// Cheap on purpose, and this is what decided the shape: a <b>directory-only</b> enumeration
    /// with <c>IgnoreInaccessible = false</c> throws and NAMES the path, so nothing here
    /// re-implements Win32 name matching — the trap this repository already paid on
    /// <c>*.sln</c>/<c>*.slnx</c>. It walks by hand rather than with
    /// <c>RecurseSubdirectories</c> for one reason: <see cref="IsExcludedDirName"/> must be
    /// honoured, or a <c>node_modules</c> with odd permissions would be reported although it is
    /// excluded anyway — and a gate whose output is noise ends up disarmed. Skipping those subtrees
    /// also makes it cheaper than the flat call.
    /// </para>
    /// <para>
    /// ⚠ The <b>first</b> one, not all of them: naming one folder is what makes the message
    /// actionable, and stopping there keeps the cost of a clean repository to a single
    /// directory-only traversal.
    /// </para>
    /// </remarks>
    public static WalkGap? FirstWalkGap(string start, string? root = null)
    {
        try
        {
            foreach (var child in Directory.EnumerateDirectories(start))
            {
                if (IsExcludedDirName(child)) continue;

                // ⚠ A LINKED directory is not descended into, by this detector or by the walk
                // itself — see WalkOptions. It is a gap all the same: its files are not analysed,
                // and saying "not followed" sends the reader somewhere else entirely than saying
                // "cannot be listed". Checked BEFORE recursing, which is also what keeps this
                // method finite: a junction pointing at an ancestor made it descend ~60 levels
                // until Windows refused the path, and it then reported an 895-character
                // `src\deep\loop\src\deep\loop\…` as the folder at fault.
                if (IsLink(child))
                    return new WalkGap(Rel(child, root), WalkGapKind.NotFollowed);

                if (FirstWalkGap(child, root) is { } deeper) return deeper;
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // ⚠ Nothing is traced here, and that is deliberate — this `catch` is not a swallowed
            // error, it IS the measurement: the exception is the answer, and the folder is RETURNED
            // to be named in the report. Tracing it would add one ring entry per tool call on a
            // workspace that has such a folder, which is the very noise DroppedLineOnce and
            // RecordOnce exist to prevent. `ex` is bound only to narrow the filter.
            _ = ex;
            return new WalkGap(Rel(start, root), WalkGapKind.Unlistable);
        }
    }

    /// <summary><c>true</c> when the entry is a symlink or a junction.</summary>
    /// <remarks>
    /// ⚠ .NET maps a Unix symlink to <see cref="FileAttributes.ReparsePoint"/> as well, so one test
    /// covers both platforms. A path that cannot even be stat'ed is not a link we can name: it is
    /// treated as listable here and caught by the enumeration above.
    /// </remarks>
    private static bool IsLink(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    // Relative to the root when there is one: an absolute path would leak the user's folders into
    // the model's context.
    private static string Rel(string path, string? root) =>
        string.IsNullOrEmpty(root) ? Path.GetFileName(path.TrimEnd('\\', '/'))
                                   : Path.GetRelativePath(root, path);

    /// <remarks>
    /// ⚠ <b>Nothing here skips reparse points, and that is deliberate</b>: the options cannot tell a
    /// linked <i>directory</i> from a linked <i>file</i>, and only the first one loops. That
    /// distinction lives in <see cref="Walk"/>, which is why this walk goes through
    /// <see cref="FileSystemEnumerable{TResult}"/> rather than <c>Directory.EnumerateFiles</c>.
    /// <see cref="EnumerationOptions.MatchType"/> must stay in step with the matcher
    /// <see cref="Walk"/> calls — the enumerable's own pattern matching is bypassed.
    /// </remarks>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible    = true,
        AttributesToSkip      = 0,
        MatchType             = MatchType.Win32,
    };

    /// <summary>
    /// The walk itself: the files under <paramref name="start"/> matching <paramref name="pattern"/>,
    /// never descending into a <b>linked</b> directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>Refusing to descend into a linked directory is a CRASH fix, not a preference.</b> A
    /// junction or symlink pointing at one of its own ancestors — a `latest` link, a Docker bind
    /// mount, a junction into a Windows profile — made this enumeration walk the cycle until the
    /// process died: measured <see cref="OutOfMemoryException"/>, and in another run more than ten
    /// minutes without returning. <see cref="EnumerationOptions.IgnoreInaccessible"/> is what makes
    /// it fatal rather than noisy: it swallows the per-directory error at the bottom of the cycle
    /// and keeps queueing directories. This is the single walk funnel of the product — the index,
    /// `search_in_files`, `list_files` and every analysis tool — so the blast radius was everything.
    /// </para>
    /// <para>
    /// ⚠ Bounding the depth instead (<c>MaxRecursionDepth</c>) was measured and rejected: it stops
    /// the crash but walks the cycle over and over, returning <b>43 paths for 2 real files</b> —
    /// the same file indexed twenty-one times under twenty-one paths. A crash traded for a poisoned
    /// index.
    /// </para>
    /// <para>
    /// ⚠ <b>And the obvious spelling costs files for nothing.</b>
    /// <c>AttributesToSkip = ReparsePoint</c> on <c>Directory.EnumerateFiles</c> is one line and
    /// stops the cycle, but the attribute is the same on a linked <i>file</i>: measured, a symlinked
    /// <c>Linked.cs</c> disappeared from the walk while no gap was reported, because
    /// <see cref="FirstWalkGap"/> only ever looks at folders. A linked file cannot loop — only a
    /// directory can — so it is data lost for nothing.
    /// <see cref="FileSystemEnumerable{TResult}"/> is the only shape that separates "descend into"
    /// from "return", and the recursion predicate carries the whole fix.
    /// </para>
    /// <para>
    /// The cost that remains is real and is SAID: the files of a legitimately linked folder are not
    /// read (measured: 2 files instead of 3), and <see cref="FirstWalkGap"/> names the folder with
    /// its own reason, so it is a declared gap and not a silence.
    /// </para>
    /// <para>
    /// ⚠ The pattern is matched by <see cref="FileSystemName.MatchesWin32Expression"/> — the
    /// framework's own matcher, the one <c>Directory.EnumerateFiles</c> uses under
    /// <see cref="MatchType.Win32"/>, never a reimplementation of Win32 wildcards (the trap already
    /// paid on <c>*.sln</c>/<c>*.slnx</c>). Its case flag mirrors
    /// <see cref="MatchCasing.PlatformDefault"/> rather than being hard-coded: <c>*.cs</c> must not
    /// start matching <c>A.CS</c> on Linux, where it did not before.
    /// </para>
    /// <para>
    /// ⚠ <b>And the matcher takes a TRANSLATED expression, not the raw pattern</b> —
    /// <c>Directory.EnumerateFiles</c> runs <see cref="FileSystemName.TranslateWin32Expression"/>
    /// first, and calling the matcher without it quietly changes what the walk answers: measured,
    /// <c>*.*</c> dropped a file with no extension (<c>Makefile</c>) and <c>*.</c> — whose Win32
    /// meaning is exactly "no extension" — returned nothing at all. Both are patterns the model
    /// writes for <c>list_files</c> and <c>search_in_files</c>.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Walk(string start, string pattern)
    {
        var expression = FileSystemName.TranslateWin32Expression(pattern);
        return new FileSystemEnumerable<string>(start, static (ref FileSystemEntry e) => e.ToSpecifiedFullPath(), WalkOptions)
        {
            ShouldRecursePredicate = static (ref FileSystemEntry e) =>
                (e.Attributes & FileAttributes.ReparsePoint) == 0,
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !e.IsDirectory && FileSystemName.MatchesWin32Expression(expression, e.FileName, IgnoreCaseHere),
        };
    }

    /// <summary>What <see cref="MatchCasing.PlatformDefault"/> resolves to on this platform.</summary>
    private static readonly bool IgnoreCaseHere = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
}
