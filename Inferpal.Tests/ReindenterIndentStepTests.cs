using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The reindenter divides each line's indentation by the file's indentation step. When the original reveals no step —
/// one line, or lines at one width — it assumed 4: in a 2-space file, levels 1 and 2 both rounded to one unit, so a
/// YAML key moved out of its parent (still valid YAML, another meaning) and a Python body left its block, without an
/// error. In a tab-indented file, a reply indented with spaces measured zero, and the whole block came out flat.
/// </summary>
public class ReindenterIndentStepTests
{
    private static string R(string original, string edited) =>
        InlineEditReindenter.Reindent(original, edited).Replace("\r\n", "\n");

    [Fact]
    public void ATwoSpaceYamlLine_KeepsTheNestingOfTheReply()
    {
        var edited = "  image: foo\n  resources:\n    limits:\n      cpu: 1";

        Assert.Equal("  image: foo\n  resources:\n    limits:\n      cpu: 1", R("  image: foo", edited));
    }

    [Fact]
    public void ATwoSpacePythonLine_KeepsItsBodyInsideTheBlock()
    {
        var edited = "  if total < 0:\n    raise ValueError(total)\n  return total";

        Assert.Equal("  if total < 0:\n    raise ValueError(total)\n  return total", R("  return total", edited));
    }

    [Fact]
    public void ATabIndentedLine_RepliedWithSpaces_KeepsItsNesting()
    {
        var edited = "\tif x:\n\t    return x";

        Assert.Equal("\tif x:\n\t\treturn x", R("\treturn x", edited));
    }

    [Fact]
    public void AFourSpaceFile_IsUnchanged()
    {
        // Reference arm: a step the original shows (4) is the one used.
        var original = "    if (a)\n    {\n        b();\n    }";
        var edited   = "    if (a)\n    {\n        b();\n        c();\n    }";

        Assert.Equal(edited, R(original, edited));
    }
}
