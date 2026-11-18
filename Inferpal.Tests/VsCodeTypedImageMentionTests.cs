using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An image typed as <c>@file</c> in VS Code's question goes as an image — as Browse attaches it and as Visual Studio does —
/// instead of "not attached": a typed mention of a PNG reached the host's text reader, which refuses a binary.
/// </summary>
public class VsCodeTypedImageMentionTests
{
    private static string Code => WebviewRebuildTests.TsCode("chatViewProvider.ts").Replace("\r\n", "\n");

    [Fact]
    public void ATypedImage_IsReadAsAnImage_BeforeAnyTextReader()
    {
        var code = Code;
        var expand = code[code.IndexOf("private async expandMentions(", StringComparison.Ordinal)..];
        expand = expand[..expand.IndexOf("\n  }\n", StringComparison.Ordinal)];
        var image = expand.IndexOf("if (typedImages && (IMAGE_FILE.test(uri.fsPath) || SVG_FILE.test(uri.fsPath)))", StringComparison.Ordinal);
        var text = expand.IndexOf("this.readAttachable(uri)", StringComparison.Ordinal);

        Assert.True(image > 0 && text > image, "the image route must come before the text reader");
        Assert.Contains("typed = await this.typedImage(uri, host, chipImages.length + images.length);", expand, StringComparison.Ordinal);
        // A host that fails to read it leaves the question to the text path, never lost.
        Assert.Contains("typed = undefined;", expand, StringComparison.Ordinal);
        // Already a chip: it goes once.
        Assert.Contains("if (chipImages.some((p) => samePath(p, uri.fsPath)))", expand, StringComparison.Ordinal);
        Assert.Contains("return { text: prompt + attachments, paths, labels, images };", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ATypedImageSentWithout_IsNamedToTheModel()
    {
        Assert.Contains("## Not attached: ${typed.token}", Code, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTypedImages_GoWithTheChipsImages_AndShowUnderTheQuestion()
    {
        var code = Code;

        // A code action's excerpt quoting an image path attaches no image.
        Assert.Contains("const mentions = await this.expandMentions(prompt, host, !codeAction,", code, StringComparison.Ordinal);
        Assert.Contains("let images: ImageDto[] = mentions.images.map((m) => m.image);", code, StringComparison.Ordinal);
        Assert.Contains("asked.images = [...mentions.images, ...chips]", code, StringComparison.Ordinal);
    }
}
