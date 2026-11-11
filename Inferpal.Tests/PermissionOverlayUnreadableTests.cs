using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A workspace's <c>.inferpal/permissions.json</c> that exists and cannot be read is not an absent one: read as "no
/// rules", the restrictions the team wrote were lifted. The last rules read stay; never read, every call is asked.
/// </summary>
[Collection(GlobalConfigPathCollection.Name)]
public sealed class PermissionOverlayUnreadableTests : IDisposable
{
    private readonly string? _previous = InferpalConfig.OverridePathForTests;
    private readonly string  _ws       = Directory.CreateTempSubdirectory("inferpal-overlay").FullName;
    private string Overlay => Path.Combine(_ws, ".inferpal", "permissions.json");
    private readonly List<string> _said = [];

    public PermissionOverlayUnreadableTests()
    {
        InferpalConfig.OverridePathForTests = Path.Combine(_ws, "config.json");
        // The machine allows every command: only the workspace's deny rules, or their absence, make a card.
        new InferpalConfig { PermissionRules = "allow run_command .*" }.Save();
        Directory.CreateDirectory(Path.GetDirectoryName(Overlay)!);
    }

    public void Dispose()
    {
        InferpalConfig.OverridePathForTests = _previous;
        try { Directory.Delete(_ws, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private sealed class CountingApproval(InferpalConfig config, string root) : ApprovalServiceBase(config, () => root)
    {
        public int Prompts;
        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(ApprovalDecision.Once);
        }
    }

    private CountingApproval Service() =>
        new(InferpalConfig.Load(), _ws) { Notice = _said.Add, RetryUnread = TimeSpan.Zero };   // a released file is read at once

    private void WriteOverlay(string text, int secondsLater)
    {
        File.WriteAllText(Overlay, text);
        File.SetLastWriteTimeUtc(Overlay, DateTime.UtcNow.AddSeconds(secondsLater));   // a new stamp, however fast
    }

    private static Task<bool> Run(CountingApproval approval, string command) =>
        approval.RequestApprovalAsync("run_command", command, CancellationToken.None);

    [Fact]
    public async Task NoOverlay_TheMachinesAllowRuleApproves()
    {
        var approval = Service();
        Assert.True(await Run(approval, "echo hi"));
        Assert.Equal(0, approval.Prompts);
        Assert.False(approval.RulesUnreadable);
        Assert.Empty(_said);
    }

    [Fact]
    public async Task AnOverlayNeverRead_AsksForEveryCall_AndSaysSoOnce()
    {
        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ", 1);   // cut off: not JSON
        var approval = Service();

        Assert.True(await Run(approval, "echo hi"));
        Assert.True(await Run(approval, "echo again"));
        Assert.Equal(2, approval.Prompts);                   // the allow rule no longer approves on its own
        Assert.True(approval.RulesUnreadable);
        Assert.True(approval.Refuses("fetch_url", "https://example.com"));
        Assert.Equal([Localization.Strings.PermissionRulesUnreadable], _said);   // once, not per call

        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ] }", 2);
        Assert.True(await Run(approval, "echo hi"));
        Assert.Equal(2, approval.Prompts);                   // read again: the allow rule approves
        Assert.False(approval.RulesUnreadable);
    }

    /// <summary>Valid JSON without a <c>rules</c> array (a placeholder, an array at the root) declares nothing to enforce:
    /// read, not unread — <c>/permissions</c> names it, no card is forced.</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("[ \"deny run_command make .*\" ]")]
    public async Task AValidOverlayWithoutRules_IsReadAsNone_NotAsUnread(string text)
    {
        WriteOverlay(text, 1);
        var approval = Service();
        Assert.True(await Run(approval, "echo hi"));
        Assert.Equal(0, approval.Prompts);
        Assert.False(approval.RulesUnreadable);
        Assert.Empty(_said);
    }

    [Fact]
    public async Task AnOverlayBrokenAfterARead_KeepsTheRulesLastRead()
    {
        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ] }", 1);
        var approval = Service();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Run(approval, "make clean"));

        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\"", 2);       // an edit that broke it
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Run(approval, "make clean"));
        Assert.True(await Run(approval, "echo hi"));
        Assert.Equal(0, approval.Prompts);
        Assert.False(approval.RulesUnreadable);
        Assert.Empty(_said);
    }

    /// <summary>What another program does: the file rewritten, then open and shut to anyone else (Windows' sharing; a
    /// POSIX lock is advisory, so the malformed arms above carry the rule there).</summary>
    [Fact]
    public async Task AnOverlayHeldByAnotherProgram_KeepsTheRulesLastRead_AndIsReadAgainOnceReleased()
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ] }", 1);
        var approval = Service();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Run(approval, "make clean"));

        WriteOverlay("{ \"rules\": [ \"deny run_command npm .*\" ] }", 2);
        using (new FileStream(Overlay, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<PermissionDeniedException>(() => Run(approval, "make clean"));   // kept
            Assert.True(await Run(approval, "npm ls"));                                // not read yet
            Assert.False(approval.RulesUnreadable);
        }
        // The failed read was not cached under the file's date: released, the new rules are read.
        await Assert.ThrowsAsync<PermissionDeniedException>(() => Run(approval, "npm ls"));
        Assert.True(await Run(approval, "make clean"));
    }

    /// <summary>The <c>/tdd</c> guard wraps the approval service: the rules travel through it, or the package check sends a
    /// name for a call they refuse (the interface's default says "nothing refuses").</summary>
    [Fact]
    public void TheTestFileGuard_AnswersWithTheRulesItWraps()
    {
        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ] }", 1);
        var guard = new Services.Commands.TestFileWriteGuard(Service());
        Assert.True(guard.Refuses("run_command", "make clean"));
        Assert.False(guard.Refuses("run_command", "echo hi"));

        WriteOverlay("{ \"rules\": [", 2);   // never read by this service before: unread
        var fresh = new Services.Commands.TestFileWriteGuard(Service());
        Assert.True(fresh.RulesUnreadable);
        Assert.True(fresh.Refuses("run_command", "echo hi"));
    }

    [Fact]
    public async Task AnOverlayHeldBeforeAnyRead_AsksForEveryCall()
    {
        if (!OperatingSystem.IsWindows()) return;
        WriteOverlay("{ \"rules\": [ \"deny run_command make .*\" ] }", 1);
        var approval = Service();
        using (new FileStream(Overlay, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(await Run(approval, "echo hi"));
            Assert.True(await Run(approval, "echo again"));          // read again (a failed read is not cached)...
            Assert.Equal(2, approval.Prompts);
            Assert.True(approval.RulesUnreadable);
            Assert.Single(_said);                                     // ...and said once, not at every read
        }
    }
}
