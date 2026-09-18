using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The LSP's fallback to the heuristic chunker cuts the feature off for the whole session, and it
/// did so <b>without a word</b>.
/// </summary>
/// <remarks>
/// This is the class this repository has already paid for six versions running on Roslyn: the user
/// turns <c>lspEnabled</c> on to get exact symbol boundaries, the server is not on the PATH or dies
/// once, and indexing falls back to its sliding window — a worse index, never announced. A missing
/// capability rendered as a result.
///
/// ⚠ These rules read the SOURCE, and they say so: the LSP session is a private nested class that
/// starts processes, so exercising it would require a real language server on the build machine's
/// PATH. A bounded scan beats coverage one will never write.
/// </remarks>
public class LspFallbackSilenceTests
{
    private static string Source() =>
        ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Lsp", "LspSemanticProvider.cs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// A single door to <c>_failed</c>, and it is the one that speaks. Five sites wrote it by hand;
    /// each cut the LSP off for the session without a word, and the next one added would have
    /// inherited the silence.
    /// </summary>
    /// <summary>
    /// A server whose output channel closed is not a running server: both fast paths of
    /// <c>EnsureInitializedAsync</c> also ask the channel. Asking only the process kept a deaf server, and
    /// every request waited out its timeout.
    /// </summary>
    [Fact]
    public void AServerWhoseChannelClosed_IsNotTakenForARunningOne()
    {
        var source = Source();
        var at = source.IndexOf("private async Task<bool> EnsureInitializedAsync(", StringComparison.Ordinal);
        Assert.True(at >= 0, "EnsureInitializedAsync moved — the rule measures nothing.");

        var body      = source[at..Math.Min(source.Length, at + 1500)];
        var fastPaths = Regex.Matches(body, @"if \(_initialized && [^\n]*return true;").Select(m => m.Value).ToList();
        Assert.Equal(2, fastPaths.Count);   // witness: the lock-free check and the one under the lock
        Assert.All(fastPaths, p => Assert.Contains("IsClosed", p, StringComparison.Ordinal));
    }

    [Fact]
    public void TurningTheLspOffForTheSession_GoesThroughTheOneDoorThatSaysWhy()
    {
        var source = Source();

        // Witness: this really is the file carrying the flag and its door.
        Assert.Contains("private bool         _failed", source, StringComparison.Ordinal);
        Assert.Contains("private void MarkFailed(", source, StringComparison.Ordinal);

        var assignments = Regex.Matches(source, @"_failed\s*=\s*true").Count;
        Assert.True(assignments == 1,
            $"{assignments} write(s) of _failed in the file: the only one allowed is MarkFailed's, "
            + "which traces the reason. A site writing it itself cuts semantic indexing off for the "
            + "whole session, in silence.");

        // And that write really is MarkFailed's.
        var door = source[source.IndexOf("private void MarkFailed(", StringComparison.Ordinal)..];
        Assert.Contains("_failed = true", door[..Math.Min(400, door.Length)], StringComparison.Ordinal);
    }

    /// <summary>
    /// The trace names the executables looked for, and they are the SAME ones actually looked for.
    /// Two lists would end up advising the installation of a binary the code does not try.
    /// </summary>
    [Fact]
    public void TheServerNamesAreDeclaredOnce()
    {
        var source = Source();

        Assert.Contains("private static string[] ServerNames(", source, StringComparison.Ordinal);

        foreach (var exe in new[] { "typescript-language-server", "pylsp", "pyright-langserver", "gopls", "rust-analyzer" })
        {
            var count = Regex.Matches(source, Regex.Escape("\"" + exe + "\"")).Count;
            Assert.True(count == 1,
                $"'{exe}' is written {count} times: the list of servers looked for must be unique "
                + "(ServerNames), otherwise the trace can name something other than what is tried.");
        }
    }

    // ── A long symbol is SPLIT, not shrunk ────────────────────────────────────

    /// <summary>
    /// A symbol over the token budget is split into pieces covering its <b>whole</b> range — its
    /// tail is not lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ The LSP tier <b>shrank</b> the chunk in 25% steps until it fitted, so the tail of a long
    /// TypeScript / Python / Go / Rust function was indexed <b>nowhere</b>: <c>search_codebase</c>
    /// could never reach it, and nothing said so. It is word for word the defect the Roslyn tier
    /// repaired, with the lesson written in its own comment — "it used to be shrunk until it fit,
    /// and its tail was indexed nowhere". A fix that closes the instance you saw leaves alive the
    /// class you did not look for.
    /// </para>
    /// <para>
    /// The consequence that stings: turning <c>lspEnabled</c> on made the index <b>worse</b> than
    /// the regex tier, whose sliding window covers the whole file.
    /// </para>
    /// </remarks>
    [Fact]
    public void ALongSymbol_IsSplitIntoPiecesCoveringItsWholeRange_NotShrunk()
    {
        // 600 lines of ~40 characters ⇒ ~10 tokens each, i.e. ~6000 > the 1000 budget.
        var lines = Enumerable.Range(0, 600)
            .Select(i => $"    const value{i:D3} = compute(alpha, beta, gamma);")
            .ToArray();
        var content = string.Join('\n', lines);

        var symbol = new Inferpal.Services.Lsp.LspDocumentSymbol
        {
            Name  = "hugeFunction",
            Kind  = Inferpal.Services.Lsp.LspSymbolKind.Function,
            Range = new Inferpal.Services.Lsp.LspRange
            {
                Start = new Inferpal.Services.Lsp.LspPosition { Line = 0 },
                End   = new Inferpal.Services.Lsp.LspPosition { Line = lines.Length - 1 },
            },
        };

        var chunks = Inferpal.Services.Lsp.LspChunker.ChunkFromSymbols(
            [symbol], Path.Combine(Root, "big.ts"), content, Root);

        // Witness: the budget really bit, so there is more than one piece.
        Assert.True(chunks.Count > 1, $"A single piece for {lines.Length} lines: the budget did not bite, the test judges nothing.");

        // And the pieces cover the whole range, with no gap and no overlap.
        var ordered = chunks.OrderBy(c => c.StartLine).ToList();
        Assert.Equal(1, ordered[0].StartLine);
        Assert.Equal(lines.Length, ordered[^1].EndLine);
        for (var i = 1; i < ordered.Count; i++)
            Assert.Equal(ordered[i - 1].EndLine + 1, ordered[i].StartLine);
    }

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ws-lsp");
}
