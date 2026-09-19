using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A command killed on its fuse hands back <b>what it had printed</b>, whichever tool ran it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The rule was written, and held by one of its two readers. <c>ShellSession</c> salvages the
/// partial output under a comment stating the cost in as many words — the previous version
/// <i>"returned the one-line timeout message alone, so a command that ran for its whole budget and
/// printed a thousand useful lines told the model nothing"</i> — and <c>UserShellTool</c>, which
/// runs the shell tools the USER defines, was still that previous version. The streams were sitting
/// in the result <see cref="ChildProcess"/> returns; the tool dropped them.
/// </para>
/// <para>
/// ⚠ The consequence is not cosmetic: a custom tool that hangs is precisely the one whose output
/// says WHY — the prompt it was waiting on, the host it could not reach — and the model was told
/// only that time ran out.
/// </para>
/// <para>
/// ⚠ <c>SmartFixValidator</c> is a deliberate exception and stays one: its timeout produces a NOTE,
/// and the partial output of a killed build is exactly what round 71 measured being read back as
/// compilation errors.
/// </para>
/// </remarks>
public class TimedOutCommandOutputTests
{
    [Fact]
    public void AKilledCommand_HandsBackWhatItPrinted()
    {
        var message = ChildProcess.TimedOutMessage(30, "Connecting to db…\nWaiting for lock");

        Assert.Contains("timed out after 30s", message, StringComparison.Ordinal);
        Assert.Contains("Connecting to db…", message, StringComparison.Ordinal);
        Assert.Contains("Waiting for lock", message, StringComparison.Ordinal);
        // The output is introduced, not concatenated blind: the model must not read it as the
        // command's normal answer.
        Assert.Contains("[output before the timeout]", message, StringComparison.Ordinal);
    }

    /// <summary>Reference arm: a command that printed nothing gets no empty section — a heading
    /// with nothing under it reads as output that was lost.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void ACommandThatPrintedNothing_GetsNoEmptySection(string? salvaged)
    {
        var message = ChildProcess.TimedOutMessage(30, salvaged);

        Assert.Equal("Error: command timed out after 30s.", message);
    }

    /// <summary>
    /// The rule that keeps the two together: the sentence has ONE writer. Both sites built it
    /// inline, which is how they came to say different things about the same event.
    /// </summary>
    /// <remarks>⚠ Read without comments: this file quotes the sentence it forbids elsewhere.</remarks>
    [Theory]
    [InlineData("Inferpal.Core", "Services", "Shell", "ShellSession.cs")]
    [InlineData("Inferpal.Core", "Services", "Tools", "UserShellTool.cs")]
    public void NoRunnerWritesTheTimeoutSentenceItself(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: this really is a file that runs a command under a fuse.
        Assert.Contains("CommandTimeoutSeconds", code, StringComparison.Ordinal);

        Assert.Contains("ChildProcess.TimedOutMessage", code, StringComparison.Ordinal);
        Assert.DoesNotContain("command timed out after", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
