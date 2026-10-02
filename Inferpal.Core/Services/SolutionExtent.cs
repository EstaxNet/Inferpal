using System.IO;
using Inferpal.Services.Signals;

namespace Inferpal.Services;

/// <summary>Why some projects of a solution stay outside the workspace root.</summary>
internal enum SolutionExtentLimit
{
    /// <summary>Every project is under the root.</summary>
    None,
    /// <summary>The solution is in no git work tree: nothing tells where the repository ends, so the root does not widen.</summary>
    NoWorkTree,
    /// <summary>These projects are outside the git work tree that holds the solution.</summary>
    BeyondWorkTree,
    /// <summary>Holding them would make the user's home folder or a drive root the workspace.</summary>
    UserFolder,
    /// <summary>The solution's folder holds <c>.inferpal/</c> files, which a wider root would stop reading.</summary>
    Layers,
}

/// <summary>
/// The workspace root a solution needs — the smallest folder holding the solution and every project it lists — and the
/// projects that root still leaves outside.
/// </summary>
/// <param name="Root">The workspace root: the solution's folder, or the folder that also holds the projects beside it.</param>
/// <param name="SolutionDir">The solution's own folder.</param>
/// <param name="Outside">Projects of the solution the root does not hold: the tools cannot read or edit them.</param>
/// <param name="Limit">Why <paramref name="Outside"/> is not empty.</param>
/// <param name="Wanted">The root that would have held them, when a bound refused it (<see cref="SolutionExtentLimit.Layers"/>,
/// <see cref="SolutionExtentLimit.UserFolder"/>).</param>
/// <remarks>
/// ⚠ A solution may list projects outside its own folder — <c>UI\UI.sln</c> naming <c>..\DAL\DAL.csproj</c>, the layout of
/// roughly one solution in twenty on GitHub. Rooted at the solution's folder, the agent could not read, search, index or
/// edit those projects (every call refused as "outside the workspace root") while <c>get_solution_info</c> listed them.
/// <para>
/// The root widens within three bounds, because the solution file is written by the repository, not by the user, and the
/// read tools run without an approval prompt:
/// <list type="bullet">
///   <item>never past the git work tree that holds the solution — a cloned repository cannot reach outside its own
///   checkout — and without one, not at all;</item>
///   <item>never to the user's home folder or a drive root, nor above them (a home folder under version control);</item>
///   <item>never while the solution's folder holds <c>.inferpal/</c> files, even if the wider root has some too: they are
///   read from the root, so moving it would stop reading them — a deny overlay among them would loosen permissions
///   without a word.</item>
/// </list>
/// What stays outside is returned, so that it can be said.
/// </para>
/// </remarks>
internal sealed record SolutionExtent(
    string Root, string SolutionDir, IReadOnlyList<SolutionProject> Outside, SolutionExtentLimit Limit,
    string? Wanted = null)
{
    /// <summary>Project names a note spells out before counting the rest.</summary>
    internal const int MaxNamed = 10;

    /// <summary>
    /// Why the projects in <see cref="Outside"/> stay out of reach, in one sentence — the model's and <c>/diagnostics</c>'s,
    /// in English like both. Empty when nothing is outside.
    /// </summary>
    internal string Reason => Limit switch
    {
        SolutionExtentLimit.NoWorkTree =>
            "The solution is in no git repository, and without one the workspace root does not widen beyond the solution's folder.",
        SolutionExtentLimit.BeyondWorkTree =>
            "They are outside the git repository that holds the solution.",
        SolutionExtentLimit.UserFolder =>
            $"Holding them would make {Wanted} — the user's home folder or a drive root — the workspace.",
        SolutionExtentLimit.Layers =>
            $"{Path.Combine(SolutionDir, ".inferpal")} holds files that a root at {Wanted} would stop reading; moving them to "
            + $"{Path.Combine(Wanted ?? SolutionDir, ".inferpal")} lets the workspace root hold the whole solution.",
        _ => string.Empty,
    };

    /// <summary>The <c>/diagnostics</c> note for <see cref="Outside"/>: how many, which, where, and why.</summary>
    internal string Describe(string solutionPath)
    {
        var names = string.Join(", ", Outside.Take(MaxNamed).Select(p => p.Name));
        if (Outside.Count > MaxNamed) names += $" and {Outside.Count - MaxNamed} more";
        return $"{Outside.Count} project(s) of {Path.GetFileName(solutionPath)} sit outside the workspace root {Root}, "
             + $"so the tools cannot read, search, index or edit them: {names}. {Reason}";
    }

    /// <summary>
    /// The decision, on paths already read: <paramref name="workTree"/> is the git work tree holding the solution
    /// (<c>null</c> = none), <paramref name="home"/> the user's home folder, <paramref name="holdsLayers"/> whether a
    /// folder holds <c>.inferpal/</c> files of the user's.
    /// </summary>
    internal static SolutionExtent Decide(
        string solutionDir, IReadOnlyList<SolutionProject> projects, string? workTree, string? home,
        Func<string, bool> holdsLayers)
    {
        var slnDir     = Bare(solutionDir);
        var notCovered = projects.Where(p => !IsUnder(DirectoryOf(p), slnDir)).ToList();
        if (notCovered.Count == 0) return new(slnDir, slnDir, [], SolutionExtentLimit.None);
        if (string.IsNullOrEmpty(workTree)) return new(slnDir, slnDir, notCovered, SolutionExtentLimit.NoWorkTree);

        var tree      = Bare(workTree);
        var reachable = notCovered.Where(p => IsUnder(DirectoryOf(p), tree)).ToList();
        var beyond    = notCovered.Where(p => !reachable.Contains(p)).ToList();
        if (reachable.Count == 0) return new(slnDir, slnDir, beyond, SolutionExtentLimit.BeyondWorkTree);

        var root = reachable.Aggregate(slnDir, (r, p) => CommonAncestor(r, DirectoryOf(p)));
        if (Path.GetDirectoryName(root) is null || (!string.IsNullOrEmpty(home) && IsUnder(Bare(home), root)))
            return new(slnDir, slnDir, notCovered, SolutionExtentLimit.UserFolder, root);
        // ⚠ Even when the wider root has .inferpal/ files of its own: the solution folder's are read today, and widening
        // would stop reading them without a word. What is read now stays read.
        if (holdsLayers(slnDir))
            return new(slnDir, slnDir, notCovered, SolutionExtentLimit.Layers, root);

        return new(root, slnDir, beyond, beyond.Count > 0 ? SolutionExtentLimit.BeyondWorkTree : SolutionExtentLimit.None);
    }

    /// <summary>The extent of the solution file at <paramref name="solutionPath"/>, read from disk.</summary>
    /// <remarks>A solution that cannot be read keeps its own folder: nothing says where its projects are.</remarks>
    internal static SolutionExtent Of(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var dir  = Path.GetDirectoryName(full) ?? full;
        try
        {
            var contents = Parsed(full);
            if (contents.Unreadable is not null) return new(Bare(dir), Bare(dir), [], SolutionExtentLimit.None);
            var extent = Decide(dir, contents.Projects, WorkTreeOf(dir),
                                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), HoldsLayers);
            // The note below is keyed by the path and caused by the file's state: readable again, it may be said again.
            Diagnostics.Forget("SolutionExtent.Of", full);
            return extent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException)
        {
            Diagnostics.RecordOnce("SolutionExtent.Of", $"{full}: {Diagnostics.RootMessage(ex)}", full);
            return new(Bare(dir), Bare(dir), [], SolutionExtentLimit.None);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Written, long Length, SolutionContents Contents)>
        _parsed = new(PathComparer.Default);

    /// <summary>
    /// The solution's projects, parsed again only when the file changed (its write time or its length).
    /// </summary>
    /// <remarks>
    /// ⚠ A root is derived on paths that run per KEYSTROKE (the slash autocomplete reads the project's templates from
    /// it): without this, every key typed after <c>/</c> read and parsed the whole solution file. The git tree and the
    /// <c>.inferpal/</c> layers are still probed each time — a few directory checks — because they change without the
    /// solution file changing.
    /// </remarks>
    private static SolutionContents Parsed(string solutionPath)
    {
        var info = new FileInfo(solutionPath);
        var (written, length) = (info.LastWriteTimeUtc, info.Length);   // Length throws on a missing file
        if (_parsed.TryGetValue(solutionPath, out var cached) && cached.Written == written && cached.Length == length)
            return cached.Contents;
        var contents = SolutionFiles.ParseProjects(solutionPath, File.ReadAllText(solutionPath));
        _parsed[solutionPath] = (written, length, contents);
        return contents;
    }

    private const string OutsideContext = "SolutionExtent";
    private static readonly Dictionary<string, string> _outsideNoted = new(StringComparer.Ordinal);

    /// <summary>
    /// Says in <c>/diagnostics</c>, once per state, which projects stay outside — the extent is recomputed on every
    /// heartbeat, so a plain record would fill the ring.
    /// </summary>
    /// <remarks>
    /// ⚠ Keyed by the note itself, and the previous one forgotten when it changes or the projects come in: the condition
    /// depends on the solution file, the git tree and <c>.inferpal/</c>, none of which is the key — without the forget,
    /// "once" would mean once in the life of the process.
    /// </remarks>
    private static void NoteOutside(string solutionPath, SolutionExtent extent)
    {
        var note = extent.Outside.Count == 0 ? null : extent.Describe(solutionPath);
        lock (_outsideNoted)
        {
            if (_outsideNoted.TryGetValue(solutionPath, out var previous) && previous != note)
            {
                Diagnostics.Forget(OutsideContext, previous);
                _outsideNoted.Remove(solutionPath);
            }
            if (note is null) return;
            _outsideNoted[solutionPath] = note;
        }
        Diagnostics.RecordOnce(OutsideContext, note, note);
    }

    /// <summary>
    /// The extent of the solution Visual Studio reports open, or <c>null</c> when it reports none. A root decided from it,
    /// so what it leaves outside is said in <c>/diagnostics</c>.
    /// </summary>
    internal static SolutionExtent? OfActiveSolution() =>
        ActiveSolutionSignal.TryReadSolutionPath() is { } sln ? Noted(sln) : null;

    /// <summary>
    /// The workspace root for a folder a solution search returned: the extent of the solution it holds, or the folder
    /// itself when it holds none (a working directory, an open file's folder). What it leaves outside is said in
    /// <c>/diagnostics</c>.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(dir))]
    internal static string? RootForDir(string? dir) =>
        string.IsNullOrEmpty(dir) ? dir
        : SolutionFiles.FirstIn(dir) is { } sln ? Noted(sln).Root
        : dir;

    // ⚠ Only where a root is DECIDED: get_solution_info reads extents too, in VS Code as well, where the root is the
    // folder the user opened and a note naming the extent's root would describe a workspace that does not exist.
    private static SolutionExtent Noted(string solutionPath)
    {
        var extent = Of(solutionPath);
        NoteOutside(Path.GetFullPath(solutionPath), extent);
        return extent;
    }

    /// <summary>
    /// Whether <paramref name="dir"/> holds <c>.inferpal/</c> files of the user's — anything but the snapshots of
    /// <c>history/</c>, which the product writes on its own.
    /// </summary>
    internal static bool HoldsLayers(string dir)
    {
        var folder = Path.Combine(dir, ".inferpal");
        if (!Directory.Exists(folder)) return false;
        return Directory.EnumerateFileSystemEntries(folder)
                        .Any(e => !string.Equals(Path.GetFileName(e), "history", PathComparer.Comparison));
    }

    /// <summary>The git work tree holding <paramref name="dir"/> — a <c>.git</c> folder, or a <c>.git</c> file for a worktree
    /// or a submodule — or <c>null</c>.</summary>
    internal static string? WorkTreeOf(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
        {
            var dotGit = Path.Combine(d.FullName, ".git");
            if (Directory.Exists(dotGit) || File.Exists(dotGit)) return d.FullName;
        }
        return null;
    }

    private static string DirectoryOf(SolutionProject project) =>
        Bare(Path.GetDirectoryName(project.AbsolutePath) ?? project.AbsolutePath);

    private static string CommonAncestor(string a, string b)
    {
        var dir = a;
        while (!IsUnder(b, dir) && Path.GetDirectoryName(dir) is { } parent) dir = Bare(parent);
        return dir;
    }

    private static bool IsUnder(string path, string root) =>
        string.Equals(path, root, PathComparer.Comparison)
        || path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar,
                           PathComparer.Comparison);

    /// <summary>Full path without a trailing separator — except a drive or filesystem root, which keeps it.</summary>
    private static string Bare(string path)
    {
        var full    = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetDirectoryName(full) is null || trimmed.Length == 0 ? full : trimmed;
    }
}
