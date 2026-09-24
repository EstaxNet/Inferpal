using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Inferpal.Models;
using Inferpal.Services.CodeActions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test file that cannot be READ was overwritten, not extended.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The rule was written one line above the defect</b>: "<i>Read any existing test file so the
/// model extends it instead of clobbering it</i>". The read was attempted all right — and its
/// failure swallowed, after which the pass carried on with <c>existing = null</c>, which is
/// <b>exactly</b> the state "there is no test file". The plan therefore said "new file", and that
/// is the branch that writes.
/// </para>
/// <para>
/// ⚠ <b>And that branch has no net on the Visual Studio side</b>: a bare
/// <c>File.WriteAllTextAsync</c> (its comment reads "Brand-new file: write it to disk"), with no
/// snapshot and no undoable edit — unlike the "extend" branch, which goes through an editor edit
/// that <c>Ctrl+Z</c> reverses. A test file held by a running build, open elsewhere exclusively, or
/// no longer readable therefore <b>lost every test in it</b>.
/// </para>
/// <para>
/// ⚠ <b>Three outcomes, not two</b>: the planner already told "nothing to test" (<c>NoChange</c>)
/// apart from a failure — what it lacked was "the file exists and I could not read it", the one of
/// the three where carrying on <b>destroys</b> something. Refused, and the cause named.
/// </para>
/// </remarks>
public sealed class TestGenerationClobberTests : IDisposable
{
    private readonly string _dir;
    private FileSystemAccessRule? _deny;
    private string? _lockedFile;

    public TestGenerationClobberTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"gentests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Unlock();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Deny(string file)
    {
        _lockedFile = file;
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(file);
            var acl  = info.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ReadData, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            info.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(file, UnixFileMode.None);
        }
    }

    private void Unlock()
    {
        if (_lockedFile is null) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(_lockedFile);
                var acl  = info.GetAccessControl();
                if (_deny is not null) { acl.RemoveAccessRule(_deny); _deny = null; }
                info.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_lockedFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { }
        _lockedFile = null;
    }

    private static FakeInferenceProvider Model(string answer) =>
        new() { ChatResult = new ChatTurnResult(answer, null, 0, 0) };

    private (string Source, string Test) Fixture(string existingTests)
    {
        var source = Path.Combine(_dir, "Widget.cs");
        File.WriteAllText(source, "public class Widget { public int Add(int a, int b) => a + b; }");

        var test = TestFilePathResolver.Resolve(source);
        if (existingTests.Length > 0) File.WriteAllText(test, existingTests);
        return (source, test);
    }

    // ── The defect ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnExistingTestFileThatCannotBeRead_IsNeverTreatedAsAbsent()
    {
        var (source, test) = Fixture("public class WidgetTests { /* trois ans de tests */ }");
        Deny(test);

        // WITNESS: the lock really holds, and the file really is there.
        Assert.True(File.Exists(test));
        Assert.ThrowsAny<Exception>(() => File.ReadAllText(test));

        var plan = await TestGenerationPlanner.PlanAsync(
            Model("public class WidgetTests { /* tout neuf */ }"),
            "m", source, "public class Widget { }", CancellationToken.None);

        // `Ok && !Extended` is what triggers the bare write on the VS side: the clobbering pair.
        Assert.False(plan.Ok && !plan.Extended,
            "The plan announces a NEW file although one exists: the branch that applies it writes "
            + "with no net and loses its content.");

        // And the cause is named, otherwise the user does not know what to free.
        Assert.True(plan.Unreadable);
        Assert.False(plan.NoChange);
        Assert.Equal(test, plan.TestPath);
    }

    [Fact]
    public async Task AndNothingIsWrittenOverIt()
    {
        // ⚠ What is measured here is the PLAN, because that is what decides: the bare write lives in
        // the VS applier, which is not executable from the suite (hence the named assertion below).
        // The content on disk is checked anyway — a planner writes nothing, and the day one of them
        // does, this must go red.
        var original = "public class WidgetTests { /* trois ans de tests */ }";
        var (source, test) = Fixture(original);
        Deny(test);

        var plan = await TestGenerationPlanner.PlanAsync(
            Model("public class WidgetTests { /* tout neuf */ }"),
            "m", source, "public class Widget { }", CancellationToken.None);

        Assert.False(plan.Ok);
        Unlock();
        Assert.Equal(original, File.ReadAllText(test));
    }

    // ── A cut answer ─────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠ Extending REWRITES the whole test file: the model gets the existing tests and returns them
    /// with the new ones. An answer that stopped at the length limit is the file's first part — applied,
    /// it deletes every test past the cut, through the very branch that looked safe.
    /// </summary>
    [Fact]
    public async Task AnAnswerCutAtTheLengthLimit_NeverReplacesTheExistingTests()
    {
        var (source, _) = Fixture("public class WidgetTests { void A() { } void B() { } void C() { } }");
        var cut = new FakeInferenceProvider
        {
            ChatResult = new ChatTurnResult("public class WidgetTests { void A() { }", null, 0, 0, CutAtLimit: true),
        };

        var plan = await TestGenerationPlanner.PlanAsync(cut, "m", source, "public class Widget { }", CancellationToken.None);

        Assert.True(plan.Extended);                  // witness: the dangerous branch
        Assert.False(plan.Ok);
        Assert.True(plan.Cut);
        Assert.Empty(plan.Content);
    }

    /// <summary>The same three screens must say it: a silent refusal reads as a broken command.</summary>
    [Theory]
    [InlineData("Inferpal.Host", "HostSlashCommands.cs")]
    [InlineData("Inferpal", "Commands", "AddTestsSelectionCommand.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs")]
    public void EveryFrontEndSaysACutAnswerWasNotWritten(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        Assert.Contains("TestsNoChange", code, StringComparison.Ordinal);        // WITNESS: a /test screen
        Assert.Contains("CodeActionReplyCut", code, StringComparison.Ordinal);
    }

    // ── The reference arms: the three other outcomes ─────────────────────────

    [Fact]
    public async Task AReadableTestFile_IsExtended()
    {
        // REFERENCE ARM: without it, a planner that always refused would sail through the test
        // above while proving nothing.
        var (source, _) = Fixture("public class WidgetTests { }");

        var plan = await TestGenerationPlanner.PlanAsync(
            Model("public class WidgetTests { /* more cases */ }"),
            "m", source, "public class Widget { }", CancellationToken.None);

        Assert.True(plan.Ok);
        Assert.True(plan.Extended);
        Assert.False(plan.Unreadable);
    }

    [Fact]
    public async Task NoTestFileAtAll_IsStillACreation()
    {
        // The other arm: "absent" must stay "absent". Refusing here would break /test.
        var (source, _) = Fixture(string.Empty);

        var plan = await TestGenerationPlanner.PlanAsync(
            Model("public class WidgetTests { /* tout neuf */ }"),
            "m", source, "public class Widget { }", CancellationToken.None);

        Assert.True(plan.Ok);
        Assert.False(plan.Extended);
        Assert.False(plan.Unreadable);
    }

    [Fact]
    public async Task TheModelSayingThereIsNothingToAdd_StaysItsOwnOutcome()
    {
        var (source, _) = Fixture("public class WidgetTests { }");

        var plan = await TestGenerationPlanner.PlanAsync(
            Model(CodeActionSentinel.Token), "m", source, "public class Widget { }",
            CancellationToken.None);

        Assert.False(plan.Ok);
        Assert.True(plan.NoChange);
        Assert.False(plan.Unreadable);
    }

    // ── The three screens that must say it ───────────────────────────────────

    /// <summary>
    /// ⚠ Named assertions: those three paths do not run from the suite (Remote UI on one side, the
    /// extension host on the other), and a mute refusal brings the defect back in another shape —
    /// the user re-running <c>/test</c> in a loop on a file they only had to unlock.
    /// </summary>
    [Theory]
    [InlineData("Inferpal.Host", "HostSlashCommands.cs")]
    [InlineData("Inferpal", "Commands", "AddTestsSelectionCommand.cs")]
    [InlineData("Inferpal", "ToolWindow", "InferpalToolWindowData.SlashCommands.cs")]
    public void EveryFrontEndNamesTheFileToFree(params string[] parts)
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(RepoRoot(), Path.Combine(parts)));

        // WITNESS: this really is a screen that reports a /test outcome — without which the absence
        // of the sentence below would prove nothing.
        Assert.Contains("TestsNoChange", code, StringComparison.Ordinal);

        Assert.Contains("TestsFileUnreadable", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
