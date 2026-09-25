using System.IO;
using System.Text.Json;
using Inferpal.Localization;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>search_in_files</c> and <c>list_files</c> given the path of a FILE answered "Directory not found: &lt;path&gt;" —
/// for a path that exists. Searching one file is the natural request, and the answer reads "this file does not exist".
/// </summary>
[Collection(CultureSerialCollection.Name)]   // compares the localized "directory not found"
public sealed class FilePathToFolderToolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-filepath-").FullName;
    private readonly string _file;

    public FilePathToFolderToolTests()
    {
        _file = Path.Combine(_root, "Service.cs");
        File.WriteAllText(_file, "class Service\n{\n    void Start() => Connect();\n}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static JsonElement Args(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement;

    [Fact]
    public async Task SearchingOneFile_SearchesIt()
    {
        var report = await new SearchInFilesTool(() => _root)
            .ExecuteAsync(Args(new { path = _file, pattern = "Connect" }), CancellationToken.None);

        Assert.Contains("Service.cs:3:", report);
    }

    [Fact]
    public async Task ListingAFile_SaysItIsOne()
    {
        var shown = await new ListFilesTool(() => _root).ExecuteAsync(Args(new { path = _file }), CancellationToken.None);

        Assert.Contains("is a file", shown);
        Assert.Contains("read_file", shown);
    }

    [Fact]
    public async Task APathThatDoesNotExist_KeepsItsAnswer()
    {
        // Reference arm.
        var missing = Path.Combine(_root, "nope");
        Assert.Equal(Strings.DirNotFound(missing),
                     await new SearchInFilesTool(() => _root).ExecuteAsync(Args(new { path = missing, pattern = "x" }), CancellationToken.None));
    }
}
