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

        Assert.Equal(Strings.SmartFixBuildErrors(2, "Build FAILED.\nMSBuild node crashed"), note);
    }

    [Fact]
    public void AMissingDotnet_StaysSilent_LikeEveryOtherToolchain()
    {
        const string output = "dotnet : The term 'dotnet' is not recognized as the name of a cmdlet";

        Assert.Null(SmartFixValidator.Interpret(1, output, dotnetFilter: true));
    }

    [Fact]
    public void AGenericToolchain_StillDecidesOnTheExitCode()
    {
        Assert.Equal(Strings.SmartFixBuildOk, SmartFixValidator.Interpret(0, "", dotnetFilter: false));
        Assert.Equal(
            Strings.SmartFixBuildErrors(1, "src/main.rs:3:5: expected `;`"),
            SmartFixValidator.Interpret(101, "src/main.rs:3:5: expected `;`", dotnetFilter: false));
    }
}
