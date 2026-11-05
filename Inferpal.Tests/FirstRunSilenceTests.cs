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

        const string anchor = "private async Task<bool> StartFirstRunDiscoveryAsync()";
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

    /// <summary>
    /// One fact decides what the heartbeat says of a first failed check: whether the first run spoke. Each deciding on its
    /// own reading of the settings, a window that lost the race said nothing at all. Read once: Retry is a gesture, and a
    /// gesture is answered.
    /// </summary>
    [Fact]
    public void TheHeartbeat_WaitsForTheFirstRun_AndSpeaksUnlessItDid()
    {
        var dir = Path.Combine(RepoRoot(), "Inferpal", "ToolWindow");
        var connection = ConventionCoverageTests.CodeOnly(Path.Combine(dir, "InferpalToolWindowData.Connection.cs"));

        Assert.Contains("var firstRunSpoke = !ok && await FirstRunSpokeAsync(ct);", connection, StringComparison.Ordinal);
        Assert.Contains("presenter.Evaluate(ok, _client.ConnectionRefusal, firstRunSpoke)", connection, StringComparison.Ordinal);
        // What the first run adopted, or took from the file, is read after it: the sentence names the address in force.
        Assert.Contains("if (!ok) (url, switchTo) = (_config.BaseUrl, InferenceProviderFactory.PendingSwitch(_client, _config.Provider));",
                        connection, StringComparison.Ordinal);
        // A check started before /setup said a switch does not erase that it was said.
        Assert.Contains("if (switchTo is not null || InferenceProviderFactory.PendingSwitch(_client, _config.Provider) is null)",
                        connection, StringComparison.Ordinal);
        // After the pre-flight flag: a question sent during the wait is refused, not sent to a server that did not answer.
        Assert.True(connection.IndexOf("_isBackendReachable = ok;", StringComparison.Ordinal)
                    < connection.IndexOf("await FirstRunSpokeAsync(ct)", StringComparison.Ordinal));
        Assert.Contains("_firstRunAtStart = StartFirstRunDiscoveryAsync();",
                        ConventionCoverageTests.CodeOnly(Path.Combine(dir, "InferpalToolWindowData.Construction.cs")), StringComparison.Ordinal);

        var spoke = Discovery(from: "private async Task<bool> FirstRunSpokeAsync(");
        Assert.Contains("Interlocked.Exchange(ref _firstRunAtStart, null)", spoke, StringComparison.Ordinal);
        Assert.Contains("discovery.WaitAsync(FirstRunWait, ct)", spoke, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a card the first run says nothing and answers false, so the heartbeat says the server is down, with its
    /// remedies: where no card comes (no winget, a server that wants a key), that sentence is all there is.
    /// </summary>
    [Fact]
    public void AFirstRunWithoutACard_LeavesTheServerToTheHeartbeat()
    {
        var discovery = Discovery();
        var automatic = discovery[discovery.IndexOf("if (automatic)", StringComparison.Ordinal)..];
        automatic = automatic[..automatic.IndexOf("await present((client.ConnectionRefusal", StringComparison.Ordinal)];

        Assert.Contains("&& await OfferServerInstallAsync(", automatic, StringComparison.Ordinal);
        Assert.Contains("return client.ConnectionRefusal is null", automatic, StringComparison.Ordinal);
        Assert.DoesNotContain("present(", automatic, StringComparison.Ordinal);
    }

    /// <summary>
    /// An adopted server's switch is said once, last in the bubble that names the server: it came out alone before it, and
    /// the heartbeat said it again. Marked only once on screen. The Send refusal answers a gesture and stays.
    /// </summary>
    [Fact]
    public void AnAdoptedServer_IsSaidOnce_InTheBubbleThatNamesIt()
    {
        var discovery = Discovery();
        var say = discovery[discovery.IndexOf("async Task<bool> SayFoundAsync(", StringComparison.Ordinal)..];
        say = say[..say.IndexOf("return true;", StringComparison.Ordinal)];

        Assert.True(say.IndexOf("await present(", StringComparison.Ordinal) < say.IndexOf("_announcedSwitch = adoptedSwitch;", StringComparison.Ordinal));
        Assert.Contains("adoptedSwitch == _announcedSwitch", say, StringComparison.Ordinal);   // a tick said it first
        Assert.DoesNotContain("present(BackendSwitchPendingText(adoptedSwitch))", discovery, StringComparison.Ordinal);
        Assert.Contains("return await SayFoundAsync(text)", discovery, StringComparison.Ordinal);
        Assert.Contains("return await SayFoundAsync(foundNote + Strings.MsgFirstRunNoModels(", discovery, StringComparison.Ordinal);
    }

    private static string Discovery(string from = "private async Task<bool> RunSetupDiscoveryAsync(")
    {
        var code = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));
        var start = code.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "the discovery has been renamed: the rule reads nothing.");
        return code[start..];
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
