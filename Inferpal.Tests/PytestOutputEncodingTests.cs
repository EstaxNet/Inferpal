using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What pytest prints reaches the report as it was written — accents and symbols included — whatever the code page of
/// the machine.
/// </summary>
/// <remarks>
/// Under Windows, Python writes to a pipe in the ANSI code page (cp1252), and the runner read the pipe as UTF-8: an
/// assertion message "café" reached the model as "caf�", and a "✓" — outside cp1252 — raised UnicodeEncodeError in the
/// run itself. Played with a real interpreter and a <c>pytest</c> module of the test's own, written where
/// <c>python -m pytest</c> finds it first (pytest itself need not be installed).
/// </remarks>
public sealed class PytestOutputEncodingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-pytest-encoding-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>A stand-in for pytest that reports one failing test whose message is not ASCII.</summary>
    private const string FakePytest = """
        import sys
        print("test_menu.py::test_cafe FAILED")
        print("FAILED test_menu.py::test_cafe - AssertionError: café ✓ 東京")
        print("========================= 1 failed in 0.01s =========================")
        sys.exit(1)
        """;

    [Fact]
    public async Task AFailureMessageWithAccents_ReachesTheReportIntact()
    {
        Assert.True(await PythonForTests.IsInstalledAsync(), "UNDECIDED: no Python on this machine");
        File.WriteAllText(Path.Combine(_root, "pytest.ini"), "[pytest]\n");
        File.WriteAllText(Path.Combine(_root, "pytest.py"), FakePytest);

        using var args = JsonDocument.Parse("""{"runner":"pytest"}""");
        var report = await new RunTestsTool(() => _root).ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("1 failed", report);                                    // witness: our stand-in ran
        Assert.Contains("café ✓ 東京", report);
        Assert.DoesNotContain("UnicodeEncodeError", report);
    }

    [Fact]
    public void ThePytestRunner_DeclaresItsPipesUtf8_AndOnlyThem()
    {
        Assert.Equal("utf-8", RunTestsTool.PytestEnvironment["PYTHONIOENCODING"]);
        Assert.False(RunTestsTool.PytestEnvironment.ContainsKey("PYTHONUTF8"));   // the tests' own open() is theirs
    }
}
