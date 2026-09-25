using System.IO;
using System.Text.Json;
using Inferpal.Models;
using Inferpal.Services.Agent;
using Inferpal.Services.Execution;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// What the model reads when a tool throws — the cause, and the only advice that is true about it.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Raw measurement, before the fix</b> (throwaway probe, verbatim):
/// <c>Error: the 'search_codebase' call failed — The type initializer for
/// 'Microsoft.Data.Sqlite.SqliteConnection' threw an exception.. Check the arguments against the
/// tool's schema and try again.</c> Three things wrong in one sentence: the cause is the
/// <b>wrapper's</b> (a fixed phrase; the real one is an <c>InnerException</c> below), the advice
/// names the arguments although they had nothing to do with the failure, and the double period
/// betrays a sentence assembled without being read back.
/// </para>
/// <para>
/// ⚠ <b>This is the class an earlier fix had closed elsewhere</b>: <c>Diagnostics.RootMessage</c> was
/// written for the indexing status line and not applied to the funnel that serves <b>all 28
/// tools</b> plus the MCP ones. A fix that closes the instance you saw leaves alive the class you
/// did not look for.
/// </para>
/// <para>
/// ⚠ <b>And advice that is wrong costs rounds</b>: "check the arguments" sends the model rewriting a
/// perfectly good call, again and again, against a failure that will not move. The discriminator is
/// the framework's own type hierarchy on the <b>root</b> exception (<c>ArgumentException</c> — what
/// <c>PathSanitizer</c> and every parameter guard throw —, <c>FormatException</c>,
/// <c>JsonException</c>), never a list of our own names.
/// </para>
/// </remarks>
public class WrappedToolFailureTests
{
    private sealed class ThrowingRegistry(Exception toThrow) : IToolRegistry
    {
        public IReadOnlyList<ToolDefinition> Definitions => [];
        public DiffInfo? ConsumeDiff() => null;
        public Task<string> ExecuteAsync(string name, JsonElement args, CancellationToken ct) =>
            throw toThrow;
    }

    private static JsonElement NoArgs => JsonDocument.Parse("{}").RootElement;

    private static Task<string> Failing(Exception ex, string tool = "search_codebase") =>
        AgentOrchestrator.ExecuteToolSafeAsync(new ThrowingRegistry(ex), tool, NoArgs,
                                               CancellationToken.None);

    private static TypeInitializationException StaticCtorFailure(string cause) =>
        new("Microsoft.Data.Sqlite.SqliteConnection", new FileNotFoundException(cause));

    // ── The cause ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailureInsideAStaticCtor_NamesTheRealCause()
    {
        var said = await Failing(StaticCtorFailure("e_sqlite3 was not found next to the extension"));

        Assert.Contains("e_sqlite3 was not found next to the extension", said, StringComparison.Ordinal);
        Assert.DoesNotContain("type initializer", said, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search_codebase", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryFailure_KeepsItsOwnMessage()
    {
        // REFERENCE ARM: only wrappers are unwrapped. An exception carrying an InnerException for
        // context keeps ITS message — that is the one that makes sense, and `PathSanitizer` builds
        // exactly that shape.
        var said = await Failing(new ArgumentException(
            "the path 'C:\\ailleurs\\x.cs' is outside the workspace root",
            new IOException("plumbing")));

        Assert.Contains("outside the workspace root", said, StringComparison.Ordinal);
        Assert.DoesNotContain("plumbing", said, StringComparison.Ordinal);
    }

    // ── The advice ───────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArgumentError_StillTellsTheModelToFixItsArguments()
    {
        // The common case, and the one this repository documents: a small model omitting a
        // required argument.
        var said = await Failing(new ArgumentException("path is required"), "read_file");

        Assert.Contains("arguments against the tool's schema", said, StringComparison.Ordinal);
        Assert.Contains("try again", said, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailureThatHasNothingToDoWithTheArguments_SaysSo()
    {
        var said = await Failing(StaticCtorFailure("e_sqlite3 was not found"));

        Assert.DoesNotContain("arguments against the tool's schema", said, StringComparison.Ordinal);
        Assert.Contains("not the cause", said, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(typeof(IOException),               false)]
    [InlineData(typeof(NullReferenceException),    false)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(ArgumentNullException),     true)]
    [InlineData(typeof(ArgumentOutOfRangeException), true)]
    [InlineData(typeof(FormatException),           true)]
    public void TheDiscriminatorIsTheFrameworksOwnHierarchy(Type type, bool aboutArguments)
    {
        var ex = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(aboutArguments, ToolFailure.IsAboutTheArguments(ex));
        // And it is read on the ROOT: wrapped, the verdict does not change.
        Assert.Equal(aboutArguments,
            ToolFailure.IsAboutTheArguments(new System.Reflection.TargetInvocationException(ex)));
    }

    /// <summary>
    /// A failure of ONE file — read-only (a TFVC or Perforce workspace keeps files read-only until checked out),
    /// locked by another program, protected — was answered "continue without this tool": the model then gave up every
    /// write of its task, when only that file is out of reach. The environmental failure keeps its advice.
    /// </summary>
    [Theory]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(IOException))]
    public void AFailureOfOneFile_DoesNotRetireTheTool(Type type)
    {
        var said = ToolFailure.Describe("write_file", (Exception)Activator.CreateInstance(type, "Access to the path 'a.cs' is denied.")!);

        Assert.DoesNotContain("continue without this tool", said, StringComparison.Ordinal);
        Assert.Contains("other files", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEnvironmentalFailure_StillRetiresTheTool()
    {
        // Reference arm: a missing native library breaks the tool for every call.
        Assert.Contains("continue without this tool", await Failing(StaticCtorFailure("e_sqlite3 was not found")),
                        StringComparison.Ordinal);
    }

    // ── The sentence itself ──────────────────────────────────────────────────

    [Fact]
    public void ACauseThatAlreadyEndsWithAPeriod_DoesNotGetASecond()
    {
        // The ".." of the raw measurement: a sentence assembled without being read back.
        var said = ToolFailure.Describe("read_file", new IOException("the file is locked."));

        Assert.DoesNotContain("..", said, StringComparison.Ordinal);
        Assert.Contains("the file is locked.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExceptionWithNoMessageAtAll_StillNamesSomething()
    {
        var said = ToolFailure.Describe("read_file", new NoMessage());

        Assert.Contains("NoMessage", said, StringComparison.Ordinal);
    }

    private sealed class NoMessage : Exception
    {
        public override string Message => string.Empty;
    }

    // ── BOTH producers ───────────────────────────────────────────────────────

    [Fact]
    public void BothProducersDescribeTheFailureTheSameWay()
    {
        // ⚠ The registry catches what a tool throws, the agent funnel catches what escapes a
        // registry (decorators, an MCP registry, EmptyToolRegistry): same situation, same reader.
        // Without this, one failure reads two ways depending on which floor saw it.
        var code = ConventionCoverageTests.CodeOnly(Path.Combine(
            RepoRoot(), "Inferpal.Core", "Services", "Execution", "ToolRegistry.cs"));

        // WITNESS: this really is the file holding the general catch of a tool execution.
        Assert.Contains("RecordToolCall", code, StringComparison.Ordinal);

        Assert.Contains("ToolFailure.Describe(name, ex)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("error: {ex.Message}", code, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
