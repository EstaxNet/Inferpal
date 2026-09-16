using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file the scan <b>took</b> but could not <b>read</b> is not a file it scanned — and saying so
/// is the difference between "nothing depends on this" and "I could not look at everything".
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Measured on this very shape</b>: a workspace of two files, well under the 500-file cap, with
/// the only dependant unreadable. <c>analyze_impact</c> answered
/// <c>Layer 1 · Direct dependants (0) — (none found — file may be unused or only referenced
/// dynamically)</c>, then <c>Risk: LOW ↳ No dependants detected — safe to refactor freely</c>, with
/// <b>no warning of any kind</b>: <see cref="ScanCoverage.Take"/> reports how many files were
/// <i>taken</i>, the per-file <c>catch</c> swallowed the read failure, and
/// <c>ScanCoverage(2, 2)</c> is not partial.
/// </para>
/// <para>
/// ⭐ The tell: <c>rename_symbol</c> — the sibling in the same folder, the one that <i>writes</i> —
/// already counted its unreadable files. The rule was written once and honoured by one of four
/// readers, which is this repository's most productive defect generator.
/// </para>
/// <para>
/// ⚠ And it is the same sentence <c>AnalyzeImpactTool</c>'s own comment records as its former
/// defect ("answered 0 dependants · safe to refactor freely"), for a different cause. A fix that
/// closes the instance you saw leaves alive the class you did not look for.
/// </para>
/// </remarks>
public sealed class UnreadableFileCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"unread-{Guid.NewGuid():N}");
    private readonly string _locked;
    private FileSystemAccessRule? _deny;

    public UnreadableFileCoverageTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "host"));
        File.WriteAllText(Path.Combine(_root, "src", "Target.cs"), """
            namespace App;
            public class Target
            {
                public void HandleTask() { }
            }
            """);
        // The only dependant, and the only caller — made unreadable below.
        _locked = Path.Combine(_root, "host", "Caller.cs");
        File.WriteAllText(_locked, """
            namespace App.Host;
            using App;
            public class Caller
            {
                public void Route() { new Target().HandleTask(); }
            }
            """);

        // Cross-platform, like InaccessibleFolderTests: a deny ACE on Windows, mode 000 elsewhere.
        if (OperatingSystem.IsWindows())
        {
            var fi  = new FileInfo(_locked);
            var acl = fi.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ReadData, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            fi.SetAccessControl(acl);
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
                var fi  = new FileInfo(_locked);
                var acl = fi.GetAccessControl();
                if (_deny is not null) acl.RemoveAccessRule(_deny);
                fi.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Both halves of the setup, asserted: the file IS enumerated (so it counts as taken) and is
    /// NOT readable (so the scan really loses it). Without both, nothing below measures anything —
    /// and the second half is exactly what a platform without file ACLs would silently drop.
    /// </summary>
    private void AssertTheFixtureDiscriminates()
    {
        var enumerated = Inferpal.Services.WorkspaceScan.EnumerateFiles(_root, "*.cs").ToList();
        Assert.Contains(enumerated, f => Path.GetFileName(f) == "Caller.cs");
        Assert.Contains(enumerated, f => Path.GetFileName(f) == "Target.cs");
        Assert.ThrowsAny<Exception>(() => File.ReadAllText(_locked));
    }

    private string TargetPath => Path.Combine(_root, "src", "Target.cs");

    [Fact]
    public async Task AnalyzeImpact_WithAnUnreadableDependant_SaysItCouldNotReadIt()
    {
        AssertTheFixtureDiscriminates();

        var tool = new AnalyzeImpactTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = TargetPath }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        // Positive assertion, on the localized sentence rather than on English: the text is
        // translated into ten languages, and asserting the absence of "safe to refactor freely"
        // would also pass on a build where the whole verdict disappeared.
        Assert.Contains(Inferpal.Localization.Strings.ScanUnreadable(1), report);
        // Witness that the report is the real one and the scan did run.
        Assert.Contains("Target", report);
    }

    [Fact]
    public async Task TraceDependency_WithAnUnreadableCaller_SaysItCouldNotReadIt()
    {
        AssertTheFixtureDiscriminates();

        var tool = new TraceDependencyTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(
            new { path = TargetPath, direction = "callers" }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.Contains("HandleTask", report);   // witness: the target really was analysed
        Assert.Contains(Inferpal.Localization.Strings.ScanUnreadable(1), report);
    }

    /// <summary>
    /// NEGATIVE WITNESS. Without it, a fix that warns unconditionally — or that appends the sentence
    /// to every report — passes both tests above while telling the reader nothing.
    /// </summary>
    [Fact]
    public async Task AnalyzeImpact_WhenEverythingWasReadable_SaysNothingAboutUnreadableFiles()
    {
        // The inverse of the fixture: the same workspace with the lock lifted.
        if (OperatingSystem.IsWindows())
        {
            var fi  = new FileInfo(_locked);
            var acl = fi.GetAccessControl();
            if (_deny is not null) { acl.RemoveAccessRule(_deny); _deny = null; }
            fi.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_locked, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.ReadAllText(_locked);   // witness: the lock really is lifted

        var tool = new AnalyzeImpactTool(() => _root);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { path = TargetPath }));

        var report = await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        Assert.DoesNotContain(Inferpal.Localization.Strings.ScanUnreadable(1), report);
        // And the other half of the witness: the dependant is now SEEN, so the report is not
        // simply empty for some other reason.
        Assert.Contains("Caller", report);
    }
}
