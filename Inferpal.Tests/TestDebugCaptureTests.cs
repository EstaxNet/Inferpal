using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Debugging;
using Xunit;

namespace Inferpal.Tests;

// §25 tranche B — the shared (editor-free) half of the capture: test-assembly discovery, the
// on-demand repro runner scaffold, and the base orchestration both front-ends inherit.
public class TestDebugCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-capture-" + Guid.NewGuid().ToString("N"));
    private readonly Func<string> _originalBaseDir = TestReproScaffold.BaseDir;

    public void Dispose()
    {
        TestReproScaffold.BaseDir = _originalBaseDir;
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // The relative directory is given with '/' and split here: a verbatim Windows path is a
    // single file name on POSIX, so the fixture used to create ONE directory whose whole name
    // was the path, and the locator - which looks for a 'bin' segment - found nothing.
    // Two of these cases were red on the ubuntu/macos CI legs, and a third was vacuously green.
    private string MakeDll(string relativeDir, string name)
    {
        var dir = Path.Combine([_root, .. relativeDir.Split('/')]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "not really a dll");
        return path;
    }

    // ── TestAssemblyLocator ─────────────────────────────────────────────────────────

    [Fact]
    public void Locator_PicksTheLongestAssemblyNamePrefixOfTheFqn()
    {
        MakeDll("src/bin/Debug/net8.0", "My.dll");
        var tests = MakeDll("tests/bin/Debug/net8.0", "My.Tests.dll");

        Assert.Equal(tests, TestAssemblyLocator.Locate(_root, "My.Tests.CalculatorTests.Adds"));
    }

    [Fact]
    public void Locator_IgnoresObjAndRefOutputs_AndAnswersNullWhenNothingMatches()
    {
        MakeDll("tests/obj/Debug/net8.0", "My.Tests.dll");                 // obj → never
        MakeDll("tests/bin/Debug/net8.0/ref", "My.Tests.dll");             // ref assembly → never

        Assert.Null(TestAssemblyLocator.Locate(_root, "My.Tests.CalculatorTests.Adds"));
        Assert.Null(TestAssemblyLocator.Locate(_root, "Other.Namespace.Test"));
        Assert.Null(TestAssemblyLocator.Locate(Path.Combine(_root, "absent"), "My.Tests.X.Y"));
    }

    // ── TestReproScaffold ───────────────────────────────────────────────────────────

    [Fact]
    public void Scaffold_SourceCarriesTheTwoMeasuredBehaviours()
    {
        // Both were probed before being written: DoNotWrapExceptions keeps the original throw
        // frames for the unhandled break (VS recipe), the wait flag is what VS attaches to.
        Assert.Contains("DoNotWrapExceptions", TestReproScaffold.ProgramFile);
        Assert.Contains("INFERPAL_WAIT_DEBUGGER", TestReproScaffold.ProgramFile);
        Assert.Contains("AssemblyDependencyResolver", TestReproScaffold.ProgramFile);
        Assert.Contains("<RollForward>LatestMajor</RollForward>", TestReproScaffold.ProjectFile);
        // The hash pins the cache folder to the source: same source, same folder.
        Assert.Equal(TestReproScaffold.SourceHash(), TestReproScaffold.SourceHash());
    }

    [Fact]
    public async Task Scaffold_BuildsOnce_ThenServesTheCachedRunner()
    {
        TestReproScaffold.BaseDir = () => Path.Combine(_root, "scaffold");

        var first = await TestReproScaffold.EnsureBuiltAsync(CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(first), "the runner should build on a machine with the SDK");
        Assert.True(File.Exists(first));
        var stamp = File.GetLastWriteTimeUtc(first!);

        var second = await TestReproScaffold.EnsureBuiltAsync(CancellationToken.None);
        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second!));   // cached, not rebuilt
    }

    [Fact]
    public void Scaffold_PurgesStaleBuilds_KeepingTheCurrentHash()
    {
        // §27.3 - the per-source-hash cache grows without bound: every shipped version left its
        // build (~1 MB) behind forever. The purge sweeps foreign hashes and keeps the current one.
        var baseDir = Path.Combine(_root, "purge");
        var stale   = Path.Combine(baseDir, "deadbeefdeadbeef");
        var keep    = Path.Combine(baseDir, TestReproScaffold.SourceHash());
        Directory.CreateDirectory(Path.Combine(stale, "bin"));
        File.WriteAllText(Path.Combine(stale, "bin", "old.dll"), "x");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "Program.cs"), "y");

        TestReproScaffold.PurgeStaleBuilds(baseDir, TestReproScaffold.SourceHash());

        Assert.False(Directory.Exists(stale), "the stale hash should have been purged");
        Assert.True(File.Exists(Path.Combine(keep, "Program.cs")), "le hash courant doit survivre");
    }

    [Fact]
    public void Scaffold_Purge_ToleratesAHeldBuild()
    {
        // An older host may hold its folder (dll loaded, build in progress): the purge must pass
        // without throwing - the leftover will be swept on the next boot.
        var baseDir = Path.Combine(_root, "purge-held");
        var held    = Path.Combine(baseDir, "cafebabecafebabe");
        Directory.CreateDirectory(held);
        using var hold = new FileStream(Path.Combine(held, "busy.dll"),
            FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        TestReproScaffold.PurgeStaleBuilds(baseDir, TestReproScaffold.SourceHash()); // does not throw
    }

    [Fact]
    public async Task Scaffold_BuildLock_IsExclusiveAcrossHolders()
    {
        // §27.3 - VS and the VS Code host share the scaffold folder: two simultaneous dotnet
        // builds in the same directory corrupt each other. The file lock serializes them.
        var dir = Path.Combine(_root, "lock");
        Directory.CreateDirectory(dir);

        await using var first = await TestReproScaffold.AcquireBuildLockAsync(
            dir, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(first);

        // Held -> a second contender times out on its budget instead of building in parallel.
        var second = await TestReproScaffold.AcquireBuildLockAsync(
            dir, TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Null(second);

        await first!.DisposeAsync();

        // Released -> the next contender goes through.
        await using var third = await TestReproScaffold.AcquireBuildLockAsync(
            dir, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.NotNull(third);
    }

    // ── TestDebugCaptureBase ────────────────────────────────────────────────────────

    private sealed class FakeCapture : TestDebugCaptureBase
    {
        public override bool IsAvailable => true;
        public (string Runner, string TestDll, string Fqn, string Cwd, string Root)? Launched;
        protected override Task<DebugStopState?> LaunchAndCaptureAsync(
            string runnerDll, string testDll, string fqn, string cwd, string root, CancellationToken ct)
        {
            Launched = (runnerDll, testDll, fqn, cwd, root);
            return Task.FromResult<DebugStopState?>(new DebugStopState("exception", 1, [], []));
        }
    }

    [Fact]
    public async Task Base_LocatesTheAssembly_EnsuresTheRunner_AndDelegatesTheLaunch()
    {
        var testDll = MakeDll("tests/bin/Debug/net8.0", "My.Tests.dll");
        // A pre-existing file at the runner path short-circuits the build: the orchestration is
        // what this test measures, not the SDK.
        TestReproScaffold.BaseDir = () => Path.Combine(_root, "scaffold");
        var runner = TestReproScaffold.RunnerDllPath();
        Directory.CreateDirectory(Path.GetDirectoryName(runner)!);
        File.WriteAllText(runner, "cached runner");

        var capture = new FakeCapture();
        var state = await capture.CaptureAsync("My.Tests.CalculatorTests.Adds", _root, CancellationToken.None);

        Assert.NotNull(state);
        Assert.NotNull(capture.Launched);
        Assert.Equal(runner, capture.Launched!.Value.Runner);
        Assert.Equal(testDll, capture.Launched!.Value.TestDll);
        Assert.Equal("My.Tests.CalculatorTests.Adds", capture.Launched!.Value.Fqn);
        Assert.Equal(Path.GetDirectoryName(testDll), capture.Launched!.Value.Cwd);
        Assert.Equal(_root, capture.Launched!.Value.Root);
    }

    [Fact]
    public async Task Base_AnswersNull_WhenNoAssemblyMatches_WithoutLaunching()
    {
        TestReproScaffold.BaseDir = () => Path.Combine(_root, "scaffold");
        var capture = new FakeCapture();

        var state = await capture.CaptureAsync("Nowhere.To.Be.Found", _root, CancellationToken.None);

        Assert.Null(state);
        Assert.Null(capture.Launched);
    }
}
