using Inferpal.Config;
using Inferpal.Localization;

namespace Inferpal.Services.CodeActions;

/// <summary>
/// A completion asked for by its key (Alt+'), not by a pause in typing: whether one goes, and what the status bar says
/// instead of showing nothing. One decision for both editors — Visual Studio's command and the host's <c>fim/ask</c>.
/// </summary>
/// <remarks>
/// A pause that brings nothing is quiet on purpose — it comes back at every pause. A key that brings nothing looks broken:
/// the person pressed it and waits, so every request that does not go, and every answer that is empty, is said.
/// </remarks>
internal static class AskedCompletion
{
    /// <summary><c>inlineCompletionTrigger</c>: a suggestion after a pause in typing (the factory value).</summary>
    public const string Auto = "auto";

    /// <summary><c>inlineCompletionTrigger</c>: only the key asks; nothing is sent otherwise.</summary>
    public const string OnDemand = "onDemand";

    /// <summary>Whether only the key asks — a value written by hand in another case included.</summary>
    public static bool IsOnDemand(string? trigger) => string.Equals(trigger?.Trim(), OnDemand, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a completion asked for in <paramref name="path"/> is not sent — the sentence for the person — or
    /// <c>null</c> when it goes.</summary>
    /// <remarks>⚠ The chat keeps the graphics card even against a key: on one card, the completion model would be loaded
    /// beside the one answering, or in its place.</remarks>
    public static string? Refusal(InferpalConfig c, string? path)
    {
        if (!c.InlineCompletionEnabled) return Strings.CompletionAskedOff(Strings.SettingsPageAutocomplete);
        if (ContentExclusions.NoticeFor(path, null) is { } excluded) return Strings.CompletionAskedExcluded(excluded);
        if (RoleServers.CompletionModel(c, null) is null) return Strings.CompletionAskedNoModel(Strings.SettingsPageAutocomplete);
        if (RoleServers.SharesMachineWithChat(c, ServerRole.Completion) && GpuScheduler.ShouldFimYield())
            return Strings.CompletionAskedChatBusy;
        return null;
    }

    /// <summary>What an asked completion says when the model brought nothing back.</summary>
    public static string Nothing => Strings.CompletionAskedNothing;

    /// <summary>What it says when the server did not answer or refused (<see cref="IInferenceProvider.FimFailing"/>): an
    /// empty answer is then not the model's, and "nothing here" would blame the caret for a server that is down.</summary>
    public static string ServerFailed => Strings.CompletionAskedServerFailed;

    /// <summary>The sentence for an empty answer.</summary>
    public static string Empty(bool serverFailed) => serverFailed ? ServerFailed : Nothing;
}
