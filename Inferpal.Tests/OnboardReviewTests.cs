using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Inferpal.Host;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The brief <c>/onboard context</c> writes never calls an unreadable folder empty.
/// </summary>
/// <remarks>
/// The brief becomes <c>.inferpal/context.md</c>, the system prompt of every later session. A top-level folder whose
/// listing failed was sampled as an empty list and filed under "nothing to sample (empty, or only build/vendor
/// folders)": every later session was told that a folder full of code holds nothing.
/// </remarks>
public sealed class RepoBriefUnreadableFolderTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"inferpal-brief-{Guid.NewGuid():N}")).FullName;
    private readonly string _locked;
    private FileSystemAccessRule? _deny;

    public RepoBriefUnreadableFolderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Core"));
        File.WriteAllText(Path.Combine(_root, "Core", "A.cs"), "// a");
        _locked = Directory.CreateDirectory(Path.Combine(_root, "Payments")).FullName;
        File.WriteAllText(Path.Combine(_locked, "Ledger.cs"), "// ledger");
        if (OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(_locked);
            var acl  = info.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_locked, UnixFileMode.None);
        }
    }

    public void Dispose()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(_locked);
                var acl  = info.GetAccessControl();
                if (_deny is not null) acl.RemoveAccessRule(_deny);
                info.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static Task<(string, int)> Git(string args, CancellationToken _) => Task.FromResult(("feat: x", 0));

    [Fact]
    public async Task AFolderThatCannotBeRead_IsNamedAsSuch_NotAsEmpty()
    {
        // WITNESS: the lock really refuses a listing (a run as root, or a platform without enforceable ACLs, measures nothing).
        var refused = false;
        try { _ = Directory.EnumerateFiles(_locked).ToList(); } catch (UnauthorizedAccessException) { refused = true; }
        if (!refused) return;

        var brief = await OnboardCommandHandler.BuildRepoBriefAsync(_root, Git, CancellationToken.None);

        Assert.Contains("`Core/` → A.cs", brief, StringComparison.Ordinal);
        Assert.Contains("could not be read", brief, StringComparison.Ordinal);
        Assert.Contains("Payments (access denied)", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing to sample", brief, StringComparison.Ordinal);
        // The brief is committed: the cause is written, never the exception's message and its absolute path.
        Assert.DoesNotContain(_root, brief, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// ⚠ <c>/onboard apply</c> saved the utility model on the host, and the VS Code extension — which pushes its own
/// explicit <c>inferpal.utilityModel</c> into the shared config at every host start — was told nothing: the next restart
/// undid the applied profile in silence. The settings panel already followed its saves; the command now does too.
/// </summary>
public partial class HostServerTests
{
    [Fact]
    public async Task OnboardApply_TellsTheExtensionTheSettingsWereSaved()
    {
        using var h = CreateHarness(cfg => cfg.UtilityModel = "old-utility");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Directory.CreateDirectory(Path.Combine(h.RootDir, ".inferpal"));
        File.WriteAllText(Path.Combine(h.RootDir, ".inferpal", "project.json"),
                          """{ "recommend": { "utilityModel": "team-utility" } }""");

        var result = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/onboard apply" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.Equal("team-utility", h.Server.CurrentSession!.Config.UtilityModel);   // WITNESS: the profile was applied
        Assert.Contains(result.Effects ?? [], e => e.Kind == "configSaved");
    }

    [Fact]
    public async Task OnboardApply_WithNothingToApply_SavesNothing_AndSaysNothingWasSaved()
    {
        // Reference arm: no save, no effect.
        using var h = CreateHarness(cfg => cfg.UtilityModel = "team-utility");
        await h.InitializeAsync().WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
        Directory.CreateDirectory(Path.Combine(h.RootDir, ".inferpal"));
        File.WriteAllText(Path.Combine(h.RootDir, ".inferpal", "project.json"),
                          """{ "recommend": { "utilityModel": "team-utility" } }""");

        var result = await h.Client.InvokeWithParameterObjectAsync<SlashCommandResult>(
            "command/slash", new { text = "/onboard apply" }).WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

        Assert.True(result.Handled);
        Assert.DoesNotContain(result.Effects ?? [], e => e.Kind == "configSaved");
    }

    [Fact]
    public void TheExtension_FollowsTheSavedSettings_WhenACommandSavedThem()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        var effects  = WebviewRebuildTests.Body(provider, "dropHostMarkdown: boolean }> ");
        Assert.Contains("case 'openFile':", effects, StringComparison.Ordinal);   // WITNESS: the switch is read
        var saved = effects[effects.IndexOf("case 'configSaved':", StringComparison.Ordinal)..];
        saved = saved[..saved.IndexOf("break;", StringComparison.Ordinal)];
        Assert.Contains("await followModelRouterSettings(this.getHost(), this.log);", saved, StringComparison.Ordinal);
        Assert.Contains("await this.configSaved();", saved, StringComparison.Ordinal);
    }
}
