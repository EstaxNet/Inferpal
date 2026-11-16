using System.Runtime.CompilerServices;
using Inferpal.GhostText;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Visual Studio never builds the chat from its saved layout (it would race the extension's host and show "tool window
/// not found"); the in-process package reopens it, by the identifier of the chat's type.
/// </summary>
public class ChatWindowRestoreTests
{
    // Read without constructing the window (its constructor wants the extension's services).
    private static ToolWindowConfiguration ConfigOf<T>() =>
        (ToolWindowConfiguration)typeof(T).GetProperty("ToolWindowConfiguration")!
            .GetValue(RuntimeHelpers.GetUninitializedObject(typeof(T)))!;

    [Fact]
    public void NeitherWindow_IsCreatedFromTheLayout()
    {
        Assert.False(ConfigOf<InferpalToolWindow>().AllowAutoCreation);
        Assert.False(ConfigOf<InferpalSettingsToolWindow>().AllowAutoCreation);
        // Witness: the configuration is the one the window declares, placement included.
        Assert.Equal(ToolWindowPlacement.DocumentWell, ConfigOf<InferpalToolWindow>().Placement);
    }

    [Fact]
    public void TheRestore_NamesTheChatWindowsType() =>
        Assert.Equal(typeof(InferpalToolWindow).FullName, ChatWindowRestore.Identifier);
}
