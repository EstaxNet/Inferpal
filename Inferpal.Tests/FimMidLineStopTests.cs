using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A completion requested in the middle of a line stops at its first line break: only that line is ever inserted.
/// </summary>
/// <remarks>
/// ⚠ Without the stop, the model wrote on — lines <see cref="FimCompletion.Finish"/> throws away — and the suggestion
/// waited for every one of them. The stop is safe because it cannot change what is inserted, which the second test
/// holds: mid-line, the line the server stops at is the line Finish would have kept.
/// </remarks>
public class FimMidLineStopTests
{
    [Theory]
    [InlineData(");\n    }")]
    [InlineData("\";\r\n}")]
    [InlineData(" + 1;")]
    public void MidLine_TheRequestStopsAtTheFirstLineBreak(string suffix) =>
        Assert.Contains("\n", FimCompletion.Stops(suffix, ["<|endoftext|>"]));

    [Theory]
    [InlineData("Math.Abs(x));\n        Console.WriteLine(y);\n    }\n}", ");\n    }")]
    [InlineData("John Doe\";\nvar age = 3;", "\";\n}")]
    [InlineData("\nreturn 0;", ");")]
    [InlineData("total + 1;\r\n    return total;\r\n", " + 1;\r\n}")]
    [InlineData("x", ")")]
    public void MidLine_WhatIsInsertedIsTheSame_WithOrWithoutTheStop(string completion, string suffix)
    {
        var newline = completion.IndexOf('\n');
        var stopped = newline < 0 ? completion : completion[..newline];   // what the server returns at the stop

        Assert.Equal(FimCompletion.Finish(completion, suffix), FimCompletion.Finish(stopped, suffix));
    }

    /// <summary>⚠ Reference arms: at the end of a line a completion may be several lines, so no line-break stop is
    /// added; and a request that already stops at a line break is not given a second one.</summary>
    [Theory]
    [InlineData("\n    }\n}")]
    [InlineData("")]
    [InlineData("   \n")]
    public void AtTheEndOfALine_TheStopsAreUnchanged(string suffix) =>
        Assert.Equal(["\n\n\n"], FimCompletion.Stops(suffix, ["\n\n\n"]));

    [Fact]
    public void AStopAlreadyThere_IsNotAddedTwice() =>
        Assert.Equal(["\n"], FimCompletion.Stops(");", ["\n"]));

    [Theory]
    [InlineData("OllamaClient.cs", 2)]
    [InlineData("LmStudioClient.cs", 1)]
    public void EveryCompletionRequest_TakesItsStopsFromTheCaret(string file, int requests)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Inference", file));

        Assert.Equal(requests, code.Split("Stop: FimCompletion.Stops(suffix,").Length - 1);
        Assert.Equal(requests, code.Split("Stop:").Length - 1);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
