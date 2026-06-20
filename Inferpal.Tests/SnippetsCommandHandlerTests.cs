using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

// Shares the "SnippetStore" collection (defined in SnippetStoreTests) so the static
// SnippetStore._fileOverride is never mutated concurrently by parallel tests.
[Collection("SnippetStore")]
public class SnippetsCommandHandlerTests : IDisposable
{
    private readonly string _tempFile;

    public SnippetsCommandHandlerTests()
    {
        _tempFile = Path.Combine(Path.GetTempPath(), $"snippets_cmd_test_{Guid.NewGuid():N}.json");
        SnippetStore._fileOverride = _tempFile;
    }

    public void Dispose()
    {
        SnippetStore._fileOverride = null;
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    private static string[] Cmd(params string[] args) => ["/snippets", .. args];

    [Fact]
    public async Task List_WhenEmpty_ReturnsHint()
    {
        var result = await SnippetsCommandHandler.HandleAsync(Cmd(), CancellationToken.None);

        Assert.Equal(Strings.SnippetsNone, result.Message);
        Assert.Null(result.CopyToClipboard);
    }

    [Fact]
    public async Task List_WithSnippets_ReturnsFormattedList()
    {
        await SnippetStore.SaveAsync("csharp", "var x = 1;", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("list"), CancellationToken.None);

        Assert.Contains(Strings.SnippetsListHeader, result.Message);
        Assert.Contains("var x = 1;", result.Message);
        Assert.Null(result.CopyToClipboard);
    }

    [Fact]
    public async Task Copy_ValidIndex_ReturnsCodeOnClipboard()
    {
        await SnippetStore.SaveAsync("csharp", "first",  CancellationToken.None);
        await SnippetStore.SaveAsync("python", "second", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("copy", "2"), CancellationToken.None);

        Assert.Equal("second", result.CopyToClipboard);
        Assert.Equal(Strings.SnippetsCopied(2), result.Message);
    }

    [Fact]
    public async Task Copy_OutOfRange_ReturnsNoSnippetAndNoClipboard()
    {
        await SnippetStore.SaveAsync("csharp", "only", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("copy", "5"), CancellationToken.None);

        Assert.Equal(Strings.SnippetsNoSuch(5), result.Message);
        Assert.Null(result.CopyToClipboard);
    }

    [Fact]
    public async Task Delete_ValidIndex_RemovesSnippet()
    {
        await SnippetStore.SaveAsync("csharp", "first",  CancellationToken.None);
        await SnippetStore.SaveAsync("python", "second", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("delete", "1"), CancellationToken.None);

        Assert.Equal(Strings.SnippetsDeleted(1), result.Message);
        var remaining = await SnippetStore.LoadAllAsync(CancellationToken.None);
        Assert.Equal("second", Assert.Single(remaining).Code);
    }

    [Fact]
    public async Task Clear_EmptiesTheLibrary()
    {
        await SnippetStore.SaveAsync("csharp", "x", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("clear"), CancellationToken.None);

        Assert.Equal(Strings.SnippetsCleared, result.Message);
        Assert.Empty(await SnippetStore.LoadAllAsync(CancellationToken.None));
    }
}
