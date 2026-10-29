namespace Inferpal.Services.Debugging;

/// <summary>
/// A debug session that says why the debugger refused an evaluation — a syntax error, evaluation turned off after a
/// timeout, the program no longer paused.
/// </summary>
/// <remarks>⚠ Without its words the tool can only guess "unknown symbol": after one evaluation timed out, every next one
/// was refused, and the model kept rewriting its expression when the remedy was to continue. Apart from
/// <see cref="IDebugSession"/>, which the in-process side shares as net472 source, where an interface has no default
/// members.</remarks>
internal interface IDebugRefusals
{
    /// <summary>The value, or the debugger's own words for why there is none (<c>Refusal</c>, null when it gave none).</summary>
    Task<(string? Value, string? Refusal)> EvaluateWithCauseAsync(string expression, int? frameId, CancellationToken ct);
}
