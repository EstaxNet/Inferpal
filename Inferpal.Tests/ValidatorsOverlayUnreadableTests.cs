using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A repository's <c>.inferpal/validators.json</c> that exists and cannot be read is not an absent one: read as "no
/// validators", the built-in check ran in place of the repository's and its "build OK" read as verified. The overlay last
/// read stays; never read, the note says the repository's checks did not run, which the verdict reads as unproven.
/// </summary>
public sealed class ValidatorsOverlayUnreadableTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-validators").FullName;
    private string Overlay => Path.Combine(_root, ".inferpal", "validators.json");
    private string Written => Path.Combine(_root, "notes.txt");

    public ValidatorsOverlayUnreadableTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".inferpal"));
        File.WriteAllText(Path.Combine(_root, "app.marker"), "");
        File.WriteAllText(Written, "text");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    /// <summary>Says no to the repository's command, so nothing runs: the count says whether it was resolved.</summary>
    private sealed class Approval : IApprovalService
    {
        public int RepositoryCommands;
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct, string? subject = null,
                                               DiffInfo? diff = null, bool forcePrompt = false)
        {
            if (toolName == "smart_fix_validator" && details.Contains("check.js", StringComparison.Ordinal)) RepositoryCommands++;
            return Task.FromResult(false);
        }
    }

    private (SmartFixValidator SmartFix, Approval Approval) Validator()
    {
        var approval = new Approval();
        // The built-in check asks the person once per workspace: refused here, so no build runs.
        return (new SmartFixValidator(new InferpalConfig { SmartFixEnabled = true }, () => _root, approval,
                                      new WorkspaceCodeConsent(approval, () => _root)), approval);
    }

    /// <summary>A C# file the built-in .NET check covers (a project beside it).</summary>
    private string CSharp()
    {
        File.WriteAllText(Path.Combine(_root, "App.csproj"), "<Project />");
        var path = Path.Combine(_root, "App.cs");
        File.WriteAllText(path, "class App { }");
        return path;
    }

    private const string Good = """{ ".txt": { "marker": "*.marker", "command": "node check.js {project}" } }""";

    private void WriteOverlay(string text, int secondsLater)
    {
        File.WriteAllText(Overlay, text);
        File.SetLastWriteTimeUtc(Overlay, DateTime.UtcNow.AddSeconds(secondsLater));
    }

    [Fact]
    public async Task NoOverlay_SaysNothing()
    {
        var (smartFix, approval) = Validator();
        Assert.Null(await smartFix.ValidateAsync(Written, CancellationToken.None));
        Assert.Equal(0, approval.RepositoryCommands);
    }

    [Fact]
    public async Task AnOverlayNeverRead_IsSaidBesideTheBuiltInCheck_AndTheVerdictIsUnproven()
    {
        WriteOverlay("{ \".cs\": { \"marker\": ", 1);   // cut off: not JSON
        var (smartFix, _) = Validator();

        var note = await smartFix.ValidateAsync(CSharp(), CancellationToken.None);

        Assert.NotNull(note);
        Assert.Contains("validators.json", note, StringComparison.Ordinal);
        Assert.True(SmartFixValidator.CouldNotVerify(note));
        Assert.Null(SmartFixValidator.ReadVerdict(note));
    }

    /// <summary>A write no check covers says nothing of it: on every such edit the note would be noise until the file is
    /// fixed (named in /diagnostics).</summary>
    [Fact]
    public async Task AnOverlayNeverRead_IsNotSaidForAWriteNoCheckCovers()
    {
        WriteOverlay("{ \".cs\": { \"marker\": ", 1);
        var (smartFix, _) = Validator();
        Assert.Null(await smartFix.ValidateAsync(Written, CancellationToken.None));
    }

    [Fact]
    public async Task AnOverlayBrokenAfterARead_KeepsTheChecksLastRead()
    {
        WriteOverlay(Good, 1);
        var (smartFix, approval) = Validator();
        await smartFix.ValidateAsync(Written, CancellationToken.None);
        Assert.Equal(1, approval.RepositoryCommands);                 // witness: the repository's check is resolved

        WriteOverlay("{ \".txt\": { \"marker\": ", 2);                // an edit that broke it
        var note = await smartFix.ValidateAsync(Written, CancellationToken.None);
        Assert.Equal(2, approval.RepositoryCommands);                 // still the repository's check
        Assert.False(note is not null && SmartFixValidator.CouldNotVerify(note));
    }

    [Fact]
    public async Task AnOverlayHeldByAnotherProgram_BeforeAnyRead_IsSaid()
    {
        if (!OperatingSystem.IsWindows()) return;   // a POSIX lock is advisory: the malformed arms carry the rule there
        WriteOverlay(Good, 1);
        var (smartFix, approval) = Validator();
        string? note;
        using (new FileStream(Overlay, FileMode.Open, FileAccess.Read, FileShare.None))
            note = await Task.Run(() => smartFix.ValidateAsync(CSharp(), CancellationToken.None));
        Assert.Equal(0, approval.RepositoryCommands);
        Assert.NotNull(note);
        Assert.True(SmartFixValidator.CouldNotVerify(note));
    }
}
