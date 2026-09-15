using System.Text;
using System.Text.Json;

namespace Inferpal.Services.Mcp;

/// <summary>
/// What the approval pipeline <b>matches on</b> for an MCP call: the raw argument JSON, followed by
/// its string values, one per line.
/// </summary>
/// <remarks>
/// <para>
/// Everything downstream of <c>ApprovalServiceBase</c> matches <b>text</b> — the user's permission
/// rules, <c>PermissionPolicy.IsOpaqueExecution</c> and <c>AgentInstructionFiles.Targets</c>. Raw
/// JSON hides from all three exactly what they look for: in
/// <c>{"path":"C:\\ws\\.inferpal\\memory.md"}</c> the path is not a path token — it carries a
/// trailing quote and a leading <c>{"path":"</c> — so <c>Targets</c> answered false on a write to
/// the agent's own future instructions, through the very source its own remarks name ("the content
/// can come from a web page, a file the model was asked to read, or an MCP server"). One "Always"
/// clicked on an MCP filesystem tool and that write happened with no prompt at all.
/// </para>
/// <para>
/// ⚠ <b>Additive on purpose</b>: the raw JSON stays first, so a rule someone already wrote against
/// it keeps matching exactly as before. The string values are appended, so the guards that need
/// bare values finally see them.
/// </para>
/// <para>
/// ⚠ And this remains what the repository says text matching is: an <b>anti-accident guard, not a
/// security boundary</b>. A server that encodes its path, or builds it from two arguments, walks
/// past it. The boundary is the approval prompt, where the human reads the call.
/// </para>
/// </remarks>
internal static class McpApprovalSubject
{
    /// <summary>Depth of the argument object we walk. Beyond it, only the raw JSON matches.</summary>
    private const int MaxDepth = 8;

    /// <summary>Values appended at most — a bound on what the rules' regexes have to chew.</summary>
    private const int MaxValues = 200;

    /// <summary>Longest value appended whole; a longer one is a blob, not a path or a command.</summary>
    private const int MaxValueChars = 2000;

    internal static string From(JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Undefined) return string.Empty;

        var raw = args.GetRawText();
        var sb = new StringBuilder(raw);
        var budget = MaxValues;

        Walk(args, 0, sb, ref budget);
        return sb.ToString();
    }

    private static void Walk(JsonElement node, int depth, StringBuilder sb, ref int budget)
    {
        if (depth > MaxDepth || budget <= 0) return;

        switch (node.ValueKind)
        {
            case JsonValueKind.String:
                var value = node.GetString();
                if (!string.IsNullOrEmpty(value) && value.Length <= MaxValueChars)
                {
                    sb.Append('\n').Append(value);
                    budget--;
                }
                break;

            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    Walk(property.Value, depth + 1, sb, ref budget);
                    if (budget <= 0) return;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, depth + 1, sb, ref budget);
                    if (budget <= 0) return;
                }
                break;
        }
    }
}
