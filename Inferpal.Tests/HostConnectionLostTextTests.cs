using System.Text.RegularExpressions;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// ⚠ VS Code: a request the host's connection cut short — a restart (settings, a new root) or a crash during a turn —
/// says so in the user's language, never vscode-jsonrpc's English sentence.
/// </summary>
/// <remarks>
/// The turn's catch printed <c>err.message</c>: "Pending response rejected since connection got disposed" — no cause,
/// no remedy, untranslated, and saved with the conversation. <c>hostErrorText</c> was already the one reader of a host
/// failure for the user; the turn and the code actions read the raw message beside it. These rules read the source.
/// </remarks>
public sealed class HostConnectionLostTextTests
{
    [Fact]
    public void TheReader_NamesALostConnection_InTheUsersLanguage()
    {
        var reader = WebviewRebuildTests.Body(WebviewRebuildTests.TsCode("hostStatus.ts"), "export function hostErrorText(");
        Assert.Contains("if (isConnectionLost(err)) {", reader, StringComparison.Ordinal);
        Assert.Contains("return t('The assistant stopped before answering — it restarted or closed. Ask again.');", reader, StringComparison.Ordinal);
        // vscode-jsonrpc's ErrorCodes.PendingResponseRejected and ErrorCodes.ConnectionInactive.
        Assert.Contains("const CONNECTION_LOST_CODES = new Set([-32097, -32096]);", WebviewRebuildTests.TsCode("hostStatus.ts"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheChat_ReadsNoRawErrorMessage()
    {
        var provider = WebviewRebuildTests.TsCode("chatViewProvider.ts");
        Assert.Contains("return hostErrorText(err);", WebviewRebuildTests.Body(provider, "private static errorText("), StringComparison.Ordinal);   // WITNESS
        Assert.Contains("const message = ChatViewProvider.errorText(err);", WebviewRebuildTests.Body(provider, "private async chatTurn("), StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(provider, @"\berr\.message\b"));
    }
}
