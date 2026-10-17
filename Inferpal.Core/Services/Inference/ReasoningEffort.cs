using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>How much a model reasons before it answers, asked call by call.</summary>
/// <remarks>
/// ⚠ Per CALL, not per model: the utility model is the chat model when its field is empty (the default), and a session
/// title must not pay the reasoning a question pays. ⚠ Asked, never imposed: each client sends it only to a model the
/// server says can take it — a value a template does not know is not a lower effort, it is anything (GLM 4.7 Flash,
/// sent "none", reasons six times longer).
/// </remarks>
internal enum ReasoningEffort
{
    /// <summary>Nothing is sent: the server's own setting for the model.</summary>
    Server,

    /// <summary>No reasoning, where the server says the model can turn it off; nothing sent elsewhere.</summary>
    Off,

    /// <summary>A short reasoning, where the server says the model takes that level.</summary>
    Low,

    /// <summary>A middle reasoning, where the server says the model takes that level.</summary>
    Medium,

    /// <summary>A long reasoning, where the server says the model takes that level.</summary>
    High,
}

/// <summary>The effort a setting names (<c>server</c>, <c>off</c>, <c>low</c>, <c>medium</c>, <c>high</c>).</summary>
internal static class ReasoningEffortSetting
{
    /// <summary>The effort <paramref name="value"/> names; anything else is the server's own setting.</summary>
    public static ReasoningEffort Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off"    => ReasoningEffort.Off,
        "low"    => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "high"   => ReasoningEffort.High,
        _        => ReasoningEffort.Server,
    };

    /// <summary>The effort a question asks for (<see cref="InferpalConfig.ReasoningEffortChat"/>).</summary>
    public static ReasoningEffort Chat(InferpalConfig config) => Parse(config.ReasoningEffortChat);

    /// <summary>The effort an agent run asks for, the whole run (<see cref="InferpalConfig.ReasoningEffortAgent"/>).</summary>
    public static ReasoningEffort Agent(InferpalConfig config) => Parse(config.ReasoningEffortAgent);
}
