using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Execution;

/// <summary>
/// Asks the public registries, and only them: nuget.org (with the local package folders NuGet is configured with) through
/// <c>dotnet package search</c>, the npm registry through <c>npm view</c>, PyPI directly.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The name is sent BEFORE the approval card, so it never goes to a server the configuration chose: a repository's
/// <c>.npmrc</c> or <c>nuget.config</c> can name any host, and the model writes the name — sent there, it would leave the
/// machine with no card shown. Each tool is first asked OFFLINE which sources apply here (<c>dotnet nuget list source</c>,
/// <c>npm config get registry</c>, the pip and uv configuration); any other than the public registry makes the answer
/// unknown, and nothing is sent. A company feed is therefore never checked — and never wrongly refused.
/// </para>
/// <para>
/// ⚠ Each command runs where the call does, with the session's environment overrides: the configuration that applies is
/// that folder's and that environment's.
/// </para>
/// <para>
/// ⚠ pip cannot say "absent": <c>pip index versions</c> prints "No matching distribution found" both for a name its index
/// does not have and for an index that does not answer. Python is therefore asked from PyPI directly, and only when nothing
/// declares another index (<see cref="OtherPythonIndex"/>).
/// </para>
/// <para>
/// ⚠ NuGet reports a source that does not answer INSIDE that source's result, and still exits 0: absent means every source
/// answered and none has the name. Its search hides prerelease-only and unlisted packages and lags behind a new one: an
/// absent from the search is confirmed by the package's own index on nuget.org.
/// </para>
/// <para>
/// ⚠ What the project serves itself is no registry's to answer for: an npm workspace package, a uv workspace member or
/// source, the project's own name, a restore source declared in MSBuild (which <c>dotnet package search</c> does not read).
/// </para>
/// </remarks>
internal sealed class PackageManagerLookup : IPackageLookup
{
    /// <summary>What one package manager command may take.</summary>
    private static readonly TimeSpan CommandBudget = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan MatchTimeout = RegexBudget.Default;

    // ⚠ No redirect is followed by any client of this code base: a redirect is an answer, not an instruction.
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public Task<PackageLookupResult> LookUpAsync(AddedPackage package, CancellationToken ct) => package.Ecosystem switch
    {
        PackageEcosystem.NuGet => NuGetAsync(package, ct),
        PackageEcosystem.Npm   => NpmAsync(package, ct),
        _                      => PyPIAsync(package, ct),
    };

    public async Task<IReadOnlyList<string>> CloseNamesAsync(AddedPackage package, CancellationToken ct)
    {
        switch (package.Ecosystem)
        {
            case PackageEcosystem.NuGet:
                var nuget = await RunAsync("dotnet", "dotnet", [], ["package", "search", package.Name, "--take", "5", "--prerelease", "--format", "json"], package, ct);
                return nuget.Unknown is null && nuget.Result.ExitCode == 0 ? NuGetNames(nuget.Result.Stdout) : [];
            case PackageEcosystem.Npm when RunTestsTool.ResolveNpm(OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists) is { } npm:
                var search = await RunAsync("npm", npm.FileName, npm.Prefix, NpmSearchArguments(package.Name), package, ct);
                return search.Unknown is null && search.Result.ExitCode == 0 ? NpmNames(search.Result.Stdout) : [];
            default:
                return [];
        }
    }

    /// <summary>The arguments of the <c>npm search</c> that finds the close names of an absent <paramref name="name"/>.</summary>
    /// <remarks>⚠ The public registry, named: <c>npm search</c> asks the DEFAULT registry, not the one a scope maps to, and
    /// a configuration that sends <c>@types</c> to the public registry and everything else to a company server would
    /// send the company the name a public lookup just found absent.</remarks>
    internal static string[] NpmSearchArguments(string name) =>
        ["search", "--json", "--searchlimit=5", "--fetch-retries=0", "--registry=https://registry.npmjs.org/", name];

    // ── NuGet ────────────────────────────────────────────────────────────────

    private static async Task<PackageLookupResult> NuGetAsync(AddedPackage package, CancellationToken ct)
    {
        if (MSBuildRestoreSource(package.Directory, package.Root) is { } declared) return PackageLookupResult.Unknown(declared);
        // Offline: the sources the configuration enables here. Any other than nuget.org is never sent the name.
        var list = await RunAsync("dotnet", "dotnet", [], ["nuget", "list", "source", "--format", "short"], package, ct);
        if (list.Unknown is { } listCause) return PackageLookupResult.Unknown(listCause);
        if (list.Result.ExitCode != 0) return PackageLookupResult.Unknown($"dotnet nuget list source did not answer (exit code {list.Result.ExitCode})");
        if (NuGetOtherSource(list.Result.Stdout) is { } other) return PackageLookupResult.Unknown($"{other} is a source other than nuget.org, and is not asked");

        var exact = await RunAsync("dotnet", "dotnet", [], ["package", "search", package.Name, "--exact-match", "--prerelease", "--format", "json"], package, ct);
        if (exact.Unknown is { } cause) return PackageLookupResult.Unknown(cause);
        var answer = NuGetAnswer(exact.Result.ExitCode, exact.Result.Stdout);
        if (answer.Answer != PackageAnswer.Absent || !NuGetOrgEnabled(list.Result.Stdout)) return answer;
        // The search leaves out unlisted packages and lags behind a new one; the package's own index on nuget.org does not.
        return await StatusAsync($"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(package.Name.ToLowerInvariant())}/index.json", ct) switch
        {
            >= 200 and < 300 => PackageLookupResult.Exists("nuget.org"),
            404              => answer,
            var status       => PackageLookupResult.Unknown($"nuget.org answered {(status is null ? "nothing" : $"HTTP {status}")} for the package index"),
        };
    }

    private static readonly string[] NuGetOrgHosts = ["api.nuget.org", "www.nuget.org", "nuget.org"];

    private static IEnumerable<string> EnabledNuGetSources(string listOutput) =>
        listOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(l => Regex.Match(l, @"^(?<flags>[A-Z]+)\s+(?<source>\S.*)$", RegexOptions.None, MatchTimeout))
                  .Where(m => m.Success && m.Groups["flags"].Value.Contains('E'))
                  .Select(m => m.Groups["source"].Value.Trim());

    /// <summary>The first enabled source of a <c>dotnet nuget list source --format short</c> that is a server other than
    /// nuget.org; <c>null</c> when only nuget.org and local folders are enabled.</summary>
    /// <remarks>⚠ A folder on a network share (<c>\\server\feed</c>, <c>file://server/feed</c>) is a server: searched, it
    /// would receive the name over the network.</remarks>
    internal static string? NuGetOtherSource(string listOutput) =>
        EnabledNuGetSources(listOutput).FirstOrDefault(s =>
            Uri.TryCreate(s, UriKind.Absolute, out var u)
            && (u.Scheme is "http" or "https" && !NuGetOrgHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase)
                || u.IsFile && u.IsUnc));

    private static bool NuGetOrgEnabled(string listOutput) =>
        EnabledNuGetSources(listOutput).Any(s => Uri.TryCreate(s, UriKind.Absolute, out var u) && NuGetOrgHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase));

    /// <summary>What <c>dotnet package search &lt;name&gt; --exact-match --format json</c> answered.</summary>
    internal static PackageLookupResult NuGetAnswer(int exitCode, string stdout)
    {
        if (exitCode != 0 || Json(stdout) is not { } doc) return PackageLookupResult.Unknown($"dotnet package search did not answer (exit code {exitCode})");
        using (doc)
        {
            var root = doc.RootElement;
            if (Problems(root) is { } problem) return PackageLookupResult.Unknown(problem);
            if (!root.TryGetProperty("searchResult", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
                return PackageLookupResult.Unknown("no NuGet source is configured");
            var sources = new List<string>();
            foreach (var source in results.EnumerateArray())
            {
                if (Problems(source) is { } sourceProblem) return PackageLookupResult.Unknown(sourceProblem);
                if (source.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Array && packages.GetArrayLength() > 0)
                    return PackageLookupResult.Exists(SourceName(source));
                sources.Add(SourceName(source));
            }
            return PackageLookupResult.Absent(Joined(sources));
        }
    }

    /// <summary>The package ids a <c>dotnet package search --format json</c> listed, every source together.</summary>
    internal static IReadOnlyList<string> NuGetNames(string stdout)
    {
        if (Json(stdout) is not { } doc) return [];
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("searchResult", out var results) || results.ValueKind != JsonValueKind.Array) return [];
            return [.. results.EnumerateArray()
                .Where(s => s.TryGetProperty("packages", out var p) && p.ValueKind == JsonValueKind.Array)
                .SelectMany(s => s.GetProperty("packages").EnumerateArray())
                .Select(p => p.TryGetProperty("id", out var id) ? id.GetString() : null)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The MSBuild file of <paramref name="directory"/> or above it, up to <paramref name="root"/>, that declares a
    /// restore source of its own; <c>null</c> when none does.</summary>
    internal static string? MSBuildRestoreSource(string directory, string root)
    {
        foreach (var dir in FoldersUpTo(directory, root))
        {
            List<string> files;
            try
            {
                files = [.. Directory.EnumerateFiles(dir, "*.*proj"), .. Directory.EnumerateFiles(dir, "Directory.*.props"),
                         .. Directory.EnumerateFiles(dir, "Directory.*.targets")];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files)
                if (Read(file) is { } text && PackageManifests.DeclaresSource("Directory.Build.props", text))
                    return $"{file} declares a restore source";
        }
        return null;
    }

    private static string? Problems(JsonElement e) =>
        e.TryGetProperty("problems", out var problems) && problems.ValueKind == JsonValueKind.Array && problems.GetArrayLength() > 0
            ? string.Join(" ", problems.EnumerateArray().Select(p => p.TryGetProperty("text", out var t) ? t.GetString() : p.GetRawText()))
            : null;

    private static string SourceName(JsonElement source) =>
        source.TryGetProperty("sourceName", out var n) && n.GetString() is { Length: > 0 } s ? s : "a NuGet source";

    // ── npm ──────────────────────────────────────────────────────────────────

    private static async Task<PackageLookupResult> NpmAsync(AddedPackage package, CancellationToken ct)
    {
        if (NpmWorkspacePackage(package.Name, package.Directory, package.Root) is { } member) return PackageLookupResult.Unknown(member);
        if (RunTestsTool.ResolveNpm(OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath, File.Exists) is not { } npm)
            return PackageLookupResult.Unknown("npm could not be started without a shell");
        // Offline: the registry the configuration chooses for this name. Any other than the public one is never sent it.
        var scope = package.Name.StartsWith('@') && package.Name.IndexOf('/') is > 1 and var slash ? package.Name[..slash] : null;
        var config = await RunAsync("npm", npm.FileName, npm.Prefix, ["config", "get", "registry", .. scope is null ? Array.Empty<string>() : [scope + ":registry"]], package, ct);
        if (config.Unknown is { } configCause) return PackageLookupResult.Unknown(configCause);
        if (config.Result.ExitCode != 0) return PackageLookupResult.Unknown($"npm config get did not answer (exit code {config.Result.ExitCode})");
        if (NpmOtherRegistry(config.Result.Stdout, scope) is { } other) return PackageLookupResult.Unknown($"{other} is a registry other than the public one, and is not asked");
        // ⚠ No retries: an unreachable registry is retried for a minute, and the card would wait for it.
        var view = await RunAsync("npm", npm.FileName, npm.Prefix, ["view", package.Name, "name", "--json", "--fetch-retries=0"], package, ct);
        return view.Unknown is { } cause ? PackageLookupResult.Unknown(cause) : NpmAnswer(view.Result.ExitCode, view.Result.Stdout, view.Result.Stderr);
    }

    /// <summary>What <c>npm view &lt;name&gt; name --json</c> answered: absent on a 404 only.</summary>
    internal static PackageLookupResult NpmAnswer(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0) return PackageLookupResult.Exists("the npm registry");
        if (Json(stdout) is { } doc)
        {
            using (doc)
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code))
                {
                    if (code.GetString() != "E404") return PackageLookupResult.Unknown($"npm answered {code.GetString()}");
                    var summary = error.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
                    var host = Regex.Match(summary, @"https?://(?<host>[^/\s]+)", RegexOptions.None, MatchTimeout);
                    return PackageLookupResult.Absent(host.Success ? $"the npm registry ({host.Groups["host"].Value})" : "the npm registry");
                }
            }
        }
        return PackageLookupResult.Unknown($"npm view did not answer (exit code {exitCode}{(stderr.Length > 0 ? ": " + FirstLine(stderr) : "")})");
    }

    private static readonly string[] PublicNpmHosts = ["registry.npmjs.org", "registry.yarnpkg.com"];

    /// <summary>The registry an <c>npm config get registry [@scope:registry]</c> chose for a name of <paramref name="scope"/>, when
    /// it is not the public one; <c>null</c> when it is.</summary>
    /// <remarks>One key prints its bare value, several print <c>key=value</c> lines; an unset key prints <c>undefined</c>.</remarks>
    internal static string? NpmOtherRegistry(string configOutput, string? scope)
    {
        var lines = configOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? Value(string key) =>
            lines.Select(l => l.StartsWith(key + "=", StringComparison.Ordinal) ? l[(key.Length + 1)..] : null).FirstOrDefault(v => v is not null)
            ?? (key == "registry" && lines.Length == 1 && !lines[0].Contains('=') ? lines[0] : null);
        var chosen = scope is not null && Value(scope + ":registry") is { } scoped && scoped != "undefined" ? scoped : Value("registry");
        if (chosen is null || chosen == "undefined") return "an unreadable npm registry setting";
        return Uri.TryCreate(chosen, UriKind.Absolute, out var u) && PublicNpmHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) ? null : chosen;
    }

    /// <summary>The package names an <c>npm search --json</c> listed.</summary>
    internal static IReadOnlyList<string> NpmNames(string stdout)
    {
        if (Json(stdout) is not { } doc) return [];
        using (doc)
        {
            return doc.RootElement.ValueKind != JsonValueKind.Array ? [] : [.. doc.RootElement.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null)
                .OfType<string>()];
        }
    }

    /// <summary>Why <paramref name="name"/> is a package of the workspace <paramref name="directory"/> belongs to, or <c>null</c>:
    /// a <c>package.json</c> declaring <c>workspaces</c>, a <c>pnpm-workspace.yaml</c> or a <c>lerna.json</c> at or above the
    /// folder, up to <paramref name="root"/>, and a package under it named so.</summary>
    internal static string? NpmWorkspacePackage(string name, string directory, string root)
    {
        foreach (var dir in FoldersUpTo(directory, root))
        {
            var declared = Read(Path.Combine(dir, "package.json")) is { } manifest && manifest.Contains("\"workspaces\"", StringComparison.Ordinal)
                        || File.Exists(Path.Combine(dir, "pnpm-workspace.yaml")) || File.Exists(Path.Combine(dir, "lerna.json"));
            if (!declared) continue;
            foreach (var member in FilesUnder(dir, "package.json", depth: 4))
                if (Read(member) is { } text && Regex.IsMatch(text, $@"""name""\s*:\s*""{Regex.Escape(name)}""", RegexOptions.None, MatchTimeout))
                    return $"{name} is a package of the workspace in {dir}";
        }
        return null;
    }

    // ── PyPI ─────────────────────────────────────────────────────────────────

    private static async Task<PackageLookupResult> PyPIAsync(AddedPackage package, CancellationToken ct)
    {
        string? Env(string v) => package.Environment.TryGetValue(v, out var o) ? o : Environment.GetEnvironmentVariable(v);
        if (PythonProjectProvides(package.Name, package.Directory, package.Root) is { } provided) return PackageLookupResult.Unknown(provided);
        if (OtherPythonIndex(package.Directory, package.Root, Env, PythonConfigFiles(package.Directory, package.Root, Env)) is { } other)
            return PackageLookupResult.Unknown(other);
        var name = PackageCommands.NormalizedPythonName(package.Name);
        return await StatusAsync($"https://pypi.org/pypi/{Uri.EscapeDataString(name)}/json", ct) is { } status
            ? PyPIAnswer(status)
            : PackageLookupResult.Unknown("PyPI did not answer");
    }

    /// <summary>The HTTP status <paramref name="url"/> answers, redirects not followed; <c>null</c> when it does not answer.</summary>
    private static async Task<int?> StatusAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            return (int)response.StatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>What PyPI's JSON API answered with <paramref name="status"/>: absent on a 404 only.</summary>
    internal static PackageLookupResult PyPIAnswer(int status) => status switch
    {
        >= 200 and < 300 => PackageLookupResult.Exists("PyPI"),
        404              => PackageLookupResult.Absent("PyPI"),
        _                => PackageLookupResult.Unknown($"PyPI answered HTTP {status}"),
    };

    /// <summary>Why the Python project <paramref name="directory"/> belongs to serves <paramref name="name"/> itself, or <c>null</c>:
    /// it is the project's own name, one of its <c>[tool.uv.sources]</c>, or the project is a uv workspace.</summary>
    internal static string? PythonProjectProvides(string name, string directory, string root)
    {
        var wanted = PackageCommands.NormalizedPythonName(name);
        foreach (var dir in FoldersUpTo(directory, root))
        {
            var path = Path.Combine(dir, "pyproject.toml");
            if (Read(path) is not { } text) continue;
            if (Regex.IsMatch(text, @"(?m)^\s*\[tool\.uv\.workspace\]", RegexOptions.None, MatchTimeout)) return $"{path} declares a uv workspace";
            if (PackageManifests.ProvidedNames("pyproject.toml", text).Contains(wanted)) return $"{path} provides {name} itself";
        }
        return null;
    }

    private static readonly string[] PythonIndexVariables =
    [
        "PIP_INDEX_URL", "PIP_EXTRA_INDEX_URL", "PIP_FIND_LINKS", "PIP_NO_INDEX",
        "UV_INDEX_URL", "UV_EXTRA_INDEX_URL", "UV_INDEX", "UV_DEFAULT_INDEX", "UV_FIND_LINKS", "UV_NO_INDEX",
    ];

    /// <summary>What declares a Python package index other than PyPI for <paramref name="directory"/>, or <c>null</c>: an
    /// environment variable, a pip or uv configuration file, a requirements file or a <c>pyproject.toml</c> of the folder or
    /// of one above it, up to <paramref name="root"/>.</summary>
    internal static string? OtherPythonIndex(string directory, string root, Func<string, string?> environment, IEnumerable<string> configFiles)
    {
        foreach (var variable in PythonIndexVariables)
            if (!string.IsNullOrWhiteSpace(environment(variable))) return $"{variable} declares another package index";

        foreach (var file in configFiles)
            if (Read(file) is { } text && Regex.IsMatch(text, @"(?m)^\s*(\[\[index\]\]|(extra[-_])?index[-_]url\s*=|index\s*=|default[-_]index\s*=|find[-_]links\s*=|no[-_]index\s*=)", RegexOptions.None, MatchTimeout))
                return $"{file} declares another package index";

        foreach (var dir in FoldersUpTo(directory, root))
        {
            List<string> manifests;
            try { manifests = [.. Directory.EnumerateFiles(dir, "requirements*.txt"), Path.Combine(dir, "pyproject.toml")]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in manifests)
                if (Read(file) is { } text && PackageManifests.DeclaresSource(Path.GetFileName(file), text))
                    return $"{file} declares another package index";
        }
        return null;
    }

    /// <summary>The pip and uv configuration files that may apply to <paramref name="directory"/>: the user's, the system's,
    /// and those of a virtual or conda environment.</summary>
    private static IEnumerable<string> PythonConfigFiles(string directory, string root, Func<string, string?> environment)
    {
        string? Env(string v) => environment(v) is { Length: > 0 } s ? s : null;
        var home = UserFolders.Of(Environment.SpecialFolder.UserProfile);
        if (Env("PIP_CONFIG_FILE") is { } pipConfig) yield return pipConfig;
        if (Env("UV_CONFIG_FILE") is { } uvConfig) yield return uvConfig;
        if (OperatingSystem.IsWindows())
        {
            var appData = UserFolders.Of(Environment.SpecialFolder.ApplicationData);
            var programData = UserFolders.Of(Environment.SpecialFolder.CommonApplicationData);
            yield return Path.Combine(appData, "pip", "pip.ini");
            yield return Path.Combine(home, "pip", "pip.ini");
            yield return Path.Combine(programData, "pip", "pip.ini");
            yield return Path.Combine(appData, "uv", "uv.toml");
            yield return Path.Combine(programData, "uv", "uv.toml");
        }
        else
        {
            var config = Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
            yield return Path.Combine(config, "pip", "pip.conf");
            yield return Path.Combine(home, ".pip", "pip.conf");
            yield return Path.Combine(home, "Library", "Application Support", "pip", "pip.conf");
            yield return "/Library/Application Support/pip/pip.conf";
            yield return "/etc/pip.conf";
            yield return "/etc/xdg/pip/pip.conf";
            yield return Path.Combine(config, "uv", "uv.toml");
            yield return "/etc/uv/uv.toml";
        }
        var environments = new List<string>();
        if (Env("VIRTUAL_ENV") is { } active) environments.Add(active);
        if (Env("CONDA_PREFIX") is { } conda) environments.Add(conda);
        foreach (var dir in FoldersUpTo(directory, root))
        {
            environments.Add(Path.Combine(dir, ".venv"));
            environments.Add(Path.Combine(dir, "venv"));
            yield return Path.Combine(dir, "uv.toml");
        }
        foreach (var env in environments)
        {
            yield return Path.Combine(env, "pip.conf");
            yield return Path.Combine(env, "pip.ini");
        }
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary><paramref name="directory"/> and each folder above it, up to <paramref name="root"/> — the folder alone when it is
    /// not under the root.</summary>
    private static IEnumerable<string> FoldersUpTo(string directory, string root)
    {
        for (var dir = new DirectoryInfo(directory); dir is not null; dir = dir.Parent)
        {
            yield return dir.FullName;
            if (PathComparer.SameDirectory(dir.FullName, root) || !PathSanitizer.IsUnderRoot(dir.FullName, root)) yield break;
        }
    }

    /// <summary>The files named <paramref name="name"/> under <paramref name="directory"/>, <paramref name="depth"/> folders down
    /// at most, never inside <c>node_modules</c> or a hidden folder.</summary>
    private static IEnumerable<string> FilesUnder(string directory, string name, int depth)
    {
        var level = new List<string> { directory };
        for (var d = 0; d <= depth && level.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                var file = Path.Combine(dir, name);
                if (d > 0 && File.Exists(file)) yield return file;
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir).Where(s => Path.GetFileName(s) is var n && n != "node_modules" && !n.StartsWith('.')));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            level = next;
        }
    }

    /// <summary>A package manager command, run where the call runs with its environment; <c>Unknown</c> names why it gave no answer.</summary>
    private static async Task<(ChildProcessResult Result, string? Unknown)> RunAsync(
        string tool, string fileName, string[] prefix, string[] arguments, AddedPackage package, CancellationToken ct)
    {
        // Both tools write UTF-8; read as the console's code page, a source named with an accent comes back mangled.
        var psi = new ProcessStartInfo
        {
            FileName = fileName, WorkingDirectory = package.Directory,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in prefix.Concat(arguments)) psi.ArgumentList.Add(a);
        foreach (var (name, value) in package.Environment) psi.Environment[name] = value;
        // Inferpal starts this command, not the user: no telemetry of the SDK, no banner, English output.
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"]               = "1";
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"]      = "en";
        psi.Environment["NO_UPDATE_NOTIFIER"]          = "1";
        try
        {
            var result = await ChildProcess.RunAsync(psi, CommandBudget, ct);
            return result.TimedOut ? (result, $"{tool} did not answer within {CommandBudget.TotalSeconds:0} s") : (result, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (default, $"{tool} could not be started ({Diagnostics.RootMessage(ex)})");
        }
    }

    private static JsonDocument? Json(string text)
    {
        var start = text.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        try { return JsonDocument.Parse(text[start..]); }
        catch (JsonException) { return null; }
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) ? TextFileEncoding.ReadText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    private static string Joined(IReadOnlyList<string> names) =>
        names.Count <= 1 ? names.FirstOrDefault() ?? "the NuGet sources" : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
