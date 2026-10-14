using Inferpal.Models;

namespace Inferpal.Services.Inference;

/// <summary>
/// The one chat operation the <see cref="AgentOrchestrator"/> depends on, whatever the backend: every
/// <see cref="IInferenceProvider"/> implements it (Ollama, LM Studio, OpenAI-compatible servers). Extracted as a
/// seam so the orchestrator can be unit-tested with a fake client (no network) — in particular its
/// intra-run summarisation path.
/// </summary>
internal interface IChatTurnClient
{
    /// <inheritdoc cref="OllamaClient.SendChatAsync"/>
    Task<ChatTurnResult> SendChatAsync(
        string model,
        List<ChatMessageDto> messages,
        IToolRegistry tools,
        Action<string>? onToken,
        CancellationToken ct,
        TaskComplexity complexity = TaskComplexity.Normal,
        string? toolChoice = null,
        Action<string>? onThinking = null);
}
