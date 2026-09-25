using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>search_in_files</c> takes "text or a regular expression", and read literal code as a regex whenever it parsed:
/// <c>DoWork(x)</c> became "DoWorkx", <c>arr[i]</c> "arri", <c>obj?.Prop</c> an optional j and any character — each
/// missing the text that IS in the file, answered "no results", which the model reads as "not used anywhere".
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares the localized "no results"
public sealed class SearchInFilesLiteralTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("inferpal-search-").FullName;

    public SearchInFilesLiteralTests() =>
        File.WriteAllText(Path.Combine(_dir, "Code.cs"), string.Join("\n",
            "class Code",
            "{",
            "    void Run() { DoWork(x); var v = arr[i]; var p = obj?.Prop; }",
            "    int Count = 42;",
            "}"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private Task<string> SearchAsync(string pattern) =>
        new SearchInFilesTool(() => _dir).ExecuteAsync(
            JsonDocument.Parse(JsonSerializer.Serialize(new { path = _dir, pattern })).RootElement, CancellationToken.None);

    [Theory]
    [InlineData("DoWork(x)")]
    [InlineData("arr[i]")]
    [InlineData("obj?.Prop")]
    public async Task ALiteralThatParsesAsARegex_IsStillFound(string pattern)
    {
        var report = await SearchAsync(pattern);

        Assert.Contains("Code.cs:3:", report);
    }

    [Fact]
    public async Task ARealRegex_StillMatchesAsARegex()
    {
        // Reference arm: `Cou\w+ = \d+` is meant as a regex and matches line 4 as one.
        Assert.Contains("Code.cs:4:", await SearchAsync(@"Cou\w+ = \d+"));
    }

    [Fact]
    public async Task ATextThatIsNotThere_IsStillNotFound()
    {
        // Reference arm: the fallback must not turn an absent text into a match.
        Assert.StartsWith(Strings.NoResults, await SearchAsync("DoWork(y)"));
    }
}
