using System;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the rolling chain-of-thought preview extracted from the tool-window VM:
// throttling, tail extraction, newline flattening, buffer bounding, and the reset
// between agent acts.
public class ThinkingPreviewTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Clock the test advances by hand.</summary>
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } = T0;
        public void Advance(int ms) => Now = Now.AddMilliseconds(ms);
    }

    [Fact]
    public void Append_EmitsPrefixedTail_OnFirstDelta()
    {
        var preview = new ThinkingPreview(() => T0);

        Assert.Equal("💭 thinking about it", preview.Append("thinking about it"));
    }

    [Fact]
    public void Append_Throttles_WithinWindow()
    {
        var clock   = new FakeClock();
        var preview = new ThinkingPreview(() => clock.Now);

        Assert.NotNull(preview.Append("first"));
        clock.Advance(50);
        Assert.Null(preview.Append(" second"));        // 50 ms < 120 ms window
        clock.Advance(100);
        Assert.Equal("💭 first second third", preview.Append(" third"));
    }

    [Fact]
    public void Append_FlattensNewlines_AndTrims()
    {
        var preview = new ThinkingPreview(() => T0);

        Assert.Equal("💭 line one line two", preview.Append("line one\nline two\n"));
    }

    [Fact]
    public void Append_ReturnsNull_WhenTailIsWhitespaceOnly()
    {
        var preview = new ThinkingPreview(() => T0);

        Assert.Null(preview.Append("   \n  "));
    }

    [Fact]
    public void Append_ShowsOnlyTheTail_OfLongReasoning()
    {
        var clock   = new FakeClock();
        var preview = new ThinkingPreview(() => clock.Now);

        preview.Append(new string('a', 3000));
        clock.Advance(200);
        var text = preview.Append("END");

        Assert.NotNull(text);
        Assert.EndsWith("END", text);
        Assert.True(text!.Length <= 162 + 2);          // "💭 " prefix + 160-char tail
    }

    [Fact]
    public void Reset_DropsBufferedReasoning_BetweenActs()
    {
        var clock   = new FakeClock();
        var preview = new ThinkingPreview(() => clock.Now);

        preview.Append("stale act-one reasoning");
        preview.Reset();
        clock.Advance(200);

        Assert.Equal("💭 fresh", preview.Append("fresh"));
    }
}
