using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Inferpal.Services;
using Inferpal.Services.Presentation;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A file git left in conflict is read for what resolving it takes (pari 178): the operation, each side's last commit on the
/// file, and the common base of every block, which git's markers do not show — the file's own text untouched below. An edit
/// that adds a marker is refused. Real git, real repositories.
/// </summary>
public sealed class MergeConflictTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-conflict-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private const string Base   = "import os\n\nLIMIT = 10\n\n\ndef page(items):\n    return list(items)[:LIMIT]\n";
    private const string Ours   = "import os\n\nLIMIT = 25  # larger pages\n\n\ndef page(items):\n    return list(items)[:LIMIT]\n";
    private const string Theirs = "import os\n\nLIMIT = int(os.environ.get(\"PAGE_LIMIT\", \"10\"))\n\n\ndef page(items):\n    return list(items)[:LIMIT]\n";

    private static bool Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in (string[])["-c", "core.autocrlf=false", .. args]) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return p.ExitCode == 0;
    }

    /// <summary>A repository whose main raised the limit and whose feature made it configurable, stopped on their conflict by
    /// <paramref name="operation"/> ("merge" or "rebase"), the markers in <paramref name="style"/>.</summary>
    private string Conflict(string operation = "merge", string style = "merge", string ours = Ours, string theirs = Theirs,
                            string basis = Base)
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "paging.py");
        void Commit(string text, string message)
        {
            File.WriteAllText(file, text);
            Assert.True(Git(repo, "add", "paging.py"));
            Assert.True(Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", message));
        }
        Assert.True(Git(repo, "init", "-q", "-b", "main"));
        Commit(basis, "Initial version");
        Assert.True(Git(repo, "checkout", "-q", "-b", "feature"));
        Commit(theirs, "Make the page size configurable");
        Assert.True(Git(repo, "checkout", "-q", "main"));
        Commit(ours, "Raise the page size for the dashboard");
        if (operation == "rebase")
        {
            Assert.True(Git(repo, "checkout", "-q", "feature"));
            Assert.False(Git(repo, "-c", $"merge.conflictStyle={style}", "-c", "user.name=T", "-c", "user.email=t@example.com",
                             "rebase", "main"));
        }
        else
            Assert.False(Git(repo, "-c", $"merge.conflictStyle={style}", "-c", "user.name=T", "-c", "user.email=t@example.com",
                             "merge", "feature"));
        Assert.Contains("<<<<<<<", File.ReadAllText(file));   // witness: git stopped on the conflict
        return file;
    }

    private static Task<string> Read(string file) =>
        new ReadFileTool(() => Path.GetDirectoryName(file)).ExecuteAsync(JsonSerializer.SerializeToElement(new { path = file }), default);

    [Fact]
    public async Task AMergeConflict_IsReadWithItsOperation_ItsSides_AndEachBlocksBase()
    {
        var file = Conflict();
        var onDisk = File.ReadAllBytes(file);

        var said = await Read(file);

        Assert.Contains("paging.py is in conflict: a merge", said);
        Assert.Contains("Raise the page size for the dashboard", said);   // ours' commit
        Assert.Contains("Make the page size configurable", said);         // theirs' commit
        Assert.Contains("Block 1, lines 3-7, base:\n    LIMIT = 10\n", said);
        Assert.Contains("<<<<<<< HEAD\nLIMIT = 25", said);                // the file's own text, as on disk
        Assert.Equal(onDisk, File.ReadAllBytes(file));                    // never rewritten
    }

    [Fact]
    public async Task AFileInCrlf_GetsTheSameBase_AtTheLinesReadFileNumbers()
    {
        var said = await Read(Conflict(ours: Ours.Replace("\n", "\r\n"), theirs: Theirs.Replace("\n", "\r\n"),
                                       basis: Base.Replace("\n", "\r\n")));

        Assert.Contains("Block 1, lines 3-7, base:\n    LIMIT = 10\n", said);
    }

    [Fact]
    public async Task AConflictInALargeFile_FitsWhatTheLoopSends_HeaderAndPageTogether()
    {
        // A page is cut to the context cap; the header shares its room, or the loop cut the result's middle — inside a block,
        // on lines already counted as read.
        var tail = string.Concat(Enumerable.Range(0, 400).Select(i => $"def helper_{i}(x):\n    return x + {i}\n"));
        var said = await Read(Conflict(ours: Ours + tail, theirs: Theirs + tail, basis: Base + tail));

        Assert.Contains("is in conflict", said);
        Assert.True(said.Length <= Inferpal.Services.Agent.AgentOrchestrator.MaxToolResultCharsInContext,
                    $"{said.Length} characters for a cap of {Inferpal.Services.Agent.AgentOrchestrator.MaxToolResultCharsInContext}");
        Assert.Contains("call read_file with start_line=", said);   // the page says where to read on
    }

    [Fact]
    public async Task ABlockWithAnEmptySide_GetsItsBase()
    {
        // Git's default style moves the line both sides share ("limit = 20") out of the block: theirs is left empty.
        var said = await Read(Conflict(basis: "limit = 10\n", ours: "limit = 20\nlog(limit)\n", theirs: "limit = 20\n"));

        Assert.Contains("base:\n    limit = 10\n", said);
    }

    [Fact]
    public async Task APathWrittenInAnotherCase_GetsTheHeader_WhereTheFileSystemIgnoresCase()
    {
        if (!OperatingSystem.IsWindows()) return;   // UNDECIDED elsewhere: another case is another file there
        var file = Conflict();

        var said = await Read(Path.Combine(Path.GetDirectoryName(file)!, "PAGING.PY"));

        Assert.Contains("is in conflict", said);
    }

    [Fact]
    public async Task ARebaseConflict_NamesTheCommitBeingReplayed()
    {
        var said = await Read(Conflict(operation: "rebase"));

        Assert.Contains("is in conflict: a rebase", said);
        Assert.Contains("the commit being replayed", said);
        Assert.Contains("LIMIT = 10", said);
    }

    [Fact]
    public async Task MarkersWrittenInTheDiff3Style_AreSaidToHoldTheBase()
    {
        var said = await Read(Conflict(style: "diff3"));

        Assert.Contains("The base of each block is in the file, after its ||||||| line.", said);
    }

    [Fact]
    public async Task LinesBothSidesAdded_HaveNoBase()
    {
        var said = await Read(Conflict(ours: Base + "\n\ndef first(items):\n    return items[0]\n",
                                       theirs: Base + "\n\ndef last(items):\n    return items[-1]\n"));

        Assert.Contains("no base lines — both sides added these lines.", said);
    }

    [Fact]
    public async Task MarkersInAFileGitDoesNotHoldInConflict_AreContent()
    {
        // A merge tool's fixture, committed with its markers.
        var repo = Path.Combine(_root, "fixtures");
        Directory.CreateDirectory(repo);
        var file = Path.Combine(repo, "sample.txt");
        File.WriteAllText(file, "<<<<<<< ours\na\n=======\nb\n>>>>>>> theirs\n");
        Assert.True(Git(repo, "init", "-q", "-b", "main"));
        Assert.True(Git(repo, "add", "sample.txt"));
        Assert.True(Git(repo, "-c", "user.name=T", "-c", "user.email=t@example.com", "commit", "-q", "-m", "Fixture"));

        var said = await Read(file);

        Assert.DoesNotContain("is in conflict", said);
        Assert.Contains("<<<<<<< ours", said);   // witness: the file was read
    }

    [Fact]
    public async Task AnAttachedConflict_ComesWithWhatReadFileSaysOfIt()
    {
        var file = Conflict();

        var attached = await MentionController.FileMentionTextAsync(file, overlay: null, CancellationToken.None);

        Assert.Contains("paging.py is in conflict: a merge", attached);
        Assert.Contains("LIMIT = 10", attached);
    }

    // ── The write guard ────────────────────────────────────────────────────────

    private const string OneBlock = "a\n<<<<<<< HEAD\nb\n=======\nc\n>>>>>>> feature\nd\n";
    private const string TwoBlocks = OneBlock + "<<<<<<< HEAD\ne\n=======\nf\n>>>>>>> feature\n";

    [Fact]
    public async Task AnEditThatAddsAMarker_IsRefused_AndSentAgainInTheRun_IsWritten()
    {
        var refused = await SyntaxGuard.RefusalAsync(Path.Combine(_root, "notes.txt"), "a\nd\n", OneBlock, _root, "run-1", default);

        Assert.Contains(MergeConflict.RefusalMarker, refused);
        Assert.True(SyntaxGuard.OffersPass(refused!));
        Assert.Null(await SyntaxGuard.RefusalAsync(Path.Combine(_root, "notes.txt"), "a\nd\n", OneBlock, _root, "run-1", default));
    }

    [Theory]
    [InlineData("a\nb\n=======\nc\n>>>>>>> feature\nd\n")]          // the opening line taken out, the rest of the block left
    [InlineData("a\n<<<<<<< HEAD\nb\n=======\nc\nd\n")]             // the closing line taken out
    [InlineData("a\nb\n=======\nc\nd\n")]                          // both markers dropped, both sides and the ======= kept
    public async Task AnEditThatLeavesHalfABlock_IsRefused(string proposed) =>
        Assert.Contains(MergeConflict.RefusalMarker,
                        await SyntaxGuard.RefusalAsync(Path.Combine(_root, "notes.txt"), OneBlock, proposed, _root, null, default));

    [Theory]
    [InlineData(TwoBlocks, OneBlock)]                              // one block resolved, one left: block by block
    [InlineData(OneBlock, OneBlock + "e\n")]                      // a fixture's markers kept while editing it
    [InlineData("Title\n=======\n", "Title\n=======\ntext\n")]   // a Markdown heading underline is no marker
    public async Task AnEditThatAddsNoMarker_IsWritten(string original, string proposed) =>
        Assert.Null(await SyntaxGuard.RefusalAsync(Path.Combine(_root, "notes.md"), original, proposed, _root, null, default));
}
