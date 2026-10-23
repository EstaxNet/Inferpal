using System.Text.Json.Serialization;

namespace Inferpal.Services.Signals;

/// <summary>The key of inline completion pressed in Visual Studio (Alt+'), on its way to the editor.</summary>
/// <param name="Refusal">Why no completion is asked — said in the status bar; <c>null</c> when one goes.</param>
/// <param name="Nothing">Said when the model brings nothing back.</param>
/// <param name="Failed">Said when nothing comes back because the server — or the sidecar — failed.</param>
/// <param name="ListHint">Said when the suggestion shows while an open list holds Tab.</param>
/// <param name="NoEditor">Said when no code editor has the focus to take the request.</param>
/// <remarks>The sentences travel with the request: the in-process assembly does not reference the Core and has no
/// <c>Strings</c> (see <see cref="InlineDiffNotices"/>).</remarks>
internal sealed record CompletionAsk(
    [property: JsonPropertyName("id")]       string  Id,
    [property: JsonPropertyName("pid")]      int     Pid,
    [property: JsonPropertyName("ts")]       long    Ts,
    [property: JsonPropertyName("refusal")]  string? Refusal,
    [property: JsonPropertyName("nothing")]  string  Nothing,
    [property: JsonPropertyName("failed")]   string  Failed,
    [property: JsonPropertyName("listHint")] string  ListHint,
    [property: JsonPropertyName("noEditor")] string  NoEditor);

/// <summary>
/// File channel from the out-of-process command (which has the Core: the settings, the chat's state, the sentences) to
/// the ghost text in devenv, which shows the suggestion — scoped to the declared Visual Studio instance, so a key pressed
/// in one devenv is never served by another.
/// </summary>
internal static class CompletionAskSignal
{
    internal const string BaseName = "completion_ask";

    internal static string RequestPath => SignalFile.ScopedPathFor(BaseName);

    /// <summary>A request older than this is not served: the person has moved on.</summary>
    internal static TimeSpan MaxAge { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Publishes a key press. Best-effort: lost, the key does nothing, as an unbound key would.</summary>
    internal static void Write(string? refusal, string nothing, string failed, string listHint, string noEditor) =>
        SignalFile.Write(RequestPath,
            new CompletionAsk(Guid.NewGuid().ToString("N"), SignalFile.CurrentPid,
                              SignalFile.Now.ToUnixTimeMilliseconds(), refusal, nothing, failed, listHint, noEditor),
            "CompletionAskSignal.Write");

    /// <summary>
    /// The request on disk, unless it is <paramref name="lastId"/> (already served: the file system reports one write
    /// several times), its writer is gone, or it is older than <see cref="MaxAge"/>. Never throws.
    /// </summary>
    internal static CompletionAsk? TryTake(string? lastId)
    {
        try
        {
            var ask = SignalFile.TryRead<CompletionAsk>(RequestPath);
            if (ask is null || ask.Id == lastId || !SignalFile.WriterAlive(ask.Pid, ask.Ts)) return null;
            var age = SignalFile.Now - DateTimeOffset.FromUnixTimeMilliseconds(ask.Ts);
            return age >= TimeSpan.Zero && age < MaxAge ? ask : null;
        }
        catch (Exception ex) { Diagnostics.Swallow("CompletionAskSignal.TryTake", ex); return null; }
    }
}
