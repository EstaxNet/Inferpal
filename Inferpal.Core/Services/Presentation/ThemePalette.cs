namespace Inferpal.Services.Presentation;

/// <summary>
/// The colour palette for one VS theme mode (dark or light): window chrome colours, plus the
/// per-message-bubble colours. Pure data + the role→background selection, extracted from the
/// tool-window VM so the dark/light hex mapping lives in one place and is unit-testable
/// (notably: every colour has both a dark and a light variant — no missing entries).
/// </summary>
internal sealed record ThemePalette(
    string WindowBg,
    string Text,
    string SubtleText,
    string CodeBg,
    string CodeText,
    string CodeBorder,
    string Border,
    string SessionBg,
    string PanelBg,
    string InputBg,
    string InputBorder,
    string HoverBg,
    string UserBubbleBg,
    string ToolBubbleBg,
    string BubbleSubtleText,
    string BubbleToolText,
    // Suggestion-popup secondary text (mention + slash autocomplete)
    string SuggestionSubtleText,
    // Attachment chip (blue)
    string AttachChipBg,
    string AttachChipText,
    string AttachChipBorder,
    // Pinned-file chip (gold)
    string PinChipBg,
    string PinChipText,
    string PinChipBorder)
{
    private static readonly ThemePalette Dark = new(
        WindowBg:         "#1E1E1E",
        Text:             "#D4D4D4",
        SubtleText:       "#A0A0A0",
        CodeBg:           "#161616",
        CodeText:         "#CE9178",
        CodeBorder:       "#333333",
        Border:           "#3F3F46",
        SessionBg:        "#1E1E28",
        PanelBg:          "#2D2D30",
        InputBg:          "#2A2A32",
        InputBorder:      "#5A5A72",
        HoverBg:          "#3F3F46",
        UserBubbleBg:         "#1A3A5C",
        ToolBubbleBg:         "#1E1E1E",
        BubbleSubtleText:     "#808080",
        BubbleToolText:       "#9CDCFE",
        SuggestionSubtleText: "#808080",
        AttachChipBg:         "#2D3048",
        AttachChipText:       "#9CDCFE",
        AttachChipBorder:     "#4A4E7A",
        PinChipBg:            "#3A2E1A",
        PinChipText:          "#E0B050",
        PinChipBorder:        "#7A5A2A");

    private static readonly ThemePalette Light = new(
        WindowBg:         "#F5F5F5",
        Text:             "#1E1E1E",
        SubtleText:       "#606060",
        CodeBg:           "#F5F5F5",
        CodeText:         "#A31515",
        CodeBorder:       "#CCCCCC",
        Border:           "#DDDDDD",
        SessionBg:        "#F0F0F8",
        PanelBg:          "#E8E8EE",
        InputBg:          "#FAFAFA",
        InputBorder:      "#9898B8",
        HoverBg:          "#D6D6E0",
        UserBubbleBg:         "#D6EAF8",
        ToolBubbleBg:         "#EBEBEB",
        BubbleSubtleText:     "#555555",
        BubbleToolText:       "#0070C1",
        SuggestionSubtleText: "#606060",
        AttachChipBg:         "#D8E4F8",
        AttachChipText:       "#0070C1",
        AttachChipBorder:     "#9CB8DC",
        PinChipBg:            "#FBF3DC",
        PinChipText:          "#9A6E00",
        PinChipBorder:        "#E0C68A");

    /// <summary>The palette for the active VS theme mode.</summary>
    public static ThemePalette For(bool isDark) => isDark ? Dark : Light;

    /// <summary>Background for a message bubble by role ("user"/"tool"; anything else is transparent).</summary>
    public string BubbleBackground(string? role) => role switch
    {
        "user" => UserBubbleBg,
        "tool" => ToolBubbleBg,
        _      => "Transparent",
    };
}
