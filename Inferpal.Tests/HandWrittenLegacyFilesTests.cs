using System.IO;
using System.Text.Json;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Governance;
using Inferpal.Services.Persistence;
using Inferpal.Services.Prompting;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The files of <c>.inferpal/</c> are written BY HAND — the project memory, plans, rules — and an older editor
/// saves them in the machine's legacy code page. Read as UTF-8, every accent reached the model as "�"; and the two
/// tools that REWRITE such a file (update_memory appending, /plan done ticking a box) wrote that "�" back over the
/// user's own text — in the plan whose promise is that only the checkbox character changes.
/// </summary>
public sealed class HandWrittenLegacyFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inferpal-tests", "handwritten-" + Guid.NewGuid().ToString("N"));

    public HandWrittenLegacyFilesTests() => Directory.CreateDirectory(Path.Combine(_root, ".inferpal", "plans"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Inferpal(string relative) => Path.Combine(_root, ".inferpal", relative);

    // "é" is 0xE9 in Windows-1252 and 1250 alike, and not valid UTF-8 on its own.
    private static readonly byte[] Memory = [.. "# Notes\r\n- caf"u8, 0xE9, .. " au lait\r\n"u8];

    private sealed class YesApproval : IApprovalService
    {
        public Task<bool> RequestApprovalAsync(string toolName, string details, CancellationToken ct,
                                               string? subject = null, DiffInfo? diff = null, bool forcePrompt = false) =>
            Task.FromResult(true);
    }

    [Fact]
    public async Task UpdateMemory_AppendingToAHandWrittenLegacyMemory_KeepsItsAccents()
    {
        File.WriteAllBytes(Inferpal("memory.md"), Memory);
        var tool = new UpdateMemoryTool(new NullEditorSurface(), new YesApproval(), new FileHistoryService(), () => _root);
        using var args = JsonDocument.Parse("""{"content":"- the tests run with dotnet test"}""");

        await tool.ExecuteAsync(args.RootElement, CancellationToken.None);

        var text = File.ReadAllText(Inferpal("memory.md"), TextFileEncoding.LegacyEncoding);
        Assert.Contains("- the tests run with dotnet test", text);                           // witness: appended
        Assert.Contains("café au lait", text);
    }

    [Fact]
    public void PlanDone_OnAHandWrittenLegacyPlan_ChangesOnlyTheCheckbox()
    {
        var path = Inferpal(Path.Combine("plans", "ship-it.md"));
        File.WriteAllBytes(path, [.. "# Caf"u8, 0xE9, .. "\n\n- [ ] premi"u8, 0xE8, .. "re "u8, 0xE9, .. "tape\n"u8]);
        var before = File.ReadAllBytes(path);

        Assert.NotNull(PlanStore.SetStepDone(_root, "ship-it", 1, true));                   // witness: ticked

        var after = File.ReadAllBytes(path);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(1, before.Zip(after).Count(p => p.First != p.Second));
    }

    [Fact]
    public void TheSystemPrompt_ShowsTheAccentsOfAHandWrittenMemory()
    {
        File.WriteAllBytes(Inferpal("memory.md"), Memory);

        var prompt = new SystemPromptBuilder(new InferpalConfig()).Build("BASE", projectRoot: _root);

        Assert.Contains("# Notes", prompt);                                                   // witness: the memory is in
        Assert.Contains("café au lait", prompt);
        Assert.DoesNotContain("�", prompt);
    }

    [Fact]
    public void ARuleWrittenInALegacyCodePage_KeepsItsAccents()
    {
        Directory.CreateDirectory(Inferpal("rules"));
        File.WriteAllBytes(Inferpal(Path.Combine("rules", "langue.md")),
            [.. "---\nalwaysApply: true\n---\nToujours r"u8, 0xE9, .. "pondre en fran"u8, 0xE7, .. "ais.\n"u8]);

        var rule = Assert.Single(RulesService.Load(Inferpal("rules")));

        Assert.Contains("Toujours répondre en français.", rule.Body);
    }

    [Fact]
    public async Task Note_AppendedToAHandWrittenLegacyNotesFile_KeepsBothTheirAccents()
    {
        // Appended in UTF-8, the note made the file valid in neither encoding: read back in the legacy code page (the
        // bytes the user wrote are not UTF-8), the new note reached the prompt as "dÃ©cision".
        File.WriteAllBytes(Inferpal("notes.md"), [.. "- [2026-09-01 10:00] caf"u8, 0xE9, .. " au lait\n"u8]);

        await NotesCommandHandler.HandleNoteAsync(_root, ["/note", "décision", "prise"], new DateTime(2026, 9, 26, 12, 0, 0), CancellationToken.None);

        var notes = await NotesStore.ReadAsync(_root, CancellationToken.None);
        Assert.Contains("décision prise", notes);                                              // witness: appended
        Assert.Contains("café au lait", notes);
    }

    [Fact]
    public async Task Note_ACharacterTheLegacyFileCannotHold_IsRefusedByName_AndTheFileIsUntouched()
    {
        // No single-byte code page holds an emoji: encoded anyway, the fallback writes "?" in its place.
        byte[] before = [.. "- [2026-09-01 10:00] caf"u8, 0xE9, .. " au lait\n"u8];
        File.WriteAllBytes(Inferpal("notes.md"), before);

        var result = await NotesCommandHandler.HandleNoteAsync(_root, ["/note", "déployé", "🚀"], DateTime.Now, CancellationToken.None);

        Assert.Contains("🚀", result.Message);                                              // named: the character…
        Assert.Contains(TextFileEncoding.LegacyEncoding.WebName, result.Message);            // …and the code page
        Assert.False(result.RefreshSystemPrompt);
        Assert.Equal(before, File.ReadAllBytes(Inferpal("notes.md")));
    }

    [Fact]
    public async Task Note_OnAUtf8NotesFile_AppendsAsBefore()
    {
        // Reference arm: the ordinary file, UTF-8 — the emoji a legacy file refuses is written, the first note kept.
        await NotesCommandHandler.HandleNoteAsync(_root, ["/note", "première"], DateTime.Now, CancellationToken.None);
        var result = await NotesCommandHandler.HandleNoteAsync(_root, ["/note", "déployé", "🚀"], DateTime.Now, CancellationToken.None);

        var notes = await NotesStore.ReadAsync(_root, CancellationToken.None);
        Assert.True(result.RefreshSystemPrompt);
        Assert.Contains("première", notes);
        Assert.Contains("déployé 🚀", notes);
    }
}
