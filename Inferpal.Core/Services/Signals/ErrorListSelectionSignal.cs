using System.Text.Json.Serialization;
using Inferpal.Services.CodeActions;

namespace Inferpal.Services.Signals;

/// <summary>A request for the lines chosen in Visual Studio's Error List.</summary>
internal sealed record ErrorListAsk(
    [property: JsonPropertyName("id")]  string Id,
    [property: JsonPropertyName("pid")] int    Pid,
    [property: JsonPropertyName("ts")]  long   Ts);

/// <summary>The in-process answer, correlated by <see cref="Id"/>.</summary>
/// <param name="Id">The request answered.</param>
/// <param name="Problems">The lines chosen, in the list's order; empty when none is.</param>
/// <param name="Failure">Why the list could not be read (English, for <c>/diagnostics</c> and the message's detail);
/// <c>null</c> when it was.</param>
internal sealed record ErrorListAnswer(
    [property: JsonPropertyName("id")]       string                      Id,
    [property: JsonPropertyName("problems")] IReadOnlyList<ProblemEntry> Problems,
    [property: JsonPropertyName("failure")]  string?                     Failure = null);

/// <summary>
/// File channel between the out-of-process "Fix with Inferpal" command of the Error List and the in-process half, the
/// only one that can read what the list has selected (<c>IErrorList</c>) — scoped to the declared Visual Studio instance.
/// </summary>
/// <remarks>
/// Asked when the command runs, never published as the selection changes: the list's table control exists once the
/// window does, and a subscription taken at the package's start would miss a list opened later, for the session.
/// </remarks>
internal static class ErrorListSelectionSignal
{
    internal static string RequestPath => SignalFile.ScopedPathFor("errorlist_ask");
    internal static string AnswerPath  => SignalFile.ScopedPathFor("errorlist_answer");

    /// <summary>A request older than this is not served: its command stopped waiting.</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(10);

    // ── Out of process (the command) ────────────────────────────────────────────

    /// <summary>
    /// Asks for the selection and waits up to <paramref name="timeout"/>: the answer, or <c>null</c> when none came (the
    /// in-process half is not loaded, or busy past the budget). Cancellation propagates.
    /// </summary>
    internal static async Task<ErrorListAnswer?> AskAsync(TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        // A leftover answer must never satisfy this request.
        SignalFile.Delete(AnswerPath);
        // Not written, nobody can answer: said as such, never as an answer that did not come in time.
        if (!SignalFile.Write(RequestPath, new ErrorListAsk(id, SignalFile.CurrentPid, SignalFile.Now.ToUnixTimeMilliseconds()),
                              "ErrorListSelectionSignal.Ask"))
            return new ErrorListAnswer(id, [], "the request could not be written to " + RequestPath);

        try
        {
            var deadline = SignalFile.Now + timeout;
            while (true)
            {
                if (TryReadAnswer(id) is { } answer) return answer;
                if (SignalFile.Now >= deadline) break;
                await Task.Delay(50, ct);
            }
        }
        catch (OperationCanceledException)
        {
            SignalFile.Delete(RequestPath);
            throw;
        }
        // Unanswered, or the wait cancelled above: withdrawn, so a list that wakes up later does not answer a command
        // nobody runs any more.
        SignalFile.Delete(RequestPath);
        return null;
    }

    internal static ErrorListAnswer? TryReadAnswer(string id)
    {
        try
        {
            var answer = SignalFile.TryRead<ErrorListAnswer>(AnswerPath);
            return answer?.Id == id ? answer : null;
        }
        catch (Exception ex) { Diagnostics.Swallow("ErrorListSelectionSignal.TryReadAnswer", ex); return null; }
    }

    // ── In process (devenv) ─────────────────────────────────────────────────────

    /// <summary>
    /// The request on disk, unless it is <paramref name="lastId"/> (already served: the file system reports one write
    /// several times), its writer is gone, or it is older than <see cref="MaxAge"/>. Never throws.
    /// </summary>
    internal static ErrorListAsk? TryTake(string? lastId)
    {
        try
        {
            var ask = SignalFile.TryRead<ErrorListAsk>(RequestPath);
            if (ask is null || ask.Id == lastId || !SignalFile.WriterAlive(ask.Pid, ask.Ts)) return null;
            var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(ask.Ts);
            return age >= TimeSpan.Zero && age < MaxAge ? ask : null;
        }
        catch (Exception ex) { Diagnostics.Swallow("ErrorListSelectionSignal.TryTake", ex); return null; }
    }

    internal static void Answer(ErrorListAnswer answer)
    {
        // Only its own request: a late answer must not take away a newer one before it is served.
        try
        {
            if (SignalFile.TryRead<ErrorListAsk>(RequestPath)?.Id == answer.Id) SignalFile.Delete(RequestPath);
        }
        catch (Exception ex) { Diagnostics.Swallow("ErrorListSelectionSignal.Answer", ex); }
        SignalFile.Write(AnswerPath, answer, "ErrorListSelectionSignal.Answer");
    }
}
