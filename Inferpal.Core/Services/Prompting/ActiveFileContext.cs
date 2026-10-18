namespace Inferpal.Services.Prompting;

/// <summary>
/// The active-file block a question carries: what follows the file the user has open — the persona of its language,
/// the instructions of its folders and globs, the rules its globs select. One composer for both front-ends — the
/// Visual Studio view model and the host.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Not in the system message (<see cref="PromptPlacement"/>): a server that reuses the cache of the request's prefix
/// would read the whole conversation again each time the user opens a file of another language or folder. The block
/// rides with the question, before <c>## User request</c>, like the workspace block.
/// </para>
/// <para>
/// ⚠ Sent only when it differs from the last one the history carries, and that is read from the history itself
/// (<see cref="LastIn"/>), never from a flag: Regenerate takes back the question that carried it, compaction can drop
/// it, a loaded or branched conversation has another — in each case the next question sends it again because the
/// history no longer holds it.
/// </para>
/// <para>Written for the model: English, not localized, like the other injected headings.</para>
/// </remarks>
internal static class ActiveFileContext
{
    /// <summary>The block's heading, then <c>: &lt;file&gt;</c>.</summary>
    public const string Header = "## Active file";

    /// <summary>The block that says no file is active any more, after one was.</summary>
    public static string NoneBlock => Header + ": none\n\n" + ModelPrompts.NoActiveFile;

    /// <summary>Whether a block carries no instruction at all — no file, or a file nothing applies to.</summary>
    private static bool SaysNothing(string block) =>
        block == NoneBlock || block.EndsWith(ModelPrompts.NoFileInstructions, StringComparison.Ordinal);

    /// <summary>
    /// The block for <paramref name="activeFile"/> (as the model's tools name it: relative to the root, or absolute
    /// outside it) and its <paramref name="sections"/> — the prompt layers placed with the question.
    /// </summary>
    /// <param name="activeFile">The active file; empty = none, and no block.</param>
    /// <param name="language">Its language, shown after the name; <c>null</c> to leave it out.</param>
    /// <param name="sections">The <see cref="PromptPlacement.Question"/> layers, in prompt order.</param>
    public static string Compose(string? activeFile, string? language, IReadOnlyList<PromptSection> sections)
    {
        if (string.IsNullOrWhiteSpace(activeFile)) return string.Empty;
        var head = Header + ": " + activeFile.Replace('\\', '/') + (string.IsNullOrEmpty(language) ? "" : $" ({language})");
        var body = string.Concat(sections.Select(s => s.Content)).TrimEnd();
        // A file nothing applies to: a short block, sent only after one that said something (ToSend).
        return body.Length == 0
            ? head + "\n\n" + ModelPrompts.NoFileInstructions
            : head + "\n\n" + ModelPrompts.ActiveFileIntro + body;
    }

    /// <summary>The last active-file block a question of <paramref name="history"/> carries; <c>null</c> = none.</summary>
    public static string? LastIn(IEnumerable<Models.ChatMessageDto> history)
    {
        foreach (var message in history.Reverse())
            if (message.Role == "user" && message.Content is { } content && Find(content) is { } found)
                return content.Substring(found.Start, found.Length);
        return null;
    }

    private static readonly string RequestMarker = "\n\n" + Agent.ChatTurnPolicy.UserRequestHeader + "\n\n";

    /// <summary>Where the active-file block of one question is: from its heading to <c>## User request</c>.</summary>
    /// <remarks>⚠ Read in the injected part only, before <c>## User request</c>: the request itself — what the user
    /// typed, a file they attached — may quote a heading like this one.</remarks>
    private static (int Start, int Length)? Find(string content)
    {
        var end = content.IndexOf(RequestMarker, StringComparison.Ordinal);
        if (end < 0) return null;
        var start = content.StartsWith(Header, StringComparison.Ordinal)
            ? 0
            : content.LastIndexOf("\n\n" + Header, end, StringComparison.Ordinal) is var at and >= 0 ? at + 2 : -1;
        return start < 0 ? null : (start, end - start);
    }

    /// <summary>
    /// What the next question carries: <paramref name="block"/> when the history's last block is another one, the
    /// <see cref="NoneBlock"/> when the file was closed after a block, <c>null</c> when the history already says it.
    /// </summary>
    /// <remarks>
    /// ⚠ A block that carries no instruction is still the NAME of the file open: after one for <c>B.md</c>, the one for
    /// <c>C.md</c> goes too, or the model keeps <c>B.md</c> as the file the user has open ("explain this file"). Before
    /// any block, such a block says nothing the model lacks, and is not sent: its text refers to an earlier block.
    /// </remarks>
    public static string? ToSend(string block, IEnumerable<Models.ChatMessageDto> history)
    {
        var last = LastIn(history);
        if (string.IsNullOrEmpty(block)) return last is null || last == NoneBlock ? null : NoneBlock;
        if (SaysNothing(block) && last is null) return null;
        return block == last ? null : block;
    }

    /// <summary>What a request built on a FRESH history carries (a background task, a fix run): <see cref="ToSend"/>
    /// with no earlier block — never the raw block, whose "no instruction applies" text refers to an earlier one.</summary>
    public static string? ForFreshHistory(string? block) =>
        string.IsNullOrEmpty(block) ? null : ToSend(block, Array.Empty<Models.ChatMessageDto>());

    /// <summary>
    /// Once the turn is measured and compacted: the question being asked — the history's last <c>user</c> message —
    /// carries <paramref name="block"/> as built now. <c>true</c> when it was changed.
    /// </summary>
    /// <remarks>
    /// ⚠ The block was judged before both. Unchanged since an earlier question, it was not sent again, and the
    /// compaction may have dropped that question: this one would go without the active file's instructions — it gets
    /// the block back. Sent with this question, it was budgeted against the window known then: a window measured
    /// smaller since would make the request overflow — it is built again.
    /// </remarks>
    public static bool Restore(List<Models.ChatMessageDto> history, string block)
    {
        var at = history.FindLastIndex(m => m.Role == "user");
        if (at < 0 || history[at].Content is not { } content) return false;
        if (Find(content) is { } carried)
        {
            if (string.IsNullOrEmpty(block) || content.Substring(carried.Start, carried.Length) == block) return false;
            history[at] = history[at] with { Content = content[..carried.Start] + block + content[(carried.Start + carried.Length)..] };
            return true;
        }
        if (ToSend(block, history) is not { } missing) return false;
        var request = content.IndexOf(RequestMarker, StringComparison.Ordinal);
        history[at] = history[at] with
        {
            Content = request < 0
                ? missing + RequestMarker + content
                : content[..request] + "\n\n" + missing + content[request..],
        };
        return true;
    }
}
