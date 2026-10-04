namespace Inferpal.ToolWindow;

/// <summary>
/// Resolves whether the active Visual Studio color theme is dark.
///
/// VS 2026 unified settings deliver <c>environment.visualExperience.colorTheme</c> as a plain word
/// ("dark" / "light" / "blue"), not the legacy per-theme GUID that VS 2012–2022 emitted. The old
/// detection only matched the legacy Light/Blue GUIDs, so every VS 2026 light theme fell through to
/// "dark" and rendered light-on-light text (unreadable). This handles both the new word format and
/// the legacy GUIDs, and falls back to the Windows app theme only when the value is unrecognized.
/// </summary>
internal static class VsThemeDetector
{
    /// <summary>
    /// Last theme resolved by the tool-window subscription (<c>ApplyThemeColors</c>), so
    /// transient UI created outside the VM hierarchy (the approval dialog) can pick the
    /// right palette without its own settings subscription. Initialized from the OS theme
    /// until the first VS notification lands.
    /// </summary>
    internal static volatile bool CurrentIsDark = OsDarkMode();

    // Legacy per-theme GUIDs (VS 2012–2022); still emitted by older settings stores.
    private const string LightThemeGuid = "de3dbbcd-f642-433c-8353-8f1df4370aba";
    private const string BlueThemeGuid  = "a4d9300f-a12c-4592-9606-be6f4e1a22ca";
    private const string DarkThemeGuid  = "1ded0138-47ce-435e-84ef-9ec1f439b749";

    /// <summary>
    /// Maps the raw <c>environment.visualExperience.colorTheme</c> value to a dark/light decision.
    /// Light wins first so "blue" / "light" can never be mistaken for dark; an explicit "dark" maps
    /// to dark; anything unrecognized (a custom theme id) falls back to the Windows app theme.
    /// </summary>
    internal static bool IsDark(string? colorThemeValue)
    {
        var t = colorThemeValue ?? string.Empty;

        if (t.Contains("light", StringComparison.OrdinalIgnoreCase)
         || t.Contains("blue",  StringComparison.OrdinalIgnoreCase)
         || t.Contains(LightThemeGuid, StringComparison.OrdinalIgnoreCase)
         || t.Contains(BlueThemeGuid,  StringComparison.OrdinalIgnoreCase))
            return false;

        if (t.Contains("dark", StringComparison.OrdinalIgnoreCase)
         || t.Contains(DarkThemeGuid, StringComparison.OrdinalIgnoreCase))
            return true;

        return OsDarkMode();
    }

    /// <summary>
    /// Windows' high contrast is on — Visual Studio then draws its own high-contrast theme, and the chat follows with
    /// its high-contrast palette. Read from the setting every high-contrast change writes
    /// (<c>HighContrast\Flags</c>, bit 1); unreadable reads as off.
    /// </summary>
    internal static bool HighContrastOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\HighContrast");
            return key?.GetValue("Flags") is string flags && int.TryParse(flags, out var bits) && (bits & 1) != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    /// <summary>Reads the Windows app theme as a last-resort fallback (dark when unset/unreadable).</summary>
    internal static bool OsDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return true; }
    }
}
