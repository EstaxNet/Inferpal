using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Serialized to avoid race conditions on the static _fileOverride field.
[CollectionDefinition("SnippetStore", DisableParallelization = true)]
public class SnippetStoreCollection { }

[Collection("SnippetStore")]
public class SnippetStoreTests : IDisposable
{
    private readonly string _tempFile;

    public SnippetStoreTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"snippets_test_{Guid.NewGuid():N}.json");
        SnippetStore._fileOverride = _tempFile;
    }

    public void Dispose()
    {
        SnippetStore._fileOverride = null;
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public async Task SaveAsync_FirstSave_CreatesFileWithOneSnippet()
    {
        await SnippetStore.SaveAsync("csharp", "var x = 1;", CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Single(snippets);
        Assert.Equal("csharp", snippets[0].Language);
        Assert.Equal("var x = 1;", snippets[0].Code);
    }

    [Fact]
    public async Task SaveAsync_MultipleSaves_AccumulatesInOrder()
    {
        await SnippetStore.SaveAsync("csharp", "first", CancellationToken.None);
        await SnippetStore.SaveAsync("python", "second", CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Equal(2, snippets.Count);
        Assert.Equal("first",  snippets[0].Code);
        Assert.Equal("second", snippets[1].Code);
    }

    [Fact]
    public async Task SaveAsync_ExceedsMaxSnippets_RemovesOldest()
    {
        // Fill to 100
        for (var i = 0; i < 100; i++)
            await SnippetStore.SaveAsync("js", $"snippet_{i}", CancellationToken.None);

        // 101st save must drop snippet_0
        await SnippetStore.SaveAsync("js", "snippet_100", CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Equal(100, snippets.Count);
        Assert.Equal("snippet_1",   snippets[0].Code);
        Assert.Equal("snippet_100", snippets[99].Code);
    }

    [Fact]
    public async Task LoadAllAsync_FileDoesNotExist_ReturnsEmpty()
    {
        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Empty(snippets);
    }

    [Fact]
    public async Task LoadAllAsync_CorruptedJson_ReturnsEmpty()
    {
        await File.WriteAllTextAsync(_tempFile, "{ this is not valid json }", CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Empty(snippets);
    }

    [Fact]
    public async Task SaveAsync_SnippetId_IsEightCharHex()
    {
        await SnippetStore.SaveAsync("ts", "code", CancellationToken.None);
        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Matches("^[0-9a-f]{8}$", snippets[0].Id);
    }

    [Fact]
    public async Task DeleteAsync_ValidIndex_RemovesSnippet()
    {
        await SnippetStore.SaveAsync("a", "first",  CancellationToken.None);
        await SnippetStore.SaveAsync("b", "second", CancellationToken.None);

        await SnippetStore.DeleteAsync(0, CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);

        Assert.Single(snippets);
        Assert.Equal("second", snippets[0].Code);
    }

    [Fact]
    public async Task DeleteAsync_IndexOutOfRange_DoesNothing()
    {
        await SnippetStore.SaveAsync("a", "only", CancellationToken.None);

        await SnippetStore.DeleteAsync(5, CancellationToken.None);  // out of range
        await SnippetStore.DeleteAsync(-1, CancellationToken.None); // negative

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);
        Assert.Single(snippets);
    }

    [Fact]
    public async Task ClearAsync_WithSnippets_WritesEmptyArray()
    {
        await SnippetStore.SaveAsync("a", "code", CancellationToken.None);

        await SnippetStore.ClearAsync(CancellationToken.None);

        var snippets = await SnippetStore.LoadAllAsync(CancellationToken.None);
        Assert.Empty(snippets);
    }

    [Fact]
    public async Task ClearAsync_FileDoesNotExist_DoesNotThrow()
    {
        // No prior save — file doesn't exist
        var ex = await Record.ExceptionAsync(() => SnippetStore.ClearAsync(CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public void FormatList_IndexLanguagePreviewAndSubCommands()
    {
        var listing = SnippetStore.FormatList(
        [
            new Snippet("id1", "csharp", "var x = 1;\nvar y = 2;", "2026-06-12T10:00:00"),
            new Snippet("id2", "",       new string('z', 100),     "2026-06-12T11:00:00"),
        ]);

        Assert.StartsWith(Strings.SnippetsListHeader, listing);
        Assert.Contains("**#1** (csharp) — `var x = 1; var y = 2;`", listing); // newline flattened
        Assert.Contains("`/snippets copy 1` • `/snippets delete 1`", listing);
        Assert.Contains($"`{new string('z', 60)}…`", listing);                 // 60-char cap
        Assert.DoesNotContain("#2 (", listing);                                // no empty language parens
    }
}
