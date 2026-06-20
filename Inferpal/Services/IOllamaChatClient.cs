using Inferpal.Models;

namespace Inferpal.Services;

/// <summary>
/// The single Ollama chat operation the <see cref="AgentOrchestrator"/> depends on. Extracted as a
/// seam so the orchestrator can be unit-tested with a fake client (no network) — in particular its
/// intra-run summarisation path. Implemented by <see cref="OllamaClient"/>.
/// </summary>
internal interface IOllamaChatClient
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
