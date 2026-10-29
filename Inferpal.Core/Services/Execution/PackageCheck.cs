using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>The package managers whose added dependencies are checked.</summary>
internal enum PackageEcosystem { NuGet, Npm, PyPI }

/// <summary>What the sources of a project answered about one package name.</summary>
internal enum PackageAnswer { Exists, Absent, Unknown }

/// <summary>One lookup: the answer, the sources that gave it, and why it could not be told.</summary>
/// <param name="Answer">Absent only when every source answered and none has the name.</param>
/// <param name="Where">The sources asked, as the refusal names them ("PyPI", "nuget.org").</param>
/// <param name="Cause">Why the answer is <see cref="PackageAnswer.Unknown"/>; <c>null</c> otherwise.</param>
internal sealed record PackageLookupResult(PackageAnswer Answer, string Where, string? Cause)
{
    public static PackageLookupResult Exists(string where) => new(PackageAnswer.Exists, where, null);
    public static PackageLookupResult Absent(string where) => new(PackageAnswer.Absent, where, null);
    public static PackageLookupResult Unknown(string cause) => new(PackageAnswer.Unknown, string.Empty, cause);
}

/// <summary>A package a call adds: the folder whose configuration chooses where it comes from, the workspace it belongs to,
/// and the environment overrides the command would run with.</summary>
internal sealed record AddedPackage(PackageEcosystem Ecosystem, string Name, string Directory, string Root, IReadOnlyDictionary<string, string> Environment);

/// <summary>Asks the sources a project restores from whether a package name exists.</summary>
internal interface IPackageLookup
{
    /// <summary>Whether <paramref name="package"/> exists on the sources its folder restores from. A source that fails is
    /// <see cref="PackageAnswer.Unknown"/>, never a throw.</summary>
    Task<PackageLookupResult> LookUpAsync(AddedPackage package, CancellationToken ct);

    /// <summary>Names those sources know that are close to <paramref name="package"/>'s, offered with a refusal.</summary>
    Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage package, CancellationToken ct);
}

/// <summary>The folder a shell session's next command starts in, and the environment overrides it inherits.</summary>
internal sealed record ShellState(string Directory, IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// A package name a write or an install command ADDS is asked to its public registry, before the approval card: one the
/// registry does not have is refused, with the close names it knows.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A guard against an invented name, never a frontier: only a certain "absent" refuses. A source that does not answer, a
/// private index or feed, a missing tool, an expired budget, a reader that cannot tell — the call goes to the approval card
/// as if nothing had been checked. A wrong refusal blocks a real package, and the model has no way past it.
/// </para>
/// <para>
/// ⚠ The check runs before the card, so it sends nothing anywhere but a public registry (<see cref="PackageManagerLookup"/>):
/// the name is the model's, and a configuration can point at any server.
/// </para>
/// <para>
/// ⚠ The calls whose resulting manifest is known: <c>run_command</c>, <c>write_file</c>, <c>apply_diff</c>, <c>apply_edits</c>.
/// A write at the editor's cursor (<c>insert_at_cursor</c>, <c>replace_selection</c>), a user's shell tool or an MCP tool
/// is not checked — the docs say so.
/// </para>
/// <para>
/// ⚠ Names only, never versions: a version that does not exist is named by the restore itself (NU1102, ETARGET, "No matching
/// distribution"), which the model repairs. And a name one letter away from a popular package is usually registered already
/// — such a name exists, and passes.
/// </para>
/// <para>
/// ⚠ A manifest is read WHOLE, before and after the change: a name is "added" when the dependency sections of the result
/// list it and the file did not. A fragment holds a project's own name and its settings beside its dependencies.
/// </para>
/// <para>
/// ⚠ A command is asked from where it RUNS: the shell session's folder and environment overrides (a <c>cd</c> or a
/// <c>$env:PIP_INDEX_URL</c> of an earlier call), then its own <c>working_directory</c>, <c>cd</c> steps and the project it
/// names (<c>dotnet add src/App.csproj package</c>, <c>npm --prefix web</c>). A step that changes where packages come from
/// (a variable, <c>dotnet nuget add source</c>, <c>npm config set</c>, a source file), or a folder this reader cannot
/// resolve, makes the rest of the command unknown.
/// </para>
/// </remarks>
internal sealed class PackageCheck(IPackageLookup lookup, TimeSpan? budget = null, TimeSpan? closeNamesBudget = null)
{
    /// <summary>The check every registry uses unless it is given its own.</summary>
    public static PackageCheck Shared { get; set; } = new(new PackageManagerLookup());

    /// <summary>The sources this check asks.</summary>
    internal IPackageLookup Lookup => lookup;

    /// <summary>What the lookups may cost a call; a name not answered by then is not refused.</summary>
    internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);

    /// <summary>What finding the close names may add once a name is known absent; the refusal stands without them.</summary>
    internal static readonly TimeSpan DefaultCloseNamesBudget = TimeSpan.FromSeconds(6);

    /// <summary>How many names are asked at once: each lookup may start a package manager.</summary>
    private const int ConcurrentLookups = 4;

    private static readonly TimeSpan MatchTimeout = RegexBudget.Default;

    private readonly TimeSpan _budget = budget ?? DefaultBudget;
    private readonly TimeSpan _closeNamesBudget = closeNamesBudget ?? DefaultCloseNamesBudget;

    private static readonly AsyncLocal<bool> ByThePerson = new();

    /// <summary>Whether the call running now is one the person typed (<see cref="ThePersonsOwnCalls"/>).</summary>
    internal static bool IsThePersonsOwnCall => ByThePerson.Value;

    /// <summary>Marks the calls made until it is disposed as the person's own (a <c>/run</c> they typed): no name of theirs
    /// is asked or refused.</summary>
    /// <remarks>
    /// ⚠ The check judges what a MODEL adds. The person who typed the name knows whether it exists — a package published a
    /// minute ago, which a registry's search does not list yet, would otherwise be refused to them, in words written for a
    /// model, with no way past it but turning the check off.
    /// </remarks>
    public static IDisposable ThePersonsOwnCalls()
    {
        var before = ByThePerson.Value;
        ByThePerson.Value = true;
        return new Restore(before);
    }

    private sealed class Restore(bool before) : IDisposable
    {
        public void Dispose() => ByThePerson.Value = before;
    }

    /// <summary>What a call answers instead of running when it adds a package no source has; <c>null</c> to let it run.</summary>
    /// <param name="refuses">Whether the person's rules refuse the call for a subject (<see cref="IApprovalService.Refuses"/>):
    /// such a call sends no name, and its card refuses it as usual.</param>
    /// <remarks>Never throws, except the caller's own cancellation.</remarks>
    public async Task<string?> RefusalAsync(string tool, JsonElement args, string? root, Func<ShellState?>? shell, CancellationToken ct,
                                            Func<string, string, bool>? refuses = null)
    {
        if (ByThePerson.Value || string.IsNullOrEmpty(root) || tool is not ("run_command" or "write_file" or "apply_diff" or "apply_edits"))
            return null;

        IReadOnlyList<AddedPackage> added;
        try { added = Added(tool, args, root, shell?.Invoke()); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("PackageCheck.Added", ex);
            return null;
        }
        if (added.Count == 0) return null;
        // ⚠ On the subjects the card judges — the command as written, each path resolved as the tool resolves it, every
        // path of a batch: judged on the raw argument, a rule written for the absolute path let the name leave.
        if (refuses is not null && RuleSubjects(tool, args, root).Any(s => refuses(tool, s))) return null;

        (AddedPackage Package, PackageLookupResult Result)[] answers;
        using (var spent = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var gate = new SemaphoreSlim(ConcurrentLookups))
        {
            spent.CancelAfter(_budget);
            answers = await Task.WhenAll(added.Select(async a =>
            {
                try
                {
                    await gate.WaitAsync(spent.Token);
                    try { return (a, await AskAsync(a, spent.Token)); }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return (a, PackageLookupResult.Unknown($"no answer within {_budget.TotalSeconds:0} s"));
                }
            }));
        }

        foreach (var (a, r) in answers)
            if (r is { Answer: PackageAnswer.Unknown, Cause: { } cause })
                Diagnostics.RecordOnce("PackageCheck", $"'{a.Name}' ({a.Ecosystem}) was not checked: {cause}", key: $"{a.Ecosystem}|{cause}");
        foreach (var (a, r) in answers)
            if (r.Answer == PackageAnswer.Absent) return Refusal(a.Name, r.Where, await CloseNamesAsync(a, ct));
        return null;
    }

    private async Task<PackageLookupResult> AskAsync(AddedPackage a, CancellationToken ct)
    {
        try { return await lookup.LookUpAsync(a, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Diagnostics.Swallow("PackageCheck.LookUp", ex);
            return PackageLookupResult.Unknown(Diagnostics.RootMessage(ex));
        }
    }

    private async Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage a, CancellationToken ct)
    {
        using var spent = CancellationTokenSource.CreateLinkedTokenSource(ct);
        spent.CancelAfter(_closeNamesBudget);
        try { return await lookup.CloseNamesAsync(a, spent.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Swallow("PackageCheck.CloseNames", ex);
            return [];
        }
    }

    /// <summary>The refusal the model reads: the sources asked, and the names that exist, so the next call can use one.</summary>
    private static string Refusal(string name, string where, IReadOnlyList<string> closeNames)
    {
        var close = closeNames.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        return $"Error: the package '{name}' does not exist on {where}, so the call was NOT executed and nothing was written. "
             + (close.Count > 0 ? $"Packages that exist with close names: {string.Join(", ", close)}. " : "")
             + "Use the exact name of a package that exists, or tell the user that you found none.";
    }

    // ── names a call adds ────────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();

    /// <summary>
    /// The steps of a command line, split at <c>&amp;&amp;</c>, <c>||</c>, <c>;</c>, <c>|</c>, a lone <c>&amp;</c> (a background job
    /// in bash, a second command in pwsh 7) and line breaks — outside quotes only.
    /// </summary>
    /// <remarks>⚠ Inside quotes a separator is part of an argument: split there, <c>pip install "x @ git+https://h/r.git#a&amp;egg=x"</c>
    /// became a bare <c>x</c>, asked of PyPI and refused as absent.</remarks>
    internal static List<string> Steps(string command)
    {
        var steps = new List<string>();
        var step  = new StringBuilder();
        char? quote = null;
        foreach (var c in command)
        {
            if (quote is { } open) { if (c == open) quote = null; }
            else if (c is '"' or '\'') quote = c;
            else if (c is '&' or '|' or ';' or '\r' or '\n') { steps.Add(step.ToString()); step.Clear(); continue; }
            step.Append(c);
        }
        steps.Add(step.ToString());
        return steps;
    }

    /// <summary>The packages <paramref name="tool"/> called with <paramref name="args"/> adds, each with where it is asked from.</summary>
    /// <param name="tool">The tool called.</param>
    /// <param name="args">Its arguments, as the model wrote them.</param>
    /// <param name="root">The workspace root.</param>
    /// <param name="shell">Where the shell session's next command starts; the workspace root when <c>null</c>.</param>
    internal static IReadOnlyList<AddedPackage> Added(string tool, JsonElement args, string root, ShellState? shell = null)
    {
        var env = shell?.Environment ?? NoOverrides;
        var found = new List<AddedPackage>();
        if (tool == "run_command")
        {
            if (args.Str("command") is not { } command) return found;
            var start = shell is not null && Directory.Exists(shell.Directory) ? shell.Directory : root;
            var dir = ExistingFolder(args.Str("working_directory"), start) ?? start;
            // One step at a time, so that `cd web && npm install x` is asked from web/, and a pipe ends a step's arguments.
            foreach (var part in Steps(command))
            {
                var step = part.Trim();
                // A step that tells a package manager where to look changes the sources of everything after it.
                if (PackageCommands.ChangesSources(step)) break;
                var cd = Regex.Match(step, @"^(?:cd|chdir|pushd|Push-Location|Set-Location|sl)(?:\s+(?:/d|-Path|-LiteralPath))?(?:\s+(?<dir>.+))?$",
                                     RegexOptions.IgnoreCase, MatchTimeout);
                if (cd.Success)
                {
                    // A folder this reader cannot resolve (a variable, `~`, one an earlier step creates) leaves the rest unknown.
                    if (ExistingFolder(cd.Groups["dir"].Value.Trim().Trim('"', '\''), dir) is not { } next) break;
                    dir = next;
                    continue;
                }
                var names = PackageCommands.Names(step);
                if (names.Names.Count == 0) continue;
                var at = dir;
                if (names.Folder is not null)
                {
                    // The project the command names, not the shell's folder, decides the sources; one not found is not asked.
                    if (ProjectFolder(names.Folder, dir) is not { } project) continue;
                    at = project;
                }
                found.AddRange(names.Names.Select(n => new AddedPackage(n.Ecosystem, n.Name, at, root, env)));
            }
            return Distinct(found);
        }

        var writes = Writes(tool, args).ToList();
        // A call that also writes where packages come from changes the sources its names would be asked against.
        if (writes.Any(w => PackageManifests.IsSourceConfiguration(Path.GetFileName(w.Path.Trim())))) return found;

        var after = new Dictionary<string, string>(PathComparer.Default);
        foreach (var (path, newText, oldText) in writes)
        {
            var file = Path.GetFileName(path.Trim());
            if (PackageManifests.EcosystemOf(file) is not { } eco || ResolvedPath(path, root) is not { } full) continue;
            var current = after.TryGetValue(full, out var pending) ? pending : File.Exists(full) ? TextFileEncoding.ReadText(full) : string.Empty;
            // A replacement that does not match the file exactly (apply_diff tolerates whitespace) is not guessed at: the
            // result is unknown, so nothing it adds is refused.
            if ((oldText is null ? newText : Replaced(current, oldText, newText)) is not { } next) continue;
            after[full] = next;
            if (PackageManifests.DeclaresSource(file, next)) continue;
            var before = PackageManifests.Names(file, current).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var provided = PackageManifests.ProvidedNames(file, next);
            found.AddRange(PackageManifests.Names(file, next)
                .Where(n => !before.Contains(n) && !provided.Contains(PackageCommands.NormalizedPythonName(n)))
                .Select(n => new AddedPackage(eco, n, Path.GetDirectoryName(full) ?? root, root, env)));
        }
        return Distinct(found);
    }

    private static List<AddedPackage> Distinct(List<AddedPackage> found) =>
        [.. found.GroupBy(a => (a.Ecosystem, a.Name.ToLowerInvariant())).Select(g => g.First())];

    /// <summary><paramref name="text"/> with the first occurrence of <paramref name="old"/> replaced, line endings aside; <c>null</c>
    /// when it does not occur.</summary>
    private static string? Replaced(string text, string old, string replacement)
    {
        var lf = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var oldLf = old.Replace("\r\n", "\n", StringComparison.Ordinal);
        var at = oldLf.Length == 0 ? -1 : lf.IndexOf(oldLf, StringComparison.Ordinal);
        return at < 0 ? null : lf[..at] + replacement.Replace("\r\n", "\n", StringComparison.Ordinal) + lf[(at + oldLf.Length)..];
    }

    private static IEnumerable<(string Path, string New, string? Old)> Writes(string tool, JsonElement args)
    {
        switch (tool)
        {
            case "write_file" when args.Str("path") is { } p && args.Str("content") is { } content:
                yield return (p, content, null);
                break;
            case "apply_diff" when args.Str("path") is { } p && args.Str("new_content") is { } n && args.Str("old_content") is { } o:
                yield return (p, n, o);
                break;
            case "apply_edits" when args.Array("edits", out var edits):
                foreach (var e in edits.EnumerateArray())
                    if (e.Str("path") is { } ep && e.Str("new_content") is { } en && e.Str("old_content") is { } eo)
                        yield return (ep, en, eo);
                break;
        }
    }

    /// <summary>What the permission rules match this call on at its card: the command, or each file it writes.</summary>
    private static IEnumerable<string> RuleSubjects(string tool, JsonElement args, string root) =>
        tool == "run_command"
            ? args.Str("command") is { } command ? [command] : []
            : Writes(tool, args).Select(w => ResolvedPath(w.Path, root)).OfType<string>();

    /// <summary><paramref name="raw"/> resolved against <paramref name="from"/>, existing or not; <c>null</c> when it is not a usable path.</summary>
    private static string? ResolvedPath(string raw, string from)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return PathSanitizer.Sanitize(raw.Trim(), from); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The folder of the project <paramref name="raw"/> names from <paramref name="from"/> — a project file's folder, or the
    /// folder itself; <c>null</c> when neither exists.</summary>
    private static string? ProjectFolder(string raw, string from) =>
        ResolvedPath(raw.Trim('"', '\''), from) is { } full
            ? Directory.Exists(full) ? full : File.Exists(full) ? Path.GetDirectoryName(full) : null
            : null;

    /// <summary><paramref name="raw"/> resolved against <paramref name="from"/> when it names a folder that exists; <c>null</c> otherwise.</summary>
    private static string? ExistingFolder(string? raw, string from) =>
        raw is not null && ResolvedPath(raw, from) is { } full && Directory.Exists(full) ? full : null;
}
