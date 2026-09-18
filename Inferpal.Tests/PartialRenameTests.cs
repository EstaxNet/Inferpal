using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>rename_symbol</c> wrote its N files in a loop and, on a write failure, <b>carried on</b>: it
/// left the symbol half renamed.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The repository had already named this harm — for ANOTHER cause.</b> The maintainer's notes
/// say of the unlistable-folder sweep: <i>"the site that stings is `rename_symbol`, which WRITES: an
/// invisible folder there does not produce an incomplete report but a PARTIAL rename"</i>. That
/// cause was closed; this one — a locked file, read-only, held by the compiler — produces
/// <b>exactly the same state</b> and had stayed open, under a message that reads like a success:
/// <c>"⚠ Applied with 1 error(s)"</c>.
/// </para>
/// <para>
/// ⚠ <b>And the remedy was written three files away.</b> <c>apply_edits</c>, same shape, same
/// phase, puts back every file already written and names the ones it could not put back — its
/// commentaire porte la raison : <i>« the description promises the model "if ANY edit cannot be
/// applied, NO file is changed"</i>. The description of <c>rename_symbol</c> promises the same
/// (<i>« replaces EVERY occurrence … across ALL source files »</i>), et un renommage partiel est
/// worse than a partial edit: it is the one refactor whose half-applied state never compiles.
/// </para>
/// </remarks>
public class PartialRenameTests
{
    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null,
                                               bool forcePrompt = false) => Task.FromResult(true);
    }

    private static JsonElement Raw(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>
    /// Makes the file unwritable, and says whether that REALLY took — under a privileged account
    /// (root on CI) the attribute guards nothing, and the test must then be undecided, not green.
    /// </summary>
    private static bool MakeUnwritable(string path)
    {
        try
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Write);
            return false;   // the write went through: the attribute guards nothing here
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException)                 { return true; }
    }

    private static void MakeWritable(string path)
    {
        try { File.SetAttributes(path, FileAttributes.Normal); } catch { /* nettoyage */ }
    }

    [Fact]
    public async Task ARenameThatCannotWriteOneFile_LeavesNoneOfThemRenamed()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-rename").FullName;
        var a   = Path.Combine(dir, "A.cs");
        var b   = Path.Combine(dir, "B.cs");
        var c   = Path.Combine(dir, "C.cs");
        const string body = "class X { void M() { OldName(); } }\n";
        await File.WriteAllTextAsync(a, body);
        await File.WriteAllTextAsync(b, body);
        await File.WriteAllTextAsync(c, body);

        // WITNESS: without a write that is genuinely refused, this test measures nothing — it is
        // UNDECIDED.
        if (!MakeUnwritable(b)) { Directory.Delete(dir, recursive: true); return; }

        try
        {
            var tool = new RenameSymbolTool(new YesApproval(), new FileHistoryService(), () => dir);

            var answer = await tool.ExecuteAsync(
                Raw($$"""{"old_name":"OldName","new_name":"NewName","dry_run":false}"""),
                CancellationToken.None);

            // The symbol is renamed NOWHERE: the half-applied state of a rename does not compile.
            Assert.Equal(body, await File.ReadAllTextAsync(a));
            Assert.Equal(body, await File.ReadAllTextAsync(c));
            Assert.DoesNotContain("NewName", await File.ReadAllTextAsync(a), StringComparison.Ordinal);

            // And it is said: "Applied with 1 error(s)" reads like an intended partial success.
            Assert.DoesNotContain("Applied to", answer, StringComparison.Ordinal);
            Assert.DoesNotContain("Applied with", answer, StringComparison.Ordinal);
        }
        finally
        {
            MakeWritable(b);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ARenameThatCanWriteEverything_StillRenamesEverything()
    {
        // Reference arm: the nominal path does not change.
        var dir = Directory.CreateTempSubdirectory("inferpal-rename").FullName;
        var a   = Path.Combine(dir, "A.cs");
        var b   = Path.Combine(dir, "B.cs");
        await File.WriteAllTextAsync(a, "class X { void M() { OldName(); } }\n");
        await File.WriteAllTextAsync(b, "class Y { void N() { OldName(); } }\n");
        try
        {
            var tool = new RenameSymbolTool(new YesApproval(), new FileHistoryService(), () => dir);

            var answer = await tool.ExecuteAsync(
                Raw($$"""{"old_name":"OldName","new_name":"NewName","dry_run":false}"""),
                CancellationToken.None);

            Assert.Contains("NewName", await File.ReadAllTextAsync(a), StringComparison.Ordinal);
            Assert.Contains("NewName", await File.ReadAllTextAsync(b), StringComparison.Ordinal);
            Assert.Contains("Applied to", answer, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── The shared funnel: one reader, not two ───────────────────────────────

    [Fact]
    public async Task TheFunnel_PutsBackEveryFileItHadAlreadyWritten()
    {
        var dir  = Directory.CreateTempSubdirectory("inferpal-multiwrite").FullName;
        var ok   = Path.Combine(dir, "ok.txt");
        // ⚠ A DIRECTORY carrying the file's name: writing to it fails on every platform and
        // whatever the account, where a "read-only" attribute does not guard against root.
        var bad  = Path.Combine(dir, "bad.txt");
        await File.WriteAllTextAsync(ok, "before\n");
        Directory.CreateDirectory(bad);
        try
        {
            var result = await SafeFileWriter.WriteAllOrRollBackAsync(
            [
                (ok,  "after\n", "before\n"),
                (bad, "after\n", "before\n"),
            ]);

            Assert.False(result.Ok);
            Assert.Equal(bad, result.FailedPath);
            Assert.Empty(result.Stuck);
            // The file already written is put back the way it was.
            Assert.Equal("before\n", await File.ReadAllTextAsync(ok));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task TheFunnel_WritesEverythingWhenNothingFails()
    {
        var dir = Directory.CreateTempSubdirectory("inferpal-multiwrite").FullName;
        var a   = Path.Combine(dir, "a.txt");
        var b   = Path.Combine(dir, "b.txt");
        await File.WriteAllTextAsync(a, "before\n");
        await File.WriteAllTextAsync(b, "before\n");
        try
        {
            var result = await SafeFileWriter.WriteAllOrRollBackAsync(
            [
                (a, "after\n", "before\n"),
                (b, "after\n", "before\n"),
            ]);

            Assert.True(result.Ok);
            Assert.Null(result.FailedPath);
            Assert.Equal("after\n", await File.ReadAllTextAsync(a));
            Assert.Equal("after\n", await File.ReadAllTextAsync(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void BothMultiFileWriters_GoThroughTheFunnel()
    {
        // The rule lives in the funnel, not copied on each side: that is what makes the third tool
        // that writes several files inherit it.
        var root = RepoRoot();
        foreach (var name in new[] { "RenameSymbolTool", "ApplyEditsTool" })
        {
            var code = ConventionCoverageTests.CodeOnly(
                Path.Combine(root, "Inferpal.Core", "Services", "Tools", $"{name}.cs"));

            Assert.Contains("WriteAllOrRollBackAsync", code, StringComparison.Ordinal);
        }
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
