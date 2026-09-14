using System.Text;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The header block of a <c>Content-Length</c> frame, as the FIM sidecar pair and the
/// language-server transport all read it.
/// </summary>
public class FrameHeaderReaderTests
{
    private static (FrameHeaderReader.Outcome Outcome, FrameHeaderReader Reader) Read(params byte[][] parts)
    {
        var reader  = new FrameHeaderReader();
        var outcome = FrameHeaderReader.Outcome.Pending;
        foreach (var b in parts.SelectMany(p => p))
        {
            outcome = reader.Feed(b);
            if (outcome != FrameHeaderReader.Outcome.Pending) break;
        }
        return (outcome, reader);
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void ReadsTheLength_AmongOtherHeaders_WhateverTheCase()
    {
        var (outcome, reader) = Read(Ascii("Content-Type: application/json\r\ncontent-length:12\r\n\r\n"));

        Assert.Equal(FrameHeaderReader.Outcome.Complete, outcome);
        Assert.Equal(12, reader.Length);
    }

    [Fact]
    public void ABomBeforeTheFirstHeader_IsTolerated()
    {
        var (outcome, reader) = Read(Bom, Ascii("Content-Length: 3\r\n\r\n"));

        Assert.Equal(FrameHeaderReader.Outcome.Complete, outcome);
        Assert.Equal(3, reader.Length);
    }

    [Fact]
    public void ABomLaterInTheBlock_IsNotAHeaderPrefix()
    {
        var (outcome, _) = Read(Ascii("X-Other: 1\r\n"), Bom, Ascii("Content-Length: 3\r\n\r\n"));

        Assert.Equal(FrameHeaderReader.Outcome.NoLength, outcome);
    }

    [Theory]
    [InlineData("Content-Length: nope\r\n\r\n")]
    [InlineData("Content-Length: -4\r\n\r\n")]
    [InlineData("Content-Length: 8388609\r\n\r\n")]
    [InlineData("Content-Type: text/plain\r\n\r\n")]
    public void HeadersWithoutAUsableLength_EndAsNoLength_AfterAHeader(string headers)
    {
        var (outcome, reader) = Read(Ascii(headers));

        Assert.Equal(FrameHeaderReader.Outcome.NoLength, outcome);
        Assert.True(reader.SawHeader);
    }

    [Fact]
    public void ALeadingBlankLine_EndsWithoutAHeader()
    {
        var (outcome, reader) = Read(Ascii("\r\n"));

        Assert.Equal(FrameHeaderReader.Outcome.NoLength, outcome);
        Assert.False(reader.SawHeader);
    }

    [Fact]
    public void ALineThatNeverEnds_IsCut()
    {
        var (outcome, _) = Read(Ascii(new string('x', FrameHeaderReader.MaxLineChars + 1)));

        Assert.Equal(FrameHeaderReader.Outcome.LineTooLong, outcome);
    }
}
