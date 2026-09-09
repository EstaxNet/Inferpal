using System.IO;

namespace Inferpal.Services.Execution;

/// <summary>
/// The files a write turns into the agent's <b>own future instructions</b>.
/// </summary>
/// <remarks>
/// <para>
/// <c>SystemPromptBuilder</c> injects <c>.inferpal/context.md</c>, <c>memory.md</c>, <c>notes.md</c>
/// and <c>rules/*.md</c> into the system prompt of <b>every later session</b>. Writing one of them
/// is therefore not an ordinary edit: it is the <i>persistence</i> half of a prompt-injection chain,
/// where the content can come from a web page, a file the model was asked to read, or an MCP server.
/// </para>
/// <para>
/// <b>The doctrine was already written, in <c>UpdateMemoryTool</c>'s own remarks</b> — "a tool that
/// writes it unattended is a tool that lets the model edit its own future instructions, permanently,
/// with no human in the loop" — and held by nobody. Measured 2026-09-09: <b>seven</b> write paths
/// reach these files (<c>update_memory</c>, <c>write_file</c>, <c>apply_diff</c>, <c>apply_edits</c>,
/// <c>delete_file</c>, <c>restore_file</c>, and the caret writes behind <c>EditorWriteGate</c>) and
/// <b>not one</b> asked for a forced prompt. Every one of them was reachable unattended: an
/// <c>allow</c> rule, <c>SecurityAlertsDisabled</c>, or — the realistic one — a single "Always" on
/// <c>write_file</c>, the main editing tool, clicked once and good for the rest of the session.
/// </para>
/// <para>
/// The answer is a <b>forced prompt, never a refusal</b>: writing memory or a rule is a legitimate,
/// useful thing for the agent to do. What must not happen is that it happens <i>silently</i>. Same
/// shape as <c>TestFileWriteGuard</c>, which already does this for the strictly less sensitive case
/// of a test file — and the reason this lives in <c>ApprovalServiceBase</c> next to
/// <see cref="PermissionPolicy.IsOpaqueExecution"/> rather than in a decorator each call site must
/// remember: a property held at the funnel cannot be forgotten by the eighth write path.
/// </para>
/// <para>
/// ⚠ <b>Bounds, stated rather than implied.</b> This matches a <i>path</i>, so it covers the tools
/// whose approval subject is a path. It does <b>not</b> cover a shell command that writes the same
/// file — <c>run_command</c>'s subject is the raw command line, and the repository is explicit that
/// text matching is an anti-accident guard, not a security boundary. It also deliberately leaves out
/// <c>.inferpal/permissions.json</c>: the workspace overlay is <b>deny-only</b>, so a write there can
/// only ever restrict what the agent may do.
/// </para>
/// </remarks>
internal static class AgentInstructionFiles
{
    /// <summary>Directory holding everything the product reads back as instructions.</summary>
    private const string Dir = ".inferpal";

    /// <summary>The three files injected whole, by name.</summary>
    private static readonly string[] Injected = ["context.md", "memory.md", "notes.md"];

    /// <summary>Sub-directory whose <c>*.md</c> are injected, glob-scoped to the active file.</summary>
    private const string RulesDir = "rules";

    /// <summary>
    /// Does this approval subject aim at a file that becomes the system prompt? The subject may
    /// carry several paths (<c>apply_edits</c> joins them with newlines) — one is enough.
    /// </summary>
    internal static bool Targets(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return false;

        foreach (var token in subject.Split('\n', ';', ','))
        {
            // Separators are normalised BEFORE any path reading, for the reason TestFileWriteGuard
            // documents: under POSIX a backslash is an ordinary character, and the subject is
            // written by a model — or by an MCP server — so it may carry either form on either
            // platform.
            var path = token.Trim().Replace('\\', '/');
            if (path.Length == 0) continue;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length - 1; i++)
            {
                if (!segments[i].Equals(Dir, StringComparison.OrdinalIgnoreCase)) continue;

                // .inferpal/<name>
                var rest = segments[(i + 1)..];
                if (rest.Length == 1 && Injected.Contains(rest[0], StringComparer.OrdinalIgnoreCase))
                    return true;

                // .inferpal/rules/**/*.md — a rule file anywhere under the rules directory.
                if (rest.Length >= 2
                    && rest[0].Equals(RulesDir, StringComparison.OrdinalIgnoreCase)
                    && Path.GetExtension(rest[^1]).Equals(".md", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
