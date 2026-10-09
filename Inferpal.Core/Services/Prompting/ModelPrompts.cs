namespace Inferpal.Services.Prompting;

/// <summary>
/// The instructions the MODEL reads as the product's own: the base system prompt and the agent loop's scaffolding.
/// One English source, never translated.
/// </summary>
/// <remarks>
/// ⚠ Translated, a prompt is ten specifications of which only the English one is ever measured — every bench runs
/// in English — and the translations had drifted: the French system prompt said "reply in French unless the
/// project convention requires English" where the English one says "respond in the same language as the user".
/// Everything else the model reads was already English (tool descriptions, tool results, block headings). The
/// reply language is carried by one line built at run time from the interface language
/// (<c>SystemPromptBuilder.EnvironmentFacts</c>); what the HUMAN reads stays in <c>Strings</c>, translated.
/// </remarks>
internal static class ModelPrompts
{
    /// <summary>The base system prompt. The reply language is stated at run time (SystemPromptBuilder.EnvironmentFacts).</summary>
    public const string SystemPrompt =
        "You are Inferpal, a local AI developer assistant integrated in the user's code editor. You have access to tools to read/write files, list directories, search code, and run shell commands. Use these tools as much as needed before replying. Respond in the same language as the user.";

    /// <summary>Heading of the repository's own instructions to coding agents in the system prompt.</summary>
    public const string RepoInstructionsHeading = "## Repository instructions";

    /// <summary>
    /// Under <see cref="RepoInstructionsHeading"/>, once: where these instructions come from and which wins a conflict.
    /// </summary>
    /// <remarks>The order states the precedence: the closest folder last, then Inferpal's own project files after
    /// them all — written for this tool, by this team, so they win.</remarks>
    public const string RepoInstructionsIntro =
        "The repository's own instructions for coding agents follow, from its root down to the active file's folder — "
        + "the closest last, and it wins over those before it. Where they conflict with the project context, agent "
        + "memory, project notes or rules after them, those win.";

    /// <summary>
    /// In place of a Claude Code <c>!`command`</c> line of a repository command: Claude Code runs it before sending and
    /// puts its output there; Inferpal never runs a repository's command unasked, so the model is told to run it.
    /// </summary>
    public static string RepoCommandNotRun(string command) =>
        $"`{command}` (not run before this message: run it yourself to see its output)";

    /// <summary>
    /// A skill joined to a question by <c>/skill</c>: its instructions, where it lives, and the files it holds — read
    /// with <c>read_skill_file</c>, a script run with <c>run_command</c> (under the user's approval, like any command).
    /// </summary>
    public static string SkillContent(string name, string folder, string body, IReadOnlyList<string> files, int omitted)
    {
        var sb = new System.Text.StringBuilder()
            .Append("The user asks you to apply the skill \"").Append(name).AppendLine("\". Its instructions:")
            .Append("<skill_content name=\"").Append(name).AppendLine("\">")
            .AppendLine(body)
            .AppendLine("</skill_content>")
            .Append("The skill's folder: ").AppendLine(folder);
        if (files.Count > 0)
        {
            sb.AppendLine("Its files (read one with read_skill_file when the instructions point to it; run a script "
                          + "with run_command, by its full path):");
            foreach (var f in files) sb.Append("- ").AppendLine(f);
            if (omitted > 0) sb.AppendLine(SkillFilesOmitted(omitted));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Under a skill's file index cut at <c>RepoSkills.MaxIndexedFiles</c>: how many are not listed.</summary>
    public static string SkillFilesOmitted(int omitted) =>
        $"… and {omitted} more file(s) not listed here: ask read_skill_file for a path the instructions name.";

    /// <summary>A skill's description in the catalog, at most: the catalog is in every question's prompt.</summary>
    internal const int SkillDescriptionChars = 300;

    /// <summary>
    /// The skills' catalog of the automatic mode, in the system prompt: what each skill is for, and how to load one.
    /// </summary>
    /// <remarks>⚠ The wording the triggering probe measured (<c>docs/probes/skills/auto-mode.md</c>): a change is a new
    /// measurement, not an edit.</remarks>
    public static string SkillsCatalog(IReadOnlyList<(string Name, string Description)> skills)
    {
        var sb = new System.Text.StringBuilder("## Skills\n\n")
            .Append("This repository provides skills: instructions for specific tasks. When the user's request matches a skill's ")
            .Append("description, first load it with read_skill_file(skill=\"<name>\", path=\"SKILL.md\"), then follow it. ")
            .Append("Do not load a skill that does not match the request.\n\n");
        foreach (var (name, description) in skills)
        {
            var oneLine = string.Join(" ", description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            sb.Append("- ").Append(name).Append(": ")
              .Append(oneLine.Length > SkillDescriptionChars ? oneLine[..SkillDescriptionChars] + "…" : oneLine).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>User message asking the model for a JSON plan (AgentOrchestrator.PlanPrompt appends the tool names).</summary>
    public const string AgentPlanPrompt =
        "Before using any tools, output ONLY a JSON object with the following structure (no prose, no markdown fences):\n"
        + "{\"goal\":\"one-sentence description of what you will do\",\"steps\":[{\"i\":1,\"desc\":\"what this step does\",\"tool\":\"expected_tool_name_or_null\"},{\"i\":2,\"desc\":\"...\"}]}\n"
        + "Include 1–6 steps — only as many as the request needs. Use null for \"tool\" if no specific tool is expected.\n"
        + "Before creating a file, plan to read an existing file of the same kind next to it.\n"
        + "After outputting the JSON, stop — do not call any tools yet.";

    /// <summary>Added to the plan prompt when rename_symbol is offered (AgentOrchestrator.PlanPrompt).</summary>
    public const string AgentPlanRename =
        "To rename a symbol (a method, class, property, function or variable), plan ONE rename_symbol step: it renames "
        + "every occurrence, callers and tests included — do not plan to edit each file by hand.";

    /// <summary>User message injected after the plan to start execution.</summary>
    public const string AgentExecutePlan =
        "Good. Now execute the plan step by step, starting from step 1. Do NOT write any explanatory text — immediately call the appropriate tool for step 1 right now.";

    /// <summary>OBSERVE injection between Act iterations: iteration (1-based), max, tools executed, steps left.</summary>
    public static string AgentObservePrompt(int iteration, int max, string toolNames, int remaining) =>
        string.Format("[OBSERVE — {0}/{1}] Tools executed: {2}. Remaining plan steps: {3}.\n"
        + "Immediately call the tool for the next step — no preamble, no explanation. If the goal is fully achieved, write the final answer to my most recent request without calling any more tools — do not repeat an earlier answer from this conversation.",
                      iteration, max, toolNames, remaining);

    /// <summary>OBSERVE injection once every plan step is done: re-anchors on the user's request and asks for the final answer instead of pushing a gratuitous extra tool call.</summary>
    public static string AgentObservePromptComplete(int iteration, int max, string toolNames, string task) =>
        string.Format("[OBSERVE — {0}/{1}] Tools executed: {2}. All plan steps are complete.\n"
        + "Now write the complete final answer to my most recent request, which was: \"{3}\" — directly, in full, and in my language, WITHOUT calling any more tools. Only call another tool if essential information is still missing. Do not repeat an answer given to an earlier, different question.",
                      iteration, max, toolNames, task);

    /// <summary>OBSERVE injection when an edit among the round's calls wrote nothing: the step stays open, never "the plan is complete".</summary>
    public static string AgentObservePromptEditUnchanged(int iteration, int max, string toolNames) =>
        string.Format("[OBSERVE — {0}/{1}] Tools executed: {2}. The edit changed no file: nothing was written.\n"
        + "Read the error above, correct the call — copy old_content exactly from the file — and send it again with every edit it contained: when apply_edits is refused, none of its edits is written, the correct ones included. Do not answer as if the change had been made.",
                      iteration, max, toolNames);

    /// <summary>OBSERVE injection when the plan is exhausted on an edit that was written but Smart Fix found compilation errors: fix them, never "the plan is complete".</summary>
    public static string AgentObservePromptBuildBroken(int iteration, int max, string toolNames) =>
        string.Format("[OBSERVE — {0}/{1}] Tools executed: {2}. The edit was written, but the project no longer builds: the compilation errors are listed above.\n"
        + "Fix them now with another edit — read the lines they name first if you need to. Do not answer as if the change were done while the build fails.",
                      iteration, max, toolNames);

    /// <summary>Nudge when an ACT response is a new JSON plan instead of a tool call or an answer (AgentOrchestrator.LooksLikePlanEcho).</summary>
    public const string AgentPlanEchoNudge =
        "That is a plan, not a step carried out. Do not write another plan. Call the tool for the next step now "
        + "- or, if the task is already complete, write your final answer to my request in plain prose.";

    /// <summary>One-shot nudge when the first ACT response narrated instead of calling a tool.</summary>
    public const string AgentNudgeToolCall =
        "You described your intentions but did not call any tools. Do NOT produce any text — call the first tool from your plan RIGHT NOW.";

    /// <summary>Final-synthesis prompt (no tools) when the loop ended without a printable answer but tools ran; quotes the user's current request (AgentOrchestrator.TaskSnippet).</summary>
    public static string AgentSynthesizePrompt(string task) =>
        string.Format("You have gathered enough information with the tools above. Do NOT call any more tools. Using those results, write the complete final answer to my most recent request, which was: \"{0}\" — directly, in full, and in my language. Answer THAT request specifically; do not repeat an earlier answer from this conversation.",
                      task);
}
