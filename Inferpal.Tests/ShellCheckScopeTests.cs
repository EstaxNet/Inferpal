using Inferpal.Localization;
using Inferpal.Services.Agent;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test run through the shell that selects some tests — <c>dotnet test --filter</c>, <c>pytest -k</c>, <c>go test -run</c> —
/// passed for part of the suite only, and the result bar says so, as it does for <c>run_tests</c> with a filter. Only a
/// selector of the runner that ran counts; an unknown form keeps "tests passed".
/// </summary>
public sealed class ShellCheckScopeTests
{
    private static ToolExecution Shell(string command, string output = "Passed!  - Failed: 0, Passed: 1") =>
        new("run_command", System.Text.Json.JsonSerializer.Serialize(new { command }), output);

    [Theory]
    [InlineData("dotnet test Fix.sln --filter FullyQualifiedName~Test1")]
    [InlineData("cd src && dotnet test --filter:Category=Fast")]
    [InlineData("pytest -k total")]
    [InlineData("python -m pytest tests/test_cart.py")]
    [InlineData("pytest tests/test_cart.py::test_total")]
    [InlineData("pytest -m slow")]
    [InlineData("npx jest -t 'adds tax'")]
    [InlineData("npm test -- --testNamePattern=cart")]
    [InlineData("go test ./... -run TestTotal")]
    [InlineData("cargo test total")]
    [InlineData("npx mocha --grep cart")]
    [InlineData("dotnet build && dotnet test --no-build --filter X")]   // the test step is read, not the build before it
    [InlineData("cargo build && cargo test total")]
    [InlineData("pytest --lf")]
    [InlineData("pytest -ktotal")]
    [InlineData("npx jest src/cart.test.ts")]
    [InlineData("npx jest --testPathPatterns cart")]
    [InlineData("go test ./... -skip TestSlow")]
    public void ASelectorOfTheRunner_MakesItPartOfTheSuite(string command) =>
        Assert.True(CheckCommand.NarrowsTests(command));

    [Theory]
    [InlineData("dotnet test Fix.sln")]
    [InlineData("dotnet test -m:1")]                 // MSBuild's -m, not a pytest marker
    [InlineData("python -m pytest")]                 // python's -m starts pytest, it selects nothing
    [InlineData("python -m pytest tests/")]          // a folder may hold the whole suite
    [InlineData("npx jest")]
    [InlineData("go test ./...")]
    [InlineData("cargo test --release")]
    [InlineData("make test")]                        // a script whose content is unknown
    [InlineData("echo --filter && dotnet test")]     // the selector belongs to another step
    [InlineData("cargo test 2>&1")]                  // a redirection, not a positional filter
    [InlineData("pytest -W ignore::DeprecationWarning")]
    [InlineData("pytest -o python_files=*_test.py")]
    [InlineData("npx jest --testPathIgnorePatterns e2e")]
    public void WithoutASelector_ItIsTheWholeSuite(string command) =>
        Assert.False(CheckCommand.NarrowsTests(command));

    [Fact]
    public void TheBar_SaysPartOfTheSuite_ForAFilteredShellRun()
    {
        var summary = RunSummary.Build([Shell("dotnet test Fix.sln --filter FullyQualifiedName~Test1")], null)!;

        Assert.Equal(RunCheck.TestsPassedPart, summary.Check);
        Assert.Equal(Strings.RunTestsPassedPart, summary.CheckText);
    }

    [Fact]
    public void TheBar_SaysTestsPassed_ForTheWholeSuite_AndARedStaysRed()
    {
        Assert.Equal(RunCheck.TestsPassed, RunSummary.Build([Shell("dotnet test Fix.sln")], null)!.Check);
        Assert.Equal(RunCheck.TestsFailed,
            RunSummary.Build([Shell("dotnet test --filter X", "Failed!  - Failed: 1\n[exit code 1]")], null)!.Check);
    }
}
