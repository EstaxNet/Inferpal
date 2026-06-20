using System;
using System.IO;
using Inferpal.Config;
using Inferpal.Services;
using Xunit;

namespace Inferpal.Tests;

// Covers the system-prompt layering extracted from the tool-window VM: base prompt →
// persona → custom prompt → template suffix → pinned files → .inferpal project files
// → glob-scoped rules. Each layer must be independent and ordered.
public class SystemPromptBuilderTests : IDisposable
{
    private readonly string _root;

    public SystemPromptBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ob-prompt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteProjectFile(string relPath, string content)
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void BaseOnly_ReturnsBasePromptVerbatim()
    {
        var prompt = new SystemPromptBuilder(new InferpalConfig()).Build("BASE");
        Assert.Equal("BASE", prompt);
    }

    [Fact]
    public void Persona_AppendedOnlyWhenAutoSwitchEnabled()
    {
        var on  = new SystemPromptBuilder(new InferpalConfig { PersonaAutoSwitch = true  }).Build("BASE", language: "csharp");
        var off = new SystemPromptBuilder(new InferpalConfig { PersonaAutoSwitch = false }).Build("BASE", language: "csharp");

        Assert.Contains("idiomatic C#", on);
        Assert.DoesNotContain("idiomatic C#", off);
    }

    [Fact]
    public void UnknownLanguage_AppendsNothing()
    {
        var prompt = new SystemPromptBuilder(new InferpalConfig { PersonaAutoSwitch = true }).Build("BASE", language: "cobol");
        Assert.Equal("BASE", prompt);
    }

    [Fact]
    public void CustomPrompt_AndTemplateSuffix_AreAppendedInOrder()
    {
        var cfg    = new InferpalConfig { CustomSystemPrompt = "CUSTOM" };
        var prompt = new SystemPromptBuilder(cfg).Build("BASE", templateSuffix: "\n\nTEMPLATE");

        Assert.True(prompt.IndexOf("BASE",     StringComparison.Ordinal)
                  < prompt.IndexOf("CUSTOM",   StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("CUSTOM",   StringComparison.Ordinal)
                  < prompt.IndexOf("TEMPLATE", StringComparison.Ordinal));
    }

    [Fact]
    public void PinnedFiles_RespectDisabledPrefix_MissingFiles_AndMaxThree()
    {
        var a = WriteProjectFile("a.txt", "AAA");
        var b = WriteProjectFile("b.txt", "BBB");
        var c = WriteProjectFile("c.txt", "CCC");
        var d = WriteProjectFile("d.txt", "DDD");
        var cfg = new InferpalConfig
        {
            // '#' = disabled (skipped before the cap). The cap of 3 counts ENTRIES, not
            // readable files: the missing path consumes a slot, so only a and b make it.
            PinnedContextFiles = $"#{c}\n{Path.Combine(_root, "missing.txt")}\n{a}\n{b}\n{d}",
        };

        var prompt = new SystemPromptBuilder(cfg).Build("BASE");

        Assert.Contains("## Pinned: a.txt", prompt);
        Assert.Contains("BBB", prompt);
        Assert.DoesNotContain("CCC", prompt);   // '#'-disabled entry
        Assert.DoesNotContain("DDD", prompt);   // beyond the cap (missing.txt used a slot)
    }

    [Fact]
    public void ProjectFiles_AppendTitledSections()
    {
        WriteProjectFile(Path.Combine(".inferpal", "context.md"), "CTX");
        WriteProjectFile(Path.Combine(".inferpal", "memory.md"),  "MEM");
        WriteProjectFile(Path.Combine(".inferpal", "notes.md"),   "NOTES");

        var prompt = new SystemPromptBuilder(new InferpalConfig()).Build("BASE", projectRoot: _root);

        Assert.Contains("## Project context\n\nCTX", prompt);
        Assert.Contains("## Agent memory\n\nMEM",    prompt);
        Assert.Contains("## Project notes\n\nNOTES", prompt);
    }

    [Fact]
    public void Rules_AreScopedByGlob_AgainstActiveFile()
    {
        WriteProjectFile(Path.Combine(".inferpal", "rules", "csharp.md"),
            "---\ndescription: CS rule\nglobs: **/*.cs\n---\nUse PascalCase.");
        WriteProjectFile(Path.Combine(".inferpal", "rules", "always.md"),
            "---\ndescription: Always rule\nalwaysApply: true\n---\nBe concise.");
        var builder = new SystemPromptBuilder(new InferpalConfig());

        var forCs = builder.Build("BASE", projectRoot: _root, activeFileRelPath: "src/Program.cs");
        Assert.Contains("Use PascalCase.", forCs);
        Assert.Contains("Be concise.", forCs);

        var forTs = builder.Build("BASE", projectRoot: _root, activeFileRelPath: "src/app.ts");
        Assert.DoesNotContain("Use PascalCase.", forTs);  // glob does not match the active file
        Assert.Contains("Be concise.", forTs);            // alwaysApply ignores the active file

        var noFile = builder.Build("BASE", projectRoot: _root, activeFileRelPath: null);
        Assert.DoesNotContain("Use PascalCase.", noFile);
        Assert.Contains("Be concise.", noFile);
    }

    [Fact]
    public void NullProjectRoot_SkipsProjectLayers()
    {
        var prompt = new SystemPromptBuilder(new InferpalConfig()).Build("BASE", projectRoot: null);
        Assert.Equal("BASE", prompt);
    }
}
