using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The same file or folder attached twice — dropped twice, picked again with @, "Attach to Chat" again — is one chip, not
/// two: a second chip would send its text twice. The rule is <see cref="ChipRepeat.Repeats"/>, asked by Visual Studio's
/// one chip funnel; VS Code's <c>addChip</c> applies it too (<c>ChipAttachmentDedupeTests</c>).
/// </summary>
public sealed class ChipRepeatTests
{
    private const string App = @"C:\src\App\Program.cs";

    [Fact]
    public void TheSameFile_IsRepeated_EvenReadAgainAfterAnEdit()
    {
        Assert.True(ChipRepeat.Repeats("Program.cs", "class P {}", App, "Program.cs", "class P {}", App));
        Assert.True(ChipRepeat.Repeats("Program.cs", "class P { int x; }", App, "Program.cs", "class P {}", App));
    }

    [Fact]
    public void ThePathsCase_FollowsTheDisk()
    {
        // The shared rule for "same file" (PathComparer): case folded on Windows and macOS, never on Linux.
        Assert.Equal(!OperatingSystem.IsLinux(),
                     ChipRepeat.Repeats("program.cs", "class P {}", @"c:\src\app\program.cs", "Program.cs", "class P {}", App));
    }

    [Fact]
    public void AnotherFileOfTheSameName_IsNotRepeated() =>
        Assert.False(ChipRepeat.Repeats("Program.cs", "class P {}", @"C:\src\Tool\Program.cs", "Program.cs", "class P {}", App));

    [Fact]
    public void AFolderOrASelection_IsRepeatedWhenItsNameAndTextAreTheSame()
    {
        Assert.True(ChipRepeat.Repeats("📁 Rounding", "Rounding/\n  Round.cs", null, "📁 Rounding", "Rounding/\n  Round.cs", null));
        // Two folders of one name in two places list different paths: two chips.
        Assert.False(ChipRepeat.Repeats("📁 Rounding", "Other/Rounding/\n  Up.cs", null, "📁 Rounding", "Rounding/\n  Round.cs", null));
        // A selection of a file is not that whole file.
        Assert.False(ChipRepeat.Repeats("Selection (Program.cs)", "class P", null, "Program.cs", "class P {}", App));
    }
}
