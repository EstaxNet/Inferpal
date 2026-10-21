using System.IO;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A message is a command only if it is written as one: a pasted path or a <c>//</c> comment is a question.
/// </summary>
/// <remarks>
/// ⚠ Decided on the first character, a question pasted after a path (<c>/home/me/app/main.py crashes on line 12</c>) or a
/// comment is answered "unknown command" and never reaches the model. One rule, <c>SlashCommandRouter.LooksLikeCommand</c>:
/// read by the agent protocol, by every site of the Visual Studio window and the host that routes a message or keeps the
/// auto-context from it, and mirrored in TypeScript for VS Code, which cannot call the Core synchronously.
/// </remarks>
public class PastedSlashQuestionTests
{
    [Theory]
    [InlineData("/home/me/app/main.py crashes on line 12")]
    [InlineData("// why does this loop?")]
    [InlineData("/c/dev/app/x.cs")]
    [InlineData("/.ssh/config is read")]
    [InlineData("/")]
    [InlineData("  /src\\app.cs")]
    [InlineData("/** Returns the user */ why is it null?")]
    [InlineData("/* TODO */ what is left here?")]
    public void APastedPathOrComment_IsAQuestion(string text) =>
        Assert.False(SlashCommandRouter.LooksLikeCommand(text));

    [Theory]
    [InlineData("/help")]
    [InlineData("  /tdd MyTests")]
    [InlineData("/folder:command some words")]
    [InlineData("/check main..HEAD")]
    [InlineData("/release-v1.2 notes")]   // a template's name may have a dot
    [InlineData("/help.")]                // a mistyped command is answered "unknown command", never sent to the model
    [InlineData("/tmp is full")]          // and so is a single word: no rule tells it from a typo
    public void ACommand_IsACommand(string text) =>
        Assert.True(SlashCommandRouter.LooksLikeCommand(text));

    [Fact]
    public void TheVisualStudioWindowAndTheHost_NeverDecideOnTheFirstCharacter()
    {
        string Code(params string[] path) =>
            ConventionCoverageTests.CodeOnly(Path.Combine([ConventionCoverageTests.RepoRoot(), .. path]));

        // Routing a message: send, queued prompt, note during a run, queued message and its refusal.
        foreach (var file in new[] { "InferpalToolWindowData.ChatTurn.cs", "InferpalToolWindowData.PendingPrompt.cs",
                                     "InferpalToolWindowData.Steering.cs", "InferpalToolWindowData.Attachments.cs" })
        {
            var code = Code("Inferpal", "ToolWindow", file);
            Assert.Contains("SlashCommandRouter.LooksLikeCommand(", code, StringComparison.Ordinal);
            Assert.DoesNotContain(".StartsWith('/')", code, StringComparison.Ordinal);
        }
        // The auto-context of a question: kept from a command only (the typing pre-search, the host's gate).
        Assert.DoesNotContain("trimmed.StartsWith('/')", Code("Inferpal", "ToolWindow", "InferpalToolWindowData.Mentions.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("trimmed.StartsWith('/')", Code("Inferpal.Host", "HostServer.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void VsCode_ReadsTheSameShape_Everywhere()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");

        // The mirror carries the Core's rule: a name that starts with a letter or a digit, with no '/' or '\'.
        var rule = WebviewRebuildTests.Body(provider, "export function looksLikeCommand(");
        Assert.Contains("typed[0] !== '/'", rule, StringComparison.Ordinal);
        Assert.Contains("/^[\\p{L}\\p{N}]/u.test(name)", rule, StringComparison.Ordinal);
        Assert.Contains("/[/\\\\]/", rule, StringComparison.Ordinal);

        Assert.Contains("looksLikeCommand(prompt)", provider, StringComparison.Ordinal);
        Assert.DoesNotContain(".startsWith('/')", provider, StringComparison.Ordinal);
    }
}
