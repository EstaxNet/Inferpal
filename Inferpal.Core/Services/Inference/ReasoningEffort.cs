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
}
