using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Services.Persistence;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The same class as <see cref="ConfigUnreadableFileTests"/>, one folder further: a store file that
/// cannot be read is replaced by an EMPTY document, and the next save writes that emptiness over
/// the original bytes.
/// </summary>
/// <remarks>
/// <c>AppDataJsonFile</c> stated in so many words that this was deliberate: "absence and corruption
/// are the same answer here […] these files hold convenience state - past benchmark runs, arena
/// votes, saved snippets - never anything the user cannot recreate". True of the first two. False
/// of the third: a snippet is a fragment of code the user <b>chose</b> to keep, and the conversation
/// it came from is usually long gone. A rule true of a subcase, written as if it held for all - and
/// the false subcase is precisely the one carrying the user's own content.
///
/// The repair therefore puts the distinction in a <b>parameter</b>, not in a paragraph.
/// </remarks>
[Collection("SnippetStore")]
public class StoreUnreadableFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"store-{Guid.NewGuid():N}");
    private readonly string _path;

    public StoreUnreadableFileTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "snippets.json");
        SnippetStore._fileOverride = _path;
    }

    public void Dispose()
    {
        SnippetStore._fileOverride = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private async Task<string> WriteThenTearAsync()
    {
        await SnippetStore.SaveAsync("csharp", "var mine = 1; // USER-MARKER", CancellationToken.None);
        var whole = await File.ReadAllTextAsync(_path);
        Assert.Contains("USER-MARKER", whole);   // reference arm: really written
        // No apostrophe in the marker: JsonSerializer escapes it as ', and a literal search
        // would fail on a perfectly correct file.
        var torn = whole[..(whole.Length / 2)];
        await File.WriteAllTextAsync(_path, torn);
        return torn;
    }

    [Fact]
    public async Task SavingAfterAnUnreadableFile_PreservesTheOriginalBytes()
    {
        var torn = await WriteThenTearAsync();

        // The user saves a new snippet: the loaded list is empty, so the write replaces a hundred
        // snippets with one.
        await SnippetStore.SaveAsync("csharp", "new one", CancellationToken.None);

        Assert.DoesNotContain("USER-MARKER", await File.ReadAllTextAsync(_path));

        var rescued = Directory.GetFiles(_dir)
            .Where(f => !string.Equals(f, _path, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToList();

        Assert.Contains(rescued, text => text == torn);
    }

    /// <summary>Ordinary path: a readable file is replaced without leaving a copy, or the folder
    /// would fill up with every snippet added.</summary>
    [Fact]
    public async Task SavingOverAReadableFile_LeavesNoCopy()
    {
        await SnippetStore.SaveAsync("csharp", "one", CancellationToken.None);
        await SnippetStore.SaveAsync("csharp", "two", CancellationToken.None);

        Assert.Equal(2, (await SnippetStore.LoadAllAsync(CancellationToken.None)).Count);
        Assert.Single(Directory.GetFiles(_dir));
    }

    /// <summary>And a genuinely disposable store does not pay that price: arena/bench recompute
    /// themselves, setting their bytes aside would only clutter <c>%AppData%</c>.</summary>
    [Fact]
    public async Task ADisposableStore_DoesNotPreserveAnything()
    {
        var path = Path.Combine(_dir, "disposable.json");
        var file = new AppDataJsonFile<List<string>>("disposable.json", "Test") { PathOverride = path };

        await file.SaveAsync(["a"], CancellationToken.None);
        var whole = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, whole[..(whole.Length / 2)]);

        await file.SaveAsync(["b"], CancellationToken.None);

        Assert.Single(Directory.GetFiles(_dir).Where(f => Path.GetFileName(f).StartsWith("disposable")));
    }
}
