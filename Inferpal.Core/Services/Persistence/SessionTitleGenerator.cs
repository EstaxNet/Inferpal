using Inferpal.Config;

namespace Inferpal.Services.Persistence;

/// <summary>
/// LLM-generated session titles, shared by both front-ends: the VS VM archives the conversation
/// on <c>/clear</c>, the Host serves the same thing over the <c>session/title</c> RPC for VS Code.
/// The call used to live in the VM only — routing it through the Core is what makes the VS Code
/// side possible without duplicating the prompt, the timeout and the sanitising rules.
/// </summary>
/// <remarks>
/// Best-effort by design: any backend hiccup (offline, no model, timeout) degrades to
/// <see cref="SessionManager.MakeSnippet"/> — a title is never worth failing a save for.
/// The model comes from the <b>utility</b> role (Model Router), so a small warm model handles it.
/// </remarks>
internal static class SessionTitleGenerator
{
    /// <summary>Hard ceiling on the title call — a session save must never hang on it.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Only the head of the first message is worth summarising.</summary>
    private const int MaxInputChars = 400;

    /// <summary>
    /// Summarises <paramref name="firstUserContent"/> into a short file-safe title. Never throws
    /// (except on <paramref name="ct"/> cancellation before the call): returns the snippet
    /// fallback whenever the model can't answer.
    /// </summary>
    public static async Task<string> GenerateAsync(
        IInferenceProvider client,
        InferpalConfig     config,
        string             firstUserContent,
        CancellationToken  ct)
    {
        var fallback = SessionManager.MakeSnippet(firstUserContent);
        if (string.IsNullOrWhiteSpace(firstUserContent)) return fallback;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var result = await client.RunAgentAsync(
                model:   await ModelRouter.ResolveUtilityAsync(config, client, cts.Token),
                history:
                [
                    new("system", SessionManager.TitleSystemPrompt),
                    new("user",   firstUserContent.Length > MaxInputChars
                                      ? firstUserContent[..MaxInputChars]
                                      : firstUserContent)
                ],
                tools:   EmptyToolRegistry.Instance,
                onStep:  _ => { },
                onToken: null,
                ct:      cts.Token);

            return SessionManager.SanitizeTitle(result.FinalResponse, fallback);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Diagnostics.Swallow("SessionTitleGenerator", ex);
            return fallback;
        }
    }
}
