using System.Reflection;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// ThemePalette centralises the dark/light hex mapping that used to be inline in the VM.
// These guard the two things worth pinning: role→background selection, and dark/light parity
// (every colour must differ between modes — a copy-paste leaving a dark hex in the light palette
// is the classic regression).
public class ThemePaletteTests
{
    [Theory]
    [InlineData("user", "#1A3A5C")]
    [InlineData("tool", "#1E1E1E")]
    [InlineData("assistant", "Transparent")]
    [InlineData(null, "Transparent")]
    public void BubbleBackground_Dark_MapsByRole(string? role, string expected) =>
        Assert.Equal(expected, ThemePalette.For(isDark: true).BubbleBackground(role));

    [Theory]
    [InlineData("user", "#D6EAF8")]
    [InlineData("tool", "#EBEBEB")]
    [InlineData("system", "Transparent")]
    public void BubbleBackground_Light_MapsByRole(string role, string expected) =>
        Assert.Equal(expected, ThemePalette.For(isDark: false).BubbleBackground(role));

    [Fact]
    public void ChipAndSuggestionColours_AreCentralised()
    {
        var dark  = ThemePalette.For(isDark: true);
        var light = ThemePalette.For(isDark: false);

        // Attachment chip (blue) and pinned-file chip (gold), consolidated from inline VM ternaries.
        Assert.Equal("#2D3048", dark.AttachChipBg);
        Assert.Equal("#D8E4F8", light.AttachChipBg);
        Assert.Equal("#3A2E1A", dark.PinChipBg);
        Assert.Equal("#FBF3DC", light.PinChipBg);

        // Suggestion-popup secondary text shared by the mention + slash autocompletes (was duplicated).
        Assert.Equal("#808080", dark.SuggestionSubtleText);
        Assert.Equal("#606060", light.SuggestionSubtleText);
    }

    [Fact]
    public void For_ReturnsDistinctPalettes()
    {
        Assert.NotEqual(ThemePalette.For(isDark: true), ThemePalette.For(isDark: false));
        Assert.Equal("#1E1E1E", ThemePalette.For(isDark: true).WindowBg);
        Assert.Equal("#F5F5F5", ThemePalette.For(isDark: false).WindowBg);
    }

    // Every colour string on the record must differ between dark and light — catches a forgotten
    // variant (same hex in both) without enumerating each property by hand.
    [Fact]
    public void EveryColour_DiffersBetweenDarkAndLight()
    {
        var dark  = ThemePalette.For(isDark: true);
        var light = ThemePalette.For(isDark: false);

        var stringProps = typeof(ThemePalette)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string));

        foreach (var prop in stringProps)
        {
            var d = (string)prop.GetValue(dark)!;
            var l = (string)prop.GetValue(light)!;
            Assert.True(d != l, $"Colour '{prop.Name}' is identical in dark and light ('{d}') — likely a missing variant.");
        }
    }
}
