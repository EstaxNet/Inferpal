using System.Collections.Generic;
using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Inferpal.Services.Docs;
using Inferpal.Services.Editor;
using Inferpal.Services.Execution;
using Inferpal.Services.Governance;
using Inferpal.Services.Lsp;
using Inferpal.Services.Mcp;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The diff a run's review reads (pari 159), from the run's real backups: a change as its lines, a created file as its head,
/// a binary by its size, a file the ignore files keep out named and never shown, a file that cannot be read named — never a
/// failure of the run.
/// </summary>
public sealed class RunDiffTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-rundiff-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var o in _owned) o.Dispose();
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ToolRegistry Registry()
    {
        var config = new InferpalConfig();
        var client = new FakeInferenceProvider();
        var editor = new NullEditorSurface();
        var approval = new NoopApproval();
        var index = new ProjectIndexService(client, config, new LspSemanticProvider());
        _owned.Add(index);
        index.SetRoot(_root);
        return new ToolRegistry(editor, approval, config, index, client, new ProjectMapService(editor),
                                new McpToolService(config, approval), new DocsIndexService(client, config),
                                new OpenDocumentOverlay(), new NullDebugSession());
    }

    private string Path(string rel) => System.IO.Path.Combine(_root, rel);

    /// <summary>The file as the run changes it: backed up first, then written, as every writing tool does.</summary>
    private static async Task ChangeAsync(FileHistoryService history, string path, byte[] content)
    {
        var (saved, _) = await history.BackUpBeforeChangeAsync(path, CancellationToken.None);
        Assert.True(saved);
        await File.WriteAllBytesAsync(path, content);
    }

    [Fact]
    public async Task EachChange_IsShownForWhatItIs()
    {
        File.WriteAllText(Path("pricing.py"), "TAX_RATE = 0.20\n\ndef total(net):\n    return net * (1 + TAX_RATE)\n");
        File.WriteAllBytes(Path("logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 1, 2, 3]);
        File.WriteAllText(Path(".inferpalignore"), "secret.env\n");
        File.WriteAllText(Path("secret.env"), "API_KEY=old\n");
        var tools = Registry();
        string? diff;
        using (tools.History.BeginRunScope())
        {
            await ChangeAsync(tools.History, Path("pricing.py"), "TAX_RATE = 0.25\n\ndef total(net):\n    return net * (1 + TAX_RATE)\n"u8.ToArray());
            await ChangeAsync(tools.History, Path("notes.md"), "# Notes\nprint('debug')\n"u8.ToArray());
            await ChangeAsync(tools.History, Path("logo.png"), [0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0, 9, 9, 9]);
            await ChangeAsync(tools.History, Path("secret.env"), "API_KEY=sk-live-new-value\n"u8.ToArray());
            diff = await tools.RunDiffAsync(12_000, CancellationToken.None);
        }

        Assert.NotNull(diff);
        Assert.Contains("### pricing.py\n", diff, StringComparison.Ordinal);
        Assert.Contains("-TAX_RATE = 0.20", diff, StringComparison.Ordinal);
        Assert.Contains("+TAX_RATE = 0.25", diff, StringComparison.Ordinal);
        Assert.Contains("### notes.md (created)\n+# Notes\n+print('debug')", diff, StringComparison.Ordinal);
        Assert.Contains("logo.png (binary", diff, StringComparison.Ordinal);
        Assert.Contains("secret.env (kept out of AI tools by the ignore files)", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-new-value", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("\0", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverTheBudget_TheDiffIsCut_AndSaysSo()
    {
        File.WriteAllText(Path("big.txt"), string.Join("\n", Enumerable.Range(0, 400).Select(i => $"line {i}")) + "\n");
        var tools = Registry();
        string? diff;
        using (tools.History.BeginRunScope())
        {
            await ChangeAsync(tools.History, Path("big.txt"),
                System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Enumerable.Range(0, 400).Select(i => $"changed {i}")) + "\n"));
            diff = await tools.RunDiffAsync(1_000, CancellationToken.None);
        }

        Assert.NotNull(diff);
        Assert.True(diff.Length < 1_200, diff.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("[cut here: read the file with read_file]", diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunThatChangedNothing_HasNoDiff()
    {
        var tools = Registry();
        using (tools.History.BeginRunScope())
            Assert.Null(await tools.RunDiffAsync(12_000, CancellationToken.None));
    }
}
