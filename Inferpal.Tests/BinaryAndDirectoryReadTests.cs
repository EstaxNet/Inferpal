using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the reading tools hand the model for a path that is not a text file. Measured: <c>read_file</c> on a .dll
/// returned 7 676 characters of which 2 426 were NULs — noise the model reads as content; on a DIRECTORY it answered
/// "file not found", for a path that exists; and <c>search_in_files</c> listed "matches" that were lines of binary.
/// </summary>
public sealed class BinaryAndDirectoryReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-binary-").FullName;

    public BinaryAndDirectoryReadTests()
    {
        File.Copy(typeof(ReadFileTool).Assembly.Location, Path.Combine(_root, "Lib.dll"));
        File.WriteAllText(Path.Combine(_root, "Notes.txt"), "System notes, in plain text.");
        File.WriteAllText(Path.Combine(_root, "Wide.txt"), "System in UTF-16", Encoding.Unicode);   // NULs, and text
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private Task<string> ReadAsync(string name) =>
        new ReadFileTool(() => _root).ExecuteAsync(
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = Path.Combine(_root, name) })).RootElement,
            CancellationToken.None);

    [Fact]
    public async Task ABinaryFile_IsNamed_NotDumped()
    {
        var shown = await ReadAsync("Lib.dll");

        Assert.Contains("binary", shown);
        Assert.DoesNotContain('\0', shown);
        Assert.True(shown.Length < 400, $"{shown.Length} characters for a binary file");
    }

    [Fact]
    public async Task ADirectory_IsNotAMissingFile()
    {
        var shown = await ReadAsync("sub");

        Assert.Contains("directory", shown);
        Assert.Contains("list_files", shown);
    }

    [Fact]
    public async Task AnEmptyFile_SaysSo()
    {
        // An empty tool result says nothing — not even "empty": the model cannot tell it from a call that did nothing.
        File.WriteAllText(Path.Combine(_root, "Empty.cs"), string.Empty);

        var shown = await ReadAsync("Empty.cs");

        Assert.Contains("Empty.cs", shown);
        Assert.Contains("empty", shown);
    }

    private sealed class SilentTool : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => Task.FromResult("  ");
    }

    [Fact]
    public async Task AnyToolThatReturnsNothing_ReachesTheModelAsNoOutput()
    {
        // The funnel, not a list of tools: `cd` in run_command prints nothing, and so may the next tool.
        var result = await Inferpal.Services.Agent.AgentOrchestrator.ExecuteToolSafeAsync(
            new SilentTool(), "run_command", JsonDocument.Parse("{}").RootElement, CancellationToken.None);

        Assert.Equal("(no output)", result);
    }

    [Fact]
    public async Task UnicodeText_IsStillText()
    {
        // Reference arm: UTF-16 is full of NULs and is text — its BOM says so.
        Assert.Contains("System in UTF-16", await ReadAsync("Wide.txt"));
    }

    [Fact]
    public async Task ASearchOverABinary_SaysItMatches_WithoutItsBytes()
    {
        var report = await new SearchInFilesTool(() => _root).ExecuteAsync(
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = _root, pattern = "System" })).RootElement,
            CancellationToken.None);

        Assert.Contains("Notes.txt:1: System notes", report);                                      // witness: text
        Assert.Contains("Wide.txt:1:", report);                                                   // UTF-16 is text
        Assert.Contains("Lib.dll: binary file matches", report);
        Assert.DoesNotContain('\0', report);
    }
}
