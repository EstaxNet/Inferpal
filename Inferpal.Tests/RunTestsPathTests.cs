using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// run_tests promises "a project, a directory, or a test file" as <c>path</c>. A path that named nothing fell back to
/// the workspace root and ran the whole suite; a test file ran its whole folder — for pytest from that folder, where
/// the project's own package does not import ("1 error during collection", measured with pytest 9.1).
/// </summary>
public sealed class RunTestsPathTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-testpath-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<string> RunAsync(object args)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        return await new RunTestsTool(() => _root).ExecuteAsync(doc.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task APathThatNamesNothing_IsRefused_NeverTheWholeSuite()
    {
        File.WriteAllText(Path.Combine(_root, "pyproject.toml"), "[project]\nname = \"demo\"\n");

        var report = await RunAsync(new { path = "tests/test_typo.py" });

        Assert.StartsWith(RunTestsTool.PathNotFound, report);
        Assert.Contains("test_typo.py", report);
    }

    [Fact]
    public void ATestFile_RunsFromItsProjectsRoot_AndIsTheOnlyTarget()
    {
        var file = Path.Combine(_root, "tests", "test_add.py");
        var (cwd, args) = RunTestsTool.PytestInvocation(Path.GetDirectoryName(file)!, _root, file, null,
            exists: p => p == Path.Combine(_root, "pyproject.toml"));

        Assert.Equal(_root, cwd);
        Assert.Contains(file, args);
    }

    [Fact]
    public void WithoutAnyMarker_TheWorkspaceRootIsTheWorkingFolder()
    {
        var tests = Path.Combine(_root, "tests");
        var (cwd, args) = RunTestsTool.PytestInvocation(tests, _root, tests, null, exists: _ => false);

        Assert.Equal(_root, cwd);
        Assert.Contains(tests, args);
    }

    [Fact]
    public void ASubprojectWithItsOwnConfig_RunsFromThere()
    {
        var sub = Path.Combine(_root, "services", "api");
        var file = Path.Combine(sub, "tests", "test_api.py");
        var (cwd, _) = RunTestsTool.PytestInvocation(Path.GetDirectoryName(file)!, _root, file, null,
            exists: p => p == Path.Combine(sub, "pyproject.toml") || p == Path.Combine(_root, "pyproject.toml"));

        Assert.Equal(sub, cwd);
    }

    [Fact]
    public void WithoutAPath_TheWholeSuiteRuns_AndAFilterStaysOneArgument()
    {
        // Reference arm: no path, no target — and a filter with quotes and spaces is one argument, not a split string.
        var (cwd, args) = RunTestsTool.PytestInvocation(_root, _root, null, "name with \"quotes\"", exists: _ => false);

        Assert.Equal(_root, cwd);
        Assert.Equal(["-m", "pytest", "-v", "--tb=short", "-q", "-k", "name with \"quotes\""], args);
    }

    private static bool PythonIsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3", "--version")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact]
    public async Task APythonTestFile_BelowItsProject_IsRunByPytest_NotReportedAsNoRunner()
    {
        if (!PythonIsInstalled()) return;   // UNDECIDED without Python on the machine — never read as a pass
        using (var venv = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3",
                   $"-m venv --without-pip \"{Path.Combine(_root, ".venv")}\"") { UseShellExecute = false })!)
        {
            await venv.WaitForExitAsync();
            if (venv.ExitCode != 0) return;   // no venv module: UNDECIDED
        }
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "test_add.py"), "def test_add():\n    assert 1 + 1 == 2\n");

        var report = await RunAsync(new { path = "tests/test_add.py" });

        // The venv made --without-pip has no pytest: reaching that sentence proves pytest was the runner chosen.
        Assert.StartsWith(RunTestsTool.PytestNotInstalled, report);
        Assert.True(TddCommandHandler.NothingRan(report));
    }

    [Fact]
    public async Task ACSharpTestFile_RunsItsProject_NotMSB4025()
    {
        // `dotnet test Foo.cs` answers MSB4025, "the project file could not be loaded" (measured); and in a subfolder of
        // its project, the file found no runner at all. It now runs the project it belongs to, and says so.
        File.WriteAllText(Path.Combine(_root, "Demo.Tests.csproj"), "<Project />");
        Directory.CreateDirectory(Path.Combine(_root, "Services"));
        File.WriteAllText(Path.Combine(_root, "Services", "FooTests.cs"), "class FooTests { }");

        var report = await RunAsync(new { path = "Services/FooTests.cs" });

        Assert.StartsWith("Note: dotnet cannot run only", report);
        Assert.Contains("Demo.Tests.csproj", report.Split('\n')[0]);
        Assert.DoesNotContain("MSB4025", report);
    }

    [Fact]
    public async Task ATestFileGivenToNpm_SaysTheWholeSuiteRan()
    {
        if (!NpmTools.Installed()) return;   // UNDECIDED without npm on the machine — never read as a pass
        Directory.CreateDirectory(Path.Combine(_root, "test"));
        File.WriteAllText(Path.Combine(_root, "package.json"),
            """{ "name": "demo", "version": "1.0.0", "scripts": { "test": "node --test" } }""");
        File.WriteAllText(Path.Combine(_root, "test", "a.test.js"),
            "const test = require('node:test');\ntest('alpha', () => {});\n");

        var report = await RunAsync(new { path = "test/a.test.js" });

        Assert.StartsWith("Note: npm cannot run only", report);
        Assert.Contains("✓ PASSED", report);
    }
}
