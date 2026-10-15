using System.IO;
using Inferpal.Models;
using Inferpal.Services;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The words typed after a code-action command reach the model with the action, in both editors.
/// </summary>
/// <remarks>
/// <c>/fix the off-by-one in the loop</c>, <c>/explain why does this stop?</c>, <c>/refactor extract the parsing</c>: the
/// router kept the command and dropped the rest, so each action ran its generic instruction — another bug "fixed",
/// "nothing to change", the whole file explained — under the question that had been asked, with nothing said.
/// </remarks>
public sealed class CodeActionRequestTests
{
    [Theory]
    [InlineData("/explain why does this loop stop?", "Explain", "why does this loop stop?")]
    [InlineData("/review  the error handling ", "Review", "the error handling")]
    [InlineData("/fix the off-by-one in the loop", "Fix", "the off-by-one in the loop")]
    [InlineData("/refactor extract the parsing", "Refactor", "extract the parsing")]
    [InlineData("/doc only the public API", "Doc", "only the public API")]
    [InlineData("/test the empty input", "Test", "the empty input")]
    public void TheRouter_KeepsWhatWasTypedAfterTheCommand(string typed, string kind, string request)
    {
        var action = Assert.IsType<SlashCodeAction>(SlashCommandRouter.Route(typed, []));
        Assert.Equal(Enum.Parse<SlashCodeActionKind>(kind), action.Kind);
        Assert.Equal(request, action.Request);
    }

    [Fact]
    public void ACommandAlone_HasNoRequest()
    {
        // Reference arm: the menu and the bare command run the action as they always did.
        Assert.Null(Assert.IsType<SlashCodeAction>(SlashCommandRouter.Route("/fix", [])).Request);
        Assert.Equal(InPlaceCodeActionPrompts.FixInstruction,
                     InPlaceCodeActionPrompts.BuildAsync(SlashCodeActionKind.Fix, null, "int x;", CancellationToken.None)
                                             .GetAwaiter().GetResult().Instruction);
        Assert.Equal("Explain.", CodeActionRequest.InTheQuestion("Explain.", null));
    }

    [Fact]
    public async Task AnInPlaceAction_GivesTheRequestToTheModel()
    {
        var (_, instruction) = await InPlaceCodeActionPrompts.BuildAsync(
            SlashCodeActionKind.Refactor, null, "int x;", CancellationToken.None, "extract the parsing");

        Assert.StartsWith(InPlaceCodeActionPrompts.RefactorInstruction, instruction, StringComparison.Ordinal);
        Assert.Contains("extract the parsing", instruction, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTestsGenerated_FollowTheRequest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"test-request-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, "Parser.cs");
            File.WriteAllText(source, "public class Parser { public int Parse(string s) => s.Length; }");
            List<ChatMessageDto>? sent = null;
            var fake = new FakeInferenceProvider
            {
                OnChatRequest = (_, messages, _, _) =>
                {
                    sent = messages;
                    return Task.FromResult(new ChatTurnResult("", null, 0, 0));
                },
            };

            await TestGenerationPlanner.PlanAsync(fake, "m", source, File.ReadAllText(source), CancellationToken.None,
                                                  request: "the empty input");

            Assert.NotNull(sent);
            Assert.Contains("the empty input", sent!.Single(m => m.Role == "user").Content, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void BothEditors_SendTheRequestWithTheAction()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var vm = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs"));
        Assert.Contains("RunCodeActionCommandAsync(code.Kind, code.Request, ct)", vm, StringComparison.Ordinal);
        Assert.Contains("RunInPlaceCodeActionAsync(kind, ct, request)", vm, StringComparison.Ordinal);
        Assert.Contains("RunGenerateTestsAsync(ct, request)", vm, StringComparison.Ordinal);
        Assert.Contains("CodeActionRequest.InTheQuestion(", vm, StringComparison.Ordinal);

        var host = ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostServer.cs"));
        Assert.Contains("BuildAsync(kind, p.Path, p.Text, cts.Token, p.Request)", host, StringComparison.Ordinal);
        Assert.Contains("RunGenerateTestsAsync(s, test.Request, cts.Token)",
                        ConventionCoverageTests.CodeOnly(Path.Combine(root, "Inferpal.Host", "HostSlashCommands.cs")), StringComparison.Ordinal);

        var ts = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("this.runCodeAction(codeAction, host, ChatViewProvider.requestOf(prompt))", ts, StringComparison.Ordinal);
        Assert.Contains("ChatViewProvider.requestOf(prompt));", ts, StringComparison.Ordinal);
        Assert.Contains("request,", ts, StringComparison.Ordinal);
    }
}

public partial class HostServerTests
{
    [Fact]
    public async Task CodeActionRun_GivesTheTypedRequestToTheModel()
    {
        using var h = CreateHarness();
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        List<ChatMessageDto>? sent = null;
        h.Fake.OnChatRequest = (_, messages, _, _) =>
        {
            sent = messages;
            return Task.FromResult(new ChatTurnResult("int y = 2;", null, 0, 0));
        };

        await h.Client.InvokeWithParameterObjectAsync<Host.CodeActionResultDto>(
                "codeAction/run", new { kind = "fix", text = "int x = 1;", selStart = 0, selEnd = 0, request = "use a long" })
            .WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Contains("use a long", sent!.Single(m => m.Role == "user").Content, StringComparison.Ordinal);
    }
}
