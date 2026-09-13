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

    // AppDataJsonFile.SaveAsync swallowed the write failure: `/snippets clear` answered "library
    // cleared" on an untouched file. A regular file as the parent directory makes the write
    // impossible on every OS.
    [Fact]
    public async Task Clear_WhenTheLibraryCannotBeWritten_SaysSo()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"snippets_blocker_{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        try
        {
            SnippetStore._fileOverride = Path.Combine(blocker, "snippets.json");
            var result = await SnippetsCommandHandler.HandleAsync(Cmd("clear"), CancellationToken.None);
            Assert.Equal(Strings.SnippetsWriteFailed, result.Message);
        }
        finally
        {
            SnippetStore._fileOverride = _tempFile;
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Delete_WhenTheLibraryCannotBeWritten_SaysSo_AndKeepsTheSnippet()
    {
        var dir  = Directory.CreateTempSubdirectory("snippets-locked-").FullName;
        var path = Path.Combine(dir, "snippets.json");
        SnippetStore._fileOverride = path;
        try
        {
            await SnippetStore.SaveAsync("csharp", "first",  CancellationToken.None);
            await SnippetStore.SaveAsync("python", "second", CancellationToken.None);

            SnippetsCommandHandler.SnippetsCommandResult result;
            if (OperatingSystem.IsWindows())
            {
                // Held open without FileShare.Delete: readable, not replaceable.
                using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                result = await SnippetsCommandHandler.HandleAsync(Cmd("delete", "1"), CancellationToken.None);
            }
            else
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                try { result = await SnippetsCommandHandler.HandleAsync(Cmd("delete", "1"), CancellationToken.None); }
                finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            }

            Assert.Equal(Strings.SnippetsWriteFailed, result.Message);
            Assert.Equal(2, (await SnippetStore.LoadAllAsync(CancellationToken.None)).Count);

            // Witness: file released, the same deletion goes through.
            Assert.Equal(Strings.SnippetsDeleted(1),
                (await SnippetsCommandHandler.HandleAsync(Cmd("delete", "1"), CancellationToken.None)).Message);
        }
        finally
        {
            SnippetStore._fileOverride = _tempFile;
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// <c>clear</c> only binds alone: <c>/snippets clear all but the first</c> emptied the whole library.
    /// </summary>
    [Fact]
    public async Task Clear_FollowedByText_ShowsTheUsage_AndKeepsTheLibrary()
    {
        await SnippetStore.SaveAsync("csharp", "x", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd("clear", "all", "but", "the", "first"), CancellationToken.None);

        Assert.Equal(Strings.SlashUsage("/snippets [list | copy <n> | delete <n> | clear]"), result.Message);
        Assert.Single(await SnippetStore.LoadAllAsync(CancellationToken.None));
    }

    /// <summary>
    /// A form the command does not know is named as such — <c>/snippets remove 2</c> used to list, which
    /// reads as the deletion having been done.
    /// </summary>
    [Theory]
    [InlineData("remove", "2")]
    [InlineData("delete", "two")]
    [InlineData("delete")]
    public async Task AnUnknownForm_ShowsTheUsage_AndDeletesNothing(params string[] args)
    {
        await SnippetStore.SaveAsync("csharp", "x", CancellationToken.None);

        var result = await SnippetsCommandHandler.HandleAsync(Cmd(args), CancellationToken.None);

        Assert.Equal(Strings.SlashUsage("/snippets [list | copy <n> | delete <n> | clear]"), result.Message);
        Assert.Single(await SnippetStore.LoadAllAsync(CancellationToken.None));
    }
}
