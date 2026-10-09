using System.IO;
using Inferpal.Config;
using Inferpal.Services.CodeActions;
using Inferpal.Services.Execution;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// Writing a file a repository gives other coding agents as instructions — AGENTS.md, CLAUDE.md, Copilot's, Cursor's,
/// Cline's, Roo's, Continue's — always reaches the human, like a write to Inferpal's own <c>.inferpal/</c>.
/// </summary>
/// <remarks>
/// These files are read back as instructions: by the next question's prompt, and by every other agent the user runs
/// in that repository. A silent write there is the persistence half of a prompt-injection chain. The list is the table
/// of formats — the property below asks the discovery what it reads, never a second list.
/// </remarks>
public sealed class RepoInstructionWriteTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "inferpal-tests", $"repo-write-{Guid.NewGuid():N}");

    public RepoInstructionWriteTests() => Directory.CreateDirectory(Path.Combine(_repo, ".git"));

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }

    private string Write(string relative)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "Use xUnit.\n");
        return path;
    }

    [Fact]
    public void EveryFileTheDiscoveryReads_IsAWriteThatForcesThePrompt()
    {
        // One file per row of the table, nested where the row allows it, and an AGENTS.md down the chain.
        var folders = RepoInstructionFormats.All.Where(f => f.Placement == RepoInstructionPlacement.Folder)
                                                .Select(f => f.Location).ToHashSet();
        foreach (var format in RepoInstructionFormats.All)
        {
            if (format.Placement != RepoInstructionPlacement.Folder)
            {
                if (!folders.Contains(format.Location)) Write(format.Location);   // .clinerules: a file OR a folder
                continue;
            }
            var name = format.FilePatterns![0].Replace("*", "rule");
            if (name == "rule") name = "rule.txt";
            Write(format.Location + "/" + name);
            if (format.Recursive) Write(format.Location + "/topic/" + name);
        }
        var active = Write("src/Api/AGENTS.md");

        var read = RepoInstructionDiscovery.Discover(_repo, Path.Combine(_repo, "src", "Api", "X.cs")).Sources;

        // Witness: every row of the table was read at least once, or the property measures less than it claims.
        Assert.Equal(RepoInstructionFormats.All.Count - 2,                     // .clinerules as a file, .roorules: replaced
                     read.Select(s => s.Format).Distinct().Count());
        Assert.Contains(read, s => s.Path == active);
        Assert.Empty(read.Where(s => !AgentInstructionFiles.Targets(s.Path)).Select(s => s.Path));
    }

    [Theory]
    [InlineData(@"C:\repo\AGENTS.md")]
    [InlineData(@"C:\repo\src\Api\AGENTS.md")]
    [InlineData("/home/dev/repo/.github/copilot-instructions.md")]
    [InlineData(@"C:\repo\.github\instructions\backend\api.instructions.md")]
    [InlineData(@"C:\repo\CLAUDE.md")]
    [InlineData(@"C:\repo\.claude\rules\build\ci.md")]
    [InlineData(@"C:\repo\.cursor\rules\components.mdc")]
    [InlineData(@"C:\repo\.cursorrules")]
    [InlineData(@"C:\repo\.clinerules")]
    [InlineData(@"C:\repo\.clinerules\01-style.md")]
    [InlineData(@"C:\repo\.roo\rules\naming.txt")]
    [InlineData(@"C:\repo\.continue\rules\logging.md")]
    // apply_edits and rename_symbol join their paths with newlines: one is enough.
    [InlineData("C:\\repo\\src\\A.cs\nC:\\repo\\AGENTS.md")]
    public void AWriteAtARepositoryInstructionFile_IsRecognised(string subject) =>
        Assert.True(AgentInstructionFiles.Targets(subject));

    [Theory]
    // Reference arm: the same names where no agent reads them, and ordinary files.
    [InlineData(@"C:\repo\docs\copilot-instructions.md")]
    [InlineData(@"C:\repo\.github\instructions\notes.md")]                 // not *.instructions.md
    [InlineData(@"C:\repo\.cursor\rules\readme.md")]                       // Cursor reads .mdc only there
    [InlineData(@"C:\repo\.continue\rules\nested\deep.md")]                // Continue's folder is not recursive
    [InlineData(@"C:\repo\.roo\rules\old.log")]                            // Roo never reads a log
    [InlineData(@"C:\repo\src\Program.cs")]
    [InlineData(@"C:\repo\README.md")]
    public void TheSameNamesWhereNoAgentReadsThem_AreOrdinaryWrites(string subject) =>
        Assert.False(AgentInstructionFiles.Targets(subject));

    [Fact]
    public async Task AnAllowRuleCoveringEveryWrite_StillAsksForAgentsMd()
    {
        // The cahier's criterion 6, through the approval funnel itself.
        var service = new Recording(new InferpalConfig { PermissionRules = "allow write_file .*" });
        const string source = @"C:\repo\src\Program.cs", agents = @"C:\repo\AGENTS.md";

        Assert.True(await service.RequestApprovalAsync("write_file", source, CancellationToken.None, subject: source));
        Assert.Equal(0, service.Prompts);                                      // the rule does cover ordinary files

        Assert.True(await service.RequestApprovalAsync("write_file", agents, CancellationToken.None, subject: agents));
        Assert.Equal(1, service.Prompts);
    }

    private sealed class Recording(InferpalConfig config) : ApprovalServiceBase(config, () => null)
    {
        public int Prompts;

        protected override Task<ApprovalDecision> PromptUserAsync(string message, DiffInfo? diff, CancellationToken ct)
        {
            Prompts++;
            return Task.FromResult(ApprovalDecision.Once);
        }
    }
}
