using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Execution;

/// <summary>
/// Wraps an <see cref="IToolRegistry"/> and keeps only the tools an agent uses most, for a window too small for every
/// definition: at 8 192 tokens the built-in tools alone fill the window, and every request would be refused before the
/// question is read.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ When: the definitions the turn would send take more than half the window in use (<see cref="Cuts"/>) — a property, so
/// a mode that already sends few tools (plan, a custom mode) is left as it is, and a larger window sends everything.
/// </para>
/// <para>
/// What stays (<see cref="Kept"/>) is what agents call most — reading, searching, editing, running commands and tests,
/// renaming — and with only those a small model keeps its results; plus every tool the conversation's QUESTIONS named
/// (<see cref="NamedIn"/>: <c>/debug</c>'s loop names <c>debug_control</c>, a person can ask for <c>view_page</c>), kept
/// until the conversation is replaced so that Continue and a follow-up still have them. ⚠ The questions as asked, never
/// the message the model receives: the active file, the auto-context and the attachments quote code, and code names
/// tools — read there, nothing would be cut. A call to a tool left out, native or written as text, is refused with the
/// reason, never run.
/// </para>
/// <para>
/// Only built-in tools are cut (<c>ModeTools.IsBuiltIn</c>, which every registered built-in tool must answer —
/// <c>ChatModesTests</c>): what the person added (MCP servers, their shell tools) stays, and so does <c>tool_search</c>,
/// the way to the MCP tools waiting on demand.
/// </para>
/// </remarks>
/// <param name="inner">The registry the turn would send.</param>
/// <param name="window">The window it is cut for, said in the refusal.</param>
/// <param name="named">The tools the conversation's questions named (<see cref="NamedIn"/>): they stay.</param>
internal sealed class SmallWindowToolRegistry(IToolRegistry inner, int window, IReadOnlyCollection<string>? named = null)
    : IToolRegistry
{
    /// <summary>The built-in tools a small window keeps.</summary>
    internal static readonly IReadOnlySet<string> Kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "read_file", "search_in_files", "run_command", "list_files", "run_tests", "apply_diff", "write_file",
        "apply_edits", "rename_symbol", "search_codebase", "get_diagnostics",
        // Offered only when skills exist, and the one way to a skill outside the workspace (read_file refuses it).
        "read_skill_file",
        // Offered only where a card can be drawn: without it the agent guesses instead of asking.
        "ask_question",
    };

    /// <summary>Whether <paramref name="toolTokens"/> of definitions leave too little of <paramref name="window"/>: more
    /// than half of it. A window not known yet (0) cuts nothing.</summary>
    internal static bool Cuts(int window, int toolTokens) => window > 0 && toolTokens * 2L > window;

    /// <summary>The registry a request sends at <paramref name="window"/>: <paramref name="tools"/>, or these tools cut
    /// when every definition would take more than half of it; a registry it would cut nothing from (one already cut) comes
    /// back as it is.</summary>
    internal static IToolRegistry For(IToolRegistry tools, int window, IReadOnlyCollection<string>? named)
    {
        if (!Cuts(window, RequestSize.ToolTokens(tools.Definitions))) return tools;
        var cut = new SmallWindowToolRegistry(tools, window, named);
        return cut.Definitions.Count < tools.Definitions.Count ? cut : tools;
    }

    /// <summary>The tool <paramref name="names"/> that <paramref name="asked"/> names as whole words, in any case — a
    /// question as typed, or a command's own prompt, never the message the model receives. ⚠ Judged on every built-in
    /// name, not on the tools offered now: <c>interact_window</c> is offered once the app it acts on runs, mid-run, and a
    /// question that named it before must still have it then.</summary>
    internal static IEnumerable<string> NamedIn(string? asked, IEnumerable<string> names) =>
        string.IsNullOrEmpty(asked) ? [] : names.Where(n => NamesWord(asked, n));

    /// <summary>
    /// The registry a question passes to its run, cut or not, marked as decided with its plan
    /// (<c>ContextManager.PrepareAsync</c>): the agent loops send it as it is (<see cref="IsDecided"/>). ⚠ Deciding again
    /// there, with no question to read, would take out the tools the question named, on a window measured a second time.
    /// The mark is on a fresh wrapper that forwards everything, never on <paramref name="tools"/>: uncut, that may be the
    /// session's own registry, and a mark on it would hold for every command after; nor on a flow: a run started from the
    /// question's flow with another registry (a queued <c>/task</c>) decides its own.
    /// </summary>
    internal static IToolRegistry MarkDecided(IToolRegistry tools) => new DecidedRegistry(tools);

    /// <summary>A question's registry as it is, under the mark <see cref="MarkDecided"/> puts on it.</summary>
    private sealed class DecidedRegistry(IToolRegistry inner) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => inner.Definitions;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) => inner.ExecuteAsync(name, args, ct);
        public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();
        public int? WritesInRun => inner.WritesInRun;
        public Task<string?> RunDiffAsync(int budget, CancellationToken ct) => inner.RunDiffAsync(budget, ct);
        public int? UnseenCallsInRun => inner.UnseenCallsInRun;
        public int? AutoApprovals => inner.AutoApprovals;
        public void ForgetWholeRead(string path) => inner.ForgetWholeRead(path);
        public IReadOnlyList<Documents.ChatImage> TakeReadImages() => inner.TakeReadImages();
        public IEnumerable<string> CallableNames => inner.CallableNames;
        public string? TurnedOffRefusal(string name) => inner.TurnedOffRefusal(name);
        public bool Knows(string name) => inner.Knows(name);
    }

    /// <summary>Whether a question decided the cut of <paramref name="tools"/> with its plan (<see cref="MarkDecided"/>).</summary>
    internal static bool IsDecided(IToolRegistry tools) => tools is DecidedRegistry;

    /// <summary>Whether a small window sends <paramref name="name"/> whatever the request: a built-in tool it keeps, or a
    /// tool that is not built in.</summary>
    internal static bool IsKept(string name) => Kept.Contains(name) || !Governance.ModeTools.IsBuiltIn(name);

    /// <summary>Whether <paramref name="text"/> holds <paramref name="name"/> not inside a longer identifier.</summary>
    private static bool NamesWord(string text, string name)
    {
        static bool Ident(char c) => char.IsLetterOrDigit(c) || c == '_';
        for (var at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0;
             at = text.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase))
            if ((at == 0 || !Ident(text[at - 1])) && (at + name.Length == text.Length || !Ident(text[at + name.Length])))
                return true;
        return false;
    }

    /// <summary>The tools of <paramref name="all"/> this registry leaves out, in their order: what the chat notice, a run's
    /// status line and <c>/tools</c> name.</summary>
    internal IReadOnlyList<string> LeftOut(IToolRegistry all)
    {
        var sent = Definitions.Select(d => d.Function.Name).ToHashSet(StringComparer.Ordinal);
        return [.. all.Definitions.Select(d => d.Function.Name).Where(n => !sent.Contains(n))];
    }

    /// <summary>Whether this request sends <paramref name="name"/>.</summary>
    private bool Sends(string name) => IsKept(name) || named?.Contains(name) == true;

    /// <summary>Whether the window took <paramref name="name"/> out: a tool the registry it wraps sends and this one does
    /// not. Any other name is that registry's to answer — plan mode's refusal, a name invented, and a tool the person
    /// turned off: its refusal names the cause they control (naming the tool in a question would not bring it back).</summary>
    private bool CutHere(string name) => !Sends(name) && inner.Definitions.Any(d => d.Function.Name == name);

    public IReadOnlyList<ToolDefinition> Definitions => inner.Definitions.Where(d => Sends(d.Function.Name)).ToList();
    public DiffInfo? ConsumeDiff() => inner.ConsumeDiff();
    public IReadOnlyList<Documents.ChatImage> TakeReadImages() => inner.TakeReadImages();
    public int? WritesInRun => inner.WritesInRun;
    public Task<string?> RunDiffAsync(int budget, CancellationToken ct) => inner.RunDiffAsync(budget, ct);
    public int? UnseenCallsInRun => inner.UnseenCallsInRun;
    public int? AutoApprovals => inner.AutoApprovals;
    public void ForgetWholeRead(string path) => inner.ForgetWholeRead(path);
    public string? TurnedOffRefusal(string name) => CutHere(name) ? null : inner.TurnedOffRefusal(name);

    /// <summary>Every name of the registry it wraps, the ones left out included: a call written as text to one of them
    /// reaches <see cref="ExecuteAsync"/> and its refusal, rather than being shown as the answer.</summary>
    public IEnumerable<string> CallableNames => inner.CallableNames;

    public bool Knows(string name) => inner.Knows(name);

    // ⚠ The window's check comes first: a name it took out never reaches the registry it wraps.
    public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
        !CutHere(name)
            ? inner.Knows(name) ? inner.ExecuteAsync(name, args, ct) : ToolRegistry.AnswerUnknownAsync(inner, name, args, Definitions, ct)
            : Task.FromResult(
                $"Error: tool '{name}' is not offered at this context window ({window} tokens): only the tools used most fit " +
                $"beside the question. Do the step with one of these instead: {string.Join(", ", Definitions.Select(d => d.Function.Name))}, " +
                $"or tell the user that this step needs '{name}'" +
                // A command's run has no question to name a tool in (named is null there).
                (named is null ? " (a larger context window brings it back)." : " (writing its name in a question, or a larger context window, brings it back).") +
                " The call was NOT executed.");
}
