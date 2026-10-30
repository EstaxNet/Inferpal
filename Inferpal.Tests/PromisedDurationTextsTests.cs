using System.Globalization;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A text that promises a duration or a gesture says what the code does, in every language: "until the editor restarts"
/// was false (an "Always" grant falls when another workspace opens), "Asked once for this workspace" too (asked again
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
    public void TheAlwaysGrant_SaysItEndsWithTheWorkspace()
    {
        var english = English(() => Strings.ApprovalAlwaysTooltip("run_command"));
        Assert.Contains("another workspace", english, StringComparison.Ordinal);
        Assert.DoesNotContain("until the editor restarts", english, StringComparison.Ordinal);
    }

    /// <summary>The fact the texts state, read in the code: a grant read under another root is forgotten, and the
    /// consent is kept for the process alone (a static, never a file).</summary>
    [Fact]
    public void TheDurationsTheTextsState_AreWhatTheCodeDoes()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var approvals = ConventionCoverageTests.CodeOnly(
            System.IO.Path.Combine(root, "Inferpal.Core", "Services", "Execution", "ApprovalServiceBase.cs"));
        var granted = approvals[approvals.IndexOf("private bool Granted(", StringComparison.Ordinal)..];
        Assert.Contains("ForgetGrantsOfAnotherRoot()", granted[..granted.IndexOf('}')], StringComparison.Ordinal);

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
