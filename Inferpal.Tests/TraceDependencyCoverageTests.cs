using System.IO;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>trace_dependency</c> builds a cross-file index of definitions, capped at 400 files, and
/// labels every callee it cannot find <c>[external]</c> — a positive claim that the call leaves the
/// workspace. Until 2026-09-10 that cap was invisible: the index used a bare <c>.Take()</c> while
/// the caller scan next to it went through <see cref="ScanCoverage"/>, and in
/// <c>direction: "callees"</c> — where the caller scan does not run at all — <b>no warning was ever
/// emitted</b>. On this very repository (652 <c>.cs</c> files) 252 of them were never indexed, so a
/// method defined in any of those read as living outside the codebase.
/// </summary>
/// <remarks>
/// The rule was already written ten lines below the defect, on the other scan: <i>"A capped
/// cross-file scan must say so: an empty caller list is otherwise indistinguishable from 'this
/// method is never called'."</i>
/// </remarks>
public class TraceDependencyCoverageTests : IDisposable
{
    /// <summary>Comfortably over <see cref="TraceDependencyTool.MaxFilesScanned"/>.</summary>
    private const int PaddingOverCap = TraceDependencyTool.MaxFilesScanned + 120;

    private readonly string _root;

    public TraceDependencyCoverageTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-trace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>The file under analysis: it calls something that exists nowhere, so it is
    /// unresolved whatever order the filesystem hands the padding files back in.</summary>
    private string WriteTarget() => Write("Target.cs", """
        namespace App;
        public class Target
        {
            public void Run() { NeverDefinedAnywhere(); }
        }
        """);

    private string Write(string rel, string src)
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, src);
        return full;
    }

    /// <summary>Pads the workspace past this tool's own index cap (400 — not the 500 of the two
    /// sibling analysis tools; assuming they shared one produced a wrong measurement first
    /// time).</summary>
    private void WritePadding(int count)
    {
        for (var i = 0; i < count; i++)
            Write($"pad/P{i:D4}.cs", $"namespace Pad; public class P{i:D4} {{ public void M{i:D4}() {{ }} }}");
    }

    private async Task<string> RunAsync(string path)
    {
        var tool = new TraceDependencyTool(() => _root);
        var json = $$"""{"path": {{JsonSerializer.Serialize(path)}}, "direction": "callees"}""";
        return await tool.ExecuteAsync(JsonDocument.Parse(json).RootElement, CancellationToken.None);
    }

    [Fact]
    public async Task CalleesOnly_OnACappedWorkspace_SaysTheScanWasPartial()
    {
        var target = WriteTarget();
        WritePadding(PaddingOverCap);

        var report = await RunAsync(target);

        // The caller scan does not run in this mode: without the INDEX's coverage this report
        // carried no warning at all. The expected text is DERIVED from Strings — it is translated
        // into ten languages, and hard-coding it would only test the English machine.
        var total = Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories).Count();
        Assert.Contains(
            Inferpal.Localization.Strings.ScanPartial(TraceDependencyTool.MaxFilesScanned, total),
            report);
    }

    [Fact]
    public async Task CalleesOnly_OnACappedWorkspace_DoesNotClaimTheCalleeIsExternal()
    {
        var target = WriteTarget();
        WritePadding(PaddingOverCap);

        var report = await RunAsync(target);

        Assert.Contains("NeverDefinedAnywhere", report);
        Assert.DoesNotContain("[external]", report);
        Assert.Contains("[not in scanned subset]", report);
    }

    [Fact]
    public async Task CalleesOnly_WhenEverythingWasScanned_StillSaysExternalAndWarnsAboutNothing()
    {
        // WITNESS: without it, a fix that removed "[external]" everywhere — or that always warned —
        // would pass both tests above. Here the workspace is read IN FULL, so the assertion is
        // legitimate and there is nothing to report.
        var target = WriteTarget();
        WritePadding(5);

        var report = await RunAsync(target);

        Assert.Contains("NeverDefinedAnywhere", report);
        Assert.Contains("[external]", report);
        Assert.DoesNotContain("[not in scanned subset]", report);
        Assert.DoesNotContain(Inferpal.Localization.Strings.ScanPartial(TraceDependencyTool.MaxFilesScanned, 6), report);
    }
}
