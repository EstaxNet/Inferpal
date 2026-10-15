using System.IO;
using Inferpal.Config;
using Inferpal.Localization;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Reading the settings file never changes the interface language: the adapter that owns the process applies it.
/// </summary>
/// <remarks>
/// Every read of the file applied the language it holds, "Auto" meaning the system's. In the VS Code host, whose language
/// is the editor's, anything that read the file put it back to the system's — the MCP servers at each refresh (they read
/// the shared list), a thread following the file at each question: menus, notices and the language the model answers in
/// changed under the user, from the editor's to Windows'.
/// </remarks>
[Collection(CultureSerialCollection.Name)]
public sealed class SettingsReadKeepsTheLanguageTests
{
    [Fact]
    public void ReadingTheSettings_LeavesTheLanguageTheEditorChose()
    {
        try
        {
            Strings.ApplyLanguage("ja");                         // what the VS Code host applied from the editor's locale

            _ = InferpalConfig.LoadOwnCopy();
            _ = InferpalConfig.LoadOwnCopyToFollow();
            _ = InferpalConfig.Load();

            Assert.Equal("ja", Strings.OverrideCulture?.Name);
        }
        finally { Strings.ApplyLanguage(null); }
    }

    [Fact]
    public void TheAdaptersThatOwnAProcess_ApplyTheLanguageThemselves()
    {
        var root = ConventionCoverageTests.RepoRoot();
        string Code(params string[] parts) => ConventionCoverageTests.CodeOnly(Path.Combine([root, .. parts]));

        // The reader applies none…
        Assert.DoesNotContain("ApplyLanguage(", Code("Inferpal.Core", "Config", "InferpalConfig.cs"), StringComparison.Ordinal);
        // …and each process that shows text applies its own: Visual Studio from its settings, the VS Code and ACP host
        // from the editor's locale or the settings, the terminal setup from its settings.
        Assert.Contains("Localization.Strings.ApplyLanguage(config.Language);", Code("Inferpal", "InferpalExtension.cs"), StringComparison.Ordinal);
        Assert.Contains("Strings.ApplyLanguage(string.IsNullOrEmpty(config.Language) ? _editorLocale : config.Language)",
                        Code("Inferpal.Host", "HostServer.cs"), StringComparison.Ordinal);
        Assert.Contains("Strings.ApplyLanguage(config.Language);", Code("Inferpal.Host", "Acp", "AcpSetup.cs"), StringComparison.Ordinal);
    }
}
