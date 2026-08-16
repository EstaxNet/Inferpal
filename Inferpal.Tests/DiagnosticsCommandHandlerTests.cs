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

    // ── §24: /diagnostics export — the support bundle ───────────────────────────

    private static DiagnosticsExportContext Ctx(
        Inferpal.Config.InferpalConfig? config = null, string? root = null) =>
        new(config ?? new Inferpal.Config.InferpalConfig(), "Test front-end",
            BackendStatus: "connected", WorkspaceRoot: root);

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
