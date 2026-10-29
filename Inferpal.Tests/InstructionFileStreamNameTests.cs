using System.IO;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A write to a file that becomes the system prompt forces the approval prompt under every name Windows resolves to
/// that file — its data stream (<c>::$DATA</c>), a trailing dot or space.
/// </summary>
/// <remarks>
/// The match compared the path as written: <c>.inferpal/memory.md::$DATA</c> is <c>memory.md</c> itself to Windows,
/// and wrote the instructions of every later session under an "Always" given to <c>write_file</c>, without a prompt.
/// </remarks>
public sealed class InstructionFileStreamNameTests
{
    [Theory]
    [InlineData(@"C:\repo\.inferpal\memory.md::$DATA")]
    [InlineData(@"C:\repo\.inferpal\memory.md::$data")]
    [InlineData(@"C:\repo\.inferpal\context.md.")]
    [InlineData(@"C:\repo\.inferpal\notes.md ")]
    [InlineData(@"C:\repo\.inferpal\rules\style.md::$DATA")]
    [InlineData(@"C:\repo\AGENTS.md::$DATA")]
    public void ANameWindowsResolvesToAnInstructionFile_ForcesThePrompt(string subject)
    {
        Assert.True(AgentInstructionFiles.Targets(subject), subject);
    }

    [Theory]
    // A separator of the subject's list inside ONE file name: its parts alone name no instruction file.
    [InlineData(@"C:\repo\.inferpal\rules\style,v2.md")]
    [InlineData(@"C:\repo\.inferpal\modes\a;b.md")]
    [InlineData(@"C:\repo\.claude\skills\x,y\SKILL.md")]
    // …and joined on one line to another path by that same separator.
    [InlineData(@"src/a.cs,.inferpal/rules/style,v2.md")]
    [InlineData(@".inferpal/rules/a;b.md;src/c.cs")]
    public void AnInstructionFileWhoseNameHoldsAListSeparator_ForcesThePrompt(string subject)
    {
        Assert.True(AgentInstructionFiles.Targets(subject), subject);
    }

    [Theory]
    // Reference arms: other files stay ordinary writes.
    [InlineData(@"C:\repo\.inferpal\memory.md.bak")]
    [InlineData(@"C:\repo\.inferpal\project.json")]
    [InlineData(@"C:\repo\src\memory.md")]
    public void AnOrdinaryFile_DoesNot(string subject)
    {
        Assert.False(AgentInstructionFiles.Targets(subject), subject);
    }

    [Fact]
    public void OnWindows_EveryToolPath_IsTheFileItWrites()
    {
        // The funnel every path-taking tool goes through: a rule written for `.env` sees `.env`.
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(@"C:\repo\.env", PathSanitizer.Sanitize(@"C:\repo\.env::$DATA"));
        Assert.Equal(@"C:\repo\.inferpal\memory.md", PathSanitizer.Sanitize(@"C:\repo\.inferpal\memory.md."));
        // An alternate stream holds what no diff shows: no tool writes there.
        Assert.Throws<ArgumentException>(() => PathSanitizer.Sanitize(@"C:\repo\Cart.cs:hidden"));
        // Reference arm: an ordinary path is unchanged.
        Assert.Equal(@"C:\repo\src\Cart.cs", PathSanitizer.Sanitize(@"C:\repo\src\Cart.cs"));
    }

    [Fact]
    public void ElsewhereAColon_IsAnOrdinaryCharacter()
    {
        // Reference arm off Windows: ':' is a legal file name character there.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal("/repo/notes:v2.md", PathSanitizer.Sanitize("/repo/notes:v2.md"));
    }

    [Fact]
    public void OnWindows_TheDataStreamName_IsTheFileItself()
    {
        // Witness that the bypass is real where it matters: the stream name writes the file.
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("inferpal-ads-").FullName;
        try
        {
            var file = Path.Combine(dir, "memory.md");
            File.WriteAllText(file + "::$DATA", "written through the stream name");
            Assert.Equal("written through the stream name", File.ReadAllText(file));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }
}
