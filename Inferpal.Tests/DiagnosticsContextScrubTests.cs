using System;
using System.Linq;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The support bundle scrubs a diagnostic's <b>context</b>, not only its detail.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ A ring entry is a pair, and the two halves sit on the SAME line of the bundle —
/// <c>**{Context}** — {Detail}</c>. Only the detail went through <c>SanitizePaths</c>. Yet contexts
/// are built by interpolation from exactly the values the scrub exists for:
/// <c>FileHistoryService.Snapshot({filePath})</c> and <c>UndoRunCommandHandler.Relativise({path})</c>
/// carry absolute paths, and <c>DocCrawler.Fetch({url})</c> carries a raw URL.
/// </para>
/// <para>
/// ⭐ <c>SanitizePaths</c>' own remarks name the case it then missed: <i>"DocCrawler and McpOAuth
/// carry raw URLs. A refused <c>curl -H "Authorization: Bearer …"</c> therefore went out as written,
/// into the file the user pastes into a public issue."</i> The rule was written with its example,
/// and the example's own field was the unscrubbed one.
/// </para>
/// <para>
/// ⚠ Export only — <c>/diagnostics list</c> must keep showing the truth, because there the user is
/// debugging their own machine and needs the real path. That is the reference arm below, and
/// without it "scrub everything" would pass while destroying the channel's purpose.
/// </para>
/// </remarks>
[Collection("Diagnostics")]
public sealed class DiagnosticsContextScrubTests : IDisposable
{
    private const string Root   = @"C:\dev\SecretProject";
    private const string Needle = "Bearer sk-not-a-real-token-000";

    public DiagnosticsContextScrubTests() => Diagnostics.Clear();
    public void Dispose() => Diagnostics.Clear();

    private static string[] Cmd(params string[] args) => ["/diagnostics", .. args];

    private static DiagnosticsExportContext Ctx(string? root = Root) =>
        new(new Inferpal.Config.InferpalConfig(), "Test front-end",
            BackendStatus: "connected", WorkspaceRoot: root);

    [Fact]
    public void AContextCarryingTheWorkspacePath_IsScrubbedInTheBundle()
    {
        Diagnostics.Swallow($@"FileHistoryService.Snapshot({Root}\src\Secret.cs)", new InvalidOperationException("nope"));

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx()).CopyToClipboard!;

        // WITNESS: the entry really reached the bundle, so "does not contain" is a scrub and not an
        // empty ring answering for us.
        Assert.Contains("FileHistoryService.Snapshot", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain(Root, bundle, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AContextCarryingASecretInAUrl_IsScrubbedInTheBundle()
    {
        // The shape SanitizePaths' own remarks describe: DocCrawler builds its context from the URL.
        Diagnostics.Swallow($"DocCrawler.Fetch(https://internal.example.com/?auth={Needle})",
                            new InvalidOperationException("refused"));

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx(root: null)).CopyToClipboard!;

        Assert.Contains("DocCrawler.Fetch", bundle, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-not-a-real-token-000", bundle, StringComparison.Ordinal);
    }

    [Fact]
    public void TheList_StillShowsTheRealPath()
    {
        // REFERENCE ARM: /diagnostics list is the user debugging their own machine — scrubbing there
        // would take away the very path they need. A fix that scrubs everywhere fails here.
        Diagnostics.Swallow($@"FileHistoryService.Snapshot({Root}\src\Secret.cs)", new InvalidOperationException("nope"));

        var shown = DiagnosticsCommandHandler.Handle(Cmd("list")).Message;

        Assert.Contains(Root, shown, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnOrdinaryContext_IsUnchanged()
    {
        // REFERENCE ARM: the scrub must not rewrite a context that carries nothing sensitive, or the
        // bundle stops naming the component that failed.
        Diagnostics.Swallow("ProjectIndexService.Pass", new InvalidOperationException("nope"));

        var bundle = DiagnosticsCommandHandler.Handle(Cmd("export"), Ctx()).CopyToClipboard!;

        Assert.Contains("ProjectIndexService.Pass", bundle, StringComparison.Ordinal);
    }
}
