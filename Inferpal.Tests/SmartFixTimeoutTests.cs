using Inferpal.Localization;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A build check that was <b>killed</b> is not a build that has errors, and a failure that names
/// none does not have <b>zero</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured</b>: <c>Interpret(-1, "Determining projects to restore...\nRestored App.csproj")</c>
/// — the partial output of a build killed on the 60 s fuse — came back as
/// <i>"🔨 Smart Fix: 2 compilation error(s) detected — please fix before continuing:"</i> followed
/// by those two restore lines, presented to the model as compilation errors. With no output at all
/// it said <i>"0 compilation error(s) detected — please fix before continuing"</i>.
/// </para>
/// <para>
/// ⚠ This runs after <b>every write</b>, and it is the same family the file's own remark documents
/// one method below: <i>"not a silence, a WRONG NUMBER, in the loop that runs after EVERY write"</i>.
/// The count was right there; what was lost is what the run actually was.
/// </para>
/// <para>
/// ⚠ And <see cref="Strings.SmartFixTimeout"/> existed, translated into ten languages, with nothing
/// able to reach it: <c>ChildProcess</c> reports a timeout by RETURNING, so the
/// <c>OperationCanceledException</c> the caller was catching never came. A sentence that cannot be
/// reached is not a promise kept.
/// </para>
/// </remarks>
public class SmartFixTimeoutTests
{
    // ── A killed build says it was killed ────────────────────────────────────

    [Theory]
    [InlineData("Determining projects to restore...\nRestored App.csproj")]
    [InlineData("")]
    public void AKilledBuild_SaysItTimedOut_InsteadOfListingItsOutputAsErrors(string partial)
    {
        var note = SmartFixValidator.Interpret(-1, partial, dotnetFilter: true, timedOut: true);

        Assert.Equal(Strings.SmartFixTimeout, note);
    }

    /// <summary>
    /// REFERENCE ARM: a real build failure still lists its errors, and still counts them. Without
    /// it, answering "timed out" to everything would pass the test above.
    /// </summary>
    [Fact]
    public void ARealBuildFailure_StillNamesItsErrors()
    {
        const string output = "Program.cs(3,5): error CS0103: The name 'x' does not exist\n"
                            + "Program.cs(9,1): error CS1002: ; expected";

        var note = SmartFixValidator.Interpret(1, output, dotnetFilter: true);

        Assert.Equal(Strings.SmartFixBuildErrors(2, SmartFixValidator.Listed(
            ["Program.cs(3,5): error CS0103: The name 'x' does not exist",
             "Program.cs(9,1): error CS1002: ; expected"])), note);
    }

    /// <summary>REFERENCE ARM: a build that passes still says so.</summary>
    [Fact]
    public void AGreenBuild_StillSaysSo() =>
        Assert.Equal(Strings.SmartFixBuildOk, SmartFixValidator.Interpret(0, "Build succeeded.", dotnetFilter: true));

    // ── A failure that names nothing does not have zero errors ───────────────

    [Fact]
    public void AFailureThatNamesNoError_IsNotZeroErrors()
    {
        // Not a timeout: a validator that simply exits non-zero without printing anything — a
        // script, a toolchain that logs elsewhere.
        var note = SmartFixValidator.Interpret(1, "", dotnetFilter: true);

        Assert.Equal(Strings.SmartFixBuildFailedNoErrors, note);
        Assert.DoesNotContain("0", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// REFERENCE ARM: a failure that prints something WITHOUT the word "error" is still reported
    /// with what it printed — the fallback that lets a non-.NET toolchain be read at all.
    /// </summary>
    [Fact]
    public void AFailureThatPrintsSomething_StillShowsIt()
    {
        var note = SmartFixValidator.Interpret(1, "FAILED: 3 assertions", dotnetFilter: true);

        Assert.Contains("FAILED: 3 assertions", note, StringComparison.Ordinal);
    }
}
