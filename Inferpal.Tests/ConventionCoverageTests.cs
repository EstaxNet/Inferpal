using System.IO;
using Xunit;

namespace Inferpal.Tests;

// §27.2 - the systemic answer to the pre-1.6.0 architecture review, modelled on
// SlashCommandCoverageTests: every convention the review had to repair site by site is now
// enforced by a scan of the sources. A new site that bypasses the guard rail fails the suite with
// a message naming the file and the rule - instead of waiting for the next architecture review.
//
// The four locked conventions:
//   1. SafeFileWriter    - the only text writer under Services\Tools (encoding/BOM preservation).
//   2. RegexBudget       - every Regex built or called statically under Services\Tools carries a
//                          timeout (workspace/web content is uncontrolled, backtracking = freeze).
//   3. AtomicFile        - the only writer of the Services\Persistence stores (write-rename, no
//                          truncation before writing). Append* stays out of scope: an append is
//                          additive by nature, there is no atomic append.
//   4. WorkspaceScan     - every recursive enumeration in a tool goes through the shared
//                          exclusions (bin/obj/.git/node_modules/.inferpal...).
public class ConventionCoverageTests
{
    // ── 1. SafeFileWriter sous Services\Tools ─────────────────────────────────

    [Fact]
    public void ToolTextWrites_GoThroughSafeFileWriter()
    {
        // The funnel itself is the only exemption: it is the one allowed to call
        // File.WriteAllTextAsync (with the detected encoding).
        string[] exempt = ["SafeFileWriter.cs"];

        foreach (var file in ToolsSources().Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(source, @"(?<![\w.])File\.WriteAllText(Async)?\s*\("),
                $"{Rel(file)} writes a text file directly - File.WriteAllText emits UTF-8 without " +
                "a BOM whatever happens (VS BOM stripped, UTF-16 silently transcoded). " +
                "Use SafeFileWriter.WritePreservingAsync, or add a justified exemption here.");
        }
    }

    // ── 2. RegexBudget sous Services\Tools ────────────────────────────────────

    [Fact]
    public void ToolRegexes_CarryAMatchTimeout()
    {
        // A match without a timeout on workspace content = one pathological minified file freezes
        // the agent turn with no error. Regex.Escape/Unescape match nothing -> out of scope.
        var callSite = new System.Text.RegularExpressions.Regex(
            @"(?<![\w.])(?:Regex\.(?:IsMatch|Match|Matches|Replace|Split)|new\s+Regex|(?<=\bRegex\s+\w{1,64}\s*=\s*)new)\s*\(");

        foreach (var file in UntrustedInputSources())
        {
            var source = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match m in callSite.Matches(source))
            {
                var args = BalancedArguments(source, source.IndexOf('(', m.Index + m.Length - 1));
                Assert.True(args is not null,
                    $"{Rel(file)}: argument list never closed after \"{Snippet(source, m.Index)}\" - unexpected balancing or source.");
                Assert.True(
                    args!.Contains("RegexBudget") || args.Contains("Timeout"),
                    $"{Rel(file)}: \"{Snippet(source, m.Index)}\" has no match timeout - " +
                    "pass RegexBudget.Default (last argument), like all of its neighbours.");
            }
        }
    }

    // ── 3. AtomicFile sous Services\Persistence ───────────────────────────────

    [Fact]
    public void PersistenceStores_WriteThroughAtomicFile()
    {
        string[] exempt = ["AtomicFile.cs"]; // the funnel writes the staging file itself

        foreach (var file in CoreSources(Path.Combine("Services", "Persistence"))
                     .Where(f => !exempt.Contains(Path.GetFileName(f))))
        {
            var source = File.ReadAllText(file);
            Assert.False(
                System.Text.RegularExpressions.Regex.IsMatch(
                    source, @"(?<![\w.])File\.(WriteAllText|WriteAllBytes)(Async)?\s*\("),
                $"{Rel(file)} writes a store directly - File.WriteAll* truncates the target before " +
                "writing (crash/full disk = store lost, and the config file is shared " +
                "VS <-> VS Code). Use AtomicFile.WriteAllText[Async]/WriteAllBytes.");
        }
    }

    // ── 4. WorkspaceScan sous Services\Tools ──────────────────────────────────

    [Fact]
    public void ToolRecursiveEnumerations_RouteThroughWorkspaceScan()
    {
        // File granularity, like the router test: a tool that enumerates recursively must at
        // least know about WorkspaceScan (EnumerateFiles, or an explicit IsExcludedPath filter).
        foreach (var file in ToolsSources())
        {
            var source = File.ReadAllText(file);
            if (!source.Contains("SearchOption.AllDirectories")) continue;

            Assert.True(source.Contains("WorkspaceScan."),
                $"{Rel(file)} enumerates recursively without referencing WorkspaceScan - it walks " +
                "bin/obj/.git/node_modules and .inferpal/history (COPIES of sources). " +
                "Use WorkspaceScan.EnumerateFiles, or filter with WorkspaceScan.IsExcludedPath.");
        }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static IEnumerable<string> ToolsSources() =>
        CoreSources(Path.Combine("Services", "Tools"));

    /// <summary>
    /// Where a regex meets input nobody in this repository controls. <c>Services\Tools</c> parses
    /// the workspace; <c>Services\Docs</c> parses HTML fetched from the open web, which is the
    /// least controlled input the product touches - and it was outside the rule until the
    /// post-1.6.0 review found the crawler running unbounded patterns over it, while its twin
    /// <c>FetchUrlTool</c> had been bounding the same ones since it was written.
    /// </summary>
    private static IEnumerable<string> UntrustedInputSources() =>
        ToolsSources().Concat(CoreSources(Path.Combine("Services", "Docs")));

    private static IEnumerable<string> CoreSources(string subdir) =>
        Directory.EnumerateFiles(
            Path.Combine(RepoRoot(), "Inferpal.Core", subdir), "*.cs", SearchOption.AllDirectories);

    private static string Rel(string path) =>
        Path.GetRelativePath(RepoRoot(), path);

    private static string Snippet(string source, int index) =>
        source.Substring(index, Math.Min(60, source.Length - index)).ReplaceLineEndings(" ");

    /// <summary>Repo root = first ancestor of the test bin folder containing README.md.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>
    /// Content of the argument list opening at <paramref name="openParen"/>, with balanced
    /// parentheses, ignoring those inside string/char literals (regex patterns are full of
    /// unmatched parentheses) and the content of verbatim/interpolated strings.
    /// </summary>
    private static string? BalancedArguments(string source, int openParen)
    {
        Assert.True(openParen >= 0, "opening parenthesis not found after the call site");
        int depth = 0;
        for (int i = openParen; i < source.Length; i++)
        {
            char c = source[i];
            switch (c)
            {
                case '/': // comments: a "// function name(" would skew the count
                    if (i + 1 < source.Length && source[i + 1] == '/')
                    {
                        while (i < source.Length && source[i] != '\n') i++;
                    }
                    else if (i + 1 < source.Length && source[i + 1] == '*')
                    {
                        var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                        i = close < 0 ? source.Length : close + 1;
                    }
                    break;

                case '(': depth++; break;
                case ')':
                    if (--depth == 0) return source[(openParen + 1)..i];
                    break;

                case '\'': // char literal : '\'' ou '('
                    i++;
                    if (i < source.Length && source[i] == '\\') i++;
                    i++; // closes the quote
                    break;

                case '"':
                {
                    // Verbatim if an @ precedes (possibly $@ / @$) - "" escapes the quote.
                    bool verbatim = (i > 0 && source[i - 1] == '@') ||
                                    (i > 1 && source[i - 1] == '$' && source[i - 2] == '@');
                    i++;
                    while (i < source.Length)
                    {
                        if (verbatim && source[i] == '"' && i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        if (!verbatim && source[i] == '\\') { i += 2; continue; }
                        if (source[i] == '"') break;
                        i++;
                    }
                    break;
                }
            }
        }
        return null; // never closed - the call site reports file + snippet
    }
}
