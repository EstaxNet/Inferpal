using System;
using System.IO;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  VS Code's settings panel stores a slash command and an agent tool under the name the Core reads.
//
//  The Visual Studio panel adds the '/' of a command typed without it and writes a tool's name as the
//  Core does (lower case, spaces as '_'). VS Code's form stored the name as typed: "secreview" was
//  saved, listed as an enabled command, and never existed — the Core drops a template without '/';
//  "My Tool" beside "my_tool" passed the duplicate check and the Core kept only one.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public sealed class SettingsNameNormalisationTests
{
    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return SettingsSchemaDriftTests.NeutralizeTypeScriptComments(
            File.ReadAllText(Path.Combine(dir!.FullName, "vscode", "src", "webview", "settingsEditors.ts")));
    }

    private static string Entry(string source, string key)
    {
        var at = source.IndexOf($"  {key}: {{", StringComparison.Ordinal);
        Assert.True(at >= 0, $"The '{key}' list is not found in settingsEditors.ts.");   // WITNESS
        var end = source.IndexOf("\n  },", at, StringComparison.Ordinal);
        return source[at..end];
    }

    [Fact]
    public void ASlashCommandTypedWithoutItsSlash_IsGivenOne()
    {
        var entry = Entry(Source(), "promptTemplates");

        Assert.Contains("normalize:", entry, StringComparison.Ordinal);
        Assert.Contains("'/' + n", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void AToolName_IsWrittenAsTheCoreReadsIt()
    {
        var entry = Entry(Source(), "customTools");

        Assert.Contains("n.toLowerCase().replace(/ /g, '_')", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFormStoresAndComparesTheNormalisedName()
    {
        var source = Source();

        Assert.Contains("const n = words.normalize(name.value.trim());", source, StringComparison.Ordinal);
        Assert.Contains("words.normalize(i.name).toLowerCase() === n.toLowerCase()", source, StringComparison.Ordinal);
    }
}
