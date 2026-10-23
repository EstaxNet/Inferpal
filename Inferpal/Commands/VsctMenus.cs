namespace Inferpal.Commands;

/// <summary>
/// The Visual Studio menus Inferpal's commands are placed in, by their command-table GUID and ID — the VSSDK's
/// <c>vsshlids.h</c>, <c>vsdebugguids.h</c> and <c>VsDbgCmd.h</c>. <c>GroupPlacement.VsctParent</c> parents a group to
/// one of them.
/// </summary>
internal static class VsctMenus
{
    /// <summary><c>guidSHLMainMenu</c>: the shell's menus and context menus.</summary>
    internal static readonly Guid ShlMainMenu = new("d309f791-903f-11d0-9efc-00a0c911004f");
    internal const uint CodeWindowContext = 0x040D;   // IDM_VS_CTXT_CODEWIN — the text editor
    internal const uint ErrorListContext  = 0x0405;   // IDM_VS_CTXT_ERRORLIST
    internal const uint DebugMenu         = 0x0089;   // IDM_VS_MENU_DEBUG

    /// <summary><c>guidVSDebugGroup</c>: the debugger's windows.</summary>
    internal static readonly Guid VsDebugGroup = new("c9dd4a58-47fb-11d2-83e7-00c04f9902c1");
    internal const uint WatchContext     = 0x0500;    // IDM_WATCH_CONTEXT
    internal const uint LocalsContext    = 0x0501;    // IDM_LOCALS_CONTEXT
    internal const uint AutosContext     = 0x0502;    // IDM_AUTOS_CONTEXT
    internal const uint CallStackContext = 0x0504;    // IDM_CALLSTACK_CONTEXT

    /// <summary><c>UICONTEXT_Debugging</c>: a debug session, running or paused.</summary>
    internal static readonly Guid DebuggingContext = new("adfc4e61-0397-11d1-9f4e-00a0c911004f");
}

/// <summary>
/// The workspace's ignore files, declared before a command reads files for the model: the chat window declares them,
/// and it may never have been opened in this session — an excluded file would be read.
/// </summary>
internal static class WorkspaceExclusions
{
    private static readonly Services.Governance.OpenWorkspaceExclusions Open = new(() => Config.InferpalConfig.Load().IgnoreFiles);

    internal static void Follow() => Open.Follow();
}
