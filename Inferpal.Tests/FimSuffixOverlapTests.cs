using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Inline completions repeat what follows the caret. Measured on a remote LM Studio: 7 of 8 mid-line completions from
/// qwen2.5-coder (a FIM model, GIVEN the suffix) and 8 of 8 from the prefix-only fallback ended with the rest of the
/// line the editor already had — accepted, <c>Console.WriteLine(|);</c> became <c>…));</c>. The completions below are
/// the ones measured.
/// </summary>
public class FimSuffixOverlapTests
{
    [Theory]
    [InlineData("\"Items: \" + string.Join(\", \", items));", ");\n    }\n}", "\"Items: \" + string.Join(\", \", items)")]
    [InlineData(" * 10);", ");\n    }\n}", " * 10")]
    [InlineData("John Doe\";", "\";\n    }\n}", "John Doe")]
    [InlineData("3, 4, 5 };", " };\n    }\n}", "3, 4, 5")]
    [InlineData("items.Max());", ");\n    }\n}", "items.Max()")]
    [InlineData("items)\n        {\n            Console.WriteLine(item);\n        }", ")\n        {\n        }", "items")]
    [InlineData(")", ")\n        {\n            return;\n        }", "")]
    [InlineData("CalculateSum(items));", ");\n    }\n}", "CalculateSum(items)")]
    public void AMeasuredCompletion_NoLongerRepeatsTheRestOfTheLine(string completion, string suffix, string expected)
    {
        Assert.Equal(expected, FimCompletion.Finish(completion, suffix));
    }

    [Theory]
    [InlineData("Math.Abs(x)", ");")]                                             // balanced: its ")" is its own
    [InlineData("\"Invalid input. Please provide a list of integers.\"", ");")]  // repeats nothing
    [InlineData("x", "")]                                                          // nothing after the caret
    public void ACompletionThatRepeatsNothing_IsKept(string completion, string suffix)
    {
        // Reference arms: only the WHOLE rest of the line is removed, never a closer that happens to match.
        Assert.Equal(completion, FimCompletion.Finish(completion, suffix));
    }

    private static string Source(string project, string file) => ConventionCoverageTests.CodeOnly(
        ConventionCoverageTests.ProjectSources(project).Single(f => Path.GetFileName(f) == file));

    [Fact]
    public void TheVisualStudioSidecar_FinishesItsCompletionsToo()
    {
        // Not executable from this suite (top-level statements of the sidecar process): a source scan, with its witness.
        var code = Source("Inferpal.Fim", "Program.cs");

        Assert.Contains("StreamFimAsync(", code);                                             // WITNESS
        Assert.Contains("FimCompletion.Finish(", code);
    }

    [Fact]
    public void AnEmptyCompletion_IsNotShown_SoTabStillIndents()
    {
        // A pending "" is not null: the next Tab was accepted — swallowed, inserting nothing, instead of indenting.
        // A backend without FIM answers "", and so does a completion that only repeated the text after the caret.
        var code = Source("Inferpal.InProc", "GhostTextController.cs");

        Assert.Contains("_adornment.Show(completion", code);                                  // WITNESS
        Assert.Contains("string.IsNullOrWhiteSpace(completion)", code);
    }

    [Fact]
    public void AtTheEndOfALine_TheClosingLinesItRepeatsAreDropped()
    {
        var completion = "return a + b;\n    }";
        var suffix     = "\n    }\n}\n";

        Assert.Equal("return a + b;", FimCompletion.Finish(completion, suffix));
    }

    [Fact]
    public void AtTheEndOfALine_AMultiLineCompletionThatRepeatsNothing_IsKept()
    {
        var completion = "if (a > b)\n        return a;\n    return b;";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n    }\n}\n"));
    }
}
