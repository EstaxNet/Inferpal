using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// Reading a tool call's arguments without trusting their shape.
/// </summary>
/// <remarks>
/// <para>
/// <b>The arguments are written by the model, not by a caller.</b> A schema says <c>top_k</c> is an
/// integer and <c>query</c> is required; a 7B model sends <c>"top_k": "5"</c> and omits
/// <c>query</c> anyway. <see cref="JsonElement.GetInt32"/> then throws
/// <see cref="InvalidOperationException"/> and <see cref="JsonElement.GetProperty(string)"/> throws
/// <see cref="KeyNotFoundException"/> — a malformed call becomes an exception out of the tool
/// instead of a sentence the model can read and correct.
/// </para>
/// <para>
/// The review of 2026-08-07 found exactly that in <c>search_codebase</c> and <c>search_docs</c>,
/// with the same two lines copied between them. These helpers exist so the safe read is also the
/// short one: everything degrades to the fallback, nothing throws.
/// </para>
/// </remarks>
internal static class ToolArgs
{
    /// <summary>A string argument, or <c>null</c> when absent, null, or not a string.</summary>
    public static string? Str(this JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>A trimmed string argument, or <c>null</c> when absent or blank.</summary>
    public static string? Trimmed(this JsonElement args, string name) =>
        args.Str(name)?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>A trimmed, lower-cased argument — the shape every <c>action</c>/<c>mode</c> uses.</summary>
    /// <remarks>
    /// <para>
    /// <b>This is the only reader of an argument the code compares against a fixed set of values</b>
    /// (<c>action</c>, <c>mode</c>, <c>direction</c>, <c>occurrence</c>, <c>runner</c>,
    /// <c>bridges</c>). Until 2026-09-09 the sentence above was an aspiration: nine sites each
    /// decided normalisation for themselves, and this helper had exactly one caller — its own unit
    /// test. Four sites trimmed and lower-cased, one lower-cased only, and three did neither.
    /// </para>
    /// <para>
    /// What that cost is not a thrown call but a <i>wrong answer with no error</i>:
    /// <c>direction: "Callers"</c> rendered a call-graph report with neither section,
    /// <c>bridges: " all"</c> scanned the whole workspace to report no cross-language bridges, and
    /// <c>mode: "Replace"</c> <b>appended</b> to the memory file instead of overwriting it. A model
    /// cannot detect any of the three. Locked by rule 22 of <c>ConventionCoverageTests</c>.
    /// </para>
    /// </remarks>
    public static string? Keyword(this JsonElement args, string name) =>
        args.Trimmed(name)?.ToLowerInvariant();

    /// <summary>An integer argument, tolerating the string form models keep sending.</summary>
    /// <remarks>
    /// <c>"top_k": "5"</c> is not valid per the schema and is common in practice. Accepting it costs
    /// nothing and turns a thrown tool call into a working one.
    /// </remarks>
    public static int Int(this JsonElement args, string name, int fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
            return fallback;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
            _ => fallback,
        };
    }

    /// <summary>A boolean argument, tolerating <c>"true"</c>/<c>"false"</c> as strings.</summary>
    public static bool Bool(this JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v))
            return fallback;

        return v.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
            _ => fallback,
        };
    }
}
