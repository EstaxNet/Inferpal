using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>historyStorage: user</c> keeps the copies of the files the agent changes in the user's own application data, one
/// folder per repository, and nothing is written into the project — while <c>/undo-run</c>, <c>/restore</c> and
/// <c>restore_file</c> work as they do with the history in the project, and switching loses no backup.
/// </summary>
public sealed class HistoryOutsideTheProjectTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"histout-{Guid.NewGuid():N}");
    private string Repo => Path.Combine(_base, "repo");
    private string User => Path.Combine(_base, "appdata", "history");

    private readonly List<IDisposable> _owned = [];

    public HistoryOutsideTheProjectTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        foreach (var owned in _owned) { try { owned.Dispose(); } catch { /* best-effort cleanup */ } }
        try
        {
            foreach (var f in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);   // git marks its objects read-only
            Directory.Delete(_base, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>A repository by its <c>.git</c> folder alone: where a history is anchored, nothing more.</summary>
    private string MarkedRepository()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        return Repo;
    }

    private FileHistoryService Outside(bool outside = true) => new()
    {
        OutsideProject  = () => outside,
        UserHistoryRoot = User,
        WorkspaceRoot   = () => Repo,
    };

    private bool ProjectHasInferpalFolder => Directory.Exists(Path.Combine(Repo, ".inferpal"));

    [Fact]
    public async Task ABackupKeptOutsideTheProject_WritesNothingIntoTheProject()
    {
        MarkedRepository();
        var file = Path.Combine(Repo, "src", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "{ \"ApiKey\": \"x\" }");
        var history = Outside();

        var snapshot = await history.SnapshotAsync(file, CancellationToken.None);

        Assert.StartsWith(User + Path.DirectorySeparatorChar, snapshot);
        Assert.False(ProjectHasInferpalFolder, "a backup kept outside the project wrote into it");
        // The folder is named after the repository, and says whose it is.
        var folder = Path.GetDirectoryName(snapshot)!;
        Assert.StartsWith("repo-", Path.GetFileName(folder));
        Assert.Equal(Path.GetFullPath(Repo) + "\n",
                     await File.ReadAllTextAsync(Path.Combine(folder, FileHistoryService.OwnerFileName)));
        // And it is a backup like any other: found, and accepted by restore_file as a source outside the workspace.
        Assert.Equal(snapshot, history.FindMostRecentSnapshot(file));
        Assert.True(history.IsSnapshotOf(snapshot, file));
    }

    [Fact]
    public async Task TheSettingSavedOnABuiltRegistry_DecidesTheNextBackup()
    {
        // The registry reads the setting at each backup, never once at construction.
        MarkedRepository();
        var file = Path.Combine(Repo, "a.cs");
        await File.WriteAllTextAsync(file, "class A {}");
        var (tools, config) = RegistryWithConfig("project");

        var inProject = await tools.History.SnapshotAsync(file, CancellationToken.None);
        config.HistoryStorage = "user";
        var outside = await tools.History.SnapshotAsync(file, CancellationToken.None);

        Assert.StartsWith(Path.Combine(Repo, ".inferpal", "history"), inProject);
        Assert.StartsWith(User + Path.DirectorySeparatorChar, outside);
    }

    // ── a whole run, through the tools ────────────────────────────────────────────────

    private static bool Git(string dir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false,
                                                    RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>A committed repository holding a.txt and b.txt — or the test is UNDECIDED, never green.</summary>
    private string CommittedRepository()
    {
        Assert.True(Git(Repo, "init", "-q", "."), "`git init` failed: this test is UNDECIDED, not green.");
        Git(Repo, "config", "user.email", "test@example.invalid");
        Git(Repo, "config", "user.name", "test");
        Git(Repo, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(Repo, "a.txt"), "alpha\n");
        File.WriteAllText(Path.Combine(Repo, "b.txt"), "bravo\n");
        Assert.True(Git(Repo, "add", "-A") && Git(Repo, "commit", "-q", "-m", "seed"),
                    "the seed commit failed: this test is UNDECIDED, not green.");
        return Repo;
    }

    private ToolRegistry Registry(string storage) => RegistryWithConfig(storage).Tools;

    private (ToolRegistry Tools, InferpalConfig Config) RegistryWithConfig(string storage)
    {
        var config = new InferpalConfig { HistoryStorage = storage };
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var lsp    = new LspSemanticProvider();
        var index  = new ProjectIndexService(client, config, lsp);
        index.SetRoot(Repo);
        var tools = new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                     new McpToolService(config, approval), new DocsIndexService(client, config),
                                     new OpenDocumentOverlay(), new NullDebugSession());
        tools.History.UserHistoryRoot = User;
        _owned.AddRange([tools, index, lsp]);   // the index's watcher holds the folder until disposed
        return (tools, config);
    }

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task ARunWithTheSettingOn_IsUndoneAndRestored_AndTheProjectNeverGetsAnInferpalFolder()
    {
        CommittedRepository();
        var tools = Registry("user");
        var a = Path.Combine(Repo, "a.txt");
        var b = Path.Combine(Repo, "b.txt");

        using (tools.History.BeginRunScope())
        {
            await tools.ExecuteAsync("read_file", Args(new { path = a }), CancellationToken.None);
            await tools.ExecuteAsync("write_file", Args(new { path = a, content = "alpha, rewritten\n" }), CancellationToken.None);
            await tools.ExecuteAsync("run_command", Args(new { command = "echo more >> b.txt" }), CancellationToken.None);
        }
        // WITNESS: both changes happened, and both are in the run.
        Assert.Equal("alpha, rewritten\n", File.ReadAllText(a));
        Assert.NotEqual("bravo\n", File.ReadAllText(b));
        Assert.Equal(2, tools.History.Runs[0].FileCount);
        Assert.NotEmpty(Directory.EnumerateFiles(User, "*" + FileHistoryService.SnapshotExtension, SearchOption.AllDirectories));

        await UndoRunCommandHandler.HandleAsync(tools.History, ["/undo-run"], Repo, CancellationToken.None);

        Assert.Equal("alpha\n", File.ReadAllText(a));
        Assert.Equal("bravo\n", File.ReadAllText(b));

        // And restore_file, outside any run: it finds the backup the undo took of the rewritten version.
        var restored = await tools.ExecuteAsync("restore_file", Args(new { path = a }), CancellationToken.None);
        Assert.Equal("alpha, rewritten\n", File.ReadAllText(a));
        Assert.DoesNotContain("Error", restored, StringComparison.Ordinal);

        Assert.False(ProjectHasInferpalFolder, "the project got an .inferpal folder although the history is kept outside it");
        var (status, _) = await GitProcess.RunAsync("status --porcelain --untracked-files=all", Repo, CancellationToken.None);
        Assert.DoesNotContain(".inferpal", status);
    }

    [Fact]
    public async Task ASnapshotPathFromTheUserFolder_IsAcceptedByRestoreFile_AndNoOtherFileThere()
    {
        MarkedRepository();
        var file = Path.Combine(Repo, "a.cs");
        await File.WriteAllTextAsync(file, "class A {}");
        var history = Outside();
        var snapshot = await history.SnapshotAsync(file, CancellationToken.None);
        await File.WriteAllTextAsync(file, "class A { int changed; }");
        var tool = new RestoreFileTool(new NoopApproval(), history, () => Repo);

        await tool.ExecuteAsync(Args(new { path = file, snapshot }), CancellationToken.None);
        Assert.Equal("class A {}", await File.ReadAllTextAsync(file));

        // Reference arm: a backup of ANOTHER file of that same folder, named as backups are, stays outside the workspace —
        // the folder alone grants nothing, only this file's own backups.
        var other = Path.Combine(Repo, "b.cs");
        await File.WriteAllTextAsync(other, "class B {}");
        var stranger = await history.SnapshotAsync(other, CancellationToken.None);
        Assert.Equal(Path.GetDirectoryName(snapshot), Path.GetDirectoryName(stranger));   // witness: same folder
        await File.WriteAllTextAsync(file, "class A { int changed; }");
        await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(Args(new { path = file, snapshot = stranger }), CancellationToken.None));
        Assert.Equal("class A { int changed; }", await File.ReadAllTextAsync(file));
    }

    // ── switching ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchingTheSetting_KeepsTheBackupsMadeBefore_Restorable(bool startsOutside)
    {
        MarkedRepository();
        var file = Path.Combine(Repo, "a.cs");
        await File.WriteAllTextAsync(file, "class A {}");
        var outside = startsOutside;
        var history = new FileHistoryService { OutsideProject = () => outside, UserHistoryRoot = User, WorkspaceRoot = () => Repo };

        var before = await history.SnapshotAsync(file, CancellationToken.None);
        Assert.Equal(startsOutside, before.StartsWith(User, StringComparison.Ordinal));   // witness: where it was written
        outside = !startsOutside;
        await File.WriteAllTextAsync(file, "class A { int changed; }");

        Assert.Equal(before, history.FindMostRecentSnapshot(file));
        Assert.Equal(before, await history.FindRestoreCandidateAsync(file, CancellationToken.None));
        Assert.True(history.IsSnapshotOf(before, file));
    }

    // ── one folder per repository ─────────────────────────────────────────────────────

    [Fact]
    public void OneRepository_IsOneFolder_ForEveryInstance_AndTwoRepositoriesAreTwo()
    {
        var other = Path.Combine(_base, "other", "repo");

        Assert.Equal(Outside().UserHistoryDir(Repo), Outside().UserHistoryDir(Repo + Path.DirectorySeparatorChar));
        Assert.NotEqual(Outside().UserHistoryDir(Repo), Outside().UserHistoryDir(other));
        // Same folder exactly where the shared rule says the two spellings are one folder: never under Linux.
        var upper = Repo.ToUpperInvariant();
        Assert.Equal(PathComparer.Default.Equals(Repo, upper),
                     Outside().UserHistoryDir(Repo) == Outside().UserHistoryDir(upper));
    }

    // ── no folder to keep it in ───────────────────────────────────────────────────────

    [Fact]
    public async Task WithoutAnAbsoluteFolderForTheUsersData_TheWriteIsRefused_AndNothingLandsInTheWorkingDirectory()
    {
        MarkedRepository();
        var file = Path.Combine(Repo, "a.cs");
        await File.WriteAllTextAsync(file, "class A {}");
        var relative = $"inferpal-nohome-{Guid.NewGuid():N}";
        var history = new FileHistoryService { OutsideProject = () => true, UserHistoryRoot = relative, WorkspaceRoot = () => Repo };
        var tool = new WriteFileTool(new NoopApproval(), history, () => Repo);

        var answer = await tool.ExecuteAsync(Args(new { path = file, content = "class A { int lost; }" }), CancellationToken.None);

        Assert.Equal(history.BackupFailedMessage(file), answer);
        // The remedy is the setting, not a folder to make writable: there is none.
        Assert.Contains("historyStorage", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("writable", answer, StringComparison.Ordinal);
        Assert.Equal("class A {}", await File.ReadAllTextAsync(file));
        Assert.False(Directory.Exists(Path.GetFullPath(relative)), "a relative history folder was created in the working directory");
        Assert.False(ProjectHasInferpalFolder);
    }

    // ── the copies kept outside the project are never read for the model ────────────

    [Fact]
    public void TheUserFoldersHistory_IsNeverWalked_NorReadForTheModel_WhenAWorkspaceHoldsIt()
    {
        var root   = FileHistoryService.DefaultUserHistoryRoot;
        var backup = Path.Combine(root, "repo-0a1b2c3d4e5f", "2026-10-21_10-00-00-000_1234abcd_.env.bak");
        var home   = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!)!;   // a workspace above it
        Assert.True(Path.IsPathFullyQualified(root), "no application data folder on this machine: UNDECIDED, not green.");

        Assert.True(WorkspaceScan.IsExcludedDirName(root));
        Assert.True(WorkspaceScan.IsExcludedDirName(Path.GetDirectoryName(backup)!));
        Assert.True(WorkspaceScan.IsExcludedPath(backup, home));
        // Reference arm: its neighbours in the same application data are not Inferpal's history.
        Assert.False(WorkspaceScan.IsExcludedDirName(Path.Combine(Path.GetDirectoryName(root)!, "rag")));
        Assert.False(WorkspaceScan.IsExcludedPath(Path.Combine(Path.GetDirectoryName(root)!, "rag", "x.db"), home));
    }

    [Fact]
    public void TheBackupFailedMessage_NamesTheFolderTheBackupWasMeantFor()
    {
        MarkedRepository();
        var file = Path.Combine(Repo, "a.cs");

        Assert.Contains(User, Outside().BackupFailedMessage(file), StringComparison.Ordinal);
        Assert.Contains(Path.Combine(Repo, ".inferpal", "history"), Outside(outside: false).BackupFailedMessage(file),
                        StringComparison.Ordinal);
    }

    // ── the setting ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("project", false)]
    [InlineData("user", true)]
    [InlineData("User", true)]
    [InlineData("elsewhere", false)]
    [InlineData("", false)]
    public void TheSetting_IsOutsideTheProject_ForUserOnly(string value, bool outside) =>
        Assert.Equal(outside, new InferpalConfig { HistoryStorage = value }.HistoryOutsideProject);

    [Fact]
    public void TheDefault_KeepsTheHistoryInTheProject() =>
        Assert.False(new InferpalConfig().HistoryOutsideProject);

    [Theory]
    [InlineData("elsewhere", false)]
    [InlineData(" User ", true)]
    public void AHandWrittenValue_IsKeptAsWritten_AndReadAsWhatInferpalDoes(string written, bool outside)
    {
        var config = JsonSerializer.Deserialize<InferpalConfig>($$"""{ "historyStorage": "{{written}}" }""")!;

        Assert.Equal(written, config.HistoryStorage);   // the settings panel round-trips the file as it is
        Assert.Equal(outside, config.HistoryOutsideProject);
    }

    [Fact]
    public void ARepositorysProfile_CannotChooseWhereTheBackupsGo()
    {
        var profile = ProjectProfile.Parse("""{ "historyStorage": "user" }""");

        Assert.Contains(profile.Ignored, k => k.Key == "historyStorage" && k.Sensitive);
    }

    [Fact]
    public void RestoreFilesDescription_NamesNoFolder()
    {
        var description = new RestoreFileTool(new NoopApproval(), new FileHistoryService(), () => Repo).Description;

        Assert.Contains("you do not need to find it", description, StringComparison.Ordinal);   // witness: the real text
        Assert.DoesNotContain(".inferpal", description, StringComparison.Ordinal);
    }
}

/// <summary>
/// Where the next backup goes is the setting in <c>config.json</c> NOW: chosen in the other editor, it applies here at
/// once — a copy that kept its own value went on writing into the project from one editor.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class HistorySettingFollowsTheFileTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _path = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"histcfg-{Guid.NewGuid():N}.json");

    public HistorySettingFollowsTheFileTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        InferpalConfig.OverridePathForTests = _path;
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { File.Delete(_path); } catch { }
    }

    private void WriteAsTheOtherEditor(string storage)
    {
        var other = JsonSerializer.Deserialize<InferpalConfig>(File.ReadAllText(_path))!;
        other.HistoryStorage = storage;
        File.WriteAllText(_path, JsonSerializer.Serialize(other));
        File.SetLastWriteTimeUtc(_path, DateTime.UtcNow.AddSeconds(5));   // a different stamp, however fast the machine
    }

    [Fact]
    public void TheOtherEditorsChoice_AppliesHere_AtTheNextBackup()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();
        Assert.False(mine.SharedHistoryOutsideProject);   // witness: the project, as saved

        WriteAsTheOtherEditor("user");
        Assert.True(mine.SharedHistoryOutsideProject);

        WriteAsTheOtherEditor("project");
        Assert.False(mine.SharedHistoryOutsideProject);
    }

    [Fact]
    public async Task TheRegistry_WritesTheNextBackupWhereTheFileSays()
    {
        var repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"histcfg-repo-{Guid.NewGuid():N}");
        var user = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"histcfg-user-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var file = Path.Combine(repo, "a.cs");
        await File.WriteAllTextAsync(file, "class A {}");
        new InferpalConfig().Save();
        var mine   = InferpalConfig.Load();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        using var lsp   = new LspSemanticProvider();
        using var index = new ProjectIndexService(client, mine, lsp);
        index.SetRoot(repo);
        using var tools = new ToolRegistry(editor, approval, mine, index, client, new ProjectMapService(editor),
                                           new McpToolService(mine, approval), new DocsIndexService(client, mine),
                                           new OpenDocumentOverlay(), new NullDebugSession());
        tools.History.UserHistoryRoot = user;
        try
        {
            WriteAsTheOtherEditor("user");

            var snapshot = await tools.History.SnapshotAsync(file, CancellationToken.None);

            Assert.StartsWith(user + Path.DirectorySeparatorChar, snapshot);
            Assert.False(Directory.Exists(Path.Combine(repo, ".inferpal")));
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { }
            try { Directory.Delete(user, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AChoiceMadeInThisEditor_IsThisEditorsUntilSaved()
    {
        new InferpalConfig().Save();
        var mine = InferpalConfig.Load();
        mine.HistoryStorage = "user";

        WriteAsTheOtherEditor("project");

        Assert.True(mine.SharedHistoryOutsideProject);
    }
}
