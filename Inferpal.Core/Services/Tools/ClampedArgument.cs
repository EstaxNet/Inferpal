using System.Text.Json;

namespace Inferpal.Services.Tools;

/// <summary>
/// A numeric argument the model asked for, brought inside the range the tool accepts — and the
/// sentence to say when that changed it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Silently clamping a value the model explicitly asked for shapes a conclusion.</b> A call
/// graph requested at depth 7 and rendered at depth 3 simply stops, and a tree that stops reads as
/// "the graph ends here" — the same failure every capped scan in this folder declares through
/// <see cref="ScanCoverage"/>. The difference is worse, not better: a scan cap is a limit of the
/// world, a clamp overrides an instruction. The lower bound bites too — <c>depth: 0</c> raised to
/// 1 turns "direct dependants only" into a transitive report, under the question that asked for
/// direct ones.
/// </para>
/// <para>
/// The notice is <b>not localized</b>, like every other text that corrects the model's own call:
/// it is addressed to whoever wrote the arguments, and the human can do nothing with it.
/// </para>
/// </remarks>
internal static class ClampedArgument
{
    /// <param name="fallback">Used when the argument is absent — never reported: nothing was asked for.</param>
    /// <returns>The value to use, and the line to put above the report, or <c>null</c> when the
    /// request was honoured exactly.</returns>
    public static (int Value, string? Notice) Read(
        JsonElement args, string name, int fallback, int min, int max)
    {
        var asked = args.Int(name, fallback);
        var used  = Math.Clamp(asked, min, max);

        // Absent, or asked for exactly what it got: nothing to say. A notice on every ordinary
        // call is the noise that gets the real ones skipped.
        if (used == asked) return (used, null);

        return (used, $"Note: '{name}' was {asked}; this tool accepts {min}-{max}, "
                    + $"so the report below uses {name}={used}.");
    }

    /// <summary>Puts the notice ABOVE the report, because it qualifies everything under it.</summary>
    /// <remarks>
    /// Same placement as the truncated-diff notice of <c>/check</c>, and for the same reason: a
    /// caveat under a result is read after the result has been believed.
    /// </remarks>
    public static string Above(string? notice, string report) =>
        notice is null ? report : notice + "\n\n" + report;
}
