using System.IO;
using Inferpal.Localization;
using Inferpal.Services;
using Inferpal.Services.Commands;
using Xunit;

namespace Inferpal.Tests;

public class NotesCommandHandlerTests : IDisposable
{
    private readonly string _root;

    public NotesCommandHandlerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"notes_cmd_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string[] Cmd(params string[] args) => args;

    [Fact]
    public async Task Note_NoText_ReturnsUsageAndDoesNotRefresh()
    {
        var result = await NotesCommandHandler.HandleNoteAsync(_root, Cmd("/note"), DateTime.Now, CancellationToken.None);

        Assert.Equal(Strings.NoteUsage, result.Message);
        Assert.False(result.RefreshSystemPrompt);
        Assert.False(File.Exists(NotesStore.NotesPath(_root)));
    }

    [Fact]
    public async Task Note_WithText_AppendsTimestampedLineAndRequestsRefresh()
    {
        var now = new DateTime(2026, 6, 15, 14, 30, 0);

        var result = await NotesCommandHandler.HandleNoteAsync(_root, Cmd("/note", "refactor", "the", "parser"), now, CancellationToken.None);

        Assert.True(result.RefreshSystemPrompt);
        Assert.Equal(Strings.NoteSaved("refactor the parser"), result.Message);
        var saved = await File.ReadAllTextAsync(NotesStore.NotesPath(_root));
        Assert.Contains("[2026-06-15 14:30] refactor the parser", saved);
    }

    [Fact]
    public async Task Notes_WhenNoFile_ReturnsHint()
    {
        var result = await NotesCommandHandler.HandleNotesAsync(_root, Cmd("/notes"), CancellationToken.None);

        Assert.Equal(Strings.NotesNoneYet, result.Message);
    }

    [Fact]
    public async Task Notes_AfterNote_ShowsContent()
    {
        await NotesCommandHandler.HandleNoteAsync(_root, Cmd("/note", "remember this"), DateTime.Now, CancellationToken.None);

        var result = await NotesCommandHandler.HandleNotesAsync(_root, Cmd("/notes"), CancellationToken.None);

        Assert.Contains(Strings.NotesHeading, result.Message);
        Assert.Contains("remember this", result.Message);
    }

    [Fact]
    public async Task Notes_Clear_RemovesFile()
    {
        await NotesCommandHandler.HandleNoteAsync(_root, Cmd("/note", "temp"), DateTime.Now, CancellationToken.None);

        var result = await NotesCommandHandler.HandleNotesAsync(_root, Cmd("/notes", "clear"), CancellationToken.None);

        Assert.Equal(Strings.NotesCleared, result.Message);
        Assert.False(File.Exists(NotesStore.NotesPath(_root)));
    }
}
