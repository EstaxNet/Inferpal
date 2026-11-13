using Inferpal.ToolWindow;
using Xunit;

namespace Inferpal.Tests;

// Regression guard for the light-theme readability bug: VS 2026 unified settings deliver the
// color theme as a plain word ("dark"/"light"/"blue"), not the legacy per-theme GUID. The old
// detection only recognized the legacy Light/Blue GUIDs, so every VS 2026 light theme was
// classified as dark and rendered light-on-light (unreadable). These cases pin the word format
// and the legacy GUIDs; unknown values fall back to the OS theme (not asserted here).
public class VsThemeDetectorTests
{
    [Theory]
    [InlineData("dark")]
    [InlineData("Dark")]
    [InlineData("1ded0138-47ce-435e-84ef-9ec1f439b749")]                 // legacy dark GUID
    [InlineData("{1ded0138-47ce-435e-84ef-9ec1f439b749}")]
    public void IsDark_True_ForDarkThemes(string value) =>
        Assert.True(VsThemeDetector.IsDark(value));

    [Theory]
    [InlineData("light")]
    [InlineData("Light")]
    [InlineData("blue")]
    [InlineData("de3dbbcd-f642-433c-8353-8f1df4370aba")]                 // legacy light GUID
    [InlineData("a4d9300f-a12c-4592-9606-be6f4e1a22ca")]                 // legacy blue GUID
    public void IsDark_False_ForLightThemes(string value) =>
        Assert.False(VsThemeDetector.IsDark(value));
}
