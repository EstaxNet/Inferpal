using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Signals;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// No path is accepted without a workspace root, and the folder Visual Studio has open IS a root.
/// </summary>
/// <remarks>
/// ⚠ Visual Studio opened on a folder (no solution) has no solution to pin a root from; an empty root accepted by
/// <c>PathSanitizer.AssertUnderRoot</c> lets the model read and write anywhere on the disk, the deny overlay and the ignore
/// files unread, and nothing says so. VS Code and the ACP agent always have a root; Visual Studio may not.
/// </remarks>
[Collection(SignalCollection.Name)]
public class NoWorkspaceRootTests : IDisposable
{
    private readonly SignalScratchDir _scratch = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "inferpal-noroot-" + Guid.NewGuid().ToString("N"));

    public NoWorkspaceRootTests()
    {
        Directory.CreateDirectory(_dir);
        SignalFile._isProcessAliveOverride = _ => true;
    }

    public void Dispose()
    {
        SignalFile._isProcessAliveOverride = null;
        SignalScope.ResetForTests();
        _scratch.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class CountingApproval : IApprovalService
    {
        public int Asked { get; private set; }
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
            string? subject = null, DiffInfo? diff = null, bool forcePrompt = false)
        {
            Asked++;
            return Task.FromResult(true);
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ── The tools: refused before any card, nothing written ────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutARoot_AWrite_IsRefusedBeforeTheCard_AndNothingIsWritten(string? root)
    {
        var approval = new CountingApproval();
        var target   = Path.Combine(_dir, "new.txt");
        var tool     = new WriteFileTool(approval, new FileHistoryService(), () => root);

        var refused = await Assert.ThrowsAsync<NoWorkspaceRootException>(() => tool.ExecuteAsync(Args(new { path = target, content = "x" }), CancellationToken.None));

        Assert.Contains("Open a folder or a solution", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, approval.Asked);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task WithARoot_TheSameWrite_IsAskedFor_AndWritten()
    {
        // Reference arm: the refusal above is the missing root, not the call.
        var approval = new CountingApproval();
        var target   = Path.Combine(_dir, "new.txt");
        var tool     = new WriteFileTool(approval, new FileHistoryService(), () => _dir);

        await tool.ExecuteAsync(Args(new { path = target, content = "x" }), CancellationToken.None);

        Assert.Equal(1, approval.Asked);
        Assert.Equal("x", File.ReadAllText(target));
    }

    [Fact]
    public async Task WithoutARoot_AnEditAndADelete_AreRefusedBeforeTheCard_AndTheFileIsUntouched()
    {
        var approval = new CountingApproval();
        var target   = Path.Combine(_dir, "kept.txt");
        File.WriteAllText(target, "before");
        var history  = new FileHistoryService();

        await Assert.ThrowsAsync<NoWorkspaceRootException>(() => new ApplyDiffTool(approval, history, () => null)
            .ExecuteAsync(Args(new { path = target, old_content = "before", new_content = "after" }), CancellationToken.None));
        await Assert.ThrowsAsync<NoWorkspaceRootException>(() => new DeleteFileTool(approval, history, () => null)
            .ExecuteAsync(Args(new { path = target }), CancellationToken.None));

        Assert.Equal(0, approval.Asked);
        Assert.Equal("before", File.ReadAllText(target));
    }

    [Fact]
    public async Task WithoutARoot_AReadAnywhereOnTheDisk_IsRefused()
    {
        var target = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(target, "s3cret");

        var refused = await Assert.ThrowsAsync<NoWorkspaceRootException>(() =>
            new ReadFileTool(() => null).ExecuteAsync(Args(new { path = target }), CancellationToken.None));

        Assert.DoesNotContain("s3cret", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Open a folder or a solution", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModel_IsNotToldToCheckItsArguments_WhenNoWorkspaceIsOpen()
    {
        // "Check the arguments and try again" sends the model through one path after another, every one refused.
        var noRoot = ToolFailure.Describe("read_file", new NoWorkspaceRootException());
        Assert.DoesNotContain("Check the arguments", noRoot, StringComparison.Ordinal);
        Assert.Contains("open a folder or a solution", noRoot, StringComparison.Ordinal);

        // Reference arm: a path outside the root is an argument the model can correct.
        Assert.Contains("Check the arguments", ToolFailure.Describe("read_file", new ArgumentException("outside")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutARoot_ACommand_IsRefusedBeforeTheCard_InsteadOfRunningInTheHostsFolder()
    {
        var approval = new CountingApproval();
        using var tool = new RunCommandTool(approval, new Inferpal.Config.InferpalConfig(), () => "");

        await Assert.ThrowsAsync<NoWorkspaceRootException>(() => tool.ExecuteAsync(Args(new { command = "echo hi" }), CancellationToken.None));
        await Assert.ThrowsAsync<NoWorkspaceRootException>(() =>
            new UserShellTool("probe", "echo hi", approval, new Inferpal.Config.InferpalConfig(), () => "")
                .ExecuteAsync(Args(new { }), CancellationToken.None));

        Assert.Equal(0, approval.Asked);
    }

    [Fact]
    public async Task WithoutARoot_ToolsThatDefaultToTheWorkspace_SayNoWorkspace_NotAMissingPath()
    {
        // Their fallbacks (an open file's repository, the working directory) are nobody's choice for the model; and an
        // empty root handed to Sanitize reads as "path missing", which the model answers with a path, refused again.
        await Assert.ThrowsAsync<NoWorkspaceRootException>(() =>
            new GetGitStatusTool(new NullEditorSurface(), () => null).ExecuteAsync(Args(new { }), CancellationToken.None));
        await Assert.ThrowsAsync<NoWorkspaceRootException>(() =>
            new RenameSymbolTool(new CountingApproval(), new FileHistoryService(), () => null)
                .ExecuteAsync(Args(new { old_name = "A", new_name = "B" }), CancellationToken.None));
    }

    // ── The signal: a folder Visual Studio has open ────────────────────────────

    [Fact]
    public void AnOpenFolder_IsReadAsTheFolder_AndNeverAsASolution()
    {
        ActiveSolutionSignal.WriteFolder(_dir);

        Assert.Equal(_dir, ActiveSolutionSignal.TryReadOpenFolder());
        Assert.Null(ActiveSolutionSignal.TryReadSolutionPath());
        Assert.Null(SolutionExtent.OfActiveSolution());
    }

    [Fact]
    public void AnOpenSolution_IsNotAnOpenFolder()
    {
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File");
        ActiveSolutionSignal.Write(sln);

        Assert.Null(ActiveSolutionSignal.TryReadOpenFolder());
        Assert.Equal(sln, ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void AnOpenFolder_IsNotRead_ByAProcessWithoutAVisualStudioPeer_NorOnceItIsGone()
    {
        ActiveSolutionSignal.WriteFolder(_dir);
        Assert.Equal(_dir, ActiveSolutionSignal.TryReadOpenFolder());   // witness: the file answers

        SignalScope.DeclareNoVsInProcessPeer();
        Assert.Null(ActiveSolutionSignal.TryReadOpenFolder());

        SignalScope.ResetForTests();
        SignalFile._isProcessAliveOverride = _ => false;               // its devenv is gone
        Assert.Null(ActiveSolutionSignal.TryReadOpenFolder());

        SignalFile._isProcessAliveOverride = _ => true;
        Directory.Delete(_dir, recursive: true);                         // the folder is gone
        Assert.Null(ActiveSolutionSignal.TryReadOpenFolder());
    }

    [Fact]
    public void AFolderAndASolution_AreToldApartByTheirField_NotByWhatExistsOnDisk()
    {
        // A solution field naming a folder, and a folder field naming a file: neither is read as the other.
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File");

        File.WriteAllText(ActiveSolutionSignal.FilePath, JsonSerializer.Serialize(new { solutionPath = _dir }));
        Assert.Null(ActiveSolutionSignal.TryReadOpenFolder());
        File.WriteAllText(ActiveSolutionSignal.FilePath, JsonSerializer.Serialize(new { folderPath = sln }));
        Assert.Null(ActiveSolutionSignal.TryReadSolutionPath());
    }

    [Fact]
    public void AnOpenFolder_HasOneSpelling_WhicheverEventWroteIt()
    {
        // Visual Studio ends one of its two spellings with a separator: a root that differs by it is re-pinned.
        ActiveSolutionSignal.WriteFolder(_dir + Path.DirectorySeparatorChar);

        Assert.Equal(_dir, ActiveSolutionSignal.TryReadOpenFolder());
    }

    [Fact]
    public void ADriveRoot_OrTheUserFolder_OpenedAsAFolder_IsNotTheWorkspace()
    {
        // The two bounds a solution's extent never widens past: the whole disk, every repository of the person.
        ActiveSolutionSignal.WriteFolder(Path.GetPathRoot(_dir)!);
        Assert.Null(SolutionExtent.ActiveRoot());

        ActiveSolutionSignal.WriteFolder(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.Null(SolutionExtent.ActiveRoot());

        // Reference arm: a project folder, even one inside the user folder, is.
        ActiveSolutionSignal.WriteFolder(_dir);
        Assert.Equal(_dir, SolutionExtent.ActiveRoot());
    }

    [Fact]
    public void TheActiveRoot_IsTheOpenFolder_WhenVisualStudioHasNoSolutionOpen()
    {
        ActiveSolutionSignal.WriteFolder(_dir);
        Assert.Equal(_dir, SolutionExtent.ActiveRoot());

        // A solution reported open wins: its extent is the root, as before.
        var sln = Path.Combine(_dir, "App.sln");
        File.WriteAllText(sln, "Microsoft Visual Studio Solution File");
        ActiveSolutionSignal.Write(sln);
        Assert.Equal(SolutionExtent.OfActiveSolution()!.Root, SolutionExtent.ActiveRoot());

        ActiveSolutionSignal.Clear();
        Assert.Null(SolutionExtent.ActiveRoot());
    }

    // The Visual Studio chat's three roots go through ActiveRoot: SolutionExtentTests.EveryRootTheVisualStudioWindowDerives_*.
}
