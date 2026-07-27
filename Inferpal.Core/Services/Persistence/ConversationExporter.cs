using System.Text;

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
