using Inferpal.Localization;
using Inferpal.Services.Execution;

namespace Inferpal.Services.Commands;

/// <summary>
/// <c>/run-copy</c>, as every editor decides it: the question as typed, the copy a gesture reaches (the one held, one an
/// editor that closed left, or a new one), and what is said when there is none.
/// </summary>
internal static class RunCopyCommandHandler
{
    /// <summary>The command's usage, as its refusals name it.</summary>
    internal const string Usage = "/run-copy <question>";

    /// <summary>The question after the command, exactly as typed (its line breaks and indentation kept); <c>""</c> when
    /// the command came alone.</summary>
    public static string Question(string typed)
    {
        var text = typed.Trim();
        var at = text.AsSpan().IndexOfAny([' ', '\t', '\r', '\n']);
        return at > 0 ? text[at..].Trim() : "";
    }

    /// <summary>A question asked on a copy: its bubble keeps the command, so asking it again goes through the start card.</summary>
    public static bool IsCopyQuestion(string text) =>
        text.Length > "/run-copy".Length && text.StartsWith("/run-copy", StringComparison.OrdinalIgnoreCase)
        && char.IsWhiteSpace(text["/run-copy".Length]);

    /// <summary>What <c>/run-copy</c> alone says when no copy is held or left.</summary>
    public static string NoneOpen => Strings.CopyNoneOpen + " " + Strings.SlashUsage(Usage);

    /// <summary>The copy a question or a review reaches: the one <paramref name="held"/>, else one an editor that closed
    /// left in <paramref name="workspace"/>'s repository (taken up by this process, for <paramref name="editor"/>); <c>null</c>
    /// when none.</summary>
    public static async Task<CopyRun?> HeldOrLeftAsync(CopyRun? held, string workspace, CancellationToken ct, string editor = "") =>
        held is { Discarded: false } ? held : await CopyRun.FindLeftAsync(workspace, ct, editor);

    /// <summary>The copy a question goes to: the one <paramref name="held"/>, else a new one at the last commit, held for
    /// <paramref name="editor"/>; the refusal, translated, when none can be made.</summary>
    public static async Task<(CopyRun? Copy, string? Refusal)> HeldOrNewAsync(CopyRun? held, string workspace, CancellationToken ct,
                                                                             string editor = "")
    {
        if (held is { Discarded: false }) return (held, null);
        var (made, refusal) = await CopyRun.StartAsync(workspace, ct, editor);
        return made is null ? (null, Strings.CopyNotMade(refusal ?? "git gave no reason")) : (made, null);
    }
}
