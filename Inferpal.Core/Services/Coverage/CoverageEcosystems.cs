using Inferpal.Localization;

namespace Inferpal.Services.Coverage;

/// <summary>
/// Which ecosystem a folder's tests are, and the coverage tool it HAS — never one installed behind the person's back:
/// a missing tool is named, with the command that adds it.
/// </summary>
internal static class CoverageEcosystems
{
    /// <summary>The ecosystem of <paramref name="folder"/> (relative to <paramref name="root"/>, empty for the whole
    /// workspace), or why there is none to measure with.</summary>
    public static async Task<(ICoverageEcosystem? Ecosystem, string? Notice)> DetectAsync(string root, string folder, CancellationToken ct)
    {
        var start  = folder.Length == 0 ? root : Path.Combine(root, folder);
        var runner = Tools.RunTestsTool.DetectRunner(start, folder.Length == 0 ? null : start, root);
        // ⚠ A folder or file inside a .NET project: run_tests looks for projects below a folder, never above it, and a
        // package.json at the root (a tool's, a front end's) would answer "npm".
        if (runner != "dotnet" && folder.Length > 0 && NearestMarkerIsDotnet(start, root)) runner = "dotnet";
        return runner switch
        {
            "dotnet" => Dotnet(root),
            "pytest" => await PythonAsync(root, folder, start, ct),
            "npm"    => Node(root, folder),
            _        => (null, Strings.CoverageRunnerUnsupported(runner == "unknown" ? "?" : runner)),
        };
    }

    /// <remarks>⚠ The suite of the whole workspace, whatever folder was asked for: a source's tests live in another
    /// project, so <c>dotnet test</c> on the source's folder runs none of them (or none at all: MSB1003). The folder only
    /// narrows the targets.</remarks>
    private static (ICoverageEcosystem?, string?) Dotnet(string root)
    {
        var projects = WorkspaceScan.EnumerateFiles(root, "*.csproj")
                                    .Where(p => DotnetCoverage.IsTestProject(ReadOrEmpty(p))).ToList();
        if (projects.Count == 0) return (null, Strings.CoverageNoTestProject);
        var target = Tools.RunTestsTool.DotnetFolderTarget(root)?.Target ?? root;
        if (projects.Any(DotnetCoverage.HasCollector)) return (new DotnetCoverage(root, target, projects, null), null);
        // The tool installed for the user (`dotnet tool install -g`), found on the PATH.
        if (Shell.ShellLauncher.FindOnPath(OperatingSystem.IsWindows() ? "dotnet-coverage.exe" : "dotnet-coverage") is { } tool)
            return (new DotnetCoverage(root, target, projects, tool), null);
        var project = Path.GetRelativePath(root, projects[0]).Replace('\\', '/');
        return (null, Strings.CoverageToolMissing("coverlet.collector", $"dotnet add {project} package coverlet.collector"));
    }

    private static async Task<(ICoverageEcosystem?, string?)> PythonAsync(string root, string folder, string start, CancellationToken ct)
    {
        var python = Tools.RunTestsTool.ResolvePython(start, root, OperatingSystem.IsWindows(), Shell.ShellLauncher.FindOnPath,
                                                      File.Exists, Environment.GetEnvironmentVariable("VIRTUAL_ENV"));
        var (code, _, _) = await CoverageTools.RunAsync(python, ["-c", "import coverage, pytest"], root, TimeSpan.FromSeconds(60), ct);
        if (code != 0) return (null, Strings.CoverageToolMissing("coverage.py", $"{python} -m pip install coverage pytest"));
        // The plugins a project's addopts can turn on, which the measure must turn off: printed by name when installed.
        var (_, plugins, _) = await CoverageTools.RunAsync(
            python, ["-c", "import importlib.util as u; print(*[m for m in ('pytest_cov', 'xdist') if u.find_spec(m)])"],
            root, TimeSpan.FromSeconds(60), ct);
        var found = plugins.Split((char[])[' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return (new PytestCoverage(root, folder, python, pytestCov: found.Contains("pytest_cov"), xdist: found.Contains("xdist")), null);
    }

    private static (ICoverageEcosystem?, string?) Node(string root, string folder)
    {
        var runner = NodeCoverage.RunnerOf(ReadOrEmpty(Path.Combine(root, "package.json")));
        if (runner is null) return (null, Strings.CoverageRunnerUnsupported("npm"));
        var modules = Path.Combine(root, "node_modules");
        switch (runner)
        {
            case NodeRunner.Vitest:
            {
                var script = Path.Combine(modules, "vitest", "vitest.mjs");
                if (!File.Exists(script)) return (null, Strings.CoverageNodeModulesMissing("npm install"));
                var provider = Directory.Exists(Path.Combine(modules, "@vitest", "coverage-v8")) ? "v8"
                             : Directory.Exists(Path.Combine(modules, "@vitest", "coverage-istanbul")) ? "istanbul" : null;
                return provider is null
                    ? (null, Strings.CoverageToolMissing("@vitest/coverage-v8", $"npm install --save-dev @vitest/coverage-v8@{VersionOf(modules, "vitest")}"))
                    : (new NodeCoverage(root, folder, NodeRunner.Vitest, script, provider), null);
            }
            case NodeRunner.Jest:
            {
                var script = Path.Combine(modules, "jest", "bin", "jest.js");
                return File.Exists(script)
                    ? (new NodeCoverage(root, folder, NodeRunner.Jest, script, null), null)
                    : (null, Strings.CoverageNodeModulesMissing("npm install"));
            }
            default:
            {
                var script = Path.Combine(modules, "c8", "bin", "c8.js");
                return File.Exists(script)
                    ? (new NodeCoverage(root, folder, NodeRunner.NodeTest, script, null), null)
                    : (null, Strings.CoverageToolMissing("c8", "npm install --save-dev c8"));
            }
        }
    }

    /// <summary>Whether the nearest project marker at or above <paramref name="start"/>, up to the root, is a .NET project
    /// or solution — rather than a package.json or a Python project's file.</summary>
    internal static bool NearestMarkerIsDotnet(string start, string root)
    {
        for (var dir = Directory.Exists(start) ? start : Path.GetDirectoryName(start); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (!Tools.PathSanitizer.IsUnderRoot(dir, root) && !PathComparer.Default.Equals(dir, root)) return false;
            try
            {
                var names = Directory.EnumerateFiles(dir).Select(Path.GetFileName).ToList();
                if (names.Any(n => n!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || SolutionFiles.IsSolution(n!)))
                    return true;
                if (names.Any(n => n is "package.json" or "pyproject.toml" or "setup.py" or "setup.cfg" or "pytest.ini" or "tox.ini"))
                    return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            if (PathComparer.Default.Equals(dir, root)) return false;
        }
        return false;
    }

    /// <summary>The version a package in <c>node_modules</c> is at, for the provider that matches it ("latest" when it
    /// cannot be read).</summary>
    private static string VersionOf(string modules, string package)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(ReadOrEmpty(Path.Combine(modules, package, "package.json")));
            return doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } version ? version : "latest";
        }
        catch (System.Text.Json.JsonException) { return "latest"; }
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return string.Empty; }
    }
}
