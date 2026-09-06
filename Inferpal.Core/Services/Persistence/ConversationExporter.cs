using System.Text;
using Inferpal.Localization;

namespace Inferpal.Services.Persistence;

/// <summary>A chat bubble flattened for export (role filtered upstream by the VM).</summary>
internal sealed record ExportMessage(string Role, string Label, string Content, string Timestamp);

/// <summary>
/// Pure document formatting for "Export Conversation" extracted from the tool-window
/// VM: the plain-text and markdown renderings share the same stats header (model,
/// turns, tool calls, tokens, duration) and per-message layout. The VM keeps the
/// snapshot capture, the save dialog, and the file write.
/// </summary>
internal static class ConversationExporter
{
    /// <summary>
    /// A turn's label, for the bubble as well as for the export: "You", the model name (or
    /// "Assistant" when there is none), or <c>🔧 tool-name</c>.
    /// </summary>
    /// <remarks>
    /// ⚠ It lives here because it lived in TWO places, and was wrong in both. On the Visual Studio
    /// side, <c>ChatMessageItem.UserMsg</c> wrote <c>Label = "Vous"</c> — hardcoded, in French —
    /// and that label goes into the exported document: a Japanese reader got "Vous" heading every
    /// one of their turns. On the VS Code side, the TypeScript exporter wrote its own emoji labels
    /// and never went through a resource at all. One possible caller now, and it is translated in
    /// all ten languages.
    /// </remarks>
    /// <param name="role">"user", "assistant" or "tool".</param>
    /// <param name="name">
    /// The model name for an assistant turn, the tool name for a tool turn. Empty for a user turn,
    /// and ignored there.
    /// </param>
    public static string RoleLabel(string role, string? name = null) => role switch
    {
        "user"      => Strings.ChatRoleYou,
        "tool"      => $"🔧 {name}",
        "assistant" => string.IsNullOrEmpty(name) ? Strings.ChatRoleAssistant : name!,
        _           => string.Empty,
    };

    /// <summary>"12m 34s" — or "—" when the session start was never recorded.</summary>
    public static string FormatDuration(TimeSpan? duration) =>
        duration.HasValue
            ? $"{(int)duration.Value.TotalMinutes}m {duration.Value.Seconds}s"
            : "—";

    /// <summary>
    /// Renders the full export document. <paramref name="asPlainText"/> picks the .txt
    /// layout (underlined headers) over the markdown one (stats table, "##" headers,
    /// "---" separators).
    /// </summary>
    public static string Build(
        IReadOnlyList<ExportMessage> messages,
        bool asPlainText,
        string modelName,
        int sessionTokens,
        string date,
        string durationStr)
    {
        var turns     = messages.Count(m => m.Role == "user");
        var toolCalls = messages.Count(m => m.Role == "tool");
        var sb        = new StringBuilder();

        if (asPlainText)
        {
            sb.AppendLine("Inferpal — Conversation Export");
            sb.AppendLine(date);
            sb.AppendLine($"Model: {modelName}  |  Turns: {turns}  |  Tool calls: {toolCalls}  |  Tokens: {sessionTokens:N0}  |  Duration: {durationStr}");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                var header = Header(msg);
                sb.AppendLine(header);
                sb.AppendLine(new string('-', header.Length));
                sb.AppendLine(msg.Content);
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("# Inferpal — Conversation Export");
            sb.AppendLine($"*{date}*");
            sb.AppendLine();
            sb.AppendLine("| Stat | Value |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Model | `{modelName}` |");
            sb.AppendLine($"| Turns | {turns} |");
            sb.AppendLine($"| Tool calls | {toolCalls} |");
            sb.AppendLine($"| Tokens | {sessionTokens:N0} |");
            sb.AppendLine($"| Duration | {durationStr} |");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                sb.AppendLine($"## {Header(msg)}");
                sb.AppendLine();
                sb.AppendLine(msg.Content);
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Header(ExportMessage msg) =>
        string.IsNullOrEmpty(msg.Timestamp) ? msg.Label : $"{msg.Label} ({msg.Timestamp})";
}
