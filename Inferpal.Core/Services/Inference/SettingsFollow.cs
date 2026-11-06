using System.Text.Json.Nodes;
using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// A copy of the settings that follows the file another process saves: what the file changed since the copy last took
/// it is taken; what the copy changed itself stays its own.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ Every window of both editors keeps its own copy, read once. Without following the file, a setting saved in VS Code
/// would do nothing in Visual Studio until it restarts, and the other way round — the approvals, the model, the MCP
/// servers — while each settings page shows its own copy as if it were the file. What a copy changed itself and did not
/// save stays its own, unless the file changed that same setting.
/// </para>
/// <para>
/// ⚠ The client of a copy speaks ONE kind of server's protocol and reads its address and key live: a new kind taken
/// here would send the old protocol to the new address — every turn refused while the new server runs. A new kind waits
/// for a restart (said by the caller, <see cref="Result.ServerChanged"/>), the copy keeping its server whole; a new
/// address or key of the same kind applies now. ⚠ Unreadable is not "the factory values": the copy keeps what it has.
/// </para>
/// </remarks>
internal static class SettingsFollow
{
    /// <summary>What a follow took.</summary>
    /// <param name="Taken">The JSON keys of the settings taken from the file.</param>
    /// <param name="ServerChanged">The file names a new kind of model server: it applies at the next start.</param>
    /// <param name="Unreadable">The file could not be read: nothing was taken.</param>
    /// <param name="SwitchTo">The kind of server the file names, when it is not the one the client speaks.</param>
    /// <param name="Busy">A turn was running: nothing was read, so this says nothing about the file.</param>
    internal sealed record Result(IReadOnlyList<string> Taken, bool ServerChanged, bool Unreadable, string? SwitchTo = null, bool Busy = false);

    /// <summary>The JSON keys that belong to the model server: left aside together with a new kind of server.</summary>
    private static readonly string[] ServerBound =
    [
        "provider", "baseUrl", "apiKey", "defaultModel", "agentModel", "utilityModel", "codeActionsModel", "inlineEditModel",
        "inlineCompletionModel", "ragEmbeddingModel", "vramBudgetGb",
    ];

    /// <summary>
    /// Follows the settings file for an editor whose windows share <paramref name="config"/> (Visual Studio), and applies
    /// what it took as a save applies it (<see cref="InferpalConfig.NotifyFollowed"/>), the new kind of server included.
    /// </summary>
    /// <param name="busy">Whether a turn may be reading these settings now (a gesture outside a question, the settings
    /// window opening): nothing is taken then — the agent loop reads its rules and its models, and the next question
    /// takes them. A question's own follow, before its turn, passes none.</param>
    public static Result TakeAndApply(InferpalConfig config, IInferenceProvider client, Func<bool>? busy = null)
    {
        if (busy?.Invoke() == true) return new([], false, false, Busy: true);
        var result = Take(config, client);
        if (result.Taken.Count > 0 || result.ServerChanged)
            config.NotifyFollowed(result.Taken, result.ServerChanged ? result.SwitchTo : null);
        return result;
    }

    /// <summary>Follows the settings file for <paramref name="config"/>, whose last reading is
    /// <see cref="InferpalConfig.FollowedFile"/> (Visual Studio: the chat window and the settings window share it).</summary>
    public static Result Take(InferpalConfig config, IInferenceProvider client)
    {
        var followed = config.FollowedFile ?? config.SnapshotNow();
        var result = Take(config, InferpalConfig.LoadOwnCopyToFollow(), client, ref followed);
        config.FollowedFile = followed;
        return result;
    }

    /// <param name="config">The copy that follows.</param>
    /// <param name="fileNow">The file as it is now; <c>null</c> when it cannot be read.</param>
    /// <param name="client">The copy's client: the kind of server it speaks.</param>
    /// <param name="followed">The file as the copy last took it; the file as it is now once taken.</param>
    public static Result Take(InferpalConfig config, InferpalConfig? fileNow, IInferenceProvider client, ref JsonObject followed)
    {
        if (fileNow is null) return new([], false, true);
        var asSaved  = fileNow.SnapshotNow();
        var switchTo = InferenceProviderFactory.PendingSwitch(client, fileNow.Provider);
        if (switchTo is not null)
        {
            // Left aside with the server: its models and its card. Taken, the client of the old kind would ask the old
            // server for the new one's models — every question refused, "model not found", until the restart.
            fileNow.Provider              = config.Provider;
            fileNow.BaseUrl               = config.BaseUrl;
            fileNow.ApiKey                = config.ApiKey;
            fileNow.DefaultModel          = config.DefaultModel;
            fileNow.AgentModel            = config.AgentModel;
            fileNow.UtilityModel          = config.UtilityModel;
            fileNow.CodeActionsModel      = config.CodeActionsModel;
            fileNow.InlineEditModel       = config.InlineEditModel;
            fileNow.InlineCompletionModel = config.InlineCompletionModel;
            fileNow.RagEmbeddingModel     = config.RagEmbeddingModel;
            fileNow.VramBudgetGb          = config.VramBudgetGb;
        }
        var taken = config.TakeFileChanges(followed, fileNow);
        // What was left aside is still the file's: a save merging over this reading (the file unreadable then) would
        // otherwise write the old server back over the new one.
        if (switchTo is not null) config.FileAsReadHolds(asSaved, ServerBound);
        // Said once: the next follow compares with the file as saved, new kind included.
        var newKind = switchTo is not null && !JsonNode.DeepEquals(asSaved["provider"], followed["provider"]);
        followed = asSaved;
        return new(taken, newKind, false, switchTo);
    }
}
