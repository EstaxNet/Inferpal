using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The pytest runner launched "python" from the PATH. macOS and current Debian/Ubuntu have no "python" at all, only
/// python3; and where pytest lives — the project's virtual environment, since those systems refuse installs into the
/// system Python (PEP 668) — was never looked at: "No module named pytest", read by /tdd as a failing suite.
/// </summary>
public sealed class PytestInterpreterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-pytest-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    // Paths built for the HOST: a literal "C:\…" has no directory part under Linux, where this test runs too.
    private static string VenvPython(string dir, bool isWindows, string venv = ".venv") =>
        isWindows ? Path.Combine(dir, venv, "Scripts", "python.exe") : Path.Combine(dir, venv, "bin", "python");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProjectsVirtualEnvironment_IsUsedFirst(bool isWindows)
    {
        var project = Path.Combine(Path.GetTempPath(), "proj");
        var python = VenvPython(project, isWindows);

        Assert.Equal(python, RunTestsTool.ResolvePython(project, project, isWindows,
            onPath: _ => "/usr/bin/x", exists: p => p == python, virtualEnv: null));
    }

    [Fact]
    public void AVenvNamedVenv_IsFoundToo()
    {
        var project = Path.Combine(Path.GetTempPath(), "proj");
        var python = VenvPython(project, isWindows: false, venv: "venv");

        Assert.Equal(python, RunTestsTool.ResolvePython(project, project, isWindows: false,
            onPath: _ => null, exists: p => p == python, virtualEnv: null));
    }

    [Fact]
    public void AVenvAtTheWorkspaceRoot_ServesATestFolderBelowIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "proj");
        var python = VenvPython(root, isWindows: false);

        Assert.Equal(python, RunTestsTool.ResolvePython(Path.Combine(root, "tests", "unit"), root, isWindows: false,
            onPath: _ => null, exists: p => p == python, virtualEnv: null));
    }

    [Fact]
    public void AVenvAboveTheWorkspaceRoot_IsNotTheProjects()
    {
        var parent = Path.Combine(Path.GetTempPath(), "home");
        var root = Path.Combine(parent, "proj");
        var outside = VenvPython(parent, isWindows: false);

        Assert.Equal("python3", RunTestsTool.ResolvePython(root, root, isWindows: false,
            onPath: n => n == "python3" ? "/usr/bin/python3" : null, exists: p => p == outside, virtualEnv: null));
    }

    [Fact]
    public void AnActivatedEnvironment_IsUsedWhenTheProjectHasNone()
    {
        var project = Path.Combine(Path.GetTempPath(), "proj");
        var env = Path.Combine(Path.GetTempPath(), "envs", "tools");
        var python = Path.Combine(env, "bin", "python");   // VIRTUAL_ENV names the environment itself

        Assert.Equal(python, RunTestsTool.ResolvePython(project, project, isWindows: false,
            onPath: _ => "/usr/bin/python3", exists: p => p == python, virtualEnv: env));
    }

    [Theory]
    [InlineData("python3", "python3")]   // macOS, Debian, Ubuntu: no "python" at all
    [InlineData("python",  "python")]    // a system that has only the unversioned name
    [InlineData(null,      "python3")]   // neither: the start failure names the one that was tried
    public void OutsideWindows_Python3IsPreferred(string? onPathName, string expected)
    {
        Assert.Equal(expected, RunTestsTool.ResolvePython(_dir, _dir, isWindows: false,
            onPath: n => n == onPathName ? "/usr/bin/" + n : null, exists: _ => false, virtualEnv: null));
    }

    [Fact]
    public void OnWindows_WithoutAVenv_PythonIsLaunchedAsBefore()
    {
        Assert.Equal("python", RunTestsTool.ResolvePython(_dir, _dir, isWindows: true,
            onPath: _ => null, exists: _ => false, virtualEnv: null));
    }

    [Fact]
    public void PytestNotInstalled_IsNothingRan_NotAFailingSuite()
    {
        // The line Python prints — measured on Ubuntu 26.04 and on Windows.
        var report = RunTestsTool.ParsePytestOutput("/usr/bin/python3: No module named pytest\n", 1, "/usr/bin/python3");

        Assert.StartsWith(RunTestsTool.PytestNotInstalled, report);
        Assert.Contains("/usr/bin/python3", report);
        Assert.True(TddCommandHandler.NothingRan(report));
        Assert.False(TddCommandHandler.TestsPassed(report));
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
    public async Task APythonProject_RunsWithItsOwnVenv()
    {
        if (!PythonIsInstalled()) return;   // UNDECIDED without Python on the machine — never read as a pass
        using (var venv = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "python" : "python3",
                   $"-m venv --without-pip \"{Path.Combine(_dir, ".venv")}\"") { UseShellExecute = false })!)
        {
            await venv.WaitForExitAsync();
            if (venv.ExitCode != 0) return;   // no venv module (Debian without python3-venv): UNDECIDED
        }
        File.WriteAllText(Path.Combine(_dir, "pytest.ini"), "[pytest]\n");

        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _dir, runner = "pytest" }));
        var report = await new RunTestsTool(() => _dir).ExecuteAsync(args.RootElement, CancellationToken.None);

        // The venv made --without-pip has no pytest: the report names ITS interpreter, and says nothing ran.
        Assert.Contains(VenvPython(_dir, OperatingSystem.IsWindows()), report);
        Assert.True(TddCommandHandler.NothingRan(report), report);
    }
}
