using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

[VisualStudioContribution]
internal class ExplainSelectionCommand : SelectionCommandBase
{
    public ExplainSelectionCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder, InferpalConfig config,
                                   IInferenceProvider client)
        : base(extensibility, contextHolder, config, client) { }

    public override CommandConfiguration CommandConfiguration => new("%ContextMenuExplain%")
    {
        Icon = new(ImageMoniker.KnownValues.QuestionMark, IconSettings.IconAndText),
    };

    protected override string BuildPrompt(string fileName) =>
        Strings.PromptExplain(fileName);
}
