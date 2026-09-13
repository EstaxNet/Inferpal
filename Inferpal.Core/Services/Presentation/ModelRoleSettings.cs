using Inferpal.Config;

namespace Inferpal.Services.Presentation;

/// <summary>
/// The "Use a separate model per role (advanced)" switch of both settings panels.
/// </summary>
/// <remarks>
/// <para>
/// The switch is not stored: a panel shows it checked when a role override is set. Unchecked, its
/// hint promises that the chat model is used everywhere — so saving with it off must clear the
/// overrides. Folding them away was not enough: the router kept using models the user could no
/// longer see (a name the backend does not have means a 404 on every agent turn), and the switch
/// came back checked at the next opening.
/// </para>
/// <para>
/// The fields concerned are those of the settings schema's <see cref="Gate"/>. The VS Code panel
/// reads that gate from the served schema; <c>ModelRoleSettingsTests</c> holds this class to it.
/// </para>
/// </remarks>
internal static class ModelRoleSettings
{
    /// <summary>The schema gate the switch reveals.</summary>
    internal const string Gate = "roles";

    /// <summary>Whether any per-role setting departs from "the chat model everywhere".</summary>
    internal static bool HasRoleOverride(InferpalConfig config) =>
        !string.IsNullOrWhiteSpace(config.AgentModel)
        || !string.IsNullOrWhiteSpace(config.CodeActionsModel)
        || !string.IsNullOrWhiteSpace(config.InlineCompletionModel)
        || !string.IsNullOrWhiteSpace(config.InlineEditModel)
        || !string.IsNullOrWhiteSpace(config.UtilityModel)
        || config.ModelRouterAuto;

    /// <summary>What an unchecked switch means: no role override, no automatic routing.</summary>
    internal static void UseChatModelEverywhere(InferpalConfig config)
    {
        config.AgentModel            = string.Empty;
        config.CodeActionsModel      = string.Empty;
        config.InlineCompletionModel = string.Empty;
        config.InlineEditModel       = string.Empty;
        config.UtilityModel          = string.Empty;
        config.ModelRouterAuto       = false;
    }
}
