using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>A request for the text of Visual Studio's Output window.</summary>
internal sealed record OutputPaneAsk(
    [property: JsonPropertyName("id")]  string Id,
    [property: JsonPropertyName("pid")] int    Pid,
    [property: JsonPropertyName("ts")]  long   Ts);

/// <summary>The in-process answer, correlated by <see cref="Id"/>.</summary>
/// <param name="Id">The request answered.</param>
/// <param name="Pane">The pane read (as Visual Studio names it); <c>null</c> when there is none.</param>
/// <param name="Text">Its last <see cref="OutputPaneSignal.MaxChars"/> characters, a line break counted as one (a little
/// more as text); <c>null</c> when nothing was read.</param>
/// <param name="Cut">Whether the pane holds more than those, before them.</param>
/// <param name="Reason">Why there is no text: <see cref="OutputPaneSignal.ReasonNone"/>,
/// <see cref="OutputPaneSignal.ReasonEmpty"/> or <see cref="OutputPaneSignal.ReasonUnreadable"/>; <c>null</c> with a text,
/// or when <paramref name="Failure"/> says what failed.</param>
/// <param name="Failure">What failed, in English (said to the person as the cause); <c>null</c> when nothing did.</param>
internal sealed record OutputPaneAnswer(
    [property: JsonPropertyName("id")]      string  Id,
    [property: JsonPropertyName("pane")]    string? Pane,
    [property: JsonPropertyName("text")]    string? Text,
    [property: JsonPropertyName("cut")]     bool    Cut,
    [property: JsonPropertyName("reason")]  string? Reason,
    [property: JsonPropertyName("failure")] string? Failure = null);

/// <summary>
/// File channel between the out-of-process <c>@output</c> mention and the in-process half, the only one that can read the
/// Output window's panes (EnvDTE's <c>OutputWindowPane.TextDocument</c>) — scoped to the declared Visual Studio instance.
/// </summary>
/// <remarks>The same request and answer as <see cref="ErrorListSelectionSignal"/>: asked when the mention is picked, never
/// published, since a pane's text changes with every build.</remarks>
internal static class OutputPaneSignal
{
    internal static string RequestPath => SignalFile.ScopedPathFor("output_ask");
    internal static string AnswerPath  => SignalFile.ScopedPathFor("output_answer");

    /// <summary>No pane at all: nothing has written to the Output window since Visual Studio started.</summary>
    internal const string ReasonNone = "none";
    /// <summary>The pane exists and holds nothing.</summary>
    internal const string ReasonEmpty = "empty";
    /// <summary>Visual Studio does not give the pane's text (a pane not yet created as a document).</summary>
    internal const string ReasonUnreadable = "unreadable";

    /// <summary>How much of a pane is sent: its end, where a build says how it ended — within one chip's size.</summary>
    internal const int MaxChars = 40_000;

    /// <summary>How long the mention waits for devenv: a pane is read in tens of milliseconds.</summary>
    internal static TimeSpan AnswerBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A request older than this is not served: its mention stopped waiting.</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(10);

    // ── Out of process (the mention) ────────────────────────────────────────────

    /// <summary>
    /// Asks for the pane and waits up to <paramref name="timeout"/>: the answer, or <c>null</c> when none came (the
    /// in-process half is not loaded, or busy past the budget). Cancellation propagates.
    /// </summary>
    internal static async Task<OutputPaneAnswer?> AskAsync(TimeSpan timeout, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        // A leftover answer must never satisfy this request.
        SignalFile.Delete(AnswerPath);
        // Not written, nobody can answer: said as such, never as an answer that did not come in time.
        if (!SignalFile.Write(RequestPath, new OutputPaneAsk(id, SignalFile.CurrentPid, SignalFile.Now.ToUnixTimeMilliseconds()),
                              "OutputPaneSignal.Ask"))
            return new OutputPaneAnswer(id, null, null, false, null, "the request could not be written to " + RequestPath);

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
        // Withdrawn: a devenv that wakes up later does not answer a mention nobody waits for.
        SignalFile.Delete(RequestPath);
        return null;
    }

    internal static OutputPaneAnswer? TryReadAnswer(string id)
    {
        try
        {
            var answer = SignalFile.TryRead<OutputPaneAnswer>(AnswerPath);
            return answer?.Id == id ? answer : null;
        }
        catch (Exception ex) { Diagnostics.Swallow("OutputPaneSignal.TryReadAnswer", ex); return null; }
    }

    // ── In process (devenv) ─────────────────────────────────────────────────────

    /// <summary>
    /// The request on disk, unless it is <paramref name="lastId"/> (already served: the file system reports one write
    /// several times), its writer is gone, or it is older than <see cref="MaxAge"/>. Never throws.
    /// </summary>
    internal static OutputPaneAsk? TryTake(string? lastId)
    {
        try
        {
            var ask = SignalFile.TryRead<OutputPaneAsk>(RequestPath);
            if (ask is null || ask.Id == lastId || !SignalFile.WriterAlive(ask.Pid, ask.Ts)) return null;
            var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(ask.Ts);
            return age >= TimeSpan.Zero && age < MaxAge ? ask : null;
        }
        catch (Exception ex) { Diagnostics.Swallow("OutputPaneSignal.TryTake", ex); return null; }
    }

    internal static void Answer(OutputPaneAnswer answer)
    {
        // Only its own request: a late answer must not take away a newer one before it is served.
        try
        {
            if (SignalFile.TryRead<OutputPaneAsk>(RequestPath)?.Id == answer.Id) SignalFile.Delete(RequestPath);
        }
        catch (Exception ex) { Diagnostics.Swallow("OutputPaneSignal.Answer", ex); }
        SignalFile.Write(AnswerPath, answer, "OutputPaneSignal.Answer");
    }
}
