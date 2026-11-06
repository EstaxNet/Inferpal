using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The build note Smart Fix appends after every write never says "build OK" about a build that failed.
/// The .NET branch only looked for <c>: error XX:</c> lines and ignored the exit code, so a build that
/// died without printing one — a 60 s timeout (exit -1), a crashed MSBuild node, a restore it could not
/// do — reached the model as a passing build.
/// </summary>
/// <remarks>Compares localized text built in this process: serialized with the culture switches.</remarks>
[Collection(CultureSerialCollection.Name)]
public class SmartFixInterpretTests
{
    [Fact]
    public void DotnetErrorLines_AreListed()
    {
        const string output = "Program.cs(3,5): error CS1002: ; expected [C:\\p\\App.csproj]\nBuild FAILED.";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true);

        Assert.Equal(
            Strings.SmartFixBuildErrors(1, "Program.cs(3,5): error CS1002: ; expected [C:\\p\\App.csproj]"),
            note);
    }

    [Fact]
    public void AProjectNeverRestored_IsNoCompilationError_AndNoVerdict()
    {
        const string output = @"C:\sdk\targets\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file "
                            + @"'C:\ws\obj\project.assets.json' not found. Run a NuGet package restore to generate this file. [C:\ws\App.csproj]";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true);

        Assert.Equal(Strings.SmartFixCouldNotRun(SmartFixValidator.NotRestoredCause), note);
        Assert.Null(SmartFixValidator.ReadVerdict(note!));   // neither green nor red: nothing was compiled
    }

    [Fact]
    public void ACommandLineDotnetRefused_IsNoCompilationError_AndNoVerdict()
    {
        const string output = "MSBUILD : error MSB1003: Specify a project or solution file. The current working directory does "
                            + "not contain a project or solution file.";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true);

        Assert.Equal(Strings.SmartFixCouldNotRun(SmartFixValidator.CommandLineRefusedCause), note);
        Assert.Null(SmartFixValidator.ReadVerdict(note!));
    }

    /// <summary>A bare <c>dotnet restore</c> at the root of two projects answers MSB1003: the remedy names what to restore.</summary>
    [Fact]
    public void TheRestoreRemedy_NamesATarget()
    {
        Assert.Contains("dotnet restore <project or solution>", Inferpal.Services.Tools.GetDiagnosticsTool.NotRestored);
        Assert.Contains("dotnet restore <project or solution>", SmartFixValidator.NotRestoredCause);
    }

    [Fact]
    public void ABatchWhereOneProjectWentUnchecked_IsNotGreen_AndRedStillWins()
    {
        var unchecked_ = Strings.SmartFixCouldNotRun(SmartFixValidator.NotRestoredCause);

        Assert.False(SmartFixValidator.ReadVerdict(Strings.SmartFixBuildOk));   // WITNESS: alone, the green reads green
        Assert.Null(SmartFixValidator.ReadVerdict(Strings.SmartFixBuildOk + "\n" + unchecked_));
        Assert.Null(SmartFixValidator.ReadVerdict(unchecked_ + "\n" + Strings.SmartFixBuildOk));
        Assert.Null(SmartFixValidator.ReadVerdict(Strings.SmartFixBuildOk + "\n" + Strings.SmartFixTimeout));
        Assert.True(SmartFixValidator.ReadVerdict(unchecked_ + "\n" + Strings.SmartFixBuildErrors(1, "a")));
    }

    [Fact]
    public void AProjectNeverRestored_BesideACompilerError_StaysRed()
    {
        const string output = @"C:\sdk\targets\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file not found. [C:\ws\A.csproj]"
                            + "\nProgram.cs(3,5): error CS1002: ; expected [C:\\ws\\B.csproj]";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true);

        Assert.True(SmartFixValidator.ReadVerdict(note!));
        Assert.Contains("CS1002", note);
    }

    [Fact]
    public void ADotnetBuildThatSucceeded_WithWarnings_IsOk()
    {
        const string output = "Program.cs(3,5): warning CS0168: The variable 'x' is declared but never used\nBuild succeeded.";

        Assert.Equal(Strings.SmartFixBuildOk, SmartFixValidator.Interpret(0, output, dotnetFilter: true));
    }

    [Theory]
    [InlineData(-1)]   // timed out: RunAsync reports it as -1 with the partial output
    [InlineData(1)]
    public void ADotnetBuildThatFailed_WithoutErrorLines_IsNotOk(int exitCode)
    {
        const string output = "Build FAILED.\nMSBuild node crashed";

        var note = SmartFixValidator.Interpret(exitCode, output, dotnetFilter: true);

        // Failed, and never counted: these lines are not compiler errors.
        Assert.Equal(Strings.SmartFixBuildFailed("Build FAILED.\nMSBuild node crashed"), note);
    }

    [Fact]
    public void AMissingDotnet_StaysSilent_LikeEveryOtherToolchain()
    {
        // What Windows PowerShell prints — the sentence (translated elsewhere) and the error id (never translated).
        const string output = "dotnet : The term 'dotnet' is not recognized as the name of a cmdlet, function, script file, or operable program.\n" +
                              "    + CategoryInfo          : ObjectNotFound: (dotnet:String) [], CommandNotFoundException\n" +
                              "    + FullyQualifiedErrorId : CommandNotFoundException\n";

        Assert.Null(SmartFixValidator.Interpret(1, output, dotnetFilter: true));
    }

    [Fact]
    public void AGenericToolchain_StillDecidesOnTheExitCode()
    {
        Assert.Equal(Strings.SmartFixBuildOk, SmartFixValidator.Interpret(0, "", dotnetFilter: false));
        Assert.Equal(
            Strings.SmartFixBuildFailed("src/main.rs:3:5: expected `;`"),
            SmartFixValidator.Interpret(101, "src/main.rs:3:5: expected `;`", dotnetFilter: false));
    }
}
