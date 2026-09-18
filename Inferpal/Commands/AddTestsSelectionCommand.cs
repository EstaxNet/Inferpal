using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Commands;

/// <summary>
/// Right-click "Add unit tests" context-menu command. Generates tests into a <b>separate test
/// file</b> next to the source (created and opened, or extended in place) via the shared
/// <see cref="TestGenerationEdit"/> pipeline — it does not stream an answer into the chat.
/// </summary>
[VisualStudioContribution]
internal class AddTestsSelectionCommand : Command
{
    private readonly VsContextHolder    _contextHolder;
    private readonly InferpalConfig     _config;
    private readonly IInferenceProvider _client;

    public AddTestsSelectionCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config, IInferenceProvider client)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
        _config        = config;
        _client        = client;
    }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuAddTests%")
    {
        Icon = new(ImageMoniker.KnownValues.RunTest, IconSettings.IconAndText),
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;

        // Prefer the snapshot captured before the context menu opened (LatestView): the live
        // IClientContext may no longer carry the selection by the time we run.
        var view = _contextHolder.LatestView
                ?? await Extensibility.Editor().GetActiveTextViewAsync(context, ct);
        if (view is null) return;

        var model  = ModelRouter.Resolve(_config, ModelRole.CodeActions);
        var result = await TestGenerationEdit.RunAsync(Extensibility, view, _client, model, ct);

        // No chat here: both outcomes that open nothing are said in a dismissable prompt, like the
        // other context-menu code actions — a click that does nothing reads as a broken command.
        // Closing the spinner is the user saying "stop": nothing to report.
        if (result.Cancelled) return;
        if (result.NoChange)
            await Extensibility.Shell().ShowPromptAsync(Strings.TestsNoChange, PromptOptions.OK, ct);
        else if (result.Unreadable)
            await Extensibility.Shell().ShowPromptAsync(
                Strings.TestsFileUnreadable(result.TestFileName), PromptOptions.OK, ct);
        else if (!result.Ok)
            await Extensibility.Shell().ShowPromptAsync(Strings.TestsGenerateFailed, PromptOptions.OK, ct);
    }
}
