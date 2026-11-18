using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Files dropped on the VS Code chat from the system's file manager (Shift held — VS Code hands a webview nothing else):
/// what the drop takes, how it is read, where its copy goes. Measured with real mouse drags (docs/probes/pari84); these
/// hold the properties that a review found missing, on the code, comments left out.
/// </summary>
public class VsCodeFileDropTests
{
    private static readonly string Webview  = WebviewRebuildTests.TsCode("webview/main.ts").Replace("\r\n", "\n");
    private static readonly string Provider = WebviewRebuildTests.TsCode("chatViewProvider.ts").Replace("\r\n", "\n");

    /// <summary>Only a drag that carries files is taken: a link or a picture dragged inside the chat (a text/uri-list) goes
    /// on as before, and a file manager's uri-list beside its files is never read as a path — on WSL, SSH or a container
    /// it would name the wrong file.</summary>
    [Fact]
    public void OnlyFilesAreTaken_NeverAUriList()
    {
        Assert.Contains("return !!dt && dt.types.includes('Files');", Webview, StringComparison.Ordinal);
        Assert.DoesNotContain("uri-list", Webview, StringComparison.Ordinal);
        Assert.DoesNotContain("attachUris", Provider, StringComparison.Ordinal);
    }

    /// <summary>A file that cannot be read is said, never dropped in silence; files are read one at a time; the size limit
    /// is one value for both sides.</summary>
    [Fact]
    public void AnUnreadableFile_IsSaid_FilesAreReadOneByOne_AndTheLimitIsShared()
    {
        Assert.Contains("reader.onerror = () => resolve(null);", Webview, StringComparison.Ordinal);
        Assert.Contains("outcome: 'unreadable'", Webview, StringComparison.Ordinal);
        Assert.Contains("const data = await readDropped(file);", Webview, StringComparison.Ordinal);
        // One function for both sides (an event log's own limit included), never a second copy of the number.
        Assert.Contains("import { droppedFileLimit } from '../droppedFiles';", Webview, StringComparison.Ordinal);
        Assert.Contains("import { droppedFileLimit } from './droppedFiles';", Provider, StringComparison.Ordinal);
        Assert.Contains("file.size > droppedFileLimit(name)", Webview, StringComparison.Ordinal);
        Assert.DoesNotContain("1024 * 1024", Webview, StringComparison.Ordinal);
    }

    /// <summary>The copy goes to a folder made for the drop (private to the account, never a fixed name in the shared temp)
    /// and is removed once read; drops are attached one after the other (the per-question image limit counts them all);
    /// an image is recognised by its type too; the copy's chip names no source file (the relevant code is not skipped for
    /// a file that is not in the workspace).</summary>
    [Fact]
    public void TheCopy_IsPrivateAndRemoved_DropsQueue_AndImagesGoByTheirType()
    {
        Assert.Contains("dir = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'inferpal-drop-'));", Provider, StringComparison.Ordinal);
        Assert.Contains("await fs.promises.rm(dir, { recursive: true, force: true })", Provider, StringComparison.Ordinal);
        Assert.DoesNotContain("'inferpal-dropped'", Provider, StringComparison.Ordinal);
        Assert.Contains("this.drops = this.drops.then(() => this.attachDroppedFile(", Provider, StringComparison.Ordinal);
        Assert.Contains("if (IMAGE_FILE.test(name) || mediaType.startsWith('image/')) {", Provider, StringComparison.Ordinal);
        Assert.Contains("read.text, label ? undefined : uri.fsPath, uri.fsPath)", Provider, StringComparison.Ordinal);
    }
}
