using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the model reads about a tool <b>before</b> it calls it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>A description is a specification: it asserts no variable fact.</b> Two of them promised
/// exhaustiveness — <c>rename_symbol</c> "replaces EVERY occurrence … across ALL source files",
/// <c>search_codebase</c> "across ALL project files" — in the two tools that build the machinery
/// which exists because they cannot: a <c>ScanCoverage</c> (files unread, folders unlisted, the
/// size cap) and a persisted index that skips what <c>indexExclude</c> excludes.
/// </para>
/// <para>
/// ⚠ <b>And the description is read FIRST.</b> The coverage line corrects a claim the model was
/// already given, and a first claim is harder to dislodge than a footnote — a model told "every
/// consumer" has no reason to weigh the caveat under the list.
/// </para>
/// <para>
/// ⭐ The line to hold: <b>atomicity is a contract the tool controls, exhaustiveness is a fact
/// about the world it does not.</b> <c>PartialRenameTests</c> read the same description as an
/// engagement and made the write all-or-nothing, which is right; the other half of the promise
/// could never be kept by any code.
/// </para>
/// <para>
/// Assertions are POSITIVE — the description names where coverage is stated — and each one is
/// paired with the arm that makes the pointer TRUE: the report really does carry that line.
/// Asserting only the absence of "every" would stay green if the report stopped saying anything.
/// </para>
/// </remarks>
public class ToolDescriptionScopeTests
{
    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static RenameSymbolTool Rename(string root) =>
        new(new YesApproval(), new FileHistoryService(), () => root);

    [Fact]
    public void RenameSymbol_DescribesItsContract_AndPointsAtTheCoverageLine()
    {
        var description = Rename(Path.GetTempPath()).Description;

        // What it MAY state: its own contract.
        Assert.Contains("all-or-nothing", description, StringComparison.OrdinalIgnoreCase);
        // And where the fact about the world is stated.
        Assert.Contains("states", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not read", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The half that makes the pointer true: a file it cannot read really does produce the coverage
    /// line the description sends the model to. ⚠ Witness — without a read that is genuinely
    /// refused this test is UNDECIDED, not green.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_WhenAFileCannotBeRead_TheReportSaysSo()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-desc").FullName;
        var a   = Path.Combine(dir, "A.cs");
        var b   = Path.Combine(dir, "B.cs");
        await File.WriteAllTextAsync(a, "class X { void M() { OldName(); } }\n");
        await File.WriteAllTextAsync(b, "class Y { void N() { OldName(); } }\n");

        FileStream? hold = null;
        try
        {
            try { hold = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.None); }
            catch { Directory.Delete(dir, recursive: true); return; }   // UNDECIDED

            try { File.ReadAllText(b); Directory.Delete(dir, recursive: true); return; }  // UNDECIDED
            catch { /* the lock really holds */ }

            var answer = await Rename(dir).ExecuteAsync(
                Raw("""{"old_name":"OldName","new_name":"NewName","dry_run":true}"""),
                CancellationToken.None);

            Assert.Contains(Strings.ScanUnreadable(1), answer, StringComparison.Ordinal);
        }
        finally
        {
            hold?.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>Reference arm: a tree it can read whole says nothing about coverage, or the caveat
    /// becomes the noise nobody reads.</summary>
    [Fact]
    public async Task RenameSymbol_OnATreeItCanReadWhole_SaysNothingAboutCoverage()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-desc").FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "A.cs"), "class X { void M() { OldName(); } }\n");

        try
        {
            var answer = await Rename(dir).ExecuteAsync(
                Raw("""{"old_name":"OldName","new_name":"NewName","dry_run":true}"""),
                CancellationToken.None);

            Assert.DoesNotContain(Strings.ScanUnreadable(1), answer, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>
    /// <c>search_codebase</c> reads a PERSISTED index, so a gap outlives the session: the
    /// description names the header that states the index state instead of claiming to cover
    /// everything.
    /// </summary>
    [Fact]
    public void SearchCodebase_PointsAtTheIndexState_InsteadOfClaimingEveryFile()
    {
        var config = new Inferpal.Config.InferpalConfig();
        var client = new FakeInferenceProvider();
        var tool   = new SemanticSearchTool(
            new Inferpal.Services.Rag.ProjectIndexService(client, config, new Inferpal.Services.Lsp.LspSemanticProvider()),
            client, config);

        Assert.Contains("index state", tool.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not 'not in the codebase'", tool.Description, StringComparison.Ordinal);
    }
}
