using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;
using static Inferpal.Services.Tools.GetDiagnosticsTool;

namespace Inferpal.Tests;

/// <summary>
/// What <c>get_diagnostics</c> says a build did is read by the end-of-turn notice, the result bar and <c>/fix-build</c>.
/// It counted only the lines that carry a code (<c>error CS1061:</c>): a build that failed on an MSBuild
/// <c>&lt;Error Text="…"/&gt;</c> — no code — next to one warning came out "0 error(s), 1 warning(s)", read Clean,
/// while dotnet exited 1. And an error of the command line (MSB1003, no project in the folder) was counted as a
/// compilation error, on code that was never compiled.
/// </summary>
public class DiagnosticsVerdictExitCodeTests
{
    // The shape the .NET SDK prints for an <Error Text="..."/> without a Code, measured.
    private const string CodeLessError = @"C:\ws\X.csproj(5,5): error : the SDK version is too old [C:\ws\X.csproj]";
    private const string AWarning      = @"C:\ws\A.cs(3,13): warning CS0168: The variable 'e' is declared but never used [C:\ws\X.csproj]";

    private static string Run(int exitCode, string stdout) =>
        Interpret(new ChildProcessResult(exitCode, stdout, string.Empty, TimedOut: false), "X.csproj", 90);

    [Fact]
    public void AnErrorWithoutACode_IsAnError()
    {
        var said = Run(1, CodeLessError + "\n" + AWarning + "\n");

        Assert.Equal(BuildVerdict.Errors, ReadVerdict(said));
        Assert.Contains("the SDK version is too old", said);
    }

    [Fact]
    public void AFailedBuild_IsNeverClean_EvenWhenItsErrorsAreUnreadable()
    {
        // dotnet exited 1 and printed one warning and no error line this tool can read.
        var said = Run(1, AWarning + "\nsomething went wrong in a custom task\n");

        Assert.NotEqual(BuildVerdict.Clean, ReadVerdict(said));
    }

    [Fact]
    public void ACommandLineError_IsNothingCompiled_NotAnErrorInTheCode()
    {
        var said = Run(1, "MSBUILD : error MSB1003: Specify a project or solution file. The current working directory does not contain a project or solution file.\n");

        Assert.Equal(BuildVerdict.NotBuilt, ReadVerdict(said));
        Assert.Contains("nothing was compiled", said);
        Assert.Contains("MSB1003", said);
    }

    [Fact]
    public void ASucceededBuildWithWarnings_IsClean()
    {
        // Reference arm.
        Assert.Equal(BuildVerdict.Clean, ReadVerdict(Run(0, AWarning + "\n")));
    }
}
