using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

[Collection("Diagnostics")]
public class DiagnosticsCommandHandlerTests : IDisposable
{
    public DiagnosticsCommandHandlerTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Diagnostics.Clear();
        Diagnostics.FileLoggingEnabled = false;
        Diagnostics.LogPathOverride = null;
    }

    private static string[] Cmd(params string[] args) => ["/diagnostics", .. args];

    [Fact]
    public void List_Empty_ReturnsEmptyNotice()
    {
        Assert.Equal(Strings.DiagnosticsEmpty, DiagnosticsCommandHandler.Handle(Cmd()).Message);
    }

    [Fact]
    public void List_WithEntries_ShowsHeaderContextAndDetail_MostRecentFirst()
    {
        Diagnostics.Swallow("CtxOld", new InvalidOperationException("first"));
        Diagnostics.Swallow("CtxNew", new InvalidOperationException("second"));

        var msg = DiagnosticsCommandHandler.Handle(Cmd()).Message;

        Assert.Contains(Strings.DiagnosticsHeader, msg);
        Assert.Contains("CtxOld", msg);
        Assert.Contains("CtxNew", msg);
        Assert.Contains("second", msg);
        // Most recent first: CtxNew appears before CtxOld.
        Assert.True(msg.IndexOf("CtxNew", StringComparison.Ordinal) < msg.IndexOf("CtxOld", StringComparison.Ordinal));
    }

    /// <summary>
    /// The screen must say it is showing a <b>fragment</b> — the support bundle, in the same file,
    /// already announces it ("N of M").
    /// </summary>
    /// <remarks>
    /// This is the screen the user opens to find their failure: a capped listing that reads as
    /// complete makes them conclude "there is nothing else", while the ring keeps up to
    /// <see cref="Diagnostics.Capacity"/> entries and up to 170 stayed underneath.
    /// </remarks>
    [Fact]
    public void List_BeyondTheCap_SaysHowManyOfHowMany()
    {
        for (var i = 0; i < 45; i++) Diagnostics.Record("Ctx" + i, "detail " + i);

        var msg = DiagnosticsCommandHandler.Handle(Cmd()).Message;

        Assert.Contains(Strings.DiagnosticsShowing(30, 45), msg);
        Assert.Contains("Ctx44", msg);    // witness: the most recent ones really are there
        Assert.DoesNotContain("Ctx0 ", msg);
    }

    /// <summary>Reference arm: under the cap, no truncation sentence.</summary>
    [Fact]
    public void List_UnderTheCap_SaysNothingAboutTruncation()
    {
        for (var i = 0; i < 5; i++) Diagnostics.Record("Ctx" + i, "detail " + i);

        var msg = DiagnosticsCommandHandler.Handle(Cmd()).Message;

        Assert.Contains("Ctx4", msg);
        Assert.DoesNotContain(Strings.DiagnosticsShowing(30, 5), msg);
    }

    [Fact]
    public void Clear_EmptiesRingAndConfirms()
    {
        Diagnostics.Record("a", "1");

        var msg = DiagnosticsCommandHandler.Handle(Cmd("clear")).Message;

        Assert.Equal(Strings.DiagnosticsCleared, msg);
        Assert.Empty(Diagnostics.Snapshot());
    }

    [Fact]
    public void On_EnablesFileLogging()
    {
        var msg = DiagnosticsCommandHandler.Handle(Cmd("on")).Message;

        Assert.Equal(Strings.DiagnosticsFileOn, msg);
        Assert.True(Diagnostics.FileLoggingEnabled);
    }

    [Fact]
    public void Off_DisablesFileLogging()
    {
        Diagnostics.FileLoggingEnabled = true;

        var msg = DiagnosticsCommandHandler.Handle(Cmd("off")).Message;

        Assert.Equal(Strings.DiagnosticsFileOff, msg);
        Assert.False(Diagnostics.FileLoggingEnabled);
    }

    // A mistyped argument returned the list: "of" left file logging on without a word, "off now"
    // switched off and ignored the rest. Only the documented shapes act.
    [Theory]
    [InlineData("of")]
    [InlineData("clr")]
    [InlineData("off now")]
    public void AnUnknownArgument_ShowsTheUsage_AndChangesNothing(string argument)
    {
        Diagnostics.FileLoggingEnabled = true;

        var msg = DiagnosticsCommandHandler.Handle(Cmd(argument.Split(' '))).Message;

        Assert.Equal(Strings.SlashUsage("/diagnostics [list | clear | on | off | export]"), msg);
        Assert.True(Diagnostics.FileLoggingEnabled);
    }

    // Witness: `list`, which worked through the fallback, stays an accepted shape.
    [Fact]
    public void List_IsAcceptedExplicitly() =>
        Assert.Equal(Strings.DiagnosticsEmpty, DiagnosticsCommandHandler.Handle(Cmd("list")).Message);

    // ── §24: /diagnostics export — the support bundle ───────────────────────────

    private static DiagnosticsExportContext Ctx(
        Inferpal.Config.InferpalConfig? config = null, string? root = null) =>
        new(config ?? new Inferpal.Config.InferpalConfig(), "Test front-end",
            BackendStatus: "connected", WorkspaceRoot: root);

    /// <summary>
    /// The in-process warning fires on <b>proof of absence</b> only.
    /// </summary>
    /// <remarks>
    /// That distinction is the one that produced a run of false verdicts: "I have no proof" read
    /// as "it is dead". Here <c>null</c> is the VS Code case (no in-process peer) and the case of a
    /// caller that does not know - it must stay quiet, or every VS Code user would read that their
    /// ghost text is broken.
    /// </remarks>
    [Theory]
    [InlineData(null,  false)]
    [InlineData(true,  false)]
    [InlineData(false, true)]
    public void List_WarnsAboutInProc_OnlyOnProofOfAbsence(bool? inProcLoaded, bool expectWarning)
    {
        var msg = DiagnosticsCommandHandler.Handle(Cmd(), null, inProcLoaded).Message;
        Assert.Equal(expectWarning, msg.Contains(Strings.DiagnosticsInProcDead, StringComparison.Ordinal));
    }

    [Fact]
    public void List_InProcWarning_SurvivesTheEmptyShortcut()
    {
        // ⚠ The in-process failure produces NO diagnostic entry at all: it happens inside devenv,
        // in an assembly this process never loaded. Without this case the warning would be
        // short-circuited by "no entries" for exactly the people who need it.
        Assert.Empty(Diagnostics.Snapshot());

        var msg = DiagnosticsCommandHandler.Handle(Cmd(), null, inProcLoaded: false).Message;

        Assert.Contains(Strings.DiagnosticsInProcDead, msg);
        Assert.Contains(Strings.DiagnosticsEmpty, msg);
    }

    /// <summary>
    /// The bundle names the window the server REALLY loaded when it is the smaller one. Configured at 100 000 under
    /// LM Studio loading 4 096 (issue #8), a bundle saying "Context window: 100000 tokens" pointed whoever read the
    /// report away from the one number that explained it.
    /// </summary>
    [Fact]
    public void Export_NamesTheLoadedWindow_WhenItIsSmallerThanTheConfiguredOne()
    {
        var ctx = Ctx(new Inferpal.Config.InferpalConfig { ContextWindowSize = 100_000 }) with { WindowInUse = 4_096 };

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), ctx).CopyToClipboard!;

        Assert.Contains("100000 tokens configured", bundle);
        Assert.Contains("4096 loaded by the server", bundle);
    }

    [Theory]
    [InlineData(0)]         // no turn has measured it yet
    [InlineData(8_192)]     // the configured one: nothing more to say
    public void Export_SaysNothingMore_WhenTheLoadedWindowIsUnknownOrTheSame(int inUse)
    {
        // Reference arm: an ordinary report keeps its ordinary line.
        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx() with { WindowInUse = inUse }).CopyToClipboard!;

        Assert.Contains("8192 tokens configured", bundle);
        Assert.DoesNotContain("loaded by the server", bundle);
    }

    [Fact]
    public void Export_ReportsTheInProcHalf_WhenTheCallerKnowsIt()
    {
        var ctx = Ctx() with { InProcHalf = "NOT LOADED (measured)" };

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), ctx).CopyToClipboard!;

        Assert.Contains("In-process half", bundle);
        Assert.Contains("NOT LOADED (measured)", bundle);
        // And the line disappears when the caller knows nothing, rather than showing a blank that
        // would read as a measurement.
        Assert.DoesNotContain("In-process half", DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx()).CopyToClipboard!);
    }

    [Fact]
    public void Export_CopiesExactlyWhatItShows()
    {
        // Transparency contract: the chat renders the very text that lands on the clipboard —
        // the user reads what they are about to paste into a public issue.
        var result = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx());

        Assert.NotNull(result.CopyToClipboard);
        Assert.StartsWith(result.CopyToClipboard!, result.Message);
        Assert.EndsWith(Strings.DiagnosticsExported, result.Message);
    }

    [Fact]
    public void Export_ContainsVersionFrontEndProviderAndToggles()
    {
        var config = new Inferpal.Config.InferpalConfig { Provider = "ollama" };

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(config)).CopyToClipboard!;

        Assert.Contains("Inferpal support bundle", bundle);
        Assert.Contains("Test front-end", bundle);
        Assert.Contains("ollama", bundle);
        Assert.Contains("connected", bundle);
        Assert.Contains("Context window", bundle);
        Assert.Contains("Toggles", bundle);
    }

    [Fact]
    public void Export_NeverContainsTheApiKey()
    {
        var config = new Inferpal.Config.InferpalConfig { ApiKey = "sk-SECRET-VALUE-123" };

        var result = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(config));

        Assert.DoesNotContain("sk-SECRET-VALUE-123", result.Message);
        Assert.Contains("set (redacted)", result.CopyToClipboard!);
    }

    [Fact]
    public void Export_RedactsRemoteEndpoints_KeepsLoopback()
    {
        Assert.Contains("localhost", DiagnosticsCommandHandler.RedactEndpoint("http://localhost:11434"));
        Assert.Contains("127.0.0.1", DiagnosticsCommandHandler.RedactEndpoint("http://127.0.0.1:1234/v1"));
        // A LAN hostname identifies the user's network: redacted, port included.
        Assert.Equal("remote endpoint (redacted)",
            DiagnosticsCommandHandler.RedactEndpoint("http://llm.internal.example:11434"));
        Assert.Equal("invalid endpoint", DiagnosticsCommandHandler.RedactEndpoint("not a url"));
    }

    [Fact]
    public void Export_SanitizesWorkspaceAndProfilePathsInEntries()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Diagnostics.Record("Ctx", $@"failed on {home}\secret.txt and C:\repo\src\A.cs");

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(root: @"C:\repo")).CopyToClipboard!;

        Assert.DoesNotContain(home, bundle);
        Assert.Contains(@"~\secret.txt", bundle);
        Assert.Contains(@"<workspace>\src\A.cs", bundle);
    }

    /// <summary>
    /// The same scrub as the ring's entries, on the MCP lines — which also carry text from outside.
    /// </summary>
    /// <remarks>
    /// ⚠ The message below is <b>measured</b>, not invented: it is exactly what .NET puts in the
    /// exception when <c>Process.Start</c> fails, and exactly what <c>McpStdioClient</c> puts in
    /// <c>LastError</c> — hence what <c>DescribeForBundle</c> returns. A misconfigured stdio MCP
    /// server therefore published the home directory and the repository path into the file the user
    /// pastes into a public issue, while the same bundle carefully replaces them with <c>~</c> and
    /// <c>&lt;workspace&gt;</c> two lines below. One rule, two readers, one of them holding it.
    /// </remarks>
    [Fact]
    public void Export_ScrubsTheMcpLines_NotOnlyTheRingEntries()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = "monserveur — NOT connected: An error occurred trying to start process "
                 + $@"'{home}\tools\mcp.exe' with working directory 'C:\repo'. --api-key sk-abc12345678";

        var bundle = DiagnosticsCommandHandler.Handle(
            Cmd("export"), Ctx(root: @"C:\repo") with { McpServers = [line] }).CopyToClipboard!;

        Assert.Contains("monserveur", bundle);           // witness: the line really is in the bundle
        Assert.Contains("<workspace>", bundle);
        Assert.DoesNotContain(home, bundle);
        Assert.DoesNotContain("sk-abc12345678", bundle);
    }

    /// <summary>Same for the two other free-text fields coming from outside.</summary>
    [Fact]
    public void Export_ScrubsTheBackendAndInProcLines()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(root: @"C:\repo") with
        {
            BackendStatus = $@"unreachable — probed {home}\.ollama",
            InProcHalf    = $@"fim NOT LOADED: sidecar missing at {home}\ext\Inferpal.Fim.exe",
        }).CopyToClipboard!;

        Assert.Contains("unreachable", bundle);          // witness: both lines are there
        Assert.Contains("fim NOT LOADED", bundle);
        Assert.DoesNotContain(home, bundle);
    }

    [Fact]
    public void Export_WithoutContext_FallsBackToList()
    {
        // Defensive: a caller that cannot supply the context gets the ordinary listing,
        // never a half-empty bundle.
        var result = DiagnosticsCommandHandler.Handle(Cmd("export"));

        Assert.Null(result.CopyToClipboard);
        Assert.Equal(Strings.DiagnosticsEmpty, result.Message);
    }

    [Fact]
    public void Export_FlagsDisabledSecurityAlertsLoudly()
    {
        var config = new Inferpal.Config.InferpalConfig { SecurityAlertsDisabled = true };

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(config)).CopyToClipboard!;

        Assert.Contains("security alerts DISABLED", bundle);
    }
}
