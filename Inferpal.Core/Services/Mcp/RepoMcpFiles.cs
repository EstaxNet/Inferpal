using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Mcp;

/// <summary>A file of the repository an agreement covers, and what it held when the agreement was asked for.</summary>
/// <param name="Path">Relative to the repository's root, with <c>/</c>.</param>
/// <param name="Digest">What it held: its content hashed; past the hashing budget, its size and last write time; for a
/// link that leads out of the repository, the link's own target, never what lies behind it.</param>
/// <param name="Stamp">Its size and last write time when the digest was taken: a later look that finds the same stamp
/// keeps the digest instead of reading the file again.</param>
internal sealed record RepoMcpFile(string Path, string Digest, string Stamp);

/// <summary>What an agreement to start a repository's server covers: its files, or why they cannot be covered.</summary>
/// <param name="Files">In path order; <c>null</c> when they cannot be covered.</param>
/// <param name="Uncovered">Why not, in English: the server's status line and <c>/diagnostics</c> read it.</param>
internal sealed record RepoMcpCoverage(IReadOnlyList<RepoMcpFile>? Files, string? Uncovered);

/// <summary>
/// The repository's files a server's definition runs: every file its command, its arguments, its environment — or the
/// script its package manager runs — names, with the folder each lives in and the <c>cwd</c> it declares, as a pull can
/// change them.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The FOLDER, not the named file alone: <c>server.js</c> loads <c>lib/tools.js</c>, and <c>dotnet run --project
/// tools/Mcp</c> names no source file at all. Except the repository's root, never listed: the whole repository would ask
/// again at every pull. A name that does not exist covers the nearest folder it would live in
/// (<c>node tools/mcp/server</c>), a dotted name the module it can be (<c>python -m tools.mcp.server</c>).
/// </para>
/// <para>
/// ⚠ In a git repository a folder is its TRACKED files: what a pull changes, and nothing this machine writes — build
/// output, installed dependencies, caches, logs, a database the server keeps beside itself. Counted, those would change
/// at every run and the question would come back until it is answered without being read. Outside git, the walk's
/// exclusions and the caches named in <see cref="Regenerated"/> stand in for it.
/// </para>
/// <para>
/// ⚠ A path the repository wrote is judged on its TEXT before the disk is touched, and a link is followed only while its
/// target stays in the repository (<see cref="Probe"/>): a link the repository commits to <c>/dev/zero</c> would be read
/// forever before any question, and one to <c>\\host\share</c> is a network logon to a host the repository chose. A link
/// that leads out is covered by its own text.
/// </para>
/// <para>
/// Not covered — the card shows the command, and that is the boundary: what the program fetches or finds by itself (an
/// <c>npx</c> package, a module on its own path), the files a build pulls in from elsewhere, a root-level script's imports.
/// </para>
/// </remarks>
internal static class RepoMcpFiles
{
    /// <summary>More files than this under the folders a server names, and no agreement can cover them.</summary>
    internal const int MaxFiles = 20_000;

    /// <summary>How much content is hashed per server; the files past it are judged by size and last write time.</summary>
    internal const long MaxHashedBytes = 64L * 1024 * 1024;

    private const int MaxLinkHops = 16;
    private const long MaxManifestBytes = 1024 * 1024;

    /// <summary>More names than this to look up, and the definition cannot be covered: each one is a look at the disk.</summary>
    private const int MaxNames = 10_000;

    /// <summary>Caches a tool writes beside the code it runs, skipped by the walk outside git (git does not track them).</summary>
    private static readonly string[] Regenerated =
        ["__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".gradle", ".next", "target"];

    /// <summary>The manifests a runner reads what it runs from, by the runner's name.</summary>
    private static readonly Dictionary<string, string[]> Manifests = new(StringComparer.OrdinalIgnoreCase)
    {
        ["npm"]    = ["package.json"], ["pnpm"] = ["package.json"], ["yarn"] = ["package.json"], ["bun"] = ["package.json"],
        ["make"]   = ["Makefile", "makefile", "GNUmakefile"],
        ["just"]   = ["justfile", "Justfile", ".justfile"],
        ["task"]   = ["Taskfile.yml", "Taskfile.yaml"],
        ["dotnet"] = ["*.csproj", "*.fsproj", "*.vbproj", "*.sln", "*.slnx", "Directory.Build.props", "Directory.Build.targets"],
        ["cargo"]  = ["Cargo.toml"],
        ["go"]     = ["go.mod"],
        ["uv"]     = ["pyproject.toml"], ["poetry"] = ["pyproject.toml"], ["pdm"] = ["pyproject.toml"],
        ["hatch"]  = ["pyproject.toml"], ["pipenv"] = ["Pipfile"],
        ["gradle"] = ["build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts"],
        ["mvn"]    = ["pom.xml"],
    };

    /// <summary>The runners whose manifest holds the command itself (<c>npm run mcp</c>): its scripts are read as tokens.</summary>
    private static readonly HashSet<string> ScriptRunners = new(StringComparer.OrdinalIgnoreCase) { "npm", "pnpm", "yarn", "bun" };

    private static readonly char[] Separators =
        [' ', '\t', '\r', '\n', '=', ',', ';', ':', '(', ')', '[', ']', '{', '}', '"', '\'', '`', '<', '>', '|', '&'];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex DottedName =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.None, MatchTimeout);

    /// <summary>What a path names, links resolved: a file, a folder, nothing, or a link that leads out of the repository.</summary>
    private enum Kind { None, File, Folder, Outside }

    /// <param name="Path">The file or folder reached; for <see cref="Kind.Outside"/>, where the link leads, as written.</param>
    private readonly record struct Entry(Kind Kind, string Path);

    /// <summary>
    /// The repository's files starting <paramref name="definition"/> runs, with what each holds — or why they cannot be
    /// covered.
    /// </summary>
    /// <param name="cap">More files than this, and they cannot be covered.</param>
    /// <param name="hashBudget">How many bytes are hashed; the files past it are judged by size and last write time.</param>
    /// <param name="previous">The files as an earlier look found them: a file whose stamp has not moved keeps its digest.</param>
    /// <param name="environment">The variables <c>${env:…}</c> reads; this process's when <c>null</c>.</param>
    /// <param name="home">What <c>${userHome}</c> is; the user's folder when <c>null</c>.</param>
    public static async Task<RepoMcpCoverage> CoverAsync(JsonObject definition, string root, int cap = MaxFiles,
        long hashBudget = MaxHashedBytes, IReadOnlyList<RepoMcpFile>? previous = null,
        Func<string, string?>? environment = null, string? home = null, CancellationToken ct = default)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        environment ??= Environment.GetEnvironmentVariable;
        home ??= UserFolders.Of(Environment.SpecialFolder.UserProfile);
        string Expand(string value) => RepoMcpServers.Expand(value, root, home, environment, inputs: null, missing: null);

        try
        {
            var named   = new Dictionary<string, Entry>(PathComparer.Default);   // by the path as named
            var folders = new HashSet<string>(PathComparer.Default);

            var folder = root;
            if (definition["cwd"] is JsonValue c && c.TryGetValue<string>(out var cwd) && cwd.Length > 0
                && Join(root, Expand(cwd)) is { } cwdPath && Probe(cwdPath, root) is { Kind: Kind.Folder } declared)
            {
                folder = declared.Path;
                folders.Add(declared.Path);
            }

            var sources = StringValues(definition).Select(Expand).ToList();
            var arguments = definition["args"] is JsonArray a
                ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? Expand(s) : null).OfType<string>().ToList()
                : [];
            var runner = definition["command"] is JsonValue cv && cv.TryGetValue<string>(out var command)
                ? System.IO.Path.GetFileNameWithoutExtension(Expand(command).Trim()) : null;
            if (runner is not null && Manifests.TryGetValue(runner, out var manifests))
                foreach (var dir in new[] { folder, root }.Distinct(PathComparer.Default))
                    foreach (var manifest in ManifestsIn(dir, manifests, root))
                    {
                        named[manifest.Path] = manifest;
                        if (ScriptRunners.Contains(runner) && manifest.Kind == Kind.File
                            && System.IO.Path.GetFileName(manifest.Path) == "package.json")
                            sources.AddRange(ScriptsRun(manifest.Path, arguments));
                    }

            var looks = 0;
            foreach (var source in sources)
                foreach (var token in Tokens(source))
                    foreach (var candidate in Candidates(token))
                    {
                        if (++looks > MaxNames) return new RepoMcpCoverage(null, "its definition names too many things to look up");
                        foreach (var dir in new[] { folder, root })
                            Name(Join(dir, candidate), root, named, folders);
                    }

            // The folders: what git tracks under them, or the walk outside git.
            var listed = await ListAsync(folders.Where(f => !Same(f, root)).Order(StringComparer.Ordinal).ToList(),
                                         root, cap, ct).ConfigureAwait(false);
            if (listed.Uncovered is { } why) return new RepoMcpCoverage(null, why);

            // ⚠ Every named file is covered, tracked or not: "git does not track it" cannot be read safely — a path
            // written in another case than git's matches no pathspec, and a workspace inside another repository's work
            // tree is tracked by nobody — and either would leave the very script the server runs uncovered.
            var entries = new Dictionary<string, Entry>(named, PathComparer.Default);
            foreach (var path in listed.Files!)
                if (!entries.ContainsKey(path)) entries[path] = Probe(path, root);
            var present = entries.Where(e => e.Value.Kind is Kind.File or Kind.Outside).ToList();
            if (present.Count > cap) return new RepoMcpCoverage(null, $"the folders it names hold more than {cap} files");

            var prior = previous?.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var budget = hashBudget;
            var files = new List<RepoMcpFile>(present.Count);
            foreach (var (path, entry) in present.Select(e => (Relative(e.Key, root), e.Value)).OrderBy(e => e.Item1, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Kind == Kind.Outside) { files.Add(new RepoMcpFile(path, "link:" + entry.Path, "")); continue; }
                if (Digest(entry.Path, prior?.GetValueOrDefault(path), ref budget) is not { } file)
                    return new RepoMcpCoverage(null, $"{path} cannot be read");
                files.Add(file with { Path = path });
            }
            return new RepoMcpCoverage(files, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
        {
            return new RepoMcpCoverage(null, $"its files could not be read ({ex.Message})");
        }
    }

    /// <summary>Adds what <paramref name="full"/> names to <paramref name="named"/> or <paramref name="folders"/>.</summary>
    private static void Name(string? full, string root, Dictionary<string, Entry> named, HashSet<string> folders)
    {
        if (full is null || !Under(full, root)) return;
        var entry = Probe(full, root);
        switch (entry.Kind)
        {
            case Kind.File:
                named[full] = entry;
                folders.Add(System.IO.Path.GetDirectoryName(entry.Path)!);
                break;
            case Kind.Folder:
                if (!WorkspaceScan.IsExcludedDirName(entry.Path)) folders.Add(entry.Path);
                break;
            case Kind.Outside:
                named[full] = entry;
                break;
            case Kind.None:
                // Not there: the nearest folder it would live in is what runs (an extension the runtime adds, a module).
                if (System.IO.Path.GetDirectoryName(entry.Path) is { } parent && !Same(parent, root)
                    && Probe(parent, root) is { Kind: Kind.Folder } existing && !WorkspaceScan.IsExcludedDirName(existing.Path))
                    folders.Add(existing.Path);
                break;
        }
    }

    /// <summary>
    /// What <paramref name="full"/> names, each link on the way followed only while its target stays in the repository —
    /// judged on the target's text before anything behind it is touched.
    /// </summary>
    /// <remarks>For <see cref="Kind.None"/>, <see cref="Entry.Path"/> is the first part of the path that is not there.</remarks>
    private static Entry Probe(string full, string root)
    {
        var current = full;
        for (var hop = 0; hop <= MaxLinkHops; hop++)
        {
            if (!Under(current, root)) return new Entry(Kind.Outside, current);
            if (Same(current, root)) return new Entry(Kind.Folder, root);
            var parts = System.IO.Path.GetRelativePath(root, current)
                                      .Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                                             StringSplitOptions.RemoveEmptyEntries);
            var at = root;
            string? next = null;
            for (var i = 0; i < parts.Length && next is null; i++)
            {
                at = System.IO.Path.Combine(at, parts[i]);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(at); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    return new Entry(Kind.None, at);
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    var (isLink, target) = LinkTarget(at, attributes);
                    if (isLink && target is null) return new Entry(Kind.Outside, at);       // a link that cannot be read
                    if (target is not null)
                    {
                        next = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(at)!, target);
                        if (i + 1 < parts.Length) next = System.IO.Path.Combine([next, .. parts[(i + 1)..]]);
                        continue;
                    }
                }
                if (i + 1 == parts.Length)
                    return new Entry((attributes & FileAttributes.Directory) != 0 ? Kind.Folder : Kind.File, at);
            }
            if (next is null) return new Entry(Kind.None, at);
            try { current = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(next)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new Entry(Kind.Outside, next);
            }
        }
        return new Entry(Kind.Outside, current);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a link, and its target as written — read from the link itself, never by opening
    /// what it leads to. A reparse point that is no link (a cloud placeholder) is not one.
    /// </summary>
    private static (bool IsLink, string? Target) LinkTarget(string path, FileAttributes attributes)
    {
        try
        {
            var target = ((attributes & FileAttributes.Directory) != 0 ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path)).LinkTarget;
            return (target is not null, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (true, null);
        }
    }

    /// <summary>
    /// The files under <paramref name="folders"/>: tracked by git, or walked outside git — or why they cannot be listed.
    /// </summary>
    private static async Task<(IReadOnlyList<string>? Files, string? Uncovered)> ListAsync(
        IReadOnlyList<string> folders, string root, int cap, CancellationToken ct)
    {
        if (folders.Count == 0) return ([], null);
        var relative = folders.Select(f => Relative(f, root)).ToList();

        // A name git's command line cannot carry as written is walked instead.
        if (relative.All(r => r.IndexOfAny(['"', '\\']) < 0))
        {
            // ⚠ A pathspec matches case even where the file system does not: `Tools/MCP` would list nothing of `tools/mcp`
            // while a sibling written in git's case lists, and the folder would be neither listed nor walked. Where the
            // file system folds case (PathComparer), git is asked to fold it too.
            var icase = PathComparer.Comparison == StringComparison.OrdinalIgnoreCase;
            var magic = icase ? ":(literal,icase)" : ":(literal)";
            var pathspecs = string.Join(" ", relative.Select(r => "\"" + magic + r + "\""));
            // ⚠ --recurse-submodules: without it a submodule is one gitlink entry, and what a pull rewrites inside it
            // is covered by nothing.
            var run = await GitProcess.CaptureAsync("ls-files -z --recurse-submodules -- " + pathspecs, root, ct).ConfigureAwait(false);
            if (run.TimedOut) return (null, "git gave no answer when its files were listed");
            if (run.ExitCode == 0)
            {
                if (run.Stdout.Length >= ChildProcess.MaxCapturedChars)
                    return (null, $"the folders it names hold more than {cap} files");
                var tracked = run.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (tracked.Length > cap) return (null, $"the folders it names hold more than {cap} files");
                var listed = tracked.Select(t => Join(root, t)).OfType<string>().Where(p => Under(p, root)).ToList();
                // ⚠ A folder git lists nothing under, while it holds files, is not git's to describe: written in
                // another case than git's (a pathspec matches case), or a workspace inside another repository's work
                // tree that does not track it. Walked, as outside git — covered too much rather than not at all.
                var untracked = folders.Where(f => !listed.Any(p => ListedUnder(p, f, icase))).ToList();
                if (untracked.Count == 0) return (listed, null);
                var (walked, overCap) = Walk(untracked, cap - listed.Count);
                return overCap ? (null, $"the folders it names hold more than {cap} files") : ([.. listed, .. walked], null);
            }
            // Not a git repository, or no git: the walk below.
        }

        var (all, over) = Walk(folders, cap);
        return over ? (null, $"the folders it names hold more than {cap} files") : (all, null);
    }

    /// <summary>
    /// Whether git's own pathspec rule puts <paramref name="path"/> under <paramref name="folder"/>: exact, or with A to Z
    /// folded when <paramref name="icase"/>.
    /// </summary>
    /// <remarks>⚠ Never <see cref="Under"/> here: git's <c>icase</c> folds ASCII letters only, the file system and
    /// <see cref="PathComparer"/> fold <c>Ö</c> and <c>ö</c> too — a file git listed under <c>ö/data</c> would pass for
    /// one under <c>Ö</c>, which git listed nothing of, and <c>Ö</c> would be neither listed nor walked.</remarks>
    private static bool ListedUnder(string path, string folder, bool icase)
    {
        static string Fold(string s) => string.Create(s.Length, s, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
        });
        var p = icase ? Fold(path) : path;
        var f = icase ? Fold(folder) : folder;
        if (string.Equals(p, f, StringComparison.Ordinal)) return true;
        var prefix = System.IO.Path.EndsInDirectorySeparator(f) ? f : f + System.IO.Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>The files under <paramref name="folders"/>, regenerated folders left out; over when more than <paramref name="cap"/>.</summary>
    private static (List<string> Files, bool OverCap) Walk(IEnumerable<string> folders, int cap)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            if (WorkspaceScan.IsExcludedDirName(folder)) continue;
            foreach (var path in WorkspaceScan.EnumerateFiles(folder, "*"))
            {
                if (IsRegenerated(path, folder)) continue;
                files.Add(path);
                if (files.Count > cap) return (files, true);
            }
        }
        return (files, false);
    }

    /// <summary>Whether a folder of <see cref="Regenerated"/> lies between <paramref name="start"/> and the file.</summary>
    private static bool IsRegenerated(string path, string start) =>
        System.IO.Path.GetRelativePath(start, path)
                      .Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar])
                      .SkipLast(1)
                      .Any(part => Regenerated.Contains(part, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What a file holds, read once: the previous digest when its stamp has not moved, its content hashed while the
    /// budget lasts, its stamp after — or <c>null</c> when not even its size can be read.
    /// </summary>
    /// <remarks>
    /// Size and write time are enough past the budget: a pull that rewrites a file rewrites its write time. A file that is
    /// there but cannot be opened (another process holds it) is judged the same way, so one rewritten meanwhile still
    /// reads as changed. The read stops at the size the file had: a file that keeps growing cannot hold the look.
    /// </remarks>
    private static RepoMcpFile? Digest(string path, RepoMcpFile? previous, ref long budget)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }

        var stamp = FormattableString.Invariant($"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        if (previous is not null && previous.Stamp == stamp) return previous;
        if (info.Length > budget) return new RepoMcpFile(path, "stamp:" + stamp, stamp);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            var left = info.Length;
            while (left > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                left -= read;
            }
            budget -= info.Length;
            return new RepoMcpFile(path, Convert.ToHexString(hash.GetHashAndReset()), stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RepoMcpFile(path, "unopened:" + stamp, stamp);
        }
    }

    /// <summary>The manifests of <paramref name="names"/> (a name or a <c>*.ext</c> pattern) in <paramref name="dir"/>.</summary>
    private static IEnumerable<Entry> ManifestsIn(string dir, string[] names, string root)
    {
        foreach (var name in names)
        {
            if (!name.StartsWith("*.", StringComparison.Ordinal))
            {
                if (Join(dir, name) is { } path && Probe(path, root) is { Kind: Kind.File or Kind.Outside } entry) yield return entry;
                continue;
            }
            string[] found;
            try { found = Directory.GetFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { found = []; }
            foreach (var path in found.Where(p => p.EndsWith(name[1..], StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                if (Probe(path, root) is { Kind: Kind.File or Kind.Outside } entry) yield return entry;
        }
    }

    /// <summary>
    /// The commands of the <c>package.json</c> scripts that <paramref name="arguments"/> name, with their <c>pre</c> and
    /// <c>post</c> scripts — and <c>server.js</c> for a <c>start</c> without a script, which npm runs then; nothing when
    /// the manifest cannot be read as one.
    /// </summary>
    private static IEnumerable<string> ScriptsRun(string packageJson, IReadOnlyList<string> arguments)
    {
        JsonObject? scripts;
        try
        {
            if (new FileInfo(packageJson).Length > MaxManifestBytes) return [];
            scripts = JsonNode.Parse(TextFileEncoding.ReadText(packageJson))?["scripts"] as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return [];
        }
        var run = new List<string>();
        foreach (var name in arguments)
        {
            if (scripts?[name] is JsonValue v && v.TryGetValue<string>(out var script))
            {
                run.Add(script);
                foreach (var hook in new[] { "pre" + name, "post" + name })
                    if (scripts[hook] is JsonValue h && h.TryGetValue<string>(out var hooked)) run.Add(hooked);
            }
            else if (name == "start") run.Add("server.js");
        }
        return run;
    }

    /// <summary>Every string the definition holds, at any depth: its command, its arguments, its environment.</summary>
    private static IEnumerable<string> StringValues(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        JsonObject o => o.SelectMany(kv => StringValues(kv.Value)),
        JsonArray a => a.SelectMany(StringValues),
        _ => [],
    };

    /// <summary>
    /// The pieces of a value that can name a file — the value whole, then split wherever a shell or an option would split
    /// it. A piece that names nothing costs one look, and a candidate too many can only ask once more.
    /// </summary>
    private static IEnumerable<string> Tokens(string text) =>
        text.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Prepend(text.Trim().Trim('"', '\'', '`'))
            .Where(t => t.Length is > 0 and < 1024)
            .Distinct(StringComparer.Ordinal);

    /// <summary>A token as a path, and, when it is a dotted name, as the module it can be (<c>a.b.c</c> → <c>a/b/c.py</c>, <c>a/b/c</c>).</summary>
    private static IEnumerable<string> Candidates(string token)
    {
        yield return token;
        if (DottedName.IsMatch(token))
        {
            var path = token.Replace('.', '/');
            yield return path + ".py";
            yield return path;
        }
    }

    /// <summary><paramref name="name"/> against <paramref name="dir"/>, made full — or <c>null</c> when it is no path.</summary>
    private static string? Join(string dir, string name)
    {
        try { return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, name))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>Whether <paramref name="full"/> lies in <paramref name="root"/>, on their text alone.</summary>
    internal static bool Under(string full, string root)
    {
        if (string.Equals(full, root, PathComparer.Comparison)) return true;
        var prefix = System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, PathComparer.Comparison);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, PathComparer.Comparison);

    /// <remarks>
    /// Only Windows' separator becomes <c>/</c>: elsewhere a backslash is part of a name, and turned into a separator it
    /// would hand git another folder to list than the one named.
    /// </remarks>
    private static string Relative(string full, string root)
    {
        var relative = System.IO.Path.GetRelativePath(root, full);
        return OperatingSystem.IsWindows() ? relative.Replace('\\', '/') : relative;
    }
}
