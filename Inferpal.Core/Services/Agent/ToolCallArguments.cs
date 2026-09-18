using System.Text.Json;
using Inferpal.Models;

namespace Inferpal.Services.Agent;

/// <summary>
/// The one reader of the arguments a model wrote for a tool call.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>"No arguments" and "arguments I could not read" are two different answers</b>, and only the
/// first one may run. Defaulting the second to <c>{}</c> turns a truncated
/// <c>run_tests {"filter":"Foo…</c> into the whole test suite — <c>ToolArgs</c> degrades every
/// unreadable argument to its fallback, by contract, so nothing downstream can notice.
/// </para>
/// <para>
/// ⚠ It lives here, and not in the provider that first needed it, because the same model output
/// reaches the product by three doors — the OpenAI-compatible stream, the inline text parser, and
/// the wire payload deserialized straight into <see cref="ToolCallFunction.Arguments"/> — and two
/// of the three used to answer differently. <c>AgentOrchestrator.ExecuteToolSafeAsync</c> is the
/// single gate that refuses what this class marks.
/// </para>
/// </remarks>
internal static class ToolCallArguments
{
    /// <summary>Parses a call's arguments text.</summary>
    /// <remarks>Empty or <c>null</c> is a call without arguments (<c>{}</c>); an object nested in a JSON
    /// string (double-encoded) is unwrapped. ⚠ Anything else — typically a turn cut off mid-call — is
    /// kept in <see cref="ToolCallFunction.UnparsedArguments"/> and must not run.</remarks>
    internal static ToolCallFunction Parse(string name, string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length == 0 || trimmed == "null") return new ToolCallFunction(name, Empty());
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return new ToolCallFunction(name, root.Clone());
            if (root.ValueKind == JsonValueKind.String && root.GetString() is { } inner
                && inner.TrimStart().StartsWith('{'))
                return Parse(name, inner);
        }
        catch (JsonException) { /* falls through to the unparsed call */ }
        return new ToolCallFunction(name, Empty()) { UnparsedArguments = raw };
    }

    /// <summary>
    /// The same verdict for a call whose arguments arrived <b>already deserialized</b> — the shape
    /// a provider hands over on the wire, which nobody judged.
    /// </summary>
    /// <remarks>
    /// ⚠ The three kinds that mean "no arguments" stay runnable: the property was absent
    /// (<see cref="JsonValueKind.Undefined"/>), it was <c>null</c>, or it was an empty object. A
    /// string, an array or a number is a call the model failed to write — except the double-encoded
    /// object, which <see cref="Parse"/> unwraps, because the streaming path accepts it and two
    /// readers of one model must not disagree about which calls are runnable.
    /// </remarks>
    internal static ToolCallFunction Judge(ToolCallFunction call) =>
        call.UnparsedArguments is not null
        || call.Arguments.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null
            ? call
            : Parse(call.Name, call.Arguments.GetRawText());

    internal static JsonElement Empty()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}
