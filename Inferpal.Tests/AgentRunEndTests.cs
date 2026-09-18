using System.IO;
using System.Linq;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// How an agent run <b>ended</b>, when it is not because the model had finished.
/// </summary>
/// <remarks>
/// <c>OrchestratorResult</c> has carried <c>ReachedIterationLimit</c> and <c>WasLoopDetected</c>
/// forever, set carefully at every site — and <b>nobody read them</b>: not the VM, not the host, not
/// the webview. Yet at the iteration ceiling the answer returned is a SYNTHESIS of what had been
/// gathered (<c>MsgIterationLimit</c> is only the fallback when the synthesis fails): the user
/// received a fluent text, indistinguishable from a task carried through, and could act on it
/// believing the investigation complete.
///
/// ⚠ The original arbitration is KEPT — "do not alarm when real work was done": the answer stays,
/// nothing replaces it. What changes is that a discreet line follows it.
///
/// ⚠ These rules read the source. Exercising the ceiling would take twenty round trips with a real
/// model; what must be prevented is these facts becoming dead again — and a scan sees that.
/// </remarks>
public class AgentRunEndTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string CoreCode(params string[] parts) =>
        ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

    private static string TsCode(params string[] parts) =>
        SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts))));

    /// <summary>
    /// Both facts are READ by both front-ends. That is the rule that was missing: they were written
    /// by the Core and consumed by nothing, so their disappearance would have broken nothing.
    /// </summary>
    [Theory]
    [InlineData("ReachedIterationLimit")]
    [InlineData("WasLoopDetected")]
    public void HowARunEnded_IsReadByBothFrontEnds(string flag)
    {
        var vm   = CoreCode("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");
        var host = CoreCode("Inferpal.Host", "HostServer.cs");

        Assert.Contains(flag, vm,   StringComparison.Ordinal);
        Assert.Contains(flag, host, StringComparison.Ordinal);
    }

    /// <summary>And the sentence really does cross the RPC to the VS Code screen — otherwise the
    /// host would compose it for nobody.</summary>
    [Fact]
    public void TheNotice_ReachesTheVsCodeScreen()
    {
        Assert.Contains("EndNotice", CoreCode("Inferpal.Host", "HostProtocol.cs"), StringComparison.Ordinal);

        foreach (var file in new[]
                 {
                     Path.Combine("vscode", "src", "protocol.ts"),
                     Path.Combine("vscode", "src", "chatViewProvider.ts"),
                     Path.Combine("vscode", "src", "webview", "main.ts"),
                 })
            Assert.Contains("endNotice", TsCode(file.Split(Path.DirectorySeparatorChar)), StringComparison.Ordinal);
    }

    /// <summary>
    /// ⚠ The answer is never REPLACED by the notice. That is the original arbitration, and losing it
    /// would make the product noisier than it was before the repair: the VM inserts one more bubble,
    /// it does not rewrite <c>agentFinalResponse</c>.
    /// </summary>
    [Fact]
    public void TheAnswerIsKept_TheNoticeIsAddedAfterIt()
    {
        var vm = CoreCode("Inferpal", "ToolWindow", "InferpalToolWindowData.ChatTurn.cs");

        Assert.Contains("InsertThemed(ChatMessageItem.AssistantMsg(agentEndNotice))", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("agentFinalResponse = agentEndNotice", vm, StringComparison.Ordinal);
        Assert.DoesNotContain("agentFinalResponse += ", vm, StringComparison.Ordinal);
    }

    /// <summary>Both sentences exist in all ten languages — otherwise the notice would come out in
    /// English inside a translated panel, which <c>LocalizationCompletenessTests</c> already
    /// catches, but the rule belongs here with the rest of the fact.</summary>
    [Theory]
    [InlineData("AgentEndedAtIterationLimit")]
    [InlineData("AgentEndedOnRepeat")]
    public void BothNoticesAreDeclared(string key)
    {
        var strings = CoreCode("Inferpal.Core", "Localization", "Strings.cs");
        Assert.Contains(key, strings, StringComparison.Ordinal);
    }
}
