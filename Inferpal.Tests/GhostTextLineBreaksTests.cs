using System.IO;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio: an accepted ghost text takes the document's line breaks, never the model's.
/// </summary>
/// <remarks>
/// The completion was inserted as the model wrote it — LF — in a CRLF document, Visual Studio's default on Windows: the
/// file was left with mixed line endings, which the next diff shows changed line by line.
/// </remarks>
public sealed class GhostTextLineBreaksTests
{
    [Theory]
    [InlineData("if (x)\n{\n    y();\n}", "\r\n", "if (x)\r\n{\r\n    y();\r\n}")]
    [InlineData("a\r\nb\nc", "\r\n", "a\r\nb\r\nc")]
    [InlineData("a\r\nb", "\n", "a\nb")]
    public void AMultiLineCompletion_TakesTheDocumentsLineBreaks(string completion, string documentBreak, string inserted) =>
        Assert.Equal(inserted, CompletionLineBreaks.In(completion, documentBreak));

    [Fact]
    public void AOneLineCompletion_IsInsertedAsIs()
    {
        // Reference arm: the common case touches nothing.
        Assert.Equal("Console.WriteLine(x);", CompletionLineBreaks.In("Console.WriteLine(x);", "\r\n"));
    }

    [Fact]
    public void TheGhostText_InsertsWhatTheDocumentsBreaksMade_AndPlacesTheCaretAfterIt()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var controller = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.InProc", "GhostText", "GhostTextController.cs"));
        Assert.Contains("CompletionLineBreaks.In(completion, DocumentLineBreak(pos.Snapshot, pos.Position))", controller, StringComparison.Ordinal);
        Assert.Contains("edit.Insert(pos.Position, text);", controller, StringComparison.Ordinal);
        Assert.Contains("pos.Position + text.Length", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("edit.Insert(pos.Position, completion);", controller, StringComparison.Ordinal);
    }
}
