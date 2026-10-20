using System.Text.Json;
using System.Text.RegularExpressions;

namespace Inferpal.Services.Execution;

/// <summary>The package names one step of a shell command installs: <c>dotnet add package</c>, <c>npm install</c>,
/// <c>pip install</c>, <c>uv add</c>.</summary>
/// <remarks>
/// <para>
/// ⚠ What is not certainly a registry name is not asked — a path, an archive, a URL, a git spec. And an option this reader
/// does not know as a switch takes the word after it as its value: an unknown option costs a check, never a refusal of
/// the date, folder or strategy that follows it (<c>--exclude-newer 2024-06-01</c>, <c>-C web</c>).
/// </para>
/// <para>
/// ⚠ A command that chooses its own source — an option that names an index, a registry, a source or a configuration
/// file — adds nothing to ask: what the project's configuration answers says nothing about that source.
/// </para>
/// </remarks>
internal static class PackageCommands
{
    private static readonly TimeSpan MatchTimeout = RegexBudget.Default;

    // Options known to take no value: the word after them is a package. Any other option takes the next word.
    private static readonly HashSet<string> PipSwitches = new(StringComparer.Ordinal)
    {
        "-U", "--upgrade", "-q", "--quiet", "-v", "--verbose", "--user", "--no-deps", "--pre", "--force-reinstall", "-I",
        "--ignore-installed", "--no-cache-dir", "--break-system-packages", "--no-build-isolation", "--dry-run",
        "--require-hashes", "--no-warn-script-location", "--disable-pip-version-check", "--no-input", "--isolated",
        "--compile", "--no-compile", "--no-color", "--prefer-binary", "--no-clean", "--ignore-requires-python",
        "--no-warn-conflicts", "--system", "--dev", "--frozen", "--locked", "--no-sync", "--raw", "--raw-sources",
        "--active", "--no-cache", "--offline", "--reinstall", "--no-progress",
    };

    private static readonly HashSet<string> NpmSwitches = new(StringComparer.Ordinal)
    {
        "-D", "--save-dev", "-S", "--save", "-E", "--save-exact", "-O", "--save-optional", "-P", "--save-prod", "-B",
        "--save-bundle", "-g", "--global", "--no-save", "--save-peer", "--no-audit", "--audit", "--no-fund", "--fund",
        "--legacy-peer-deps", "--strict-peer-deps", "--force", "-f", "--ignore-scripts", "--prefer-offline",
        "--prefer-online", "--offline", "--dry-run", "--no-package-lock", "--package-lock-only", "--foreground-scripts",
        "--no-optional", "--silent", "--quiet", "-s", "-q", "-d", "--verbose", "--no-bin-links", "--install-links",
        "--include-workspace-root", "-ws", "--workspaces", "--no-progress", "--json", "--no-color", "--global-style",
    };

    private static readonly HashSet<string> DotnetSwitches = new(StringComparer.Ordinal)
    {
        "-n", "--no-restore", "--prerelease", "--interactive",
    };

    /// <summary>The names one step installs, and the project folder it names (<c>dotnet add src/App.csproj package</c>,
    /// <c>npm --prefix web</c>, <c>uv add --directory web</c>) as written, or <c>null</c>.</summary>
    internal sealed record StepNames(IReadOnlyList<(PackageEcosystem Ecosystem, string Name)> Names, string? Folder);

    private static readonly StepNames Nothing = new([], null);

    /// <summary>A step that tells a package manager where packages come from: whatever follows it is asked against sources
    /// the configuration on disk does not have yet.</summary>
    internal static bool ChangesSources(string step) =>
        Regex.IsMatch(step, @"(?i)\b(pip|uv|npm_config|nuget|yarn|pnpm|conda)_[a-z0-9_]*\s*=|\bdotnet\s+nuget\s+(add|update|enable|disable|remove)\s+source\b"
                          + @"|\b(npm|pnpm|yarn|pip3?|uv)\s+config\s+set\b|\bnpm\s+set\b|\bnuget\.config\b|\.npmrc\b|\.yarnrc|\bpip\.(conf|ini)\b|\buv\.toml\b",
                      RegexOptions.None, MatchTimeout);

    /// <summary>The names <paramref name="step"/> installs, with their package manager.</summary>
    internal static StepNames Names(string step)
    {
        var dotnet = Regex.Match(step, @"\bdotnet\s+(?:add\s+(?:(?!package\b)(?<project>\S+)\s+)?package|package\s+add)\s+(?<rest>.+)$", RegexOptions.None, MatchTimeout);
        if (dotnet.Success)
        {
            var words = Words(dotnet.Groups["rest"].Value);
            var project = dotnet.Groups["project"].Success ? Unquoted(dotnet.Groups["project"].Value) : ValueOf(words, "--project");
            return new(Read(words, DotnetSwitches, o => o is "-s" || o.Contains("source", StringComparison.Ordinal) || o.Contains("config", StringComparison.Ordinal),
                            w => Regex.IsMatch(w, @"^[A-Za-z0-9_][\w.\-]*$", RegexOptions.None, MatchTimeout) ? w : null, PackageEcosystem.NuGet, firstOnly: true),
                       project);
        }
        var npm = Regex.Match(step, @"(?:^|[\s/\\])npm(?:\.cmd)?\s+(?<before>(?:\S+\s+)*?)(?:install|i|add)\s+(?<rest>.+)$", RegexOptions.None, MatchTimeout);
        if (npm.Success)
        {
            var words = Words(npm.Groups["rest"].Value);
            var before = Words(npm.Groups["before"].Value);
            return new(Read(words, NpmSwitches, o => o.Contains("registry", StringComparison.Ordinal) || o.Contains("config", StringComparison.Ordinal),
                            NpmSpecName, PackageEcosystem.Npm, firstOnly: false),
                       ValueOf(before.Concat(words).ToList(), "--prefix", "-C"));
        }
        var pip = Regex.Match(step, @"(?:\bpip(?:3(?:\.\d+)?)?(?:\.exe)?|\buv\s+pip|-m\s+pip)\s+install\s+(?<rest>.+)$|\buv\s+add\s+(?<rest>.+)$", RegexOptions.None, MatchTimeout);
        if (pip.Success)
        {
            var words = Words(pip.Groups["rest"].Value);
            return new(Read(words, PipSwitches,
                            o => o.Contains("index", StringComparison.Ordinal) || o.Contains("find-links", StringComparison.Ordinal) || o is "-i" or "-f"
                              || o.Contains("config", StringComparison.Ordinal),
                            PythonRequirementName, PackageEcosystem.PyPI, firstOnly: false),
                       ValueOf(words, "--directory", "--project"));
        }
        return Nothing;
    }

    /// <summary>The value of the first of <paramref name="options"/> in <paramref name="words"/> (<c>--prefix web</c> or
    /// <c>--prefix=web</c>); <c>null</c> when none is there.</summary>
    private static string? ValueOf(List<string> words, params string[] options)
    {
        for (var i = 0; i < words.Count; i++)
            foreach (var o in options)
            {
                if (words[i] == o && i + 1 < words.Count) return words[i + 1];
                if (words[i].StartsWith(o + "=", StringComparison.Ordinal)) return words[i][(o.Length + 1)..];
            }
        return null;
    }

    private static string Unquoted(string word) => word.Trim('"', '\'');

    private static List<(PackageEcosystem, string)> Read(List<string> words, HashSet<string> switches, Func<string, bool> ownSource,
                                                         Func<string, string?> nameOf, PackageEcosystem ecosystem, bool firstOnly)
    {
        var names = new List<(PackageEcosystem, string)>();
        if (words.Any(w => w.StartsWith('-') && ownSource(w.Split('=')[0]))) return names;
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.StartsWith('-'))
            {
                if (!w.Contains('=') && !switches.Contains(w)) i++;
                continue;
            }
            if (nameOf(w) is { } name) names.Add((ecosystem, name));
            if (firstOnly) break;
        }
        return names;
    }

    /// <summary>The words of an argument list, quotes removed, up to a redirection.</summary>
    private static List<string> Words(string rest)
    {
        var words = new List<string>();
        foreach (Match m in Regex.Matches(rest, @"""(?<w>[^""]*)""|'(?<w>[^']*)'|(?<w>\S+)", RegexOptions.None, MatchTimeout))
        {
            var w = m.Groups["w"].Value;
            if (w.StartsWith('>') || w.StartsWith('<') || w.StartsWith("2>", StringComparison.Ordinal) || w == "&") break;
            words.Add(w);
        }
        return words;
    }

    /// <summary>The registry name of an npm install spec (<c>x</c>, <c>x@1</c>, <c>@s/x@1</c>, <c>alias@npm:x@1</c>); <c>null</c>
    /// for a path, an archive, a URL or a git spec.</summary>
    internal static string? NpmSpecName(string spec)
    {
        var aliased = spec.IndexOf("@npm:", StringComparison.Ordinal);
        if (aliased > 0) spec = spec[(aliased + 5)..];
        if (spec.Contains(':') || spec.StartsWith('.') || spec.StartsWith('~') || spec.StartsWith('/') || spec.Contains('\\')
            || spec.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || spec.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = spec.StartsWith('@')
            ? (spec.IndexOf('@', 1) is var at and > 0 ? spec[..at] : spec)
            : spec.Split('@')[0];
        return Regex.IsMatch(name, @"^(@[a-z0-9][\w.\-~]*/)?[A-Za-z0-9][\w.\-~]*$", RegexOptions.None, MatchTimeout) ? name : null;
    }

    /// <summary>The project name a PEP 508 requirement starts with (<c>requests[socks]>=2; python_version&gt;"3"</c>); <c>null</c>
    /// for a path, an archive, a URL or a direct reference.</summary>
    internal static string? PythonRequirementName(string requirement)
    {
        var r = requirement.Trim();
        if (r.Length == 0 || r.Contains("://", StringComparison.Ordinal) || r.Contains(" @ ", StringComparison.Ordinal) || r.Contains('/') || r.Contains('\\')
            || r.StartsWith('.') || r.EndsWith(".whl", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || r.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            return null;
        var m = Regex.Match(r, @"^(?<name>[A-Za-z0-9](?:[A-Za-z0-9._\-]*[A-Za-z0-9])?)\s*(?:\[[^\]]*\])?\s*(?:$|[<>=!~;,@(\s])", RegexOptions.None, MatchTimeout);
        return m.Success ? m.Groups["name"].Value : null;
    }

    /// <summary>A Python project name as PyPI compares it (PEP 503): lower case, runs of <c>-_.</c> as one <c>-</c>.</summary>
    internal static string NormalizedPythonName(string name) =>
        Regex.Replace(name, @"[-_.]+", "-", RegexOptions.None, MatchTimeout).ToLowerInvariant();
}

/// <summary>The dependencies a manifest declares: <c>*.csproj</c> and the central <c>Directory.Packages.props</c>,
/// <c>package.json</c>, <c>requirements*.txt</c> and <c>pyproject.toml</c>.</summary>
/// <remarks>
/// ⚠ Only the dependency sections, and only what a registry serves: a project's own name, its scripts, engines or
/// settings, a path, a link, a workspace member, a git spec or an MSBuild expression is not a package to ask for.
/// </remarks>
internal static class PackageManifests
{
    private static readonly TimeSpan MatchTimeout = RegexBudget.Default;

    /// <summary>The package manager whose dependencies <paramref name="fileName"/> declares; <c>null</c> for any other file.</summary>
    internal static PackageEcosystem? EcosystemOf(string fileName) =>
        fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ? PackageEcosystem.NuGet
        : fileName.Equals("package.json", StringComparison.OrdinalIgnoreCase) ? PackageEcosystem.Npm
        : fileName.StartsWith("requirements", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ? PackageEcosystem.PyPI
        : null;

    /// <summary>A file that says where packages come from: a call that writes one changes the sources it would be checked against.</summary>
    internal static bool IsSourceConfiguration(string fileName) =>
        fileName.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
        || fileName is ".npmrc" or ".yarnrc" or ".yarnrc.yml" or "pip.conf" or "pip.ini" or "uv.toml" or "poetry.toml" or "pdm.toml";

    /// <summary>The manifest <paramref name="text"/> declares a package source of its own (an index, a restore source).</summary>
    internal static bool DeclaresSource(string fileName, string text) => EcosystemOf(fileName) switch
    {
        PackageEcosystem.NuGet => Regex.IsMatch(text, @"<\s*Restore(Additional\w*)?Sources\b", RegexOptions.IgnoreCase, MatchTimeout),
        PackageEcosystem.PyPI  => Regex.IsMatch(text, @"(?m)^\s*(-i\b|--index-url\b|--extra-index-url\b|-f\b|--find-links\b|--no-index\b)|^\s*\[\[?tool\.(uv\.index|poetry\.source|pdm\.source)\]?\]|^\s*(extra[-_])?index[-_]url\s*=", RegexOptions.None, MatchTimeout),
        _                      => false,
    };

    /// <summary>The package names <paramref name="text"/>, the whole content of the manifest <paramref name="fileName"/>, depends on.</summary>
    internal static IEnumerable<string> Names(string fileName, string text) => EcosystemOf(fileName) switch
    {
        PackageEcosystem.NuGet => NuGetNames(text),
        PackageEcosystem.Npm   => NpmNames(text),
        PackageEcosystem.PyPI  => fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ? PyprojectNames(text) : RequirementsNames(text),
        _                      => [],
    };

    /// <summary>The names the project itself provides in <paramref name="text"/>: a <c>pyproject.toml</c>'s own name and the
    /// packages its <c>[tool.uv.sources]</c> serves from elsewhere.</summary>
    internal static IReadOnlyCollection<string> ProvidedNames(string fileName, string text)
    {
        var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!fileName.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase)) return provided;
        foreach (var (section, line) in TomlLines(text))
        {
            if (section is "project" or "tool.poetry" && Regex.Match(line, @"^name\s*=\s*[""'](?<n>[^""']+)[""']", RegexOptions.None, MatchTimeout) is { Success: true } own)
                provided.Add(PackageCommands.NormalizedPythonName(own.Groups["n"].Value));
            if (section == "tool.uv.sources" && Regex.Match(line, @"^[""']?(?<n>[A-Za-z0-9][A-Za-z0-9._\-]*)[""']?\s*=", RegexOptions.None, MatchTimeout) is { Success: true } src)
                provided.Add(PackageCommands.NormalizedPythonName(src.Groups["n"].Value));
            if (section.StartsWith("tool.uv.sources.", StringComparison.Ordinal))
                provided.Add(PackageCommands.NormalizedPythonName(section["tool.uv.sources.".Length..].Trim('"', '\'')));
        }
        return provided;
    }

    private static IEnumerable<string> NuGetNames(string text)
    {
        foreach (Match m in Regex.Matches(text, @"<Package(?:Reference|Version)\b[^>]*?\bInclude\s*=\s*""(?<id>[^""]+)""", RegexOptions.None, MatchTimeout))
            foreach (var id in m.Groups["id"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!id.Contains("$(", StringComparison.Ordinal) && !id.Contains("@(", StringComparison.Ordinal) && !id.Contains("%(", StringComparison.Ordinal))
                    yield return id;
    }

    private static readonly string[] NpmSections = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];

    private static List<string> NpmNames(string text)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return names;
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return names;
        foreach (var section in NpmSections)
        {
            if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var dep in deps.EnumerateObject())
            {
                if (dep.Value.ValueKind != JsonValueKind.String || dep.Value.GetString() is not { } spec) continue;
                // An alias installs the package after "npm:"; a value that is not a version, a range or a tag (a path, a
                // link, a workspace, a git or GitHub spec) comes from no registry.
                var name = spec.StartsWith("npm:", StringComparison.Ordinal) ? PackageCommands.NpmSpecName(spec[4..])
                         : Regex.IsMatch(spec, @"^[\w.\-^~<>=|*+ ]*$", RegexOptions.None, MatchTimeout) && !spec.StartsWith('.') ? PackageCommands.NpmSpecName(dep.Name)
                         : null;
                if (name is not null) names.Add(name);
            }
        }
        return names;
    }

    private static IEnumerable<string> RequirementsNames(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split(" #")[0].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('-')) continue;
            if (PackageCommands.PythonRequirementName(line) is { } name) yield return name;
        }
    }

    /// <summary>The names in <c>[project] dependencies</c>, <c>[project.optional-dependencies]</c>, <c>[dependency-groups]</c>
    /// and Poetry's dependency tables — never an inline table (<c>{ include-group = "test" }</c>, a path or git source).</summary>
    private static List<string> PyprojectNames(string text)
    {
        var names = new List<string>();
        var inArray = false;
        foreach (var (section, line) in TomlLines(text))
        {
            var opens = section == "project"
                ? Regex.IsMatch(line, @"^dependencies\s*=\s*\[", RegexOptions.None, MatchTimeout)
                : section is "project.optional-dependencies" or "dependency-groups" && Regex.IsMatch(line, @"^[\w.\-""']+\s*=\s*\[", RegexOptions.None, MatchTimeout);
            if (opens || inArray)
            {
                var body = opens ? line[(line.IndexOf('[') + 1)..] : line;
                var withoutTables = Regex.Replace(body, @"\{[^}]*\}", "", RegexOptions.None, MatchTimeout);
                foreach (Match q in Regex.Matches(withoutTables, @"""(?<r>[^""]+)""|'(?<r>[^']+)'", RegexOptions.None, MatchTimeout))
                    if (PackageCommands.PythonRequirementName(q.Groups["r"].Value) is { } name) names.Add(name);
                // The array ends at a bracket outside its strings: "requests[socks]" does not close it.
                inArray = !Regex.Replace(withoutTables, @"""[^""]*""|'[^']*'", "", RegexOptions.None, MatchTimeout).Contains(']');
                continue;
            }

            var poetry = section is "tool.poetry.dependencies" or "tool.poetry.dev-dependencies"
                      || Regex.IsMatch(section, @"^tool\.poetry\.group\.[^.]+\.dependencies$", RegexOptions.None, MatchTimeout);
            if (poetry && Regex.Match(line, @"^(?<n>[A-Za-z0-9][A-Za-z0-9._\-]*)\s*=", RegexOptions.None, MatchTimeout) is { Success: true } k
                && !k.Groups["n"].Value.Equals("python", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(line, @"\b(path|git|url|develop)\s*=", RegexOptions.None, MatchTimeout))
                names.Add(k.Groups["n"].Value);
        }
        return names;
    }

    /// <summary>Each trimmed line of a TOML text with the table it sits in; table headers themselves are not returned.</summary>
    private static IEnumerable<(string Section, string Line)> TomlLines(string text)
    {
        var section = string.Empty;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Split(" #")[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && !line.Contains('='))
            {
                section = line.Trim('[', ']').Trim();
                continue;
            }
            yield return (section, line);
        }
    }
}
