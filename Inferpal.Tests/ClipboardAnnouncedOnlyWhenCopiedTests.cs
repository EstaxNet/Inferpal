using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio announces a copy to the clipboard only over a copy that landed — otherwise it says not to paste.
/// </summary>
/// <remarks>
/// ⚠ The copy is best-effort (another program can hold the clipboard), and it returned nothing: <c>/diagnostics export</c>
/// said "Support bundle copied to clipboard — paste it into a GitHub issue" over a failed copy, and the user pasted
/// whatever the clipboard held before — a secret as likely as not — into a public issue. VS Code already warned. The
/// clipboard needs an interactive desktop, so these rules read the source.
/// </remarks>
public class ClipboardAnnouncedOnlyWhenCopiedTests
{
    private static string ToolWindow(string file) => ConventionCoverageTests.CodeOnly(Path.Combine(
        ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal", "ToolWindow", file));

    [Fact]
    public void TheHelper_SaysWhetherTheCopyLanded()
    {
        var helper = ToolWindow("ClipboardHelper.cs");

        Assert.Contains("public static bool TrySet(", helper);
        Assert.Contains("System.Windows.Clipboard.SetText(payload); copied = true;", helper);
        // A copy still waiting when the budget runs out is not known to have landed.
        var timedOut = helper.IndexOf("if (!thread.Join(JoinBudget))", StringComparison.Ordinal);
        var landed   = helper.IndexOf("return copied;", StringComparison.Ordinal);
        Assert.True(timedOut > 0 && landed > timedOut, "the bounded wait moved — the rule reads nothing");   // WITNESS
        Assert.Contains("return false;", helper[timedOut..landed]);
    }

    [Fact]
    public void EveryCommandThatAnnouncesACopy_ReadsWhetherItLanded()
    {
        var slash = ToolWindow("InferpalToolWindowData.SlashCommands.cs");

        foreach (var context in new[] { "Clipboard.CopyDiagnostics", "Clipboard.CopySnippet" })
        {
            var call = slash.IndexOf($"\"{context}\")", StringComparison.Ordinal);
            Assert.True(call > 0, $"{context}: the copy moved — the rule reads nothing");   // WITNESS
            var line = slash[slash.LastIndexOf('\n', call)..slash.IndexOf('\n', call)];
            Assert.Contains("!ClipboardHelper.TrySet(", line);
            var after = slash[call..Math.Min(slash.Length, call + 200)];
            Assert.Contains("Strings.ClipboardNotCopied", after);
        }
    }
}
