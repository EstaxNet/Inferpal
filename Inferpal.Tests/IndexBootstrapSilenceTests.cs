using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Three stacked silences on the ONE path that decides whether the semantic index exists.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>The terminal failure of the indexing pass traced nothing.</b> Its <c>catch</c> wrote a
/// status line and nothing else: neither <c>/diagnostics</c> nor the support bundle carried a trace
/// of it. It is the one failure that stops the <b>whole</b> pass — after it, <c>search_codebase</c>
/// and the automatic context are blind to the entire repository — and it is the one that left
/// nothing to read.
/// </para>
/// <para>
/// ⚠ <b>And the cause that status line displayed named nothing</b>: a failure to load the native
/// SQLite library arrives wrapped in a <c>TypeInitializationException</c>, whose own message is
/// <i>"The type initializer for 'X' threw an exception"</i> — a fixed sentence that says neither
/// which file is missing nor where it was looked for. <c>Diagnostics.RootMessage</c> unwraps the
/// three framework wrappers (<c>TypeInitialization</c>, <c>TargetInvocation</c>, <c>Aggregate</c>)
/// and <b>only</b> those: an ordinary exception carrying an <c>InnerException</c> keeps its own
/// message, because that is the one that makes sense.
/// </para>
/// <para>
/// ⚠ <b>And the two links upstream were mute as well</b> — the bare <c>catch</c> of
/// <c>SqliteBootstrapper</c> and its resolver returning <c>IntPtr.Zero</c> without a word, having
/// just tried two precise paths that it alone knows. Those two are not executable from the suite
/// (it would take a package without the native library): <b>named</b> assertions, like
/// <c>InProcContractTests</c>, with their witness.
/// </para>
/// </remarks>
[Collection("Diagnostics")]
public sealed class IndexBootstrapSilenceTests : IDisposable
{
    private readonly string _root;

    public IndexBootstrapSilenceTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"idxboot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Alpha.cs"),
            "public class Alpha\n{\n    public int One() => 1;\n    public int Two() => 2;\n"
            + "    public int Three() => 3;\n    public int Four() => 4;\n}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// Makes this root's database file impossible to open, by putting a <b>directory</b> in its
    /// place — the closest thing to a SQLite that will not open, without touching a single static
    /// shared by the suite.
    /// </summary>
    /// <remarks>
    /// ⚠ The path is recomputed here rather than read off an instance: opening the database and
    /// <b>then</b> blocking it blocks nothing, since the pooled connection is reused by the pass.
    /// Copying the formula is safe because the test checks it immediately — the <c>ThrowsAny</c>
    /// witness goes red the day it stops naming the same file.
    /// </remarks>
    private string BlockTheDatabase()
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(_root.ToLowerInvariant().TrimEnd('\\', '/'))))[..12]
            .ToLowerInvariant();

        var dbPath = Path.Combine(TestRagStore.Dir, hash + ".db");
        Directory.CreateDirectory(dbPath);
        return dbPath;
    }

    /// <summary>30 s: the runner builds, then runs two series in parallel (trap in CLAUDE.md).</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what, Func<string> state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}. State: {state()}");
    }

    private static ProjectIndexService NewService() =>
        new(new FakeInferenceProvider(),
            new InferpalConfig { RagEnabled = true },
            new LspSemanticProvider());

    // ── The failure that stops the whole pass ────────────────────────────────

    [Fact]
    public async Task APassThatCannotOpenItsDatabase_ReachesTheDiagnosticsChannel()
    {
        // WITNESS: the block really holds — without it the pass would succeed and this test would
        // measure nothing at all.
        var dbPath = BlockTheDatabase();
        Assert.ThrowsAny<Exception>(() => new RagDatabase(_root));

        using var svc = NewService();
        svc.StartIndexing(_root);

        await WaitUntilAsync(() => svc.Status.Contains("error", StringComparison.OrdinalIgnoreCase),
                             "the pass hands back on an error", () => svc.Status);

        // ⚠ The failure is looked up by its EXACT context: nine sites in that file trace under a
        // context starting with "ProjectIndexService", and a prefix would have let the neighbour
        // green this test in place of the path it measures. And the class joins the "Diagnostics"
        // collection because the ring is static: a class running in parallel that calls Clear()
        // between the action and the assertion makes this test red on a product that is fine
        // (measured once, one run in two hundred).
        Assert.Contains(Diagnostics.Snapshot(),
            e => e.Context == "ProjectIndexService.IndexingPass");
        Assert.True(Directory.Exists(dbPath));
    }

    [Fact]
    public async Task APassOnAHealthyTree_SaysNoError()
    {
        // REFERENCE ARM: without it, a pass that always failed would sail through the test above
        // while proving nothing.
        using var svc = NewService();
        svc.StartIndexing(_root);

        await WaitUntilAsync(() => !svc.IsIndexing && svc.Status.Length > 0,
                             "the pass finishes", () => svc.Status);

        Assert.DoesNotContain("error", svc.Status, StringComparison.OrdinalIgnoreCase);
    }

    // ── The cause, unwrapped ─────────────────────────────────────────────────

    [Fact]
    public void TheMessageOfAWrapper_IsTheMessageOfWhatItWraps()
    {
        var real = new FileNotFoundException("e_sqlite3.dll est introuvable");

        Assert.Equal(real.Message,
            Diagnostics.RootMessage(new TypeInitializationException("Sqlite", real)));
        Assert.Equal(real.Message,
            Diagnostics.RootMessage(new System.Reflection.TargetInvocationException(real)));
        Assert.Equal(real.Message,
            Diagnostics.RootMessage(new AggregateException(real)));
    }

    [Fact]
    public void NestedWrappers_AreUnwrappedAllTheWayDown()
    {
        var real = new FileNotFoundException("e_sqlite3.dll est introuvable");
        var wrapped = new System.Reflection.TargetInvocationException(
            new TypeInitializationException("Sqlite", new AggregateException(real)));

        Assert.Equal(real.Message, Diagnostics.RootMessage(wrapped));
    }

    [Fact]
    public void AnOrdinaryExceptionThatHasAnInnerOne_KeepsItsOwnMessage()
    {
        // ⚠ The reference arm that matters: unwrapping ALWAYS to the bottom would replace a message
        // written for the user ("the index folder is not writable") with a plumbing detail. Only the
        // framework's wrappers are unwrapped.
        var outer = new IOException("the index folder is not writable",
                                    new UnauthorizedAccessException("access denied"));

        Assert.Equal("the index folder is not writable",
                     Diagnostics.RootMessage(outer));
    }

    [Fact]
    public void AWrapperWithNothingInside_KeepsItsOwnMessage()
    {
        // The loop must stop, and return something rather than nothing.
        var empty = new AggregateException("rien dedans");

        Assert.Equal(empty.Message, Diagnostics.RootMessage(empty));
    }

    [Fact]
    public void TheStatusLineOfAFailedPass_GoesThroughTheUnwrapper()
    {
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Rag", "ProjectIndexService.cs"));

        // WITNESS: the error status line still exists, in this shape.
        Assert.Contains("RAG: error", code, StringComparison.Ordinal);

        Assert.Contains("Diagnostics.RootMessage(ex)", code, StringComparison.Ordinal);
        Assert.Contains("Diagnostics.Swallow(\"ProjectIndexService", code, StringComparison.Ordinal);
    }

    // ── The two bootstrapper links (not executable here) ─────────────────────

    /// <summary>
    /// ⚠ Named assertions: reproducing those two paths would take a package <b>without</b> the
    /// native library, i.e. a different product from the one under test.
    /// </summary>
    [Fact]
    public void BothFailurePathsOfTheBootstrapper_AreSaid()
    {
        var path = Path.Combine(RepoRoot(), "Inferpal.Core", "Services", "Rag",
                                "SqliteBootstrapper.cs");
        var code = ConventionCoverageTests.CodeOnly(path);

        // WITNESS, both halves: this is indeed the file that registers the resolver, and the one
        // that returns empty-handed when it has found nothing.
        Assert.Contains("NativeLibrary.SetDllImportResolver", code, StringComparison.Ordinal);
        Assert.Contains("return IntPtr.Zero;", code, StringComparison.Ordinal);

        // 1. Loading the provider: no more bare catch.
        Assert.Contains("Diagnostics.Swallow(\"SqliteBootstrapper", code, StringComparison.Ordinal);

        // 2. The resolution that fails: said ONCE (this resolver is called on every P/Invoke), and
        //    it names the paths it tried — nobody else knows them.
        var at = code.IndexOf("Diagnostics.RecordOnce(\"SqliteBootstrapper", StringComparison.Ordinal);
        Assert.True(at >= 0, "The resolver returns IntPtr.Zero without saying what it looked for.");
        var note = code[at..code.IndexOf(';', at)];
        Assert.Contains("ridPath", note, StringComparison.Ordinal);
        Assert.Contains("flatPath", note, StringComparison.Ordinal);
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
