using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// End to end: <c>run_tests</c> with a <c>path</c> asks pytest to count the whole suite after a green run, and puts the
/// "Not run:" note above its report. Played with a real interpreter and a <c>pytest</c> module of the test's own, written
/// where <c>python -m pytest</c> finds it first, that answers the run and <c>--collect-only</c> as pytest 9.2 does.
/// </summary>
public sealed class PytestPartOfTheSuiteRunTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-pytest-part-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string FakePytest = """
        import sys
        if "--collect-only" in sys.argv:
            print("tests/test_a.py::test_one\ntests/test_a.py::test_two\ntests/test_b.py::test_three\n\n3 tests collected in 0.01s")
            sys.exit(0)
        print("collected 2 items\n\ntests/test_a.py ..   [100%]\n\n============ 2 passed in 0.04s ============")
        sys.exit(0)
        """;

    private async Task<string> RunAsync(string path)
    {
        Assert.True(await PythonForTests.IsInstalledAsync(), "UNDECIDED: no Python on this machine");
        File.WriteAllText(Path.Combine(_root, "pytest.ini"), "[pytest]\n");
        File.WriteAllText(Path.Combine(_root, "pytest.py"), FakePytest);
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "test_a.py"), "def test_one():\n    assert 1\n");
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { runner = "pytest", path }));
        return await new RunTestsTool(() => _root).ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task AGreenRunOfOneFile_SaysTheOtherTestsDidNotRun()
    {
        var report = await RunAsync("tests/test_a.py");

        Assert.Contains("2 passed", report);                                   // witness: our stand-in ran
        Assert.Contains("ran: 2 of the 3 tests pytest collects. Not run: 1 other test", report);
        Assert.True(RunTestsTool.RanPartOfTheSuite(report, filter: null));
    }

    [Fact]
    public async Task AGreenRunOfTheProjectRoot_IsNotCompared()
    {
        var report = await RunAsync(".");

        Assert.Contains("2 passed", report);                                   // witness
        Assert.DoesNotContain("Not run:", report);
    }
}
