using System.IO;
using System.Linq;
using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>@folder</c> hands the model a file <b>listing</b> and a subset of the <b>bodies</b>, and said
/// nothing about the difference.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>MentionController.BuildFolderContext</c> prints every file it found under "Files:", then
/// includes the text of the first <b>30</b> of them within a 60 000-character budget. A model reading
/// 120 names and 30 bodies has no way to tell which is which — it answers "that function is not used
/// in this folder" from a folder it was shown the index of.
/// </para>
/// <para>
/// ⚠ <b>Four cuts, one sentence between them.</b> The body cap, the character budget (a plain
/// <c>break</c>), a file that could not be read (<c>catch { continue; }</c>) and the walk's own
/// ceilings — 200 files, 4 levels deep — were all silent. The function <i>does</i> write
/// "…(truncated)" for a body it had to cut in half, so the rule was known; it was applied to the one
/// cut that is visible in the text and to none of the four that are not.
/// </para>
/// <para>
/// Shared by both front-ends (the VS view-model and <c>HostSlashCommands</c>), so the silence was the
/// same in Visual Studio and VS Code.
/// </para>
/// </remarks>
public sealed class FolderMentionCoverageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"folderctx-{Guid.NewGuid():N}");

    public FolderMentionCoverageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Write(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    private static string Body(string marker) => $"public class C {{ /* {marker} */ }}";

    private string Build() => MentionController.BuildFolderContext(_root, CancellationToken.None);

    [Fact]
    public void Folder_WithMoreFilesThanTheBodyCap_SaysHowManyBodiesItIncluded()
    {
        for (var i = 0; i < 40; i++) Write($"F{i:D2}.cs", Body($"MARKER{i:D2}"));

        var context = Build();

        // Witness: the listing really holds all forty, and the bodies really stop at thirty.
        Assert.Equal(40, Enumerable.Range(0, 40).Count(i => context.Contains($"F{i:D2}.cs", StringComparison.Ordinal)));
        Assert.Contains("MARKER00", context, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER39", context, StringComparison.Ordinal);

        Assert.Contains("30 of 40", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_ThatFitsEntirely_SaysNothingAboutTruncation()
    {
        // NEGATIVE WITNESS: without it, a build that always appended a note would pass every test
        // here while measuring nothing.
        for (var i = 0; i < 3; i++) Write($"F{i}.cs", Body($"MARKER{i}"));

        var context = Build();

        Assert.Contains("MARKER2", context, StringComparison.Ordinal);   // the last body is really there
        Assert.DoesNotContain("not included", context, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_WithAnUnreadableFile_SaysSo_InsteadOfListingItAsIfItWereThere()
    {
        Write("Readable.cs", Body("READABLE"));
        var locked = Write("Locked.cs", Body("LOCKED"));

        // FileShare.None is this repository's own witness for "unreadable": .NET turns it into an
        // flock on Unix, so one gesture covers the four CI legs.
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(locked));   // the lock discriminates

        var context = Build();

        Assert.Contains("READABLE", context, StringComparison.Ordinal);   // witness: the walk ran
        Assert.Contains("Locked.cs", context, StringComparison.Ordinal);  // it IS in the listing
        Assert.Contains("could not be read", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_HittingTheCharacterBudget_SaysHowManyFilesItLeftOut()
    {
        // Four files of 25 000 characters against a 60 000-character budget: two fit whole, the
        // third is cut in half — that one has always said "…(truncated)" — and the FOURTH is
        // dropped entirely by a bare `break`, which said nothing at all.
        var filler = new string('x', 25_000);
        for (var i = 0; i < 4; i++) Write($"Big{i}.cs", $"/* MARKER{i} */ /* {filler} */");

        var context = Build();

        Assert.Contains("MARKER0", context, StringComparison.Ordinal);    // witness: bodies were included
        // REFERENCE ARM: the cut that was already stated must stay stated.
        Assert.Contains("…(truncated)", context, StringComparison.Ordinal);
        Assert.DoesNotContain("MARKER3", context, StringComparison.Ordinal);
        Assert.Contains("budget", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_DeeperThanTheWalkGoes_SaysSo()
    {
        Write("Top.cs", Body("TOP"));
        // The walk stops below four levels; the sixth is beyond it and was simply absent.
        Write(Path.Combine("a", "b", "c", "d", "e", "Deep.cs"), Body("DEEP"));

        var context = Build();

        Assert.Contains("Top.cs", context, StringComparison.Ordinal);     // witness
        Assert.DoesNotContain("DEEP", context, StringComparison.Ordinal); // it really is out
        Assert.Contains("deeper than", context, StringComparison.Ordinal);
    }

    [Fact]
    public void Folder_WithMoreFilesThanTheWalkLists_SaysTheListingIsCapped()
    {
        for (var i = 0; i < 210; i++) Write($"G{i:D3}.cs", Body($"M{i:D3}"));

        var context = Build();

        Assert.Contains("G000.cs", context, StringComparison.Ordinal);    // witness: the walk ran
        Assert.Contains("200 files", context, StringComparison.Ordinal);
    }
}
