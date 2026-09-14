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
            sb.AppendLine(Strings.ExportTitle);
            sb.AppendLine(date);
            sb.AppendLine($"{Strings.ExportModel}: {modelName}  |  {Strings.ExportTurns}: {turns}  |  {Strings.ExportToolCalls}: {toolCalls}  |  {Strings.ExportTokens}: {sessionTokens:N0}  |  {Strings.ExportDuration}: {durationStr}");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                var header = Header(msg);
                sb.AppendLine(header);
                sb.AppendLine(new string('-', header.Length));
                sb.AppendLine(Body(msg));
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine($"# {Strings.ExportTitle}");
            sb.AppendLine($"*{date}*");
            sb.AppendLine();
            sb.AppendLine($"| {Strings.ExportStatColumn} | {Strings.ExportValueColumn} |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| {Strings.ExportModel} | `{modelName}` |");
            sb.AppendLine($"| {Strings.ExportTurns} | {turns} |");
            sb.AppendLine($"| {Strings.ExportToolCalls} | {toolCalls} |");
            sb.AppendLine($"| {Strings.ExportTokens} | {sessionTokens:N0} |");
            sb.AppendLine($"| {Strings.ExportDuration} | {durationStr} |");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var msg in messages)
            {
                sb.AppendLine($"## {Header(msg)}");
                sb.AppendLine();
                sb.AppendLine(Body(msg));
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string Header(ExportMessage msg) =>
        string.IsNullOrEmpty(msg.Timestamp) ? msg.Label : $"{msg.Label} ({msg.Timestamp})";

    // The document says what the chat showed: a streamed answer keeps the model's inline reasoning in its content.
    private static string Body(ExportMessage msg) => MarkdownParser.ShownText(msg.Role, msg.Content);
}
