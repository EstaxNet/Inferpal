using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The tools that <b>read</b> the workspace — <c>search_in_files</c>, <c>list_files</c>,
/// <c>generate_project_map</c> — must declare the walk's holes, exactly as the analysis tools do.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The rule was written inside the walk itself and held by none of the three.</b>
/// <see cref="WorkspaceScan"/> names its consumers — <i>"the index, <c>search_in_files</c>,
/// <c>list_files</c> and every analysis tool"</i> — and its remark on not following links promises
/// that the cost <i>"is SAID: […] <see cref="WorkspaceScan.FirstWalkGap"/> names the folder with its
/// own reason, so it is a declared gap and not a silence"</i>. Six sites honoured it
/// (<c>analyze_impact</c>, <c>trace_dependency</c>, the nexus, <c>rename_symbol</c>, the symbol
/// scanner, the index); the two tools that sentence names by hand did not, and neither did the map.
/// </para>
/// <para>
/// ⚠ <b>And "no results" is the answer that costs the most.</b> <see cref="SearchInFilesTool"/>'s own
/// comment says it: <i>"'No results' is a CONCLUSION the model acts on — it stops looking"</i>. That
/// file enumerates every other reason its answer may be partial — the walk that could not start, the
/// oversized files, the unreadable ones, the 100-match cap — and missed the one whose files are never
/// enumerated at all.
/// </para>
/// <para>
/// The two shapes are the ones the walk already knows: a folder the process cannot list (a database
/// volume mounted in the repository and owned by a container's user, a locked junction under a
/// Windows profile) and a <b>linked</b> folder, which the walk stopped following when the cycle
/// crash was fixed — so a legitimately linked source folder became newly invisible, and only the
/// analysis tools said so.
/// </para>
/// </remarks>
[Collection(SignalCollection.Name)]
public sealed class ReadToolsWalkGapTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"readgap-{Guid.NewGuid():N}");
    private readonly string _lockedDir;
    private FileSystemAccessRule? _deny;

    /// <summary>A string that exists only inside the folder the walk cannot see.</summary>
    private const string HiddenNeedle = "NeedleOnlyInsideTheLockedFolder";

    /// <summary>A string that exists in the part of the tree the walk does see.</summary>
    private const string VisibleNeedle = "NeedleInThePlainFolder";

    public ReadToolsWalkGapTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Visible.cs"), $$"""
            namespace App;
            public class Visible { public string Tag => "{{VisibleNeedle}}"; }
            """);

        _lockedDir = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "Hidden.cs"), $$"""
            namespace App.Hidden;
            public class Hidden { public string Tag => "{{HiddenNeedle}}"; }
            """);

        Deny(_lockedDir, ref _deny);
    }

    public void Dispose()
    {
        Unlock();
        try { Directory.Delete(_root, recursive: true); } catch { }
        _scratch.Dispose();
    }

    // ── Fixture plumbing ──────────────────────────────────────────────────────

    private static void Deny(string dir, ref FileSystemAccessRule? rule)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(dir);
            var acl  = info.GetAccessControl();
            rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(dir, UnixFileMode.None);
        }
    }

    private void Unlock()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(_lockedDir);
                var acl  = info.GetAccessControl();
                if (_deny is not null) { acl.RemoveAccessRule(_deny); _deny = null; }
                info.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_lockedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch { }
    }

    /// <summary>
    /// The lock really discriminates. Without this witness every assertion below would be green on a
    /// platform without enforceable ACLs — or in a run as root — while measuring nothing at all.
    /// </summary>
    private void AssertTheFixtureDiscriminates()
    {
        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.EnumerateFiles(_lockedDir).ToList());

        var walk = WorkspaceScan.EnumerateFiles(_root, "*", _root, out var failed).ToList();
        Assert.False(failed);                                            // the walk believes it succeeded
        Assert.DoesNotContain(walk, f => Path.GetFileName(f) == "Hidden.cs");
        Assert.Contains(walk, f => Path.GetFileName(f) == "Visible.cs");
    }

    private Task<string> SearchAsync(string needle, string pattern = "*")
    {
        var tool = new SearchInFilesTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(
            new { path = _root, pattern = needle, file_pattern = pattern }));
        return tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    private Task<string> ListAsync()
    {
        var tool = new ListFilesTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = _root, pattern = "*" }));
        return tool.ExecuteAsync(args.RootElement, CancellationToken.None);
    }

    private Task<string> MapAsync()
    {
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        index.SetRoot(_root);
        return new ProjectMapService(new NullEditorSurface(), index).GenerateMapAsync(CancellationToken.None);
    }

    private static string Skipped(string folder) => Inferpal.Localization.Strings.ScanFolderSkipped(folder);

    // ── search_in_files ───────────────────────────────────────────────────────

    [Fact]
    public async Task SearchInFiles_WithAnUnlistableFolder_SaysSoInsteadOfAnswering_NoResults()
    {
        AssertTheFixtureDiscriminates();

        var report = await SearchAsync(HiddenNeedle);

        Assert.Contains(Skipped("pgdata"), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchInFiles_WithAnUnlistableFolder_StillSaysSoWhenItDidFindSomething()
    {
        // ⚠ The note must not hang off "the result list is empty": a search that found three hits in
        // the readable part is just as partial, and that is the answer a refactoring is built on.
        AssertTheFixtureDiscriminates();

        var report = await SearchAsync(VisibleNeedle);

        Assert.Contains("Visible.cs", report, StringComparison.Ordinal);   // witness: the search really ran
        Assert.Contains(Skipped("pgdata"), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchInFiles_OnAFullyListableTree_SaysNothingAboutSkippedFolders()
    {
        // NEGATIVE WITNESS: without it, a tool that always appended the note would pass the two above
        // while measuring nothing.
        Unlock();
        Directory.EnumerateFiles(_lockedDir).ToList();   // witness: the lock is really gone

        var report = await SearchAsync(HiddenNeedle);

        Assert.Contains("Hidden.cs", report, StringComparison.Ordinal);    // the other half: the file IS seen now
        Assert.DoesNotContain(Skipped("pgdata"), report, StringComparison.Ordinal);
    }

    // ── list_files ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListFiles_WithAnUnlistableFolder_SaysSo()
    {
        AssertTheFixtureDiscriminates();

        var report = await ListAsync();

        Assert.Contains("Visible.cs", report, StringComparison.Ordinal);   // witness: the listing is the real one
        Assert.Contains(Skipped("pgdata"), report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFiles_OnAFullyListableTree_SaysNothingAboutSkippedFolders()
    {
        Unlock();
        Directory.EnumerateFiles(_lockedDir).ToList();

        var report = await ListAsync();

        Assert.Contains("Hidden.cs", report, StringComparison.Ordinal);
        Assert.DoesNotContain(Skipped("pgdata"), report, StringComparison.Ordinal);
    }

    // ── generate_project_map ──────────────────────────────────────────────────

    [Fact]
    public async Task ProjectMap_WithAnUnlistableFolder_SaysSo()
    {
        AssertTheFixtureDiscriminates();

        var map = await MapAsync();

        Assert.Contains("Visible", map, StringComparison.Ordinal);         // witness: the map is the real one
        Assert.Contains(Skipped("pgdata"), map, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectMap_OnAFullyListableTree_SaysNothingAboutSkippedFolders()
    {
        Unlock();
        Directory.EnumerateFiles(_lockedDir).ToList();

        var map = await MapAsync();

        Assert.Contains("Hidden", map, StringComparison.Ordinal);
        Assert.DoesNotContain(Skipped("pgdata"), map, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectMap_CountsTheFilesItCouldNotRead_InsteadOfCallingThemScanned()
    {
        // ⚠ The header line is literally labelled "Scanned: N source files", and N was the number of
        // files the walk TOOK. A file locked by a build, or deleted between the walk and the read,
        // was counted there and skipped by a silent `catch` — the exact wording ScanCoverage was
        // written to stop.
        Unlock();
        var locked = Path.Combine(_root, "src", "Locked.cs");
        File.WriteAllText(locked, "namespace App; public class Locked { }");

        // FileShare.None is the repository's own witness for "unreadable": .NET turns it into an
        // flock on Unix, so one gesture covers the four CI legs.
        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var map = await MapAsync();

            Assert.Contains("Visible", map, StringComparison.Ordinal);     // witness: the map ran
            Assert.Contains(Inferpal.Localization.Strings.ScanUnreadable(1), map, StringComparison.Ordinal);
        }
    }

    // ── The other cause: a folder that is a link ──────────────────────────────

    [Fact]
    public async Task SearchInFiles_WithALinkedFolder_SaysItWasNotFollowed()
    {
        // ⚠ This gap did not exist before the cycle fix: the walk used to follow links. Closing the
        // crash made a legitimately linked source folder invisible, and only the analysis tools said
        // so — the tool a model uses to decide "this symbol is nowhere" did not.
        Unlock();

        var shared = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"readgap-shared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "Shared.cs"), $$"""
            namespace App.Shared;
            public class Shared { public string Tag => "{{HiddenNeedle}}"; }
            """);
        var link = Path.Combine(_root, "linked");
        try { Directory.CreateSymbolicLink(link, shared); } catch { }

        try
        {
            Assert.True(Directory.Exists(link),
                "The directory link could not be created (symbolic-link privilege): this test is "
                + "UNDECIDED, not green.");
            Assert.True(File.Exists(Path.Combine(link, "Shared.cs")),
                "The link exists but does not reach the shared folder: the witness does not discriminate.");

            var report = await SearchAsync(HiddenNeedle);

            Assert.Contains(Inferpal.Localization.Strings.ScanFolderNotFollowed("linked"), report,
                StringComparison.Ordinal);
        }
        finally
        {
            // ⚠ The LINK first, always: a recursive delete that walks through it takes its target with it.
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(shared, recursive: true); } catch { }
        }
    }
}
