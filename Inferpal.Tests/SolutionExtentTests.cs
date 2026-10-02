using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The workspace root holds the whole solution: a solution that lists projects beside its own folder
/// (<c>UI\UI.sln</c> naming <c>..\DAL\DAL.csproj</c>) no longer leaves them out of the tools' reach — and what still
/// stays out is said.
/// </summary>
/// <remarks>
/// Rooted at the <c>.sln</c>'s folder, every read, search and edit of those projects was refused as "outside the
/// workspace root", the index never held them, and <c>get_solution_info</c> listed them as if they could be opened.
/// The widening is bounded (git work tree, home folder, <c>.inferpal/</c> layers): each bound has its case here, and the
/// ordinary solution — every project under its folder — has its reference arm.
/// </remarks>
[Collection(SignalCollection.Name)]
public class SolutionExtentTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _base =
        Path.Combine(Path.GetTempPath(), $"inferpal-extent-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { File.Delete(LastKnownSolutionFile.FilePath); } catch { }
        try { Directory.Delete(_base, recursive: true); } catch { }
        _scratch.Dispose();
    }

    private string P(params string[] parts) => Path.Combine([_base, .. parts]);

    private static SolutionProject Project(string name, string absolutePath) =>
        new(name, Path.GetFileName(absolutePath), Path.GetFullPath(absolutePath));

    private static bool NoLayers(string _) => false;

    // ── The decision ────────────────────────────────────────────────────────────

    [Fact]
    public void ASolutionWhoseProjectsAreUnderItsFolder_KeepsItsFolder()
    {
        var extent = SolutionExtent.Decide(
            P("repo"), [Project("App", P("repo", "src", "App", "App.csproj")), Project("Tests", P("repo", "tests", "T", "T.csproj"))],
            workTree: P("repo"), home: null, NoLayers);

        Assert.Equal(P("repo"), extent.Root);
        Assert.Empty(extent.Outside);
        Assert.Equal(SolutionExtentLimit.None, extent.Limit);
        Assert.Equal(string.Empty, extent.Reason);
    }

    [Fact]
    public void ProjectsBesideTheSolution_InTheSameRepository_AreHeldByTheRoot()
    {
        var extent = SolutionExtent.Decide(
            P("repo", "UI"),
            [Project("UI", P("repo", "UI", "UI.csproj")), Project("DAL", P("repo", "DAL", "DAL.csproj")),
             Project("BLL", P("repo", "Layers", "BLL", "BLL.csproj"))],
            workTree: P("repo"), home: null, NoLayers);

        Assert.Equal(P("repo"), extent.Root);
        Assert.Equal(P("repo", "UI"), extent.SolutionDir);
        Assert.Empty(extent.Outside);
    }

    [Fact]
    public void TheRootWidensOnlyAsFarAsTheProjectsNeed()
    {
        // The repository goes higher; the root stops at the folder that holds the solution and its projects.
        var extent = SolutionExtent.Decide(
            P("repo", "apps", "shop", "UI"), [Project("DAL", P("repo", "apps", "shop", "DAL", "DAL.csproj"))],
            workTree: P("repo"), home: null, NoLayers);

        Assert.Equal(P("repo", "apps", "shop"), extent.Root);
    }

    [Fact]
    public void WithoutAGitWorkTree_TheRootDoesNotWiden_AndSaysWhy()
    {
        var extent = SolutionExtent.Decide(
            P("work", "UI"), [Project("DAL", P("work", "DAL", "DAL.csproj"))],
            workTree: null, home: null, NoLayers);

        Assert.Equal(P("work", "UI"), extent.Root);
        Assert.Equal("DAL", Assert.Single(extent.Outside).Name);
        Assert.Equal(SolutionExtentLimit.NoWorkTree, extent.Limit);
        Assert.Contains("no git repository", extent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectInAnotherRepository_StaysOutside_WhileTheOthersComeIn()
    {
        var extent = SolutionExtent.Decide(
            P("repo", "UI"),
            [Project("DAL", P("repo", "DAL", "DAL.csproj")), Project("QLNet", P("other", "QLNet", "QLNet.csproj"))],
            workTree: P("repo"), home: null, NoLayers);

        Assert.Equal(P("repo"), extent.Root);
        Assert.Equal("QLNet", Assert.Single(extent.Outside).Name);
        Assert.Equal(SolutionExtentLimit.BeyondWorkTree, extent.Limit);
        Assert.Contains("outside the git repository", extent.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRootNeverBecomesTheHomeFolder_EvenUnderVersionControl()
    {
        // A home folder kept in git (dotfiles), and a solution that names a project at its top.
        var home   = P("home");
        var extent = SolutionExtent.Decide(
            P("home", "code", "App", "UI"), [Project("Keys", P("home", ".ssh", "Keys.csproj"))],
            workTree: home, home: home, NoLayers);

        Assert.Equal(P("home", "code", "App", "UI"), extent.Root);
        Assert.Single(extent.Outside);
        Assert.Equal(SolutionExtentLimit.UserFolder, extent.Limit);
        Assert.Equal(home, extent.Wanted);
    }

    [Fact]
    public void TheRootNeverBecomesADriveRoot()
    {
        var drive  = Path.GetPathRoot(_base)!;
        var extent = SolutionExtent.Decide(
            Path.Combine(drive, "UI"), [Project("DAL", Path.Combine(drive, "DAL", "DAL.csproj"))],
            workTree: drive, home: null, NoLayers);

        Assert.Equal(Path.Combine(drive, "UI"), extent.Root);
        Assert.Equal(SolutionExtentLimit.UserFolder, extent.Limit);
    }

    [Fact]
    public void LayersInTheSolutionFolder_KeepTheRoot_AndNameWhereToMoveThem()
    {
        // Widening would stop reading UI\.inferpal — a deny overlay there would loosen permissions without a word.
        var slnDir = P("repo", "UI");
        var extent = SolutionExtent.Decide(
            slnDir, [Project("DAL", P("repo", "DAL", "DAL.csproj"))],
            workTree: P("repo"), home: null, holdsLayers: d => d == slnDir);

        Assert.Equal(slnDir, extent.Root);
        Assert.Equal(SolutionExtentLimit.Layers, extent.Limit);
        Assert.Contains(Path.Combine(slnDir, ".inferpal"), extent.Reason, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(P("repo"), ".inferpal"), extent.Reason, StringComparison.Ordinal);

        // Reference arm: layers only at the wider root are read there once it is the root — nothing holds it back.
        var above = SolutionExtent.Decide(
            slnDir, [Project("DAL", P("repo", "DAL", "DAL.csproj"))],
            workTree: P("repo"), home: null, holdsLayers: d => d == P("repo"));
        Assert.Equal(P("repo"), above.Root);
    }

    [Fact]
    public void LayersAtBothPlaces_StillKeepTheRoot_TheSolutionFoldersAreTheOnesReadToday()
    {
        // Widening here would switch from UI\.inferpal (read today) to repo\.inferpal, silently.
        var slnDir = P("repo", "UI");
        var extent = SolutionExtent.Decide(
            slnDir, [Project("DAL", P("repo", "DAL", "DAL.csproj"))],
            workTree: P("repo"), home: null, holdsLayers: _ => true);

        Assert.Equal(slnDir, extent.Root);
        Assert.Equal(SolutionExtentLimit.Layers, extent.Limit);
    }

    // ── On disk, through the paths that decide a root ───────────────────────────

    /// <summary><c>repo/UI/UI.sln</c> listing <c>..\DAL\DAL.csproj</c>, with <c>repo/.git</c> when <paramref name="git"/>.</summary>
    private string LayeredRepository(bool git)
    {
        Directory.CreateDirectory(P("repo", "UI"));
        Directory.CreateDirectory(P("repo", "DAL"));
        if (git) Directory.CreateDirectory(P("repo", ".git"));
        File.WriteAllText(P("repo", "UI", "UI.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(P("repo", "DAL", "DAL.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(P("repo", "DAL", "Repository.cs"), "public class Repository { }\n");
        var sln = P("repo", "UI", "UI.sln");
        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"UI\", \"UI.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n"
            + "EndProject\n"
            + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"DAL\", \"..\\DAL\\DAL.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\n"
            + "EndProject\n");
        return sln;
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task TheSolutionVisualStudioReportsOpen_RootsTheToolsWhereTheyCanReadItsProjects()
    {
        var sln = LayeredRepository(git: true);
        ActiveSolutionSignal.Write(sln);

        var root = SolutionExtent.OfActiveSolution()?.Root;
        Assert.Equal(P("repo"), root);

        var text = await new ReadFileTool(() => root)
            .ExecuteAsync(Args(new { path = P("repo", "DAL", "Repository.cs") }), CancellationToken.None);
        Assert.Contains("public class Repository", text, StringComparison.Ordinal);

        // Reference arm, the defect itself: rooted at the .sln's folder, the same read is refused.
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => new ReadFileTool(() => P("repo", "UI"))
            .ExecuteAsync(Args(new { path = P("repo", "DAL", "Repository.cs") }), CancellationToken.None));
        Assert.Contains("outside the workspace root", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGitFile_AWorktreeOrASubmodule_IsAWorkTreeToo()
    {
        LayeredRepository(git: false);
        File.WriteAllText(P("repo", ".git"), "gitdir: ../.git/worktrees/repo\n");

        Assert.Equal(P("repo"), SolutionExtent.WorkTreeOf(P("repo", "UI")));
        Assert.Equal(P("repo"), SolutionExtent.RootForDir(P("repo", "UI")));
    }

    [Fact]
    public void WhatStaysOutside_IsSaidInDiagnostics_OnceAndAgainWhenItComesBack()
    {
        var sln = LayeredRepository(git: false);
        Diagnostics.Clear();

        Assert.Equal(P("repo", "UI"), SolutionExtent.RootForDir(P("repo", "UI")));
        SolutionExtent.RootForDir(P("repo", "UI"));   // a heartbeat later: not twice
        var notes = Diagnostics.Snapshot().Where(e => e.Context == "SolutionExtent").ToList();
        var note  = Assert.Single(notes);
        Assert.Contains("1 project(s) of UI.sln sit outside the workspace root", note.Detail, StringComparison.Ordinal);
        Assert.Contains("DAL", note.Detail, StringComparison.Ordinal);
        Assert.Contains("no git repository", note.Detail, StringComparison.Ordinal);

        // The repository gets a git tree: the projects come in, the note is over...
        Directory.CreateDirectory(P("repo", ".git"));
        Assert.Equal(P("repo"), SolutionExtent.RootForDir(P("repo", "UI")));
        // ...and when the condition comes back, it is said again rather than "once in the life of the process".
        Directory.Delete(P("repo", ".git"));
        SolutionExtent.RootForDir(P("repo", "UI"));
        Assert.Equal(2, Diagnostics.Snapshot().Count(e => e.Context == "SolutionExtent"));
        _ = sln;
    }

    [Fact]
    public void ASolutionEditedAfterwards_IsReadAgain()
    {
        // The parse is cached for the per-keystroke paths; an edited solution must not keep its old extent.
        var sln = LayeredRepository(git: true);
        Assert.Equal(P("repo"), SolutionExtent.Of(sln).Root);

        File.WriteAllText(sln,
            "Microsoft Visual Studio Solution File, Format Version 12.00\n"
            + "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"UI\", \"UI.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n"
            + "EndProject\n");
        Assert.Equal(P("repo", "UI"), SolutionExtent.Of(sln).Root);
    }

    [Fact]
    public void OnlyTheProductsSnapshots_AreNotLayers()
    {
        Directory.CreateDirectory(P("a", ".inferpal", "history"));
        Assert.False(SolutionExtent.HoldsLayers(P("a")));

        File.WriteAllText(P("a", ".inferpal", "context.md"), "# Context\n");
        Assert.True(SolutionExtent.HoldsLayers(P("a")));
        Assert.False(SolutionExtent.HoldsLayers(P("nothing-here")));
    }

    [Fact]
    public void ASolutionFolderWithLayers_KeepsItsRoot_OnDiskToo()
    {
        LayeredRepository(git: true);
        Directory.CreateDirectory(P("repo", "UI", ".inferpal"));
        File.WriteAllText(P("repo", "UI", ".inferpal", "permissions.json"), "{\"rules\":[]}");

        var extent = SolutionExtent.Of(P("repo", "UI", "UI.sln"));
        Assert.Equal(P("repo", "UI"), extent.Root);
        Assert.Equal(SolutionExtentLimit.Layers, extent.Limit);
    }

    // ── What the model reads ────────────────────────────────────────────────────

    [Fact]
    public async Task GetSolutionInfo_SaysWhichProjectsTheToolsCannotReach_AboveTheList()
    {
        var sln = LayeredRepository(git: false);

        var report = await new GetSolutionInfoTool(new NullEditorSurface(), () => P("repo", "UI"))
            .ExecuteAsync(Args(new { path = sln }), CancellationToken.None);

        var note = report.IndexOf("Outside  : 1 of them sit outside the workspace root", StringComparison.Ordinal);
        Assert.True(note >= 0, report);
        Assert.True(note < report.IndexOf("── DAL", StringComparison.Ordinal), report);
        Assert.Contains("no git repository", report, StringComparison.Ordinal);
        Assert.Contains("[outside the workspace root: the tools cannot read, search or edit it]", report, StringComparison.Ordinal);

        // Reference arm: with the root that holds the whole solution, nothing is said.
        var whole = await new GetSolutionInfoTool(new NullEditorSurface(), () => P("repo"))
            .ExecuteAsync(Args(new { path = sln }), CancellationToken.None);
        Assert.DoesNotContain("Outside", whole, StringComparison.Ordinal);
        Assert.DoesNotContain("[outside the workspace root", whole, StringComparison.Ordinal);
    }

    // ── The adapters (not runnable from the suite: a Visual Studio window) ──────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void EveryRootTheVisualStudioWindowDerives_GoesThroughTheExtent()
    {
        var rag = ConventionCoverageTests.CodeOnly(
            Path.Combine(RepoRoot(), "Inferpal", "ToolWindow", "InferpalToolWindowData.Rag.cs"));

        // Witness: the reader sees the three derivations.
        foreach (var site in new[] { "private void PinWorkspaceRoot()", "FindReliableProjectRoot() =>", "FindProjectRoot(IReadOnlyList<string>? openPaths = null) =>" })
            Assert.Contains(site, rag, StringComparison.Ordinal);

        Assert.True(rag.Split("SolutionExtent.OfActiveSolution()").Length - 1 >= 3, "pin, reliable and plain roots");
        Assert.True(rag.Split("SolutionExtent.RootForDir(").Length - 1 >= 2, "both locator fallbacks");
        Assert.DoesNotContain("TryReadSolutionPath()", rag, StringComparison.Ordinal);
    }
}
