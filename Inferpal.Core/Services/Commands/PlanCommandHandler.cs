using System.Text;
using Inferpal.Localization;
using Inferpal.Services.Persistence;

namespace Inferpal.Services.Commands;

/// <summary>
/// Pure logic of <c>/plan</c> — plan mode, and the persistent plans that outlive the conversation
/// (roadmap §17).
/// </summary>
/// <remarks>
/// <para>
/// Bare <c>/plan</c> keeps toggling read-only plan mode, as it always has; every other form works on
/// a file under <c>.inferpal/plans/</c>. Both live here rather than half in each front-end, because
/// the set of sub-commands is exactly the kind of table that drifts when it is written twice — that
/// drift has already been paid for once (283 + 141 duplicated lines, 2026-07-31).
/// </para>
/// <para>
/// The handler decides and reports; it never toggles anything itself. Plan mode is front-end state
/// (a VM property in Visual Studio, a session flag in the host), and so is the active plan, so both
/// come back as fields of the result for the caller to apply.
/// </para>
/// <para>
/// ⚠ Nothing here executes a step. Reading a plan hands its text to a human, and any action that
/// follows goes through the ordinary approval prompt — see <see cref="PlanDocument"/> for why that
/// matters for a file that ships with every clone.
/// </para>
/// </remarks>
internal static class PlanCommandHandler
{
    /// <param name="Message">Markdown to display.</param>
    /// <param name="ToggleMode">Bare <c>/plan</c>: flip read-only plan mode and rebuild the prompt.</param>
    /// <param name="SetActivePlan">
    /// New active plan name, or the empty string to clear it. <c>null</c> leaves it untouched —
    /// distinct from "clear", which is why this is not just a nullable name.
    /// </param>
    /// <param name="OpenPath">A file the front-end should open in the editor.</param>
    internal readonly record struct PlanCommandResult(
        string? Message,
        bool    ToggleMode    = false,
        string? SetActivePlan = null,
        string? OpenPath      = null);

    /// <summary>Sub-commands, so a plan can never be named one of them by accident.</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "list", "save", "open", "show", "done", "undone", "next", "help",
    };

    /// <param name="projectRoot">Workspace root; plans live under <c>.inferpal/plans</c>.</param>
    /// <param name="parts">Tokenised command.</param>
    /// <param name="lastAssistantText">
    /// The model's most recent answer — what <c>/plan save</c> turns into a file. The conversation
    /// is the caller's, so it is passed in rather than reached for.
    /// </param>
    /// <param name="activePlan">Plan the session is currently working on, if any.</param>
    public static PlanCommandResult Handle(
        string? projectRoot, string[] parts, string? lastAssistantText, string? activePlan)
    {
        var sub = parts.Length >= 2 ? parts[1].Trim() : string.Empty;

        // Bare `/plan` is unchanged: it toggles read-only plan mode.
        if (sub.Length == 0) return new(null, ToggleMode: true);
        if (sub.Equals("help", StringComparison.OrdinalIgnoreCase)) return new(Strings.PlanUsage);

        if (string.IsNullOrEmpty(projectRoot)) return new(Strings.SlashContextNoSln);
        var root = projectRoot!;

        return sub.ToLowerInvariant() switch
        {
            "list"            => List(root),
            "save"            => Save(root, parts, lastAssistantText),
            "open" or "show"  => Open(root, Arg(parts, 2) ?? activePlan),
            "done"            => SetDone(root, parts, activePlan, done: true),
            "undone"          => SetDone(root, parts, activePlan, done: false),
            "next"            => Next(root, Arg(parts, 2) ?? activePlan),
            // Not a sub-command: `/plan port-the-index` opens that plan directly.
            _                 => Open(root, sub),
        };
    }

    // ── Sub-commands ────────────────────────────────────────────────────────────

    private static PlanCommandResult List(string root)
    {
        var plans = PlanStore.List(root);
        if (plans.Count == 0) return new(Strings.PlanListEmpty);

        var sb = new StringBuilder(Strings.PlanListHeader).Append("\n\n");
        foreach (var p in plans)
            sb.Append("- ").Append(p.Complete ? "✅ " : "▫ ")
              .Append('`').Append(p.Name).Append("` — ").Append(p.Title)
              .Append(" (").Append(p.Done).Append('/').Append(p.Total).Append(")\n");

        return new(sb.ToString().TrimEnd());
    }

    private static PlanCommandResult Save(string root, string[] parts, string? lastAssistantText)
    {
        var steps = PlanDocument.ExtractSteps(lastAssistantText);
        if (steps.Count == 0) return new(Strings.PlanNoSteps);

        // `/plan save port the index` — everything after `save` is the name, so a title with
        // spaces does not have to be quoted.
        var rawName = string.Join(' ', parts.Skip(2)).Trim();
        var title   = rawName.Length > 0 ? rawName : FirstHeading(lastAssistantText) ?? "Plan";
        var name    = PlanStore.SanitizeName(title);

        var path = PlanStore.Save(root, name, PlanDocument.Render(title, steps));

        return new(Strings.PlanSaved(name, steps.Count, path),
                   SetActivePlan: name, OpenPath: path);
    }

    private static PlanCommandResult Open(string root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(Strings.PlanNoActive);

        var doc = PlanStore.Load(root, name!);
        if (doc is null) return new(Strings.PlanNotFound(PlanStore.SanitizeName(name)));

        return new(Render(doc, PlanStore.SanitizeName(name)),
                   SetActivePlan: PlanStore.SanitizeName(name));
    }

    private static PlanCommandResult SetDone(string root, string[] parts, string? activePlan, bool done)
    {
        if (!int.TryParse(Arg(parts, 2), out var number))
            return new(Strings.PlanUsage);

        // `/plan done 3 other-plan` targets another plan without making it active first.
        var name = Arg(parts, 3) ?? activePlan;
        if (string.IsNullOrWhiteSpace(name)) return new(Strings.PlanNoActive);

        var before = PlanStore.Load(root, name!);
        if (before is null) return new(Strings.PlanNotFound(PlanStore.SanitizeName(name)));

        var after = PlanStore.SetStepDone(root, name!, number, done);
        if (after is null)
        {
            // Either the step does not exist, or it already had that state — two different
            // answers, because "already done" is not a mistake and should not read like one.
            var step = before.Steps.FirstOrDefault(s => s.Number == number);
            return new(step is null
                ? Strings.PlanStepUnknown(number, before.Steps.Count)
                : Strings.PlanStepAlready(number, done ? Strings.PlanStateDone : Strings.PlanStateTodo));
        }

        var head = done
            ? Strings.PlanStepTicked(number, after.Steps.First(s => s.Number == number).Text)
            : Strings.PlanStepUnticked(number);

        return new(head + "\n\n" + Render(after, PlanStore.SanitizeName(name)),
                   SetActivePlan: PlanStore.SanitizeName(name));
    }

    private static PlanCommandResult Next(string root, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(Strings.PlanNoActive);

        var doc = PlanStore.Load(root, name!);
        if (doc is null) return new(Strings.PlanNotFound(PlanStore.SanitizeName(name)));

        return new(doc.NextStep is { } step
                       ? Strings.PlanNextStep(step.Number, step.Text)
                       : Strings.PlanComplete(doc.Title),
                   SetActivePlan: PlanStore.SanitizeName(name));
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a plan as a checklist. Deliberately not the file's own text: the file may carry
    /// prose and notes that belong in an editor, while the chat needs the state at a glance.
    /// </summary>
    internal static string Render(PlanDocument doc, string name)
    {
        var sb = new StringBuilder();
        sb.Append(Strings.PlanHeader(doc.Title, name, doc.DoneCount, doc.Steps.Count)).Append("\n\n");

        foreach (var s in doc.Steps)
            sb.Append(s.Done ? "- [x] " : "- [ ] ")
              .Append(s.Number).Append(". ").Append(s.Text).Append('\n');

        if (doc.Steps.Count == 0) sb.Append(Strings.PlanNoStepsInFile).Append('\n');
        return sb.ToString().TrimEnd();
    }

    private static string? Arg(string[] parts, int index) =>
        parts.Length > index && !string.IsNullOrWhiteSpace(parts[index]) ? parts[index].Trim() : null;

    /// <summary>First markdown heading of the model's answer, used to name a saved plan.</summary>
    private static string? FirstHeading(string? markdown)
    {
        foreach (var line in (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.TrimStart();
            if (!t.StartsWith('#')) continue;
            var text = t.TrimStart('#').Trim();
            if (text.Length > 0) return text;
        }
        return null;
    }
}
