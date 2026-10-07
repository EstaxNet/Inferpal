using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A model that does not stop writes the gap, then the lines that follow the caret, then whatever comes after them —
/// another method, a Markdown fence, prose. Trimming only the completion's LAST lines finds nothing to trim there, and
/// the whole run-on was inserted. The completions below are raw outputs of the FIM bench
/// (docs/probes/model-bench/fim-arret.md): replayed through <see cref="FimCompletion.Finish"/>, cutting where the
/// repetition STARTS raised the bench from 215 to 258 right answers of 576, and changed none of the recommended models'.
/// </summary>
public class FimRunOnCompletionTests
{
    [Fact]
    public void ACompletionThatRunsOnPastTheLinesThatFollow_IsCutWhereItStartsRepeatingThem()
    {
        // Caret at the end of an indented line inside a constructor; the method and the class close below.
        var raw = " Y = y;\n    }\n}\n```\n\nNow we want to find all points that lie on the line segment between two given points.";

        Assert.Equal(" Y = y;", FimCompletion.Finish(raw, "\n    }\n}\n"));
    }

    [Fact]
    public void ARunOnThatRepeatsTwoFollowingLines_KeepsOnlyTheGap()
    {
        var raw = "total += price;\n}\nreturn total;\n\n// or simply:\nreturn prices.Sum();\n\nBut the problem says...";

        Assert.Equal("total += price;", FimCompletion.Finish(raw, "\n}\nreturn total;\n"));
    }

    [Fact]
    public void ABlockThatClosesItsOwnBrace_KeepsIt_EvenWhenTheNextLineIsABrace()
    {
        // Reference arm: the "}" inside the completion closes the block the completion opened — it matches the line
        // that follows, but it is not a repetition. Cut there, the method's brace would close the block.
        var completion = "if (x)\n{\n    y();\n}\nz();";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\n}\n"));
    }

    [Fact]
    public void ACompletionThatRepeatsNothing_IsKeptWhole()
    {
        var completion = "var total = 0;\nforeach (var p in prices)\n    total += p;";

        Assert.Equal(completion, FimCompletion.Finish(completion, "\nreturn total;\n}\n"));
    }
}
