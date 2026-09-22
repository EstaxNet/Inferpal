using System.IO;
using System.Linq;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Docs;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A sub-command <c>/docs</c> does not recognise is <b>named</b>, never quietly served as the list.
/// </summary>
/// <remarks>
/// <para>
/// ⭐ The rule is written, with its reason, at the other site that has it — <c>/diagnostics</c>:
/// <i>"a mistyped argument that fell back to the list left <c>of</c> with no effect on file logging,
/// without a word."</i> That handler checks the exact shapes before dispatching; <c>/docs</c> fell
/// through to <c>default:</c>, which lists the sources.
/// </para>
/// <para>
/// ⚠ A listing is the worst possible silent answer here, because it looks like a success.
/// <c>/docs remov &lt;id&gt;</c> returns the sources — the one the user meant to delete still among
/// them — with nothing said, so the reply reads as "here is your state after the command" rather
/// than "that word means nothing to me". Same for <c>/docs reindx</c>: no crawl starts, and the list
/// that comes back looks like confirmation.
/// </para>
/// <para>
/// ⚠ The reference arms matter as much as the assertion: a bare <c>/docs</c> and an explicit
/// <c>/docs list</c> must still list, or the guard would break the command it protects.
/// </para>
/// </remarks>
[Collection(DocsDatabaseCollection.Name)]
public sealed class DocsUnknownSubCommandTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "inferpal-tests", $"docssub-{Guid.NewGuid():N}");

    public DocsUnknownSubCommandTests() => DocsDatabase.OverrideDirForTests = _dir;

    public void Dispose()
    {
        DocsDatabase.OverrideDirForTests = null;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>A configuration holding one source, so the list has something to show.</summary>
    private static InferpalConfig ConfigWithASource()
    {
        var site = DocSite.Create("https://docs.example.com/", "Example");
        return new InferpalConfig { DocSitesJson = DocSite.Serialize([site]) };
    }

    private static Task<string> RunAsync(InferpalConfig config, params string[] parts) =>
        DocsCommandHandler.HandleAsync(
            config,
            new DocsIndexService(new FakeInferenceProvider(), config),
            parts,
            new Progress<string>(_ => { }),
            CancellationToken.None);

    [Fact]
    public async Task AMistypedSubCommand_IsNamed_NotServedAsTheList()
    {
        var config = ConfigWithASource();

        var reply = await RunAsync(config, "/docs", "reindx");

        // WITNESS: the source really is listable, so "does not contain it" is a refusal and not an
        // empty corpus answering for us.
        var listed = await RunAsync(config, "/docs", "list");
        Assert.Contains("Example", listed, StringComparison.Ordinal);

        Assert.DoesNotContain("Example", reply, StringComparison.Ordinal);
        Assert.Contains("/docs", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMistypedRemove_DoesNotAnswerWithTheSourceStillThere()
    {
        // The dangerous shape: the reply to `remov` was the list, with the source the user meant to
        // delete still in it and nothing saying the word was not understood.
        var reply = await RunAsync(ConfigWithASource(), "/docs", "remov", "example");

        Assert.DoesNotContain("Example", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABareDocs_StillLists()
    {
        // REFERENCE ARM: the default sub-command is `list`, and the guard must not break it.
        var reply = await RunAsync(ConfigWithASource(), "/docs");

        Assert.Contains("Example", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplicitList_StillLists()
    {
        // REFERENCE ARM: `list` is a real sub-command, not just the fallback it used to share.
        var reply = await RunAsync(ConfigWithASource(), "/docs", "list");

        Assert.Contains("Example", reply, StringComparison.Ordinal);
    }
}
