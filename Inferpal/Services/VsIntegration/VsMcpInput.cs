using Inferpal.Localization;
using Inferpal.Services.Mcp;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Inferpal.Services.VsIntegration;

/// <summary>Asks the user, in Visual Studio's own input prompt, for a value a repository's MCP server declares
/// (<c>${input:id}</c>).</summary>
/// <remarks>⚠ Visual Studio's input prompt cannot hide what is typed: for a value marked <c>password</c>, the question
/// says so, so nobody types a secret believing it hidden.</remarks>
internal static class VsMcpInput
{
    public static async Task<string?> AskAsync(VisualStudioExtensibility vs, RepoMcpServer server, RepoMcpInput input, CancellationToken ct)
    {
        var question = Strings.McpRepoInputQuestion(server.Name, input.Description)
                       + (input.Password ? " " + Strings.McpRepoInputNotMasked : "");
        var answer = await vs.Shell().ShowPromptAsync(question, new InputPromptOptions { DefaultText = input.Default ?? "" }, ct);
        return string.IsNullOrEmpty(answer) ? null : answer;
    }
}
