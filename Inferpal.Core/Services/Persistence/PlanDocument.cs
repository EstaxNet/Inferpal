using System.Text;
using System.Text.RegularExpressions;
using Inferpal.Services.Governance;
using Inferpal.Services.Tools;

namespace Inferpal.Services.Persistence;

/// <summary>One step of a plan, as it appears in the file.</summary>
/// <param name="Number">1-based position in the plan, in file order.</param>
/// <param name="Text">The step itself, without the checkbox or any leading numbering.</param>
/// <param name="Done">Whether its box is ticked.</param>
/// <param name="LineIndex">0-based line the checkbox lives on — how a tick is applied surgically.</param>
internal sealed record PlanStep(int Number, string Text, bool Done, int LineIndex);

/// <summary>
/// A persistent plan (roadmap §17): the markdown document under <c>.inferpal/plans/</c>, its steps,
/// and the one edit the product is allowed to make to it — ticking a box.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this repairs, stated as the roadmap criterion requires.</b> Today <c>/plan</c> only
/// toggles <see cref="Execution.PlanModeToolRegistry"/>: the plan itself is an ordinary chat message.
/// It dies at the next <c>/clear</c>, does not survive a restart, and nothing anywhere links what was
/// planned to what was done — there is no plan object in the code base at all. That is a named,
/// observable defect, not a bet on a gain.
/// </para>
/// <para>
/// <b>A plan is a human document the product annotates — not a serialised object it owns.</b> This is
/// the design decision everything else follows from, and it is why <see cref="WithStepDone"/> does a
/// surgical single-line edit instead of re-rendering. A plan will be edited by hand, reviewed in a
/// diff and committed; if ticking step 3 reflowed the prose, dropped a comment or reordered a list,
/// the feature would destroy the artefact it exists to keep. Everything outside the checkbox lines
/// is preserved byte for byte.
/// </para>
/// <para>
/// <b>⚠ Security — a plan file is committed, therefore it is repo-authored input.</b> It ships with
/// any clone, exactly like <c>.inferpal/validators.json</c> did before 2026-08-01, and it is a
/// *sequence of instructions*, which is a stronger lever than anything else under <c>.inferpal/</c>.
/// The rule is the same one, written here before the code:
/// <list type="number">
///   <item><b>Only structure is parsed, never directives.</b> A plan yields a title, an ordered list
///   of steps and their done-state. There is no key, no command, no tool name and no permission this
///   parser can read — the same allow-list reflex as <see cref="ProjectProfile"/>, where an unknown
///   key is ignored rather than interpreted. Frontmatter is limited to <c>description</c>.</item>
///   <item><b>The text of a step is prose handed to the model, and nothing more.</b> It steers, like
///   a rule file already does; it grants nothing. Every action a step leads to goes through the
///   ordinary approval prompt, which stays the frontier.</item>
///   <item><b>No grouped approval, ever</b> — the §9 rule, restated. Consenting to a plan is not
///   consenting to the writes it will turn out to need. There is no "run the whole plan".</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class PlanDocument
{
    /// <summary>Raw file text, newline-normalised to <c>\n</c>.</summary>
    public string Text { get; }

    /// <summary>Title: the first level-1 heading, else the frontmatter description, else the name.</summary>
    public string Title { get; }

    /// <summary>Steps in file order.</summary>
    public IReadOnlyList<PlanStep> Steps { get; }

    private PlanDocument(string text, string title, IReadOnlyList<PlanStep> steps)
    {
        Text  = text;
        Title = title;
        Steps = steps;
    }

    /// <summary>Steps already ticked.</summary>
    public int DoneCount => Steps.Count(s => s.Done);

    /// <summary>The next unticked step, or <c>null</c> when the plan is finished.</summary>
    public PlanStep? NextStep => Steps.FirstOrDefault(s => !s.Done);

    // `- [ ] text`, `* [x] text`, `1. [ ] text` — the three shapes an editor or a model produces.
    // Indentation is captured so a nested step keeps its place when the box is ticked.
    private static readonly Regex _checkbox = new(
        @"^(?<lead>\s*(?:[-*+]|\d{1,3}[.)])\s+\[)(?<mark>[ xX])(?<tail>\]\s*)(?<text>.*)$",
        RegexOptions.Compiled, RegexBudget.Default);

    private static readonly Regex _heading = new(
        @"^#\s+(?<title>.+?)\s*$", RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>Parses a plan file. Never throws: a file with no checkbox is a plan with no step.</summary>
    /// <param name="fallbackTitle">Used when the document carries no heading and no description.</param>
    public static PlanDocument Parse(string? text, string fallbackTitle = "")
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        var (fm, _)    = RulesService.ParseFrontMatter(normalized);

        var lines = normalized.Split('\n');
        var steps = new List<PlanStep>();
        string? heading = null;

        for (var i = 0; i < lines.Length; i++)
        {
            if (heading is null && _heading.Match(lines[i]) is { Success: true } h)
                heading = h.Groups["title"].Value;

            var m = _checkbox.Match(lines[i]);
            if (!m.Success) continue;

            // Strip a "3." the author wrote inside the step: the number shown to the user is the
            // position in the file, so displaying both would let them disagree after an edit.
            var body = StripLeadingNumber(m.Groups["text"].Value.Trim());
            steps.Add(new PlanStep(
                Number:    steps.Count + 1,
                Text:      body,
                Done:      m.Groups["mark"].Value is "x" or "X",
                LineIndex: i));
        }

        var title = heading
                    ?? (fm.TryGetValue("description", out var d) && !string.IsNullOrWhiteSpace(d)
                        ? d.Trim() : null)
                    ?? fallbackTitle;

        return new PlanDocument(normalized, title, steps);
    }

    /// <summary>
    /// Returns the document text with step <paramref name="number"/> ticked or unticked. Only the
    /// single checkbox character changes; every other byte of the file is preserved.
    /// </summary>
    /// <returns><c>null</c> when the step does not exist or already has that state — nothing to write.</returns>
    public string? WithStepDone(int number, bool done)
    {
        var step = Steps.FirstOrDefault(s => s.Number == number);
        if (step is null || step.Done == done) return null;

        var lines = Text.Split('\n');
        var m     = _checkbox.Match(lines[step.LineIndex]);
        if (!m.Success) return null;   // file changed under us; the caller re-reads rather than guesses

        lines[step.LineIndex] = m.Groups["lead"].Value
                              + (done ? "x" : " ")
                              + m.Groups["tail"].Value
                              + m.Groups["text"].Value;
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Renders a fresh plan from a title and step texts — used by <c>/plan save</c> when turning the
    /// model's proposal into a file. Existing files are never re-rendered through this.
    /// </summary>
    public static string Render(string title, IEnumerable<string> steps)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(string.IsNullOrWhiteSpace(title) ? "Plan" : title.Trim()).Append("\n\n");

        var any = false;
        foreach (var raw in steps)
        {
            var step = StripLeadingNumber(StripCheckbox(raw).Trim());
            if (step.Length == 0) continue;
            sb.Append("- [ ] ").Append(step).Append('\n');
            any = true;
        }

        if (!any) sb.Append("- [ ] (no step)\n");
        return sb.ToString();
    }

    // A numbered or bulleted list item, used only when reading a plan out of a chat answer.
    private static readonly Regex _numbered = new(
        @"^\s*\d{1,3}[.)]\s+(?<text>\S.*)$", RegexOptions.Compiled, RegexBudget.Default);
    private static readonly Regex _bulleted = new(
        @"^\s*[-*+]\s+(?<text>\S.*)$", RegexOptions.Compiled, RegexBudget.Default);

    /// <summary>
    /// Reads the steps out of a model's answer, so <c>/plan save</c> can turn a proposal made in
    /// the chat into a file. Checkboxes win, then a numbered list, then bullets — the three ways a
    /// model writes a plan, in decreasing order of how sure we can be that it meant steps.
    /// </summary>
    /// <remarks>
    /// <b>Fenced code is skipped.</b> A plan proposal almost always contains a snippet, and a
    /// diff's <c>- removed line</c> or a shell block's <c>- flag</c> reads exactly like a bullet.
    /// Turning those into steps would produce a plan the user has to clean up by hand on its first
    /// save, which is the fastest way to make the feature not worth using.
    /// </remarks>
    public static IReadOnlyList<string> ExtractSteps(string? markdown)
    {
        var checkboxes = new List<string>();
        var numbered   = new List<string>();
        var bulleted   = new List<string>();
        var inFence    = false;

        foreach (var line in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; continue; }
            if (inFence) continue;

            if (_checkbox.Match(line) is { Success: true } c)
                checkboxes.Add(StripLeadingNumber(c.Groups["text"].Value.Trim()));
            else if (_numbered.Match(line) is { Success: true } n)
                numbered.Add(n.Groups["text"].Value.Trim());
            else if (_bulleted.Match(line) is { Success: true } b)
                bulleted.Add(b.Groups["text"].Value.Trim());
        }

        return checkboxes.Count > 0 ? checkboxes
             : numbered.Count   > 0 ? numbered
             : bulleted;
    }

    /// <summary>Drops an author's own "1." / "2)" prefix so numbering has a single source.</summary>
    internal static string StripLeadingNumber(string text) =>
        Regex.Replace(text, @"^\d{1,3}[.)]\s+", string.Empty, RegexOptions.None, RegexBudget.Default);

    /// <summary>Drops a checkbox the model already wrote, so rendering never doubles it.</summary>
    private static string StripCheckbox(string text) =>
        Regex.Replace(text, @"^\s*(?:[-*+]|\d{1,3}[.)])\s*\[[ xX]\]\s*", string.Empty,
                      RegexOptions.None, RegexBudget.Default);
}
