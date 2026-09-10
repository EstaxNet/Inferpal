using Inferpal.Config;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A declared MCP server that fails to start left <b>no</b> readable trace (measured 2026-09-10):
/// the error was filed in <c>McpServerStatus.Error</c> and read by the Visual Studio settings window
/// alone. In VS Code — no panel, no diagnostic entry, no message — the user simply saw their tools
/// missing. And the support bundle, the only artifact a maintainer ever receives, carried nothing
/// but "MCP: on".
/// </summary>
public class McpBundleVisibilityTests
{
    private static InferpalConfig Config() => new() { McpEnabled = true };

    private static string Export(IReadOnlyList<string>? mcp) =>
        DiagnosticsCommandHandler.Handle(
            ["/diagnostics", "export"],
            new DiagnosticsExportContext(Config(), "tests", McpServers: mcp))
        .CopyToClipboard ?? string.Empty;

    [Fact]
    public void ADeadServer_IsNamedInTheBundle_WithItsReason()
    {
        var bundle = Export(["github — connected, 12 tool(s)",
                             "filesystem — NOT connected: executable not found"]);

        Assert.Contains("MCP servers", bundle);
        Assert.Contains("filesystem — NOT connected: executable not found", bundle);
    }

    [Fact]
    public void AHealthyServer_IsNamedToo_BecauseSilenceIsNotAnAnswer()
    {
        // Listing only failures would make "nothing shown" ambiguous: no server at all, or all of
        // them healthy? The report has to settle that for its reader.
        var bundle = Export(["github — connected, 12 tool(s)"]);

        Assert.Contains("github — connected, 12 tool(s)", bundle);
    }

    [Fact]
    public void NoServerConfigured_AddsNoSection()
    {
        // WITNESS: without it, a section rendered unconditionally would pass both tests above and
        // fill every report with an empty heading.
        Assert.DoesNotContain("MCP servers", Export([]));
        Assert.DoesNotContain("MCP servers", Export(null));
    }

    [Fact]
    public void TheBundleStillCarriesItsOtherFacts()
    {
        // WITNESS: the MCP section is added, it replaces nothing — a bundle stripped of the rest
        // would pass the three tests above.
        var bundle = Export(["github — connected, 1 tool(s)"]);

        Assert.Contains("Toggles", bundle);
        Assert.Contains("Recent diagnostics", bundle);
    }
}
