using System.Text.Json;
using Inferpal.Services.Mcp;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An MCP tool that answers with something other than text was reported to the model as having
/// answered <b>nothing</b>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <c>ExtractCallResult</c> keeps <c>text</c> blocks and the <c>text</c> of <c>resource</c> blocks,
/// and drops everything else the protocol allows — <c>image</c>, <c>audio</c>, <c>resource_link</c>,
/// a resource carrying a <c>blob</c> instead of text. Forwarding them is not the point: a local
/// text model cannot read a PNG. <b>Saying so is.</b> A screenshot server, a chart server, a
/// diagram server answers with one image block, the text buffer stays empty, and the model is told
/// <c>(no output)</c> — so it concludes the tool did nothing and tries something else, or reports
/// the failure to the user.
/// </para>
/// <para>
/// ⚠ The line above the drop already carries half of this lesson: <i>"A malformed block is skipped:
/// it failed the whole call, and the text of the others was lost."</i> The crash was closed; the
/// silence beside it was not.
/// </para>
/// </remarks>
public sealed class McpNonTextBlockTests
{
    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void AToolAnsweringOnlyWithAnImage_IsNotReportedAsHavingAnsweredNothing()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [ { "type": "image", "data": "iVBORw0KGgo=", "mimeType": "image/png" } ] }
        """), "screenshot");

        Assert.DoesNotContain("(no output)", text, StringComparison.Ordinal);
        Assert.Contains("image", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolAnsweringWithTextAndAnImage_KeepsTheText_AndSaysWhatItDropped()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [
            { "type": "text", "text": "saved to /tmp/shot.png" },
            { "type": "image", "data": "iVBORw0KGgo=", "mimeType": "image/png" }
        ] }
        """), "screenshot");

        Assert.Contains("saved to /tmp/shot.png", text, StringComparison.Ordinal);   // witness
        Assert.Contains("image", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AResourceCarryingABlobInsteadOfText_IsNamedToo()
    {
        // The protocol allows a resource to carry `blob` rather than `text`; it read as absent.
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [ { "type": "resource", "resource": { "uri": "file:///x.bin", "blob": "AAEC" } } ] }
        """), "reader");

        Assert.DoesNotContain("(no output)", text, StringComparison.Ordinal);
        Assert.Contains("resource", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATextOnlyAnswer_IsUnchanged()
    {
        // NEGATIVE WITNESS: the note must appear only when something really was dropped, or every
        // assertion above would pass on a build that always appends it.
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [ { "type": "text", "text": "line one" },
                       { "type": "resource", "resource": { "text": "line two" } } ] }
        """), "t");

        Assert.Equal("line one\nline two", text.Replace("\r\n", "\n"));
    }

    [Fact]
    public void AnEmptyAnswer_IsStillTheEmptyPlaceholder()
    {
        // REFERENCE ARM: a tool that genuinely returned nothing must keep saying so — that is a
        // different fact from "it returned something I cannot pass on".
        Assert.Equal("(no output)", McpJsonRpc.ExtractCallResult(El("""{ "content": [] }"""), "t"));
        Assert.Equal("(no output)", McpJsonRpc.ExtractCallResult(El("{}"), "t"));
    }

    [Fact]
    public void AMalformedBlock_IsStillSkippedWithoutFailingTheCall()
    {
        // REFERENCE ARM for the fix that came before this one: the text of the sound blocks survives
        // a broken neighbour. A malformed block is not a dropped *kind* — nothing was returned to
        // pass on — so it must not be counted as one either.
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "content": [ null, { "type": 1 }, { "type": "text", "text": "kept" } ] }
        """), "t");

        Assert.StartsWith("kept", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorResultThatCarriesOnlyAnImage_IsStillReportedAsAnError()
    {
        var text = McpJsonRpc.ExtractCallResult(El("""
        { "isError": true, "content": [ { "type": "image", "data": "AAEC" } ] }
        """), "danger");

        Assert.Contains("reported an error", text, StringComparison.Ordinal);
        Assert.Contains("image", text, StringComparison.Ordinal);
    }
}
