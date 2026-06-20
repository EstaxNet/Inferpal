using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

public class DurationFieldsTests
{
    // ── Clamp ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", 99, "")]            // empty preserved (clearable mid-edit)
    [InlineData("abc", 59, "")]         // no digits → empty
    [InlineData("30", 59, "30")]        // in range
    [InlineData("150", 99, "99")]       // clamped to max
    [InlineData("60", 59, "59")]        // clamped
    [InlineData("1h2", 59, "12")]       // non-digits stripped → 12
    [InlineData("007", 99, "7")]        // leading zeros normalised
    public void Clamp_SanitisesAndClamps(string input, int max, string expected)
    {
        Assert.Equal(expected, DurationFields.Clamp(input, max));
    }

    // ── Split ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Split_ZeroPadsMinutesAndSeconds()
    {
        Assert.Equal(("0", "00", "00"), DurationFields.Split(0));
        Assert.Equal(("0", "01", "30"), DurationFields.Split(90));
        Assert.Equal(("1", "01", "01"), DurationFields.Split(3661));
        Assert.Equal(("2", "00", "00"), DurationFields.Split(7200));
    }

    [Fact]
    public void Split_NegativeTreatedAsZero()
    {
        Assert.Equal(("0", "00", "00"), DurationFields.Split(-5));
    }

    [Fact]
    public void Split_HoursAreNotPaddedAndCanExceedTwoDigits()
    {
        Assert.Equal(("100", "00", "00"), DurationFields.Split(100 * 3600));
    }

    // ── Combine ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Combine_SumsFields()
    {
        Assert.Equal(3661, DurationFields.Combine("1", "01", "01"));
        Assert.Equal(7200, DurationFields.Combine("2", "", ""));
        Assert.Equal(90, DurationFields.Combine("", "1", "30"));
    }

    [Fact]
    public void Combine_NonNumericFieldsCountAsZero()
    {
        Assert.Equal(0, DurationFields.Combine("x", "", "z"));
        Assert.Equal(0, DurationFields.Combine("", "", ""));
    }

    // ── Round-trip ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(45)]
    [InlineData(120)]
    [InlineData(3661)]
    [InlineData(86399)]
    public void Combine_OfSplit_RoundTrips(int seconds)
    {
        var (h, m, s) = DurationFields.Split(seconds);
        Assert.Equal(seconds, DurationFields.Combine(h, m, s));
    }
}
