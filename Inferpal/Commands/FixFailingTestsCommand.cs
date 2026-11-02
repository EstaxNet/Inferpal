using Inferpal.Services;
using Inferpal.ToolWindow;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

namespace Inferpal.Commands;

/// <summary>
/// "Fix Failing Tests with Inferpal" in Test Explorer's context menu: <c>/tdd</c> in the chat, as if typed. Test Explorer
/// gives no extension the test clicked, so the loop runs the whole suite; <c>/tdd &lt;name&gt;</c> typed narrows it. VS
/// Code's Test view passes the clicked test's name instead.
/// </summary>
[VisualStudioContribution]
internal class FixFailingTestsCommand : Command
{
    private readonly VsContextHolder _contextHolder;

    public FixFailingTestsCommand(VisualStudioExtensibility extensibility, VsContextHolder contextHolder)
        : base(extensibility)
    {
        _contextHolder = contextHolder;
    }

    public override CommandConfiguration CommandConfiguration => new("%InferpalFixFailingTestsCommand%")
    {
        Icon = new(ImageMoniker.KnownValues.EditBug, IconSettings.IconAndText),
    };

    // GroupPlacement.VsctParent parents a group to a VSCT menu; CommandPlacement.VsctParent would need a group ID.
    [VisualStudioContribution]
    internal static CommandGroupConfiguration TestExplorerContextGroup { get; } =
        new(GroupPlacement.VsctParent(VsctMenus.TestWindowCmdSet, VsctMenus.TestExplorerContext, 0x0011))
        {
            Children = [GroupChild.Command<FixFailingTestsCommand>()],
        };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken ct)
    {
        _contextHolder.Context = context;
        _contextHolder.SetPendingPrompt("/tdd");
        await Extensibility.Shell().ShowToolWindowAsync<InferpalToolWindow>(activate: true, ct);
    }
}
