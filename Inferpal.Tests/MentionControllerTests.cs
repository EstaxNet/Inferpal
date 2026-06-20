using System;
using System.IO;
using System.Linq;
using System.Threading;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the pure @mention logic extracted from the tool-window VM: prompt-state parsing,
// category matching, prompt text transforms, and the @file/@folder filesystem searches.
// The popup UI, debounce, and attachments stay in the VM (not tested here).
public class MentionControllerTests
{
    // ── Prompt parsing ─────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ClassifiesTypingCommittedAndNone()
    {
        var bare = Assert.IsType<MentionTypingCategory>(MentionController.Parse("look at @"));
        Assert.Equal("", bare.Partial);

        var partial = Assert.IsType<MentionTypingCategory>(MentionController.Parse("look at @fi"));
        Assert.Equal("fi", partial.Partial);

        var committed = Assert.IsType<MentionCommittedQuery>(MentionController.Parse("see @file Program"));
        Assert.Equal("file", committed.Category);
        Assert.Equal("Program", committed.Query);

        // Category matching is case-insensitive and normalised to lower-case.
        var upper = Assert.IsType<MentionCommittedQuery>(MentionController.Parse("@CODE auth logic"));
        Assert.Equal("code", upper.Category);
        Assert.Equal("auth logic", upper.Query);

        // Committed queries may contain spaces ("@code auth logic") — they run to the end
        // of the prompt and only a newline or another '@' breaks them.
        var spaced = Assert.IsType<MentionCommittedQuery>(MentionController.Parse("@file Foo bar"));
        Assert.Equal("Foo bar", spaced.Query);

        Assert.IsType<MentionNone>(MentionController.Parse("no mention here"));
        Assert.IsType<MentionNone>(MentionController.Parse("@file Foo\nnext line"));
    }

    [Fact]
    public void Parse_InstantTokensStayTypingUntilSelected()
    {
        // "@tree" is not a query-based category: even fully typed it remains a category choice.
        var typing = Assert.IsType<MentionTypingCategory>(MentionController.Parse("@tree"));
        Assert.Equal("tree", typing.Partial);
    }

    // ── Category matching ──────────────────────────────────────────────────────

    [Fact]
    public void MatchCategories_PrefixOnTokenWithoutAt()
    {
        Assert.Equal(MentionController.Categories.Length, MentionController.MatchCategories("").Count);

        var f = MentionController.MatchCategories("f");
        Assert.Equal(["@file", "@folder"], f.Select(c => c.Token));

        var clip = Assert.Single(MentionController.MatchCategories("clip"));
        Assert.Equal(MentionKind.Clipboard, clip.Kind);
        Assert.False(clip.QueryBased);

        Assert.Empty(MentionController.MatchCategories("zzz"));
    }

    // ── Prompt transforms ──────────────────────────────────────────────────────

    [Fact]
    public void CommitCategory_ReplacesPartialTokenWithCommittedToken()
    {
        Assert.Equal("explain @file ", MentionController.CommitCategory("explain @fi", "@file"));
        Assert.Equal("@folder ", MentionController.CommitCategory("@", "@folder"));
    }

    [Fact]
    public void StripMentionToken_HandlesBareAndCommittedForms()
    {
        Assert.Equal("explain", MentionController.StripMentionToken("explain @fi"));
        Assert.Equal("explain", MentionController.StripMentionToken("explain @file Program.cs"));
        Assert.Equal("explain", MentionController.StripMentionToken("explain @code auth logic"));
        Assert.Equal("untouched text", MentionController.StripMentionToken("untouched text"));
    }

    // ── Filesystem searches ────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "obuddy-mention-" + Guid.NewGuid().ToString("N"))).FullName;

        public string AddFile(string relPath, string content = "x")
        {
            var full = System.IO.Path.Combine(Path, relPath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindFiles_FiltersScoresAndSkipsBuildFolders()
    {
        using var tmp = new TempDir();
        var exact    = tmp.AddFile("svc.cs");                  // name == query + ext → score 3
        var prefix   = tmp.AddFile(@"src\SvcHost.cs");         // prefix → score 2
        var contains = tmp.AddFile(@"src\MySvc.cs");           // contains → score 1
        tmp.AddFile(@"bin\svc.cs");                            // bin → skipped
        tmp.AddFile(@"src\svc.txt");                           // extension not indexable
        tmp.AddFile(@"src\Other.cs");                          // name does not contain query

        var found = MentionController.FindFiles(tmp.Path, "svc", CancellationToken.None);

        Assert.Equal([exact, prefix, contains], found);        // ranked by score
    }

    [Fact]
    public void FindFolders_EmptyQueryListsAll_AndSkipsBuildFolders()
    {
        using var tmp = new TempDir();
        tmp.AddFile(@"Services\a.cs");
        tmp.AddFile(@"Models\b.cs");
        tmp.AddFile(@"obj\c.cs");
        tmp.AddFile(@"node_modules\d.js");

        var all = MentionController.FindFolders(tmp.Path, "", CancellationToken.None);
        Assert.Equal(["Models", "Services"], all.Select(Path.GetFileName).OrderBy(n => n));

        var filtered = MentionController.FindFolders(tmp.Path, "serv", CancellationToken.None);
        Assert.Equal("Services", Path.GetFileName(Assert.Single(filtered)));
    }

    [Fact]
    public void BuildFolderContext_ListsFilesAndIncludesBodies()
    {
        using var tmp = new TempDir();
        tmp.AddFile("readme.md", "hello body");
        tmp.AddFile(@"sub\code.cs", "class C {}");
        tmp.AddFile("ignored.bin", "binary");                  // extension not indexable

        var context = MentionController.BuildFolderContext(tmp.Path, CancellationToken.None);

        Assert.StartsWith("Folder: " + tmp.Path, context);
        Assert.Contains("readme.md", context);
        Assert.Contains(@"sub\code.cs", context);
        Assert.Contains("hello body", context);
        Assert.Contains("class C {}", context);
        Assert.DoesNotContain("ignored.bin", context);
    }

    [Fact]
    public void RelLabel_RelativeUnderRoot_ParentDirOtherwise()
    {
        using var tmp = new TempDir();
        var inside = tmp.AddFile(@"src\A.cs");
        Assert.Equal(@"src\A.cs", MentionController.RelLabel(inside, tmp.Path));

        var outsideRoot = Path.Combine(tmp.Path, "src");
        var elsewhere   = tmp.AddFile("B.cs");                 // sibling of src → not under it
        Assert.Equal(tmp.Path, MentionController.RelLabel(elsewhere, outsideRoot));
    }
}
