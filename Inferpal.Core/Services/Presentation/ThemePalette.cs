namespace Inferpal.Services.Presentation;

/// <summary>
/// The colour palette for one VS theme mode (dark or light): window chrome colours, plus the
/// per-message-bubble colours. Pure data + the role→background selection, extracted from the
/// tool-window VM so the dark/light hex mapping lives in one place and is unit-testable
/// (notably: every colour has both a dark and a light variant — no missing entries).
/// </summary>
/// <remarks>
/// The values are the redesign's tokens (ground, surface, line, chip, accent, ok, add): the chat window draws
/// them, the VS Code panel takes the same roles from its own theme.
/// </remarks>
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
    string BubbleSubtleText,
    string BubbleToolText,
    // Suggestion-popup secondary text (mention + slash autocomplete)
    string SuggestionSubtleText,
    // Attachment chip
    string AttachChipBg,
    string AttachChipText,
    string AttachChipBorder,
    // Pinned-file chip (gold)
    string PinChipBg,
    string PinChipText,
    string PinChipBorder,
    // Diff viewer (tool bubbles): classic green-background additions / red-background deletions,
    // tuned per theme so the diff stays legible on both dark and light bubbles.
    string DiffAddBg,
    string DiffAddText,
    string DiffRemoveBg,
    string DiffRemoveText,
    // A raised surface (cards, the run line, the result bar, the composer), the selected segment, the accent.
    string Surface,
    string Chip,
    string Accent,
    string AccentText,
    string AccentLine,
    // A passed check, a connected server; a failed one, an error banner.
    string Ok,
    string ErrorText,
    string ErrorBg,
    string ErrorLine,
    // A server that answered and refused (a wrong key): running, not reachable for us.
    string Warn,
    // The edge of a selected segment and of a question's bubble: their own fill elsewhere, drawn in high contrast,
    // where a fill cannot be told from the ground.
    string ChipLine,
    string UserBubbleBorder,
    // Text and glyphs on the accent (Allow once, Send).
    string OnAccent)
{
    private static readonly ThemePalette Dark = new(
        WindowBg:         "#1B1B1F",
        Text:             "#E8E8EC",
        SubtleText:       "#A3A3AD",
        CodeBg:           "#18181C",
        CodeText:         "#E8E8EC",
        CodeBorder:       "#34343C",
        Border:           "#34343C",
        SessionBg:        "#232328",
        PanelBg:          "#232328",
        InputBg:          "#232328",
        InputBorder:      "#4B3D80",
        HoverBg:          "#2E2B3D",
        UserBubbleBg:         "#2F2A4A",
        BubbleSubtleText:     "#A3A3AD",
        BubbleToolText:       "#C9C9D1",
        SuggestionSubtleText: "#A3A3AD",
        AttachChipBg:         "#2E2B3D",
        AttachChipText:       "#E8E8EC",
        AttachChipBorder:     "#3D3858",
        PinChipBg:            "#3A2E1A",
        PinChipText:          "#E0B050",
        PinChipBorder:        "#7A5A2A",
        DiffAddBg:            "#1C2E22",
        DiffAddText:          "#6FD08F",
        DiffRemoveBg:         "#2A1E1F",
        DiffRemoveText:       "#F2B8B5",
        Surface:              "#232328",
        Chip:                 "#2E2B3D",
        Accent:               "#7C4DFF",
        AccentText:           "#B39DFF",
        AccentLine:           "#4B3D80",
        Ok:                   "#4CC27A",
        ErrorText:            "#F2B8B5",
        ErrorBg:              "#2A1E1F",
        ErrorLine:            "#5C3030",
        Warn:                 "#E2B341",
        ChipLine:             "#2E2B3D",
        UserBubbleBorder:     "#2F2A4A",
        OnAccent:             "#FFFFFF");

    private static readonly ThemePalette Light = new(
        WindowBg:         "#F5F5F7",
        Text:             "#1F1F1F",
        SubtleText:       "#5C5C66",
        CodeBg:           "#F1F1F4",
        CodeText:         "#1F1F1F",
        CodeBorder:       "#DADAE0",
        Border:           "#DADAE0",
        SessionBg:        "#FFFFFF",
        PanelBg:          "#FFFFFF",
        InputBg:          "#FFFFFF",
        InputBorder:      "#B9A7F5",
        HoverBg:          "#ECEAF5",
        UserBubbleBg:         "#E8E3FF",
        BubbleSubtleText:     "#5C5C66",
        BubbleToolText:       "#3B3B44",
        SuggestionSubtleText: "#5C5C66",
        AttachChipBg:         "#ECEAF5",
        AttachChipText:       "#1F1F1F",
        AttachChipBorder:     "#D3CCEB",
        PinChipBg:            "#FBF3DC",
        PinChipText:          "#9A6E00",
        PinChipBorder:        "#E0C68A",
        DiffAddBg:            "#E3F4E8",
        DiffAddText:          "#1E7F3C",
        DiffRemoveBg:         "#FBE9E9",
        DiffRemoveText:       "#B3261E",
        Surface:              "#FFFFFF",
        Chip:                 "#ECEAF5",
        Accent:               "#6A3DE8",
        AccentText:           "#5A2FD6",
        AccentLine:           "#B9A7F5",
        Ok:                   "#1E7F3C",
        ErrorText:            "#B3261E",
        ErrorBg:              "#FBE9E9",
        ErrorLine:            "#F0B4B4",
        Warn:                 "#8A6D00",
        ChipLine:             "#ECEAF5",
        UserBubbleBorder:     "#E8E3FF",
        OnAccent:             "#FFFFFF");

    /// <summary>
    /// Windows' high contrast: black ground, white text and lines, yellow for what is selected or acted on — no tint
    /// a reader with low vision has to tell from the ground, and every shape drawn by a line.
    /// </summary>
    private static readonly ThemePalette HighContrast = new(
        WindowBg:         "#000000",
        Text:             "#FFFFFF",
        SubtleText:       "#FFFFFF",
        CodeBg:           "#000000",
        CodeText:         "#FFFFFF",
        CodeBorder:       "#FFFFFF",
        Border:           "#FFFFFF",
        SessionBg:        "#000000",
        PanelBg:          "#000000",
        InputBg:          "#000000",
        InputBorder:      "#FFFF00",
        HoverBg:          "#3A3A3A",
        UserBubbleBg:         "#000000",
        BubbleSubtleText:     "#FFFFFF",
        BubbleToolText:       "#FFFFFF",
        SuggestionSubtleText: "#FFFFFF",
        AttachChipBg:         "#000000",
        AttachChipText:       "#FFFFFF",
        AttachChipBorder:     "#FFFFFF",
        PinChipBg:            "#000000",
        PinChipText:          "#FFFF00",
        PinChipBorder:        "#FFFF00",
        DiffAddBg:            "#000000",
        DiffAddText:          "#3FF23F",
        DiffRemoveBg:         "#000000",
        DiffRemoveText:       "#FF8080",
        Surface:              "#000000",
        Chip:                 "#000000",
        Accent:               "#FFFF00",
        AccentText:           "#FFFF00",
        AccentLine:           "#FFFF00",
        Ok:                   "#3FF23F",
        ErrorText:            "#FF8080",
        ErrorBg:              "#000000",
        ErrorLine:            "#FF8080",
        Warn:                 "#FFFF00",
        ChipLine:             "#FFFF00",
        UserBubbleBorder:     "#FFFFFF",
        OnAccent:             "#000000");

    /// <summary>The palette for the active VS theme mode; Windows' high contrast wins over both.</summary>
    public static ThemePalette For(bool isDark, bool highContrast = false) =>
        highContrast ? HighContrast : isDark ? Dark : Light;

    /// <summary>The palette whose code ground is <paramref name="codeBg"/> — how an item that holds only its colours
    /// knows its theme.</summary>
    public static ThemePalette WithCodeBg(string codeBg) =>
        codeBg == Dark.CodeBg ? Dark : codeBg == HighContrast.CodeBg ? HighContrast : Light;

    /// <summary>Background for a message bubble by role: only a question sits in a bubble; an answer, its steps and
    /// its notices are drawn on the window's ground.</summary>
    public string BubbleBackground(string? role) => role switch
    {
        "user" => UserBubbleBg,
        _      => "Transparent",
    };
}
