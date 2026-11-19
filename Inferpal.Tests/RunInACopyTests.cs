using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Execution;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A run that works in a copy of the repository (pari 106): its tools read, write and run inside the copy, nothing reaches
/// the person's own folder before they apply — three hostile attempts refused and said (a write by the absolute path, a
/// command run in their folder, a write through a link out of the copy) — and what acts on the person's editor, debugger or
/// index is not offered there.
/// </summary>
[Collection(WorkCopyCollection.Name)]
public sealed class RunInACopyTests : IDisposable
{
    private readonly DirectoryInfo _repo = Directory.CreateTempSubdirectory("inferpal-runcopy-repo-");
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) try { owned.Dispose(); } catch { }
        Services.Governance.ContentExclusions.ForgetForTests(_repo.FullName);
        foreach (var dir in new[] { _repo })
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir.FullName, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                dir.Delete(recursive: true);
            }
            catch { }
    }

    private bool Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _repo.FullName, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-c", "user.email=test@example.com", "-c", "user.name=Test", "-c", "commit.gpgsign=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        return p.WaitForExit(30_000) && p.ExitCode == 0;
    }

    private async Task<WorkCopy> CopyAsync()
    {
        File.WriteAllText(Path.Combine(_repo.FullName, "Pay.cs"), "class Pay\n{\n    int A() => 1;\n}\n");
        Assert.True(Git("init", "-q", ".") && Git("add", "-A") && Git("commit", "-q", "-m", "start"), "git is needed: this test is UNDECIDED, not green.");
        var (copy, refusal) = await WorkCopy.CreateAsync(_repo.FullName, CancellationToken.None);
        Assert.True(copy is not null, refusal);
        return copy!;
    }

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(_repo.FullName);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    /// <summary>Every file of the person's folder (git's own included), by path, with its bytes' hash.</summary>
    private Dictionary<string, string> Fingerprint() =>
        Directory.EnumerateFiles(_repo.FullName, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => Path.GetRelativePath(_repo.FullName, f),
                               f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task TheRun_WritesAndRunsInTheCopy_AndThreeHostileAttempts_AreRefused_TheUsersFolderUntouched()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        var before = Fingerprint();
        string written, hostileWrite, hostileCommand, witness;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            // The run's own work lands in the copy.
            written = await tools.ExecuteAsync("write_file", Args(new { path = "Pay.cs", content = "class Pay\n{\n    int A() => 2;\n}\n" }), CancellationToken.None);
            witness = await tools.ExecuteAsync("run_command", Args(new { command = "echo ran > witness.txt" }), CancellationToken.None);
            // 1. A write by the absolute path of the person's file.
            hostileWrite = await tools.ExecuteAsync("write_file", Args(new { path = Path.Combine(_repo.FullName, "Pay.cs"), content = "hacked" }), CancellationToken.None);
            // 2. A command run in the person's folder.
            hostileCommand = await tools.ExecuteAsync("run_command", Args(new { command = "echo x", working_directory = _repo.FullName }), CancellationToken.None);
        }

        Assert.DoesNotContain("Error", written, StringComparison.Ordinal);
        Assert.Contains("int A() => 2;", File.ReadAllText(Path.Combine(copy.Folder, "Pay.cs")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(copy.Folder, "witness.txt")), witness);                // the command ran in the copy
        Assert.StartsWith("Error", hostileWrite.TrimStart('❌', ' '), StringComparison.Ordinal);
        Assert.Contains("copy of the repository", hostileCommand, StringComparison.Ordinal);
        Assert.Equal(before, Fingerprint());                                                         // not one byte moved
    }

    [Fact]
    public async Task AWriteThroughALinkOutOfTheCopy_IsRefused()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        try { Directory.CreateSymbolicLink(Path.Combine(copy.Folder, "linked"), _repo.FullName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // undecided here: this machine cannot create links without a privilege
        }
        var before = Fingerprint();
        string answer;
        using (RunRoot.Enter(copy, _repo.FullName))
            answer = await tools.ExecuteAsync("write_file", Args(new { path = "linked/Pay.cs", content = "hacked" }), CancellationToken.None);

        Assert.Contains("Error", answer, StringComparison.Ordinal);
        Assert.Equal(before, Fingerprint());
    }

    [Fact]
    public async Task ACommandThatNamesTheUsersFolder_IsRefused_OneInsideTheCopyRuns()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        string named, inside;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            named = await tools.ExecuteAsync("run_command", Args(new { command = $"echo x > \"{Path.Combine(_repo.FullName, "out.txt")}\"" }), CancellationToken.None);
            inside = await tools.ExecuteAsync("run_command", Args(new { command = "echo inside" }), CancellationToken.None);
        }

        Assert.Contains("names the user's own folder", named, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_repo.FullName, "out.txt")));
        Assert.Contains("inside", inside, StringComparison.Ordinal);                                 // reference arm
    }

    [Theory]
    [InlineData(@"C:\Users\ana", @"C:\Users\ana\AppData\Local\Inferpal\copies\r\1", @"cd C:\Users\ana\AppData\Local\Inferpal\copies\r\1\src", false)]
    [InlineData(@"C:\Users\ana", @"C:\Users\ana\AppData\Local\Inferpal\copies\r\1", @"del C:\Users\ana\notes.txt", true)]
    [InlineData(@"C:\dev\shop", @"D:\copies\shop\1", "cat C:/dev/shop/Pay.cs", true)]
    // A folder whose name starts like theirs is not theirs.
    [InlineData(@"C:\dev\shop", @"D:\copies\shop\1", @"type C:\dev\shopfront\a.txt", false)]
    // Climbing back from the copy to their folder.
    [InlineData(@"C:\dev\shop", @"C:\dev\copies\shop\1", @"del C:\dev\copies\shop\1\..\..\..\shop\Pay.cs", true)]
    // A quoted path with a space.
    [InlineData(@"C:\dev\my shop", @"D:\copies\1", "type \"C:\\dev\\my shop\\Pay.cs\"", true)]
    public void ACommandIsJudgedOnThePathItReaches_NotOnItsSpelling(string origin, string copy, string command, bool refused)
    {
        if (!OperatingSystem.IsWindows()) return;   // undecided here: Windows paths, which this system does not resolve
        Assert.Equal(refused, RunRoot.ReachesOrigin(command, copy, origin));
    }

    [Fact]
    public async Task AFileTheUsersIgnoreFileKeepsOut_StaysUnreadInTheCopy_EvenWhenThatIgnoreFileIsNotCommitted()
    {
        File.WriteAllText(Path.Combine(_repo.FullName, "secret.txt"), "the key");
        var copy = await CopyAsync();
        // Written after the copy: an uncommitted ignore file, which the copy does not hold.
        File.WriteAllText(Path.Combine(_repo.FullName, ".aiignore"), "secret.txt\n");
        var tools = Registry();
        string secret, pay;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            secret = await tools.ExecuteAsync("read_file", Args(new { path = "secret.txt" }), CancellationToken.None);
            pay = await tools.ExecuteAsync("read_file", Args(new { path = "Pay.cs" }), CancellationToken.None);
        }

        Assert.True(File.Exists(Path.Combine(copy.Folder, "secret.txt")));                          // the copy holds it
        Assert.Contains("excluded from AI tools", secret, StringComparison.Ordinal);
        Assert.DoesNotContain("the key", secret, StringComparison.Ordinal);
        Assert.Contains("class Pay", pay, StringComparison.Ordinal);                                 // reference arm
    }

    /// <summary>Answers every card with "Once", and counts them.</summary>
    private sealed class Card(InferpalConfig config, Func<string?> root) : ApprovalServiceBase(config, root)
    {
        public int Shown;

        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown++;
            return Task.FromResult(ApprovalDecision.Once);
        }

        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct)
        {
            Shown++;
            return Task.FromResult(ApprovalDecision.Once);
        }
    }

    [Fact]
    public async Task TheUsersDenyRules_HoldInTheCopy_EvenWhenTheirFileIsNotCommitted()
    {
        var copy = await CopyAsync();
        Directory.CreateDirectory(Path.Combine(_repo.FullName, ".inferpal"));
        File.WriteAllText(Path.Combine(_repo.FullName, ".inferpal", "permissions.json"), """{ "rules": ["deny run_command curl"] }""");
        var card = new Card(new InferpalConfig(), () => _repo.FullName);
        bool denied = false, other;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            try { await card.RequestApprovalAsync("run_command", "curl https://example.com", CancellationToken.None, subject: "curl https://example.com"); }
            catch (PermissionDeniedException) { denied = true; }
            other = await card.RequestApprovalAsync("run_command", "echo x", CancellationToken.None, subject: "echo x");
        }

        Assert.False(File.Exists(Path.Combine(copy.Folder, ".inferpal", "permissions.json")));       // the copy lacks it
        Assert.True(denied, "the person's deny rule did not hold in the copy");
        Assert.True(other);                                                                          // reference arm
        Assert.Equal(1, card.Shown);
    }

    /// <summary>Declines the card that asks to run the workspace's code, approves every other one.</summary>
    private sealed class DecliningCode(string repo) : IApprovalService
    {
        public int Asked;

        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               Services.CodeActions.DiffInfo? diff = null, bool forcePrompt = false)
        {
            if (subject is not null && PathComparer.SameDirectory(subject, repo)) { Asked++; return Task.FromResult(false); }
            return Task.FromResult(true);
        }
    }

    [Fact]
    public async Task ANoToRunningTheRepositorysCode_HoldsInItsCopy()
    {
        File.WriteAllText(Path.Combine(_repo.FullName, "Shop.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        var copy = await CopyAsync();
        var approval = new DecliningCode(_repo.FullName);
        var index = new ProjectIndexService(new FakeInferenceProvider(), new InferpalConfig(), new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(_repo.FullName);
        var editor = new NullEditorSurface();
        var tools = new ToolRegistry(editor, approval, new InferpalConfig(), index, new FakeInferenceProvider(), new ProjectMapService(editor),
                                     new McpToolService(new InferpalConfig(), approval), new DocsIndexService(new FakeInferenceProvider(), new InferpalConfig()),
                                     new OpenDocumentOverlay(), new NullDebugSession());
        var mine = await tools.ExecuteAsync("msbuild_property", Args(new { name = "TargetFramework" }), CancellationToken.None);
        string inCopy;
        using (RunRoot.Enter(copy, _repo.FullName))
            inCopy = await tools.ExecuteAsync("msbuild_property", Args(new { name = "TargetFramework" }), CancellationToken.None);

        Assert.Contains(WorkspaceCodeConsent.Refusal, mine, StringComparison.Ordinal);               // reference arm: asked, declined
        Assert.Contains(WorkspaceCodeConsent.Refusal, inCopy, StringComparison.Ordinal);
        Assert.Equal(1, approval.Asked);                                                             // never asked again in the copy
    }

    [Fact]
    public async Task ThePrompt_StatesTheCopysRoot_InsideTheRun()
    {
        var copy = await CopyAsync();
        var builder = new Services.Prompting.SystemPromptBuilder(new InferpalConfig(), workspaceRoot: _repo.FullName);
        var outside = builder.EnvironmentFacts();
        string inside;
        using (RunRoot.Enter(copy, _repo.FullName))
            inside = builder.EnvironmentFacts();

        Assert.Contains($"The workspace root is {_repo.FullName}.", outside, StringComparison.Ordinal);   // reference arm
        Assert.Contains($"The workspace root is {Path.GetFullPath(copy.Folder)}.", inside, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARename_AfterAWriteInTheCopy_PlansOnTheCodeTheWriteLeft()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        string renamed;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            // A first rename builds the copy's C# index.
            await tools.ExecuteAsync("rename_symbol", Args(new { old_name = "A", new_name = "One" }), CancellationToken.None);
            // Lines written above the class: every offset the first index read has moved.
            await tools.ExecuteAsync("write_file", Args(new { path = "Pay.cs", content = "// Payments.\n// The amounts due.\nclass Pay\n{\n    int One() => 1;\n}\n" }), CancellationToken.None);
            renamed = await tools.ExecuteAsync("rename_symbol", Args(new { old_name = "Pay", new_name = "Payment" }), CancellationToken.None);
        }

        Assert.Equal("// Payments.\n// The amounts due.\nclass Payment\n{\n    int One() => 1;\n}\n",
                     File.ReadAllText(Path.Combine(copy.Folder, "Pay.cs")).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task AWorkspaceOnASubfolder_WorksInTheSameSubfolderOfTheCopy_WhereItsIgnoreRulesHold()
    {
        var app = Directory.CreateDirectory(Path.Combine(_repo.FullName, "app")).FullName;
        File.WriteAllText(Path.Combine(app, ".env"), "KEY=1");
        File.WriteAllText(Path.Combine(app, "Main.cs"), "class Main { }\n");
        var copy = await CopyAsync();
        File.WriteAllText(Path.Combine(app, ".aiignore"), ".env\n");     // not committed
        var config = new InferpalConfig();
        var index = new ProjectIndexService(new FakeInferenceProvider(), config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(app);
        var editor = new NullEditorSurface();
        var tools = new ToolRegistry(editor, new NoopApproval(), config, index, new FakeInferenceProvider(), new ProjectMapService(editor),
                                     new McpToolService(config, new NoopApproval()), new DocsIndexService(new FakeInferenceProvider(), config),
                                     new OpenDocumentOverlay(), new NullDebugSession());
        string env, main, root;
        using (RunRoot.Enter(copy, app))
        {
            root = RunRoot.Copy!;
            env = await tools.ExecuteAsync("read_file", Args(new { path = ".env" }), CancellationToken.None);
            main = await tools.ExecuteAsync("read_file", Args(new { path = "Main.cs" }), CancellationToken.None);
        }

        Assert.True(PathComparer.SameDirectory(Path.Combine(copy.Folder, "app"), root), root);
        Assert.Contains("excluded from AI tools", env, StringComparison.Ordinal);
        Assert.Contains("class Main", main, StringComparison.Ordinal);                               // reference arm
    }

    [Theory]
    [InlineData(@"D:\", @"D:\src\file.cs", true)]
    [InlineData(@"D:\src", @"D:\src2\file.cs", false)]
    [InlineData(@"D:\src", @"D:\src", true)]
    public void AFolder_HoldsWhatIsBelowIt_ADriveRootToo(string root, string path, bool holds)
    {
        if (!OperatingSystem.IsWindows()) return;   // undecided here: Windows paths, which this system does not resolve
        Assert.Equal(holds, RunRoot.Holds(root, path));
    }

    /// <summary>Answers every card with "Always", and counts them.</summary>
    private sealed class AlwaysCard(InferpalConfig config, Func<string?> root) : ApprovalServiceBase(config, root)
    {
        public int Shown;

        protected override Task<ApprovalDecision> PromptUserAsync(ApprovalPrompt prompt, CancellationToken ct)
        {
            Shown++;
            return Task.FromResult(ApprovalDecision.Always);
        }

        protected override Task<ApprovalDecision> PromptUserAsync(string message, Services.CodeActions.DiffInfo? diff, CancellationToken ct)
        {
            Shown++;
            return Task.FromResult(ApprovalDecision.Always);
        }
    }

    [Fact]
    public async Task AnAlwaysGivenInOneCopyRun_IsNotFoundByTheNextRunInTheSameFolder()
    {
        var copy = await CopyAsync();
        var card = new AlwaysCard(new InferpalConfig(), () => _repo.FullName);
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            await card.RequestApprovalAsync("run_command", "echo a", CancellationToken.None, subject: "echo a");
            await card.RequestApprovalAsync("run_command", "echo b", CancellationToken.None, subject: "echo b");
        }
        Assert.Equal(1, card.Shown);                                                                 // reference arm: "Always" held in its run
        using (RunRoot.Enter(copy, _repo.FullName))
            await card.RequestApprovalAsync("run_command", "echo c", CancellationToken.None, subject: "echo c");

        Assert.Equal(2, card.Shown);
    }

    [Fact]
    public async Task AGitCommandThatChangesTheSharedStore_IsRefusedInTheCopy_OneThatReadsRuns()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        string stash, branch, status, listing;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            stash = await tools.ExecuteAsync("run_command", Args(new { command = "git stash" }), CancellationToken.None);
            branch = await tools.ExecuteAsync("run_command", Args(new { command = "echo x && git branch -f master HEAD" }), CancellationToken.None);
            status = await tools.ExecuteAsync("run_command", Args(new { command = "git status --short" }), CancellationToken.None);
            listing = await tools.ExecuteAsync("run_command", Args(new { command = "git branch --show-current" }), CancellationToken.None);
        }

        Assert.Contains("shares its git store", stash, StringComparison.Ordinal);
        Assert.Contains("shares its git store", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("shares its git store", status, StringComparison.Ordinal);            // reference arm
        Assert.Contains(copy.Branch, listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobTheUserStartedInTheirFolder_IsNeitherListedNorStoppedByTheCopyRun()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        var started = await tools.ExecuteAsync("run_command", Args(new { command = "ping -n 30 127.0.0.1", background = true }), CancellationToken.None);
        var id = System.Text.RegularExpressions.Regex.Match(started, @"bg\d+").Value;
        Assert.False(string.IsNullOrEmpty(id), started);
        string listed, stopped;
        try
        {
            using (RunRoot.Enter(copy, _repo.FullName))
            {
                listed = await tools.ExecuteAsync("run_command", Args(new { action = "list" }), CancellationToken.None);
                stopped = await tools.ExecuteAsync("run_command", Args(new { action = "stop", id }), CancellationToken.None);
            }
            Assert.DoesNotContain(id, listed, StringComparison.Ordinal);
            Assert.Contains("user's own folder", stopped, StringComparison.Ordinal);
            var mine = await tools.ExecuteAsync("run_command", Args(new { action = "list" }), CancellationToken.None);
            Assert.Contains(id, mine, StringComparison.Ordinal);                                     // reference arm: still running
        }
        finally { await tools.ExecuteAsync("run_command", Args(new { action = "stop", id }), CancellationToken.None); }
    }

    [Fact]
    public async Task ARunEnteredAgainInTheSameCopy_KeepsItsAgreements_AndLeavingIt_GivesTheOuterOneBack()
    {
        var copy = await CopyAsync();
        string? outer, inner, after;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            outer = RunRoot.GrantsScope;
            using (RunRoot.Enter(copy, _repo.FullName)) inner = RunRoot.GrantsScope;
            after = RunRoot.GrantsScope;
        }

        Assert.NotNull(outer);
        Assert.Equal(outer, inner);
        Assert.Equal(outer, after);
        Assert.Null(RunRoot.GrantsScope);
    }

    [Fact]
    public async Task WhatActsOnTheEditorDebuggerOrIndex_IsNotOfferedInACopy_AndRefusedIfCalled()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        var outside = tools.Definitions.Select(d => d.Function.Name).ToHashSet();
        HashSet<string> inCopy;
        string call;
        using (RunRoot.Enter(copy, _repo.FullName))
        {
            inCopy = tools.Definitions.Select(d => d.Function.Name).ToHashSet();
            call = await tools.ExecuteAsync("insert_at_cursor", Args(new { text = "x" }), CancellationToken.None);
        }

        Assert.Contains("insert_at_cursor", outside);                                                 // reference arm
        foreach (var name in new[] { "insert_at_cursor", "replace_selection", "search_codebase" })
            Assert.DoesNotContain(name, inCopy);
        Assert.Contains("write_file", inCopy);
        Assert.Contains("acts on the user's editor", call, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property the copy's offering reads, held by what a tool is built with: one that takes the person's editor,
    /// debugger or index declares whether it acts on them — a new one cannot be offered in a copy by default.
    /// </summary>
    [Fact]
    public void EveryToolBuiltWithTheEditorDebuggerOrIndex_SaysWhetherItActsOnThem()
    {
        // Built with the editor, and declaring false on purpose: each follows the copy's root and skips its editor part there.
        var exempt = new Dictionary<string, string>
        {
            ["GetDiagnosticsTool"]  = "builds in the copy; the editor's panel is skipped there",
            ["GetGitStatusTool"]    = "reads the git root of the run's root; the open files only stand in without a root",
            ["GetSolutionInfoTool"] = "looks in the copy; the open solution and the open files are skipped there",
        };
        var reaching = new[] { typeof(Services.Editor.IEditorSurface), typeof(Services.Debugging.IDebugSession), typeof(ProjectIndexService), typeof(ProjectMapService) };
        var tools = typeof(ToolRegistry).Assembly.GetTypes()
            .Where(t => typeof(Services.Tools.ITool).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                         .Any(c => c.GetParameters().Any(p => reaching.Contains(p.ParameterType))))
            .ToList();

        Assert.True(tools.Count >= 10, $"only {tools.Count} tools found: the scan no longer reads them");   // witness
        foreach (var tool in tools.Where(t => !exempt.ContainsKey(t.Name)))
            Assert.True(tool.GetProperty(nameof(Services.Tools.ITool.ActsOnTheEditorOrIndex)) is not null,
                        $"{tool.Name} is built with the editor, debugger or index: declare ActsOnTheEditorOrIndex, or exempt it with its reason");
    }

    [Fact]
    public async Task OutsideTheRun_TheWorkspacesRootIsBack()
    {
        var copy = await CopyAsync();
        var tools = Registry();
        using (RunRoot.Enter(copy, _repo.FullName)) { }
        var answer = await tools.ExecuteAsync("write_file", Args(new { path = "New.cs", content = "class New { }\n" }), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_repo.FullName, "New.cs")), answer);
        Assert.False(File.Exists(Path.Combine(copy.Folder, "New.cs")));
    }

    [Fact]
    public void AFolderOpenedThroughALink_IsPlacedInTheRepositoryGitNames_AndBothSpellingsAreTheOrigin()
    {
        // git names the repository with its links resolved (macOS's /var is /private/var); the editor names the folder as
        // it was opened. Compared as written, the two shared no prefix, and every question for a copy failed.
        var real = Directory.CreateTempSubdirectory("inferpal-runroot-real-").FullName;
        var link = real + "-link";
        var copyTop = Directory.CreateTempSubdirectory("inferpal-runroot-copy-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(real, "app"));
            if (OperatingSystem.IsWindows())
            {
                using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                mklink.WaitForExit();
            }
            else Directory.CreateSymbolicLink(link, real);
            Assert.True(Directory.Exists(Path.Combine(link, "app")), "the link was not made: this test is UNDECIDED, not green.");

            using (RunRoot.Enter(copyTop, real, Path.Combine(link, "app")))
            {
                Assert.Equal(Path.Combine(copyTop, "app"), RunRoot.Copy);
                Assert.Equal(Path.Combine(link, "app", "x.txt"), RunRoot.InOrigin(Path.Combine(copyTop, "app", "x.txt")));
                Assert.True(RunRoot.ReachesOrigin($"cat \"{Path.Combine(real, "app", "x.txt")}\""));
                Assert.True(RunRoot.ReachesOrigin($"cat \"{Path.Combine(link, "app", "x.txt")}\""));
                Assert.False(RunRoot.ReachesOrigin($"cat \"{Path.Combine(copyTop, "app", "x.txt")}\""));   // reference arm
            }
        }
        finally
        {
            try { Directory.Delete(link); } catch { }
            try { Directory.Delete(real, recursive: true); } catch { }
            try { Directory.Delete(copyTop, recursive: true); } catch { }
        }
    }
}
