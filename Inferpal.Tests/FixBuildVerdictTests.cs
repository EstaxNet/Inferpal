using System.IO;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;
using BuildVerdict = Inferpal.Services.Tools.GetDiagnosticsTool.BuildVerdict;

namespace Inferpal.Tests;

/// <summary>
/// <c>/fix-build</c> announces a clean build only when <c>get_diagnostics</c> says a build ran and found no
/// error — never on the mere absence of an error line. A mistyped path, a missing project, a build that
/// died without parseable diagnostics or a refused path all used to read as "✅ Build succeeded".
/// </summary>
/// <remarks>Compares localized text built in this process: serialized with the culture switches.</remarks>
[Collection(CultureSerialCollection.Name)]
public class FixBuildVerdictTests
{
    [Theory]
    [InlineData("App.sln")]
    [InlineData("my (app) [v2].sln")]
    public void ASuccessfulBuild_IsClean(string file) =>
        Assert.Equal(BuildVerdict.Clean, GetDiagnosticsTool.ReadVerdict(Strings.DiagBuildOk(file)));

    [Fact]
    public void WarningsOnly_AreClean()
    {
        var output = Strings.DiagSummary(0, 1, "App.sln")
                   + "\n\nProgram.cs(3,5): warning CS0168: The variable 'x' is declared but never used";

        Assert.Equal(BuildVerdict.Clean, GetDiagnosticsTool.ReadVerdict(output));
    }

    [Fact]
    public void ErrorLines_AreErrors()
    {
        var output = Strings.DiagSummary(1, 0, "App.sln") + "\n\nProgram.cs(3,5): error CS1002: ; expected";

        Assert.Equal(BuildVerdict.Errors, GetDiagnosticsTool.ReadVerdict(output));
    }

    [Theory]
    [InlineData("file-not-found")]
    [InlineData("no-project")]
    [InlineData("build-failed-without-diagnostics")]
    [InlineData("tool-error")]
    [InlineData("error-message")]
    [InlineData("empty")]
    public void AnAnswerThatIsNotABuildResult_IsNotBuilt(string shape)
    {
        var output = shape switch
        {
            "file-not-found"                   => Strings.ToolFileNotFound(Path.Combine("proj", "Ap.sln")),
            "no-project"                       => Strings.DiagNoProject,
            "build-failed-without-diagnostics" => Strings.DiagBuildFailed(1, "Build FAILED."),
            // ⚠ The sentence comes from the PRODUCER, never copied: a copy at the reading end
            // matches until the day the writing end is reworded, then stops without a sign.
            "tool-error"                       => Services.Execution.ToolFailure.Describe(
                                                      "get_diagnostics",
                                                      new ArgumentException("The path is outside the workspace root.")),
            "error-message"                    => Strings.MsgError("boom"),
            _                                  => "",
        };

        Assert.Equal(BuildVerdict.NotBuilt, GetDiagnosticsTool.ReadVerdict(output));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheFixBuildLoop_DecidesOnTheVerdict()
    {
        var vm = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.PromptHistory.cs"));

        // Witness: the loop still lives in this file.
        Assert.Contains("HandleFixBuildCommandAsync", vm);

        Assert.Contains("GetDiagnosticsTool.ReadVerdict(", vm);
        Assert.DoesNotContain("OutputHasBuildErrors(", vm);
    }
}
