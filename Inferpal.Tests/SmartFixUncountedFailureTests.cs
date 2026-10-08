using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Smart Fix gives a NUMBER of compilation errors only where it can count them (the .NET compiler's error lines); a
/// failed build of another toolchain is said failed, with its output, and no number.
/// </summary>
/// <remarks>
/// The count was the number of lines kept from the output: lines that say "error", else every line. One Rust error
/// prints three such lines ("error[E0425]", "For more information about this error", "could not compile … due to 1
/// previous error"), a Go build its package header: "3 compilation error(s)" for one, after every write. A .NET build
/// that failed without an error line ("Build FAILED.", "MSBuild node crashed") read "2 compilation error(s)".
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class SmartFixUncountedFailureTests
{
    private const string OneRustError =
        "error[E0425]: cannot find value `y` in this scope\n" +
        " --> src/main.rs:2:13\n" +
        "  |\n" +
        "2 |     let x = y;\n" +
        "  |             ^ not found in this scope\n" +
        "\n" +
        "For more information about this error, try `rustc --explain E0425`.\n" +
        "error: could not compile `demo` (bin \"demo\") due to 1 previous error\n";

    private const string OneGoError = "# example.com/demo\n./main.go:5:2: undefined: x\n";

    private static string FailedHead() => Strings.SmartFixBuildFailed(string.Empty).Split('\n')[0].TrimEnd();

    [Theory]
    [InlineData(101, OneRustError)]
    [InlineData(1, OneGoError)]
    public void AnotherToolchainsFailure_IsSaidFailed_WithItsOutput_AndNoNumber(int exitCode, string output)
    {
        try
        {
            Strings.ApplyLanguage("en");
            var note = SmartFixValidator.Interpret(exitCode, output, dotnetFilter: false)!;

            Assert.StartsWith(FailedHead(), note);
            Assert.DoesNotContain("compilation error(s)", note);
            Assert.Contains(output.Split('\n')[0].Trim(), note);                      // the output, to fix from
            Assert.True(SmartFixValidator.ReadVerdict(note));                         // still a red build for the turn's end
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void ADotnetBuildThatFailedWithoutAnErrorLine_IsNotCountedEither()
    {
        var note = SmartFixValidator.Interpret(1, "Build FAILED.\nMSBuild node crashed", dotnetFilter: true)!;

        Assert.Equal(Strings.SmartFixBuildFailed("Build FAILED.\nMSBuild node crashed"), note);
        Assert.True(SmartFixValidator.ReadVerdict(note));
    }

    [Fact]
    public void TheDotnetCompilersErrors_AreCounted()
    {
        // Reference arm: two distinct error lines are two errors.
        const string output =
            "Program.cs(3,5): error CS0103: The name 'y' does not exist in the current context [App.csproj]\n" +
            "Program.cs(4,5): error CS0103: The name 'z' does not exist in the current context [App.csproj]\n" +
            "Program.cs(3,5): error CS0103: The name 'y' does not exist in the current context [App.csproj]\n" +
            "Build FAILED.\n";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true)!;

        Assert.StartsWith(Strings.SmartFixBuildErrors(2, string.Empty).Split('\n')[0], note);
        Assert.True(SmartFixValidator.ReadVerdict(note));
    }
}
