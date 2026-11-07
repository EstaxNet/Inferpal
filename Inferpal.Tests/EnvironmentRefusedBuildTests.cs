using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Commands;
using Inferpal.Services.Tools;
using Xunit;
using static Inferpal.Services.Tools.GetDiagnosticsTool;

namespace Inferpal.Tests;

/// <summary>
/// A machine whose .NET SDK or workloads do not match what a project asks for stops dotnet before the compiler: a target
/// framework the SDK does not support (NETSDK1045), a workload that is not installed (NETSDK1147), a <c>global.json</c>
/// whose SDK is absent (the host's sentence, no code). Read as "1 error(s)" or "the code did not compile", the model
/// repairs code that was never compiled. Each reader says nothing was compiled instead — the lines are dotnet's own,
/// captured from a real SDK.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public sealed class EnvironmentRefusedBuildTests
{
    private const string TargetNotSupported =
        @"C:\Program Files\dotnet\sdk\11.0.100\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.TargetFrameworkInference.targets(195,5): "
        + "error NETSDK1045: The current .NET SDK does not support targeting .NET 99.0.  Either target .NET 11.0 or lower, or use a "
        + @"version of the .NET SDK that supports .NET 99.0. [C:\ws\App\App.csproj]";
    private const string WorkloadMissing =
        @"C:\Program Files\dotnet\sdk\11.0.100\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): "
        + @"error NETSDK1147: To build this project, the following workloads must be installed: android [C:\ws\Mobile\Mobile.csproj]";
    private const string NotRestoredLine =
        @"C:\sdk\targets\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file "
        + @"'C:\ws\Lib\obj\project.assets.json' not found. Run a NuGet package restore to generate this file. [C:\ws\Lib\Lib.csproj]";
    private const string RealError = @"C:\ws\App\A.cs(3,13): error CS0103: The name 'c' does not exist [C:\ws\App\App.csproj]";
    // What the host writes on stderr for a global.json whose SDK is not installed (exit code 155 on Windows).
    private const string SdkNotFound =
        "The command could not be loaded, possibly because:\n  * You intended to execute a .NET application:\n"
        + "      The application 'build' does not exist or is not a managed .dll or .exe.\n  * You intended to execute a .NET SDK command:\n"
        + "      A compatible .NET SDK was not found.\n\nRequested SDK version: 1.0.100\nglobal.json file: C:\\ws\\global.json\n";

    private static string Diagnose(string stdout) =>
        Interpret(new ChildProcessResult(1, stdout, string.Empty, TimedOut: false), "App.csproj", 90);

    // ── get_diagnostics ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(TargetNotSupported, "NETSDK1045")]
    [InlineData(WorkloadMissing, "NETSDK1147")]
    public void ARefusedEnvironment_WasNotBuilt_AndItsReasonIsDotnets(string line, string code)
    {
        var said = Diagnose(line + "\n");

        Assert.StartsWith(EnvironmentRefused, said);
        Assert.Contains(code, said);
        Assert.Equal(BuildVerdict.NotBuilt, ReadVerdict(said));
        Assert.False(OutputHasErrors(said));
    }

    [Fact]
    public void AFreshClone_MixingANotRestoredProject_AndAMissingWorkload_IsNotRed()
    {
        var said = Diagnose(NotRestoredLine + "\n" + WorkloadMissing + "\n");

        Assert.StartsWith(EnvironmentRefused, said);
        Assert.Contains("NETSDK1004", said);
        Assert.False(OutputHasErrors(said));
    }

    [Fact]
    public void ARealError_BesideARefusedEnvironment_IsStillAnError()
    {
        var said = Diagnose(WorkloadMissing + "\n" + RealError + "\n");

        Assert.Equal(BuildVerdict.Errors, ReadVerdict(said));
        Assert.True(OutputHasErrors(said));
    }

    [Fact]
    public void TheCode_WrittenElsewhereInTheLine_DoesNotCount()
    {
        // A real error whose message quotes the code: the diagnostic's own code decides, never the word.
        var quoting = @"C:\ws\App\Notes.cs(1,1): error CS1002: ; expected near NETSDK1045 [C:\ws\App\App.csproj]";

        Assert.False(IsEnvironmentRefusal(quoting));
        Assert.Equal(BuildVerdict.Errors, ReadVerdict(Diagnose(quoting + "\n")));
    }

    [Fact]
    public void AMissingImport_StaysAnError_ForItCannotBeToldFromABrokenOne()
    {
        var import = @"C:\ws\App\App.csproj(9,3): error MSB4019: The imported project ""C:\ws\App\build\missing.targets"" was not found. [C:\ws\App\App.csproj]";

        Assert.False(IsEnvironmentRefusal(import));
        Assert.Equal(BuildVerdict.Errors, ReadVerdict(Diagnose(import + "\n")));
    }

    [Fact]
    public void AGlobalJsonWithoutItsSdk_IsSaid_FromStderr()
    {
        // The host writes it on stderr only, with no error code: the build check showed stdout, and said "failed" with no reason.
        var said = Interpret(new ChildProcessResult(155, string.Empty, SdkNotFound, TimedOut: false), "App.csproj", 90);

        Assert.StartsWith(EnvironmentRefused, said);
        Assert.Contains("Requested SDK version: 1.0.100", said);
        Assert.False(OutputHasErrors(said));
    }

    [Fact]
    public void OneClassifier_ReadsEachStopTheSameWay()
    {
        Assert.Equal(BuildStop.Environment, StopOf([TargetNotSupported]));
        Assert.Equal(BuildStop.Environment, StopOf([NotRestoredLine, WorkloadMissing]));
        Assert.Equal(BuildStop.NotRestored, StopOf([NotRestoredLine]));
        Assert.Equal(BuildStop.CommandLine, StopOf(["MSBUILD : error MSB1003: Specify a project or solution file."]));
        Assert.Equal(BuildStop.None, StopOf([WorkloadMissing, RealError]));
        Assert.Equal(BuildStop.None, StopOf([]));
    }

    // ── Smart Fix ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SmartFix_CouldNotCheck_OnARefusedEnvironment_OrAGlobalJsonWithoutItsSdk()
    {
        var expected = Strings.SmartFixCouldNotRun(SmartFixValidator.EnvironmentRefusedCause);

        Assert.Equal(expected, SmartFixValidator.Interpret(1, TargetNotSupported + "\n", dotnetFilter: true));
        Assert.Equal(expected, SmartFixValidator.Interpret(1, NotRestoredLine + "\n" + WorkloadMissing + "\n", dotnetFilter: true));
        Assert.Equal(expected, SmartFixValidator.Interpret(155, SdkNotFound, dotnetFilter: true));
        // The reference: a real compilation error is still one.
        Assert.Equal(Strings.SmartFixBuildErrors(1, RealError), SmartFixValidator.Interpret(1, RealError + "\n", dotnetFilter: true));
    }

    // ── run_tests and /tdd ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(155)]
    public void RunTests_SaysNothingRan_AndTdd_CountsItSo(int exitCode)
    {
        var raw = exitCode == 155 ? SdkNotFound : WorkloadMissing + "\n";
        var said = RunTestsTool.ParseDotnetOutput(raw, exitCode);

        Assert.StartsWith(RunTestsTool.EnvironmentRefused, said);
        Assert.DoesNotContain(RunTestsTool.BuildFailed, said, StringComparison.Ordinal);
        Assert.True(TddCommandHandler.NothingRan(said));
        Assert.False(TddCommandHandler.TestsFailed(said));
    }

    [Fact]
    public void RunTests_PutsTheLineThatNamesTheEnvironmentFirst_AndSaysWhatItCut()
    {
        // A fresh clone: many projects not restored, one workload missing — listed after them by dotnet.
        var lines = Enumerable.Range(1, 24).Select(i => NotRestoredLine.Replace("Lib", "Lib" + i)).Append(WorkloadMissing);
        var said = RunTestsTool.ParseDotnetOutput(string.Join("\n", lines) + "\n", 1);

        Assert.StartsWith(RunTestsTool.EnvironmentRefused, said);
        Assert.Contains("NETSDK1147", said.Split('\n')[1]);
        Assert.Contains("more error(s) not listed", said);
    }

    [Fact]
    public void RunTests_TheHostsSentence_BesideARealError_StaysRed()
    {
        // A nested dotnet against a pinned global.json prints the host's sentence; the compile error beside it is the code's.
        var said = RunTestsTool.ParseDotnetOutput(SdkNotFound + RealError + "\n", 1);

        Assert.StartsWith(RunTestsTool.BuildFailed, said);
        Assert.True(TddCommandHandler.TestsFailed(said));
    }

    [Fact]
    public void TheHostsSentence_BeforeDotNet8_IsReadToo()
    {
        Assert.True(IsSdkNotFound("A compatible installed .NET SDK for global.json version [6.0.100] from [C:\\ws\\global.json] was not found."));
        Assert.False(IsSdkNotFound("Build succeeded."));
    }

    [Fact]
    public void ProjectsOnlyNotRestored_AreNotTheEnvironment()
    {
        Assert.NotEqual(BuildStop.Environment, StopOf([NotRestoredLine, NotRestoredLine.Replace("Lib", "Lib2")]));
        Assert.DoesNotContain(RunTestsTool.EnvironmentRefused, RunTestsTool.ParseDotnetOutput(NotRestoredLine + "\n", 1), StringComparison.Ordinal);
    }

    [Fact]
    public void ANonDotnetValidator_IsNotReadAsDotnet()
    {
        // Another toolchain's failure that happens to print the host's sentence is that toolchain's failure.
        Assert.NotEqual(Strings.SmartFixCouldNotRun(SmartFixValidator.EnvironmentRefusedCause),
                        SmartFixValidator.Interpret(1, SdkNotFound, dotnetFilter: false));
    }

    [Fact]
    public void RunTests_ARealCompileError_IsStillRed()
    {
        var said = RunTestsTool.ParseDotnetOutput(RealError + "\n", 1);

        Assert.StartsWith(RunTestsTool.BuildFailed, said);
        Assert.True(TddCommandHandler.TestsFailed(said));
    }
}
