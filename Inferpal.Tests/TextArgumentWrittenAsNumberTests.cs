using System.IO;
using System.Text.Json;
using Inferpal.Services.Agent;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A text argument the model wrote as a number or a boolean is still that text.
/// </summary>
/// <remarks>
/// <see cref="ToolArgs.Int"/> reads <c>"5"</c>; its mirror did not exist. Gemma writes its values bare —
/// <c>search_in_files{pattern:404}</c>, <c>run_tests{filter:1234}</c> — and JSON callers write <c>"query": 2024</c>: read as
/// absent, the search was refused as "pattern is required" and a test run ran the whole suite instead of the tests asked
/// for.
/// </remarks>
public sealed class TextArgumentWrittenAsNumberTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-numarg-" + Guid.NewGuid().ToString("N"));

    public TextArgumentWrittenAsNumberTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Errors.cs"), "class Errors\n{\n    const int NotFound = 404;\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Task<string> Search(JsonElement args) =>
        new SearchInFilesTool(() => _root).ExecuteAsync(args, CancellationToken.None);

    [Fact]
    public async Task GemmasBareNumber_IsThePatternItWrote()
    {
        var (calls, _) = InlineToolCallParser.TryParse(
            "<|tool_call>call:search_in_files{path:<|\"|>" + _root.Replace("\\", "\\\\") + "<|\"|>,pattern:404}<tool_call|>");
        var args = Assert.Single(calls!).Function.Arguments;
        Assert.Equal(JsonValueKind.Number, args.GetProperty("pattern").ValueKind);   // witness: Gemma's value IS a number

        var report = await Search(args);

        Assert.Contains("NotFound = 404", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonNumber_IsThePatternItWrote()
    {
        var report = await Search(JsonSerializer.SerializeToElement(new { path = _root, pattern = 404 }));

        Assert.Contains("NotFound = 404", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"x\": true}", "true")]
    [InlineData("{\"x\": 1e5}", "1e5")]
    [InlineData("{\"x\": -3}", "-3")]
    [InlineData("{\"x\": \"text\"}", "text")]
    public void ToolArgs_ReadsAScalarAsTheTextItWasWrittenAs(string json, string expected) =>
        Assert.Equal(expected, JsonDocument.Parse(json).RootElement.Str("x"));

    [Fact]
    public async Task AnObjectWhereTextIsExpected_IsStillNoText()
    {
        // Reference arm: a structure is not a scalar written in another type — the tool still names what is missing.
        Assert.Null(JsonDocument.Parse("{\"x\": {\"a\": 1}}").RootElement.Str("x"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Search(JsonSerializer.SerializeToElement(new { path = _root, pattern = new { a = 1 } })));
    }
}
