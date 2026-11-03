using Inferpal.Config;

namespace Inferpal.Services.Inference;

/// <summary>
/// What a change of the chat's server takes with it: the graphics memory and the models of the roles that run on the
/// chat's server belong to the server they were chosen for.
/// </summary>
/// <remarks>
/// ⚠ One rule for every gesture that moves the chat to another address — Visual Studio's settings, the VS Code
/// settings panel (<c>config/update</c>), <c>/setup</c> adopting a server it found, the first run of an ACP agent, the
/// setup card (<c>setup/apply</c>), <c>/import</c>. Kept across a move, the old machine's card would size the models and
/// the context of the new one, and an embedding model the new server does not have would turn code search to keywords in
/// silence. ⚠ Judged on the LIVE settings, read from the file first (<see cref="Before"/>), after the gesture's own
/// changes are laid over them: a card measured or a model picked since a form opened is the old server's too.
/// </remarks>
internal static class ServerChange
{
    /// <summary>
    /// When <paramref name="after"/> puts the chat on another address than <paramref name="before"/>, starts over in
    /// <paramref name="after"/> what the old server chose and the same gesture did not change: the graphics memory (0 —
    /// measured again on this machine's server, asked otherwise), and the completion and code search models of each role
    /// whose server moved with it (empty: the chat's model for completion, the best embedding model the new server has
    /// for search). A role whose own server did not move keeps its model. Returns whether anything started over.
    /// </summary>
    /// <remarks>⚠ "The same gesture did not change it" is read as "the value is the old one": a figure or a model typed
    /// again identical on the new server is taken for the old server's, and starts over too.</remarks>
    public static bool Follow(InferpalConfig before, InferpalConfig after)
    {
        if (ServerScan.SameAddress(before.BaseUrl, after.BaseUrl)) return false;
        var changed = false;
        // Compared as the settings box shows it: a figure written with two decimals comes back from the box rounded.
        if (after.VramBudgetGb > 0 && Shown(after.VramBudgetGb) == Shown(before.VramBudgetGb))
        {
            after.VramBudgetGb = 0;
            changed = true;
        }
        if (Moved(ServerRole.Completion) && after.InlineCompletionModel.Length > 0
            && string.Equals(after.InlineCompletionModel, before.InlineCompletionModel, StringComparison.Ordinal))
        {
            after.InlineCompletionModel = string.Empty;
            changed = true;
        }
        if (Moved(ServerRole.Search) && after.RagEmbeddingModel.Length > 0
            && string.Equals(after.RagEmbeddingModel, before.RagEmbeddingModel, StringComparison.Ordinal))
        {
            after.RagEmbeddingModel = string.Empty;
            changed = true;
        }
        return changed;

        // A role on a server of its own keeps it, at the old chat address or at the new one alike.
        bool Moved(ServerRole role) =>
            !ServerScan.SameAddress(RoleServers.Of(before, role).BaseUrl, RoleServers.Of(after, role).BaseUrl);

        static string Shown(double gb) => gb.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The settings a gesture starts from, as the file holds them for what this rule decides: a model or a figure another
    /// editor saved since this copy was read is the old server's too, and a copy that never held it would neither clear
    /// it nor save over it.
    /// </summary>
    /// <param name="config">The live settings of the gesture.</param>
    /// <param name="fromFile">Whether the file is read first — a gesture on the live settings of an editor; not a pure
    /// applier handed a copy (<c>/import</c>), whose caller reads and saves.</param>
    public static InferpalConfig Before(InferpalConfig config, bool fromFile = true)
    {
        if (fromFile)
        {
            config.FillFromFile(nameof(InferpalConfig.VramBudgetGb));
            config.FillFromFile(nameof(InferpalConfig.InlineCompletionModel));
            config.FillFromFile(nameof(InferpalConfig.RagEmbeddingModel));
        }
        return System.Text.Json.JsonSerializer.Deserialize<InferpalConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;
    }
}
