using System.IO;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// Pure, testable resolution of the conventional unit-test file path for a source file.
/// <para>
/// The idiomatic name of the detected language (<c>Foo.cs → FooTests.cs</c>, <c>foo.py → test_foo.py</c>,
/// <c>foo.ts → foo.test.ts</c>, <c>foo.go → foo_test.go</c>, <c>Foo.java → FooTest.java</c>…), where that language
/// keeps its tests: the C# test project that references the source's project, Maven's <c>src/test</c> tree, the
/// folder pytest collects, and next to the source otherwise.
/// </para>
/// <para>
/// If the source file already looks like a test file, its own path is returned so <c>/test</c>
/// extends it in place rather than creating a <c>FooTestsTests</c> sibling.
/// </para>
/// </summary>
internal static class TestFilePathResolver
{
    /// <summary>Returns the absolute path of the test file for <paramref name="sourcePath"/>.</summary>
    public static string Resolve(string sourcePath)
    {
        var dir  = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var ext  = Path.GetExtension(sourcePath);                  // ".cs"
        var name = Path.GetFileNameWithoutExtension(sourcePath);   // "Foo" (or "foo.test" for foo.test.ts)

        // Already a test file → edit it in place.
        if (IsTestFileName(name))
            return sourcePath;

        var testFile = ext.ToLowerInvariant() switch
        {
            ".cs" or ".swift"                              => $"{name}Tests{ext}",
            ".java" or ".kt" or ".php"                     => $"{name}Test{ext}",
            ".go" or ".rb" or ".rs"                        => $"{name}_test{ext}",
            ".py"                                          => $"test_{name}{ext}",
            ".ts" or ".tsx" or ".js" or ".jsx"
                or ".mjs" or ".cjs"                        => $"{name}.test{ext}",
            _                                              => $"{name}Tests{ext}",
        };

        var besideTheSource = Path.Combine(dir, testFile);
        return ext.ToLowerInvariant() switch
        {
            ".cs"             => InDotnetTestProject(sourcePath, testFile) ?? besideTheSource,
            ".java" or ".kt"  => InMavenTestTree(sourcePath, testFile) ?? besideTheSource,
            ".py"             => InPythonTestTree(sourcePath, testFile) ?? besideTheSource,
            _                 => besideTheSource,
        };
    }

    private static readonly string[] PythonProjectFiles = ["pyproject.toml", "pytest.ini", "setup.cfg", "tox.ini", "setup.py"];

    /// <summary>
    /// Where pytest collects: the first folder its configuration names (<c>testpaths</c>), else a <c>tests</c> or
    /// <c>test</c> folder at the project root — an existing test file of that name in it first. <c>null</c> when the
    /// project names none (pytest then looks everywhere, beside the source included).
    /// </summary>
    /// <remarks>
    /// ⚠ A project that names its test folder never collects a test written beside the source: the new file silently
    /// never runs, and <c>/tdd</c> on it answers "no test matched".
    /// </remarks>
    private static string? InPythonTestTree(string sourcePath, string testFile)
    {
        try
        {
            var marker = FindUp(Path.GetDirectoryName(sourcePath),
                                f => PythonProjectFiles.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase), levels: 8);
            if (marker is null) return null;
            var projectRoot = Path.GetDirectoryName(marker)!;

            var testDir = PytestTestpaths(projectRoot)
                .Select(p => Path.GetFullPath(Path.Combine(projectRoot, p)))
                .FirstOrDefault(Directory.Exists)
                ?? new[] { "tests", "test" }.Select(d => Path.Combine(projectRoot, d)).FirstOrDefault(Directory.Exists);
            if (testDir is null) return null;

            var existing = WorkspaceScan.EnumerateFiles(testDir, testFile)
                .FirstOrDefault(f => PathComparer.Default.Equals(Path.GetFileName(f), testFile));
            return existing ?? Path.Combine(testDir, testFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("TestFilePathResolver.InPythonTestTree", ex);
            return null;
        }
    }

    /// <summary>
    /// The <c>testpaths</c> pytest reads, from the first config file that sets them, in pytest's own order of precedence:
    /// <c>pytest.ini</c>, <c>pyproject.toml</c> (<c>[tool.pytest.ini_options]</c>), <c>tox.ini</c>, <c>setup.cfg</c>
    /// (<c>[tool:pytest]</c>).
    /// </summary>
    private static IReadOnlyList<string> PytestTestpaths(string projectRoot)
    {
        foreach (var (file, section) in (ReadOnlySpan<(string, string)>)
                 [("pytest.ini", "[pytest]"), ("pyproject.toml", "[tool.pytest.ini_options]"),
                  ("tox.ini", "[pytest]"), ("setup.cfg", "[tool:pytest]")])
        {
            var path = Path.Combine(projectRoot, file);
            if (!File.Exists(path)) continue;
            var inSection = false;
            foreach (var raw in Tools.TextFileEncoding.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inSection = line.Equals(section, StringComparison.OrdinalIgnoreCase); continue; }
                if (!inSection || !line.StartsWith("testpaths", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                // `testpaths = tests integration` (ini) or `testpaths = ["tests", "integration"]` (toml).
                return line[(eq + 1)..].Split([' ', '\t', ',', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries);
            }
        }
        return [];
    }

    // ── Where the language puts its tests ─────────────────────────────────────────
    //
    // ⚠ "Next to the source" is right for JS/TS and Go, whose runners find a test anywhere — and for Python only when
    // pytest looks everywhere (see InPythonTestTree). In C# and under
    // Maven it is the PRODUCTION project/source set, which references no test framework: the new FooTests.cs is
    // compiled into it and the build breaks ("'Fact' could not be found") — and the FooTests.cs that already exists
    // in the test project is never found, so a second one is created instead of that one being extended.

    private static readonly string[] TestFrameworkMarkers = ["xunit", "nunit", "mstest", "Microsoft.NET.Test.Sdk"];

    /// <summary>
    /// The test file inside the test project that references the source's project: an existing one of that name,
    /// else the source's folder mirrored there. <c>null</c> when there is no such project, or the source already lives
    /// in a test project.
    /// </summary>
    private static string? InDotnetTestProject(string sourcePath, string testFile)
    {
        try
        {
            var project = FindUp(Path.GetDirectoryName(sourcePath), f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), levels: 8);
            if (project is null || IsTestProject(project)) return null;
            var projectDir = Path.GetDirectoryName(project)!;

            var root = FindUp(projectDir, SolutionFiles.IsSolution, levels: 6);
            var searchRoot = root is not null ? Path.GetDirectoryName(root)! : Path.GetDirectoryName(projectDir) ?? projectDir;

            var testProject = WorkspaceScan.EnumerateFiles(searchRoot, "*.csproj")
                .Where(p => !PathComparer.Default.Equals(p, project))
                .Where(p => IsTestProject(p) && References(p, project))
                .OrderByDescending(p => Path.GetFileNameWithoutExtension(p)
                    .StartsWith(Path.GetFileNameWithoutExtension(project), StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (testProject is null) return null;
            var testDir = Path.GetDirectoryName(testProject)!;

            // An existing test file of that name, anywhere in the test project, is the one to extend.
            var existing = WorkspaceScan.EnumerateFiles(testDir, testFile)
                .FirstOrDefault(f => PathComparer.Default.Equals(Path.GetFileName(f), testFile));
            if (existing is not null) return existing;

            var relativeDir = Path.GetRelativePath(projectDir, Path.GetDirectoryName(sourcePath)!);
            return Path.GetFullPath(Path.Combine(testDir, relativeDir == "." ? string.Empty : relativeDir, testFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("TestFilePathResolver.InDotnetTestProject", ex);
            return null;
        }
    }

    /// <summary>Maven and Gradle: <c>src/main/java/…/Foo.java</c> is tested in <c>src/test/java/…/FooTest.java</c>.</summary>
    private static string? InMavenTestTree(string sourcePath, string testFile)
    {
        var sep  = Path.DirectorySeparatorChar;
        var dir  = Path.GetDirectoryName(sourcePath) + sep;
        foreach (var language in (string[])["java", "kotlin"])
        {
            var main = $"{sep}src{sep}main{sep}{language}{sep}";
            var at   = dir.LastIndexOf(main, StringComparison.Ordinal);
            if (at >= 0)
                return Path.Combine(dir[..at] + $"{sep}src{sep}test{sep}{language}{sep}" + dir[(at + main.Length)..], testFile);
        }
        return null;
    }

    /// <summary>
    /// The test framework the project around <paramref name="testPath"/> uses — read from its <c>.csproj</c>, or from the
    /// <c>test</c> script of its <c>package.json</c> — or <c>null</c> when nothing says.
    /// </summary>
    /// <remarks>
    /// ⚠ Asked to "infer the framework from the source", a new test file gets none — production code names no test
    /// framework: the model writes xUnit in an NUnit or MSTest project, Jest in a Vitest one, and the file does not
    /// compile or run there.
    /// </remarks>
    internal static string? FrameworkFor(string testPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(testPath);
            if (Path.GetExtension(testPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var csproj = FindUp(dir, f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase), levels: 8);
                if (csproj is null) return null;
                var text = File.ReadAllText(csproj);
                return text.Contains("nunit",  StringComparison.OrdinalIgnoreCase) ? "NUnit"
                     : text.Contains("mstest", StringComparison.OrdinalIgnoreCase) ? "MSTest"
                     : text.Contains("xunit",  StringComparison.OrdinalIgnoreCase) ? "xUnit"
                     : null;
            }
            if (Path.GetExtension(testPath).ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs")
            {
                var package = FindUp(dir, f => Path.GetFileName(f).Equals("package.json", StringComparison.OrdinalIgnoreCase), levels: 8);
                var script  = package is null ? null : PackageJson.TestScript(Path.GetDirectoryName(package)!);
                return script is null ? null
                     : script.Contains("vitest", StringComparison.OrdinalIgnoreCase) ? "Vitest"
                     : script.Contains("jest",   StringComparison.OrdinalIgnoreCase) ? "Jest"
                     : script.Contains("mocha",  StringComparison.OrdinalIgnoreCase) ? "Mocha"
                     : System.Text.RegularExpressions.Regex.IsMatch(script, @"(?<!\S)--test(?!\S)",
                           System.Text.RegularExpressions.RegexOptions.None, RegexBudget.Default) ? "the node:test runner"
                     : null;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Swallow("TestFilePathResolver.FrameworkFor", ex);
            return null;
        }
    }

    private static bool IsTestProject(string csproj)
    {
        var text = File.ReadAllText(csproj);
        return TestFrameworkMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static bool References(string testCsproj, string projectCsproj) =>
        File.ReadAllText(testCsproj).Contains(Path.GetFileName(projectCsproj), StringComparison.OrdinalIgnoreCase);

    /// <summary>The first file <paramref name="matches"/> accepts in <paramref name="dir"/> or a parent.</summary>
    private static string? FindUp(string? dir, Func<string, bool> matches, int levels)
    {
        for (var i = 0; i < levels && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
        {
            if (!Directory.Exists(dir)) continue;
            var hit = Directory.EnumerateFiles(dir).FirstOrDefault(matches);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// True when <paramref name="nameWithoutExt"/> already follows a common test-file convention
    /// (<c>FooTests</c>, <c>FooTest</c>, <c>foo_test</c>, <c>foo_spec</c>, <c>test_foo</c>, <c>foo.test</c>, <c>foo.spec</c>).
    /// Case-sensitive for the <c>Test</c>/<c>Tests</c> suffix: "CalculatorTests" is the convention, "Contest" is a word.
    /// </summary>
    public static bool IsTestFileName(string nameWithoutExt)
    {
        var n = nameWithoutExt;
        return n.EndsWith("Tests", System.StringComparison.Ordinal)
            || n.EndsWith("Test",  System.StringComparison.Ordinal)
            || n.EndsWith("_test", System.StringComparison.OrdinalIgnoreCase)
            || n.EndsWith("_spec", System.StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("test_", System.StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".test", System.StringComparison.OrdinalIgnoreCase)
            || n.EndsWith(".spec", System.StringComparison.OrdinalIgnoreCase);
    }
}
