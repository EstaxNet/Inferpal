using System.IO;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// When the first run cannot complete, it says so <b>in the conversation</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ The first run is the one moment where silence costs the most. The window opens on an empty
/// conversation, the discovery runs detached, and its whole body sat under
/// <c>catch (Exception ex) { Diagnostics.Swallow("Rag.FirstRun", ex); }</c>: a read-only
/// <c>%AppData%</c>, a probe that throws, anything at all, and a new user gets a chat that did
/// nothing — no welcome, no model, no cause. The ring of <c>/diagnostics</c> is not where someone
/// who has just installed an extension looks.
/// </para>
/// <para>
/// ⚠ The rule was already written twice in this repository — <i>"a palette command that does
/// NOTHING is indistinguishable from one that failed"</i>, and <i>what fails after the fact is said
/// in the conversation, not in a log</i> — and this is the site where the conversation is empty and
/// waiting, which is the best place it will ever have.
/// </para>
/// <para>
/// ⚠ Not executable from the suite (Remote UI, a detached startup task): a source scan with its
/// witnesses, like <c>ArchiveFailureSilenceTests</c> and <c>InProcContractTests</c>.
/// </para>
/// </remarks>
public class FirstRunSilenceTests
{
    private static string FirstRunBody()
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));

        const string anchor = "private async Task StartFirstRunDiscoveryAsync()";
        var start = code.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, "the first-run entry point has been renamed — the rule reads nothing.");

        // Brace matching from the method's opening brace: a window of N characters would spill into
        // the next member, which is how a guard of this shape was already fooled once in this repo.
        var open  = code.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..i];
        }
        Assert.Fail("unterminated method body.");
        return string.Empty;
    }

    [Fact]
    public void AFirstRunThatCouldNotComplete_SaysSoInTheConversation()
    {
        var body = FirstRunBody();

        // WITNESS: this really is the detached first run, and it really still catches everything.
        Assert.Contains("RunSetupDiscoveryAsync", body, StringComparison.Ordinal);
        Assert.Contains("catch (Exception", body, StringComparison.Ordinal);

        var caught = body[body.IndexOf("catch (Exception", StringComparison.Ordinal)..];

        // The cause reaches the user through the presenter the first run already uses, and it is
        // traced as well: the ring is for the developer, the bubble for the person looking at it.
        Assert.Contains("FirstRunPresentAsync", caught, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Swallow", caught, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation is not a failure — the one outcome that must stay silent, as everywhere else in
    /// this product. Without this, "say everything" would announce a closing window as a breakdown.
    /// </summary>
    [Fact]
    public void ACancelledFirstRun_StaysSilent()
    {
        Assert.Contains("catch (OperationCanceledException) { }", FirstRunBody(), StringComparison.Ordinal);
    }

    /// <summary>The sentence names a gesture that exists: <c>/setup</c> re-runs the discovery.</summary>
    [Fact]
    public void TheSentence_NamesARemedyTheRouterKnows()
    {
        var message = Inferpal.Localization.Strings.FirstRunFailed("boom");

        Assert.Contains("/setup", message, StringComparison.Ordinal);
        Assert.Contains("boom", message, StringComparison.Ordinal);
        Assert.NotNull(Inferpal.Services.SlashCommandRouter.Catalog.FirstOrDefault(c => c.Cmd == "/setup"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
