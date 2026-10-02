using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Lsp;
using Inferpal.Services.Presentation;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An @-mention that has nothing to attach says why, in place of a chip: <c>@folder</c> on a missing path or on a
/// file, <c>@code</c> with no index.
/// </summary>
/// <remarks>
/// Both front-ends put the refusal under the chip: a typo read "this folder could not be listed — it may have been
/// removed", a file read the same, and <c>@code</c> attached "index not available" under a 🔮 chip as if it were the code
/// found. A folder that exists but cannot be read, or one the walk skips by name, keeps its chip and its note
/// (SilentMentionTests): those notes are about a folder the user did name.
/// </remarks>
public class MentionRefusalTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"mention-refusal-{Guid.NewGuid():N}")).FullName;
    private readonly List<IDisposable> _services = [];

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch { } }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AFolderThatIsNotThere_OrIsAFile_IsRefusedWithItsReason()
    {
        File.WriteAllText(Path.Combine(_root, "Pricing.cs"), "class Pricing { }\n");

        var missing = MentionController.FolderPath("srcc", _root);
        Assert.Equal(Strings.DirNotFound(missing), MentionController.FolderRefusal(missing));

        var file = MentionController.FolderPath("Pricing.cs", _root);
        Assert.Equal(Strings.MentionFolderIsFile("Pricing.cs"), MentionController.FolderRefusal(file));
    }

    [Fact]
    public void AFolderThatIsThere_IsWalked_AndARelativeNameIsReadUnderTheRoot()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var folder = MentionController.FolderPath("src", _root);
        Assert.Equal(Path.Combine(_root, "src"), folder);                  // the root, never the process's directory
        Assert.Null(MentionController.FolderRefusal(folder));
    }

    [Fact]
    public async Task CodeWithoutAnIndex_IsRefused_AndWithOne_IsSearched()
    {
        var config = new InferpalConfig { RagEnabled = true };
        var client = new FakeInferenceProvider { Embedding = [0.1f, 0.2f] };
        var index  = new ProjectIndexService(client, config, new LspSemanticProvider());
        _services.Add(index);

        Assert.Equal(Strings.RagIndexNotReady(index.Status), MentionController.CodeRefusal(index));

        // Reference arm: an indexed workspace is searched — no notice.
        File.WriteAllText(Path.Combine(_root, "OrderService.cs"),
            "namespace Shop;\npublic class OrderService\n{\n    public int Count() => 0;\n    public int Total() => 1;\n}\n");
        index.StartIndexing(_root);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while ((index.IsIndexing || index.ChunkCount == 0) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(index.ChunkCount > 0, $"the workspace was not indexed: {index.Status}");
        Assert.Null(MentionController.CodeRefusal(index));
    }
}
