using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Inferpal.Config;
using Inferpal.Services.Lsp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A folder the indexing pass could not <b>list</b> is named by <c>/index</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This is the most consequential place in the product for that silence, because the index is
/// PERSISTED.</b> A one-off tool report is wrong for one answer; an index built without a folder
/// makes <c>search_codebase</c> and the per-turn auto-context blind to it for as long as the
/// database lives, across restarts — while <c>/index</c>, the very screen one opens to check the
/// index, showed a chunk count and a root and nothing else.
/// </para>
/// <para>
/// ⚠ No arithmetic here could have revealed it: <c>EnumerationOptions.IgnoreInaccessible</c> skips
/// the folder without throwing, so the files are ABSENT from every count rather than subtracted
/// from one — and the pass's own honest path (<c>EnumerateSourceFiles</c> returning <c>null</c> so a
/// partial list never replaces the index) never fires either, because nothing threw.
/// </para>
/// </remarks>
public sealed class IndexSkippedFolderTests : IDisposable
{
    private readonly string _root;
    private readonly string _lockedDir;
    private readonly List<ProjectIndexService> _services = [];
    private FileSystemAccessRule? _deny;

    /// <summary>
    /// ⚠ Recorded at CONSTRUCTION time, before the lock, and never asked again afterwards.
    /// </summary>
    /// <remarks>
    /// A <c>chmod 000</c> on a directory also removes the right to TRAVERSE it, so on POSIX
    /// <c>File.Exists</c> on a file inside it returns <c>false</c> — while on Windows the deny only
    /// covers <c>ListDirectory</c> and the answer stays <c>true</c>. Asking afterwards therefore
    /// failed this witness on both POSIX legs of CI, against a product that behaved correctly:
    /// <b>a witness has to be checkable on every platform the test runs on</b>, otherwise it
    /// measures the platform instead of the product.
    /// </remarks>
    private readonly bool _lockedFileWritten;

    public IndexSkippedFolderTests()
    {
        TestRagStore.Redirect();
        _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"idxskip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "Alpha.cs"), SampleClass("Alpha"));

        _lockedDir = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(_lockedDir);
        File.WriteAllText(Path.Combine(_lockedDir, "Beta.cs"), SampleClass("Beta"));
        _lockedFileWritten = File.Exists(Path.Combine(_lockedDir, "Beta.cs"));

        if (OperatingSystem.IsWindows())
        {
            var dir = new DirectoryInfo(_lockedDir);
            var acl = dir.GetAccessControl();
            _deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.ListDirectory, AccessControlType.Deny);
            acl.AddAccessRule(_deny);
            dir.SetAccessControl(acl);
        }
        else
        {
            File.SetUnixFileMode(_lockedDir, UnixFileMode.None);
        }
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        Unlock();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Unlock()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var dir = new DirectoryInfo(_lockedDir);
                var acl = dir.GetAccessControl();
                if (_deny is not null) { acl.RemoveAccessRule(_deny); _deny = null; }
                dir.SetAccessControl(acl);
            }
            else
            {
                File.SetUnixFileMode(_lockedDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch { }
    }

    private static string SampleClass(string name) => string.Join('\n',
        $"public class {name}",
        "{",
        "    public int One()   => 1;",
        "    public int Two()   => 2;",
        "    public int Three() => 3;",
        "    public int Four()  => 4;",
        "    public int Five()  => 5;",
        "    public int Six()   => 6;",
        "}");

    private ProjectIndexService NewService()
    {
        var svc = new ProjectIndexService(new FakeInferenceProvider(),
                                          new InferpalConfig { RagEnabled = true },
                                          new LspSemanticProvider());
        _services.Add(svc);
        return svc;
    }

    /// <summary>
    /// ⚠ 30 s, not a couple of seconds: the runner compiles and then plays two test series in
    /// parallel. This test WAITS; it does not measure an overrun.
    /// </summary>
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

    [Fact]
    public async Task TheIndexingPass_RecordsTheFolderItCouldNotList()
    {
        // Witness, both halves: the lock holds, and the locked file really is C# the indexer
        // would have indexed without it.
        Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.EnumerateFiles(_lockedDir).ToList());
        Assert.True(_lockedFileWritten);

        var svc = NewService();
        svc.StartIndexing(_root);

        await WaitUntilAsync(() => svc.SkippedFolder is not null,
                             "la passe enregistre le dossier illisible", () => svc.Status);

        Assert.Equal("pgdata", svc.SkippedFolder);
    }

    [Fact]
    public async Task TheIndexingPass_OnAFullyListableTree_RecordsNothing()
    {
        // NEGATIVE WITNESS: without it, a pass that always recorded a folder would pass the test
        // above while measuring nothing.
        Unlock();
        Assert.NotEmpty(Directory.EnumerateFiles(_lockedDir).ToList());   // the lock really is lifted

        var svc = NewService();
        svc.StartIndexing(_root);

        // Wait until the pass has actually run — otherwise `SkippedFolder` would be `null`
        // because nothing happened, which is the false green of this shape of test.
        await WaitUntilAsync(() => svc.ChunkCount > 0,
                             "la passe indexe les deux fichiers", () => svc.Status);

        Assert.Null(svc.SkippedFolder);
    }

    [Fact]
    public async Task SlashIndex_NamesTheSkippedFolder()
    {
        var svc = NewService();
        svc.StartIndexing(_root);
        await WaitUntilAsync(() => svc.SkippedFolder is not null && svc.ChunkCount > 0,
                             "la passe a tourné et enregistré le dossier", () => svc.Status);

        var report = Inferpal.Services.Commands.IndexCommandHandler.Handle(
            svc, new InferpalConfig { RagEnabled = true }, ["/index"], _root);

        // Positive assertion on the localized sentence, reused from the analysis tools: one
        // cause, one sentence. Asserting the absence of something else would also pass on an
        // empty report.
        Assert.Contains(Inferpal.Localization.Strings.ScanFolderSkipped("pgdata"), report);
        // Witness: this really is the full report, not the "not started yet" path.
        Assert.Contains(svc.ChunkCount.ToString("N0"), report);
    }
}
