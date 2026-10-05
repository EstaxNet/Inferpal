using System.Text.Json;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ <c>run_tests</c> puts its notes ABOVE the report (a note qualifies what follows): a 'timeout_seconds' out of
/// range, a path its runner cannot narrow to, a filter Node did not apply. The verdict readers read the FIRST line —
/// so a green run under a note read as red (<c>/tdd</c> then spent five fix rounds on a suite that passed), and a red
/// one under a note read as "no verdict" (the end-of-turn notice stayed silent under "all tests pass"). The notes come
/// from the real producers here; <see cref="RunTestsTool.WithoutNotes"/> is the one reader that skips them.
/// </summary>
public class RunTestsNoteAboveVerdictTests
{
    private const string Green = "✓ PASSED — 3 test(s) passed.";
    private const string Red   = "✗ FAILED — 1 of 3 test(s) failed:\n  Failed CalcTests.Adds [2 ms]";

    private static string TimeoutNote()
    {
        using var doc = JsonDocument.Parse("""{"timeout_seconds": 5000}""");
        var (_, notice) = ClampedArgument.Read(doc.RootElement, "timeout_seconds", 600, 1, 1800);
        Assert.NotNull(notice);
        return notice!;
    }

    private static string FilterNote()
    {
        var plan = RunTestsTool.NpmFilter("node --test test/calc.test.js", "adds", null, nodeOptionsTakeTestFlags: false);
        Assert.NotNull(plan.Note);
        return plan.Note!;
    }

    private static string PathNote() =>
        RunTestsTool.PathDoesNotNarrow("npm", "test/calc.test.js", "C:/repo") ?? throw new InvalidOperationException();

    private static string Above(string kind, string report) => kind switch
    {
        "timeout"      => ClampedArgument.Above(TimeoutNote(), report),
        "filter"       => FilterNote() + report,
        "path"         => PathNote() + report,
        "timeout+path" => ClampedArgument.Above(TimeoutNote(), PathNote() + report),
        _              => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("timeout")]
    [InlineData("filter")]
    [InlineData("path")]
    [InlineData("timeout+path")]
    public void AGreenRun_UnderANote_IsStillGreen(string kind)
    {
        var report = Above(kind, Green);
        Assert.True(TddCommandHandler.TestsPassed(report), report);
        Assert.False(TddCommandHandler.TestsFailed(report), report);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("filter")]
    [InlineData("path")]
    [InlineData("timeout+path")]
    public void ARedRun_UnderANote_IsStillRed(string kind)
    {
        var report = Above(kind, Red);
        Assert.True(TddCommandHandler.TestsFailed(report), report);
        Assert.False(TddCommandHandler.TestsPassed(report), report);
    }

    [Fact]
    public void ARunWhereNothingRan_IsNeverTakenForANote()   // reference arm: its own ⚠ line is the verdict
    {
        var report = ClampedArgument.Above(TimeoutNote(), RunTestsTool.NoTestFound + "\n\n  ✓ some raw line");
        Assert.False(TddCommandHandler.TestsPassed(report));
        Assert.False(TddCommandHandler.TestsFailed(report));
        Assert.True(TddCommandHandler.NothingRan(report));
    }
}
