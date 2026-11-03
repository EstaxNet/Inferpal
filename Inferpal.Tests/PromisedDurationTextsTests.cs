using System.Globalization;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A text that promises a duration or a gesture says what the code does, in every language: "until the editor restarts"
/// was false (an "Always" grant holds for its workspace until a restart), "Asked once for this workspace" too (asked again
/// after a restart), and "/clear starts a new one" named a gesture an ACP thread refuses.
/// </summary>
[Collection(CultureSerialCollection.Name)]   // reads the localized sentences
public class PromisedDurationTextsTests
{
    private static readonly string[] Cultures = ["en", "de", "es", "fr", "it", "ja", "ko", "pl", "ru", "zh-CN"];

    private static IEnumerable<(string Culture, string Text)> InEveryLanguage(Func<string> read)
    {
        var saved = Strings.OverrideCulture?.Name;
        var texts = new List<(string, string)>();
        try
        {
            foreach (var c in Cultures)
            {
                Strings.ApplyLanguage(c);
                texts.Add((c, read()));
            }
        }
        finally { Strings.ApplyLanguage(saved); }
        return texts;
    }

    private static string English(Func<string> read) => InEveryLanguage(read).First(t => t.Culture == "en").Text;

    [Fact]
    public void TheAlwaysGrant_SaysItHoldsForTheWorkspace_UntilARestart()
    {
        // Where another folder restarts the host (VS Code, ACP), no promise of coming back; Visual Studio keeps it.
        var tooltip = English(() => Inferpal.Services.Presentation.ApprovalCard.Build(
            new Inferpal.Services.Presentation.ApprovalPrompt("run_command", "dotnet test", "dotnet test", null, "Run?"), "C:\\repo").AlwaysTooltip);
        Assert.Equal(English(() => Strings.ApprovalAlwaysTooltipUntilRestart("run_command")), tooltip);
        Assert.DoesNotContain("another solution", English(() => Strings.ApprovalAlwaysTooltipUntilRestart("run_command")), StringComparison.Ordinal);
        var vs = ConventionCoverageTests.CodeOnly(System.IO.Path.Combine(ConventionCoverageTests.RepoRoot(),
            "Inferpal", "Services", "VsIntegration", "VsApprovalService.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(vs, @"ApprovalCard\.Build\(prompt, RootDir, keepsGrantsAcrossSolutions: true\)").Count);

        var english = English(() => Strings.ApprovalAlwaysTooltip("run_command"));
        Assert.Contains("until a restart", english, StringComparison.Ordinal);
        Assert.Contains("kept when you come back from another solution", english, StringComparison.Ordinal);
        Assert.DoesNotContain("until another workspace opens", english, StringComparison.Ordinal);
    }

    /// <summary>The fact the texts state, read in the code: a grant is read under its own root only, and the
    /// consent is kept for the process alone (a static, never a file).</summary>
    [Fact]
    public void TheDurationsTheTextsState_AreWhatTheCodeDoes()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var approvals = ConventionCoverageTests.CodeOnly(
            System.IO.Path.Combine(root, "Inferpal.Core", "Services", "Execution", "ApprovalServiceBase.cs"));
        // Read under the root open now, kept per root: never another workspace's, found again on coming back.
        var granted = approvals[approvals.IndexOf("private bool Granted(", StringComparison.Ordinal)..];
        Assert.Contains("GrantsKey(_rootDir())", granted[..granted.IndexOf("}", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("Dictionary<string, HashSet<string>> _sessionAllowed = new(PathComparer.Default)", approvals, StringComparison.Ordinal);

        var consent = ConventionCoverageTests.CodeOnly(
            System.IO.Path.Combine(root, "Inferpal.Core", "Services", "Execution", "WorkspaceCodeConsent.cs"));
        Assert.Contains("private static readonly Dictionary<string, bool> Answered", consent, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Write", consent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCodeConsent_SaysItIsAskedAgainAfterARestart() =>
        Assert.EndsWith("Asked once for this workspace, and again after a restart.",
                        English(() => Strings.ProjectCodeConsent("C:\\repo")), StringComparison.Ordinal);

    /// <summary>The overflow message is built in the Core, for every front-end: its remedy is one they all have.</summary>
    [Fact]
    public void TheEarlierConversationsRemedy_NamesNoCommand_InAnyLanguage()
    {
        var texts = InEveryLanguage(() => Strings.ContextPartEarlier(1234)).ToList();
        Assert.Equal(Cultures.Length, texts.Count);                                    // witness: every language read
        Assert.All(texts, t => Assert.Contains("1234", t.Text, StringComparison.Ordinal));
        Assert.All(texts, t => Assert.DoesNotContain("/clear", t.Text, StringComparison.Ordinal));
    }
}
