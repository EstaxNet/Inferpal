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
    [InlineData("user", "#2F2A4A")]
    [InlineData("tool", "Transparent")]
    [InlineData("assistant", "Transparent")]
    [InlineData(null, "Transparent")]
    public void BubbleBackground_Dark_MapsByRole(string? role, string expected) =>
        Assert.Equal(expected, ThemePalette.For(isDark: true).BubbleBackground(role));

    [Theory]
    [InlineData("user", "#E8E3FF")]
    [InlineData("tool", "Transparent")]
    [InlineData("system", "Transparent")]
    public void BubbleBackground_Light_MapsByRole(string role, string expected) =>
        Assert.Equal(expected, ThemePalette.For(isDark: false).BubbleBackground(role));

    [Fact]
    public void ChipAndSuggestionColours_AreCentralised()
    {
        var dark  = ThemePalette.For(isDark: true);
        var light = ThemePalette.For(isDark: false);

        // Attachment chip (the redesign's chip token) and pinned-file chip (gold), consolidated from inline VM ternaries.
        Assert.Equal("#2E2B3D", dark.AttachChipBg);
        Assert.Equal("#ECEAF5", light.AttachChipBg);
        Assert.Equal("#3A2E1A", dark.PinChipBg);
        Assert.Equal("#FBF3DC", light.PinChipBg);

        // Suggestion-popup secondary text shared by the mention + slash autocompletes (was duplicated).
        Assert.Equal("#A3A3AD", dark.SuggestionSubtleText);
        Assert.Equal("#5C5C66", light.SuggestionSubtleText);
    }

    [Fact]
    public void DiffColours_AreCentralised()
    {
        var dark  = ThemePalette.For(isDark: true);
        var light = ThemePalette.For(isDark: false);

        // Classic diff colouring (green background = addition, red background = deletion),
        // moved here from hardcoded dark-only hex in DiffComputer so both themes render it.
        Assert.Equal("#1C2E22", dark.DiffAddBg);
        Assert.Equal("#2A1E1F", dark.DiffRemoveBg);
        Assert.Equal("#E3F4E8", light.DiffAddBg);
        Assert.Equal("#FBE9E9", light.DiffRemoveBg);
    }

    [Fact]
    public void For_ReturnsDistinctPalettes()
    {
        Assert.NotEqual(ThemePalette.For(isDark: true), ThemePalette.For(isDark: false));
        Assert.Equal("#1B1B1F", ThemePalette.For(isDark: true).WindowBg);
        Assert.Equal("#F5F5F7", ThemePalette.For(isDark: false).WindowBg);
    }

    // Every colour string on the record must differ between dark and light — catches a forgotten
    // variant (same hex in both) without enumerating each property by hand.
    [Fact]
    public void EveryColour_DiffersBetweenDarkAndLight()
    {
        var dark  = ThemePalette.For(isDark: true);
        var light = ThemePalette.For(isDark: false);

        // OnAccent is the one colour both themes share on purpose: white reads on either accent.
        var stringProps = typeof(ThemePalette)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.Name != nameof(ThemePalette.OnAccent));

        foreach (var prop in stringProps)
        {
            var d = (string)prop.GetValue(dark)!;
            var l = (string)prop.GetValue(light)!;
            Assert.True(d != l, $"Colour '{prop.Name}' is identical in dark and light ('{d}') — likely a missing variant.");
        }
    }
}
