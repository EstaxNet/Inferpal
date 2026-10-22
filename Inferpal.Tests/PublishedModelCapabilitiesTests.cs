using System.IO;
using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Services.Commands;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A model that answers no question is offered to no chat list: what the server publishes decides first, the name after.
/// </summary>
/// <remarks>
/// ⚠ Read from names alone, a decision model (Ollama publishes it with <c>decision</c> alone) and an embedding model whose
/// name carries no keyword were offered as chat models, and picked, every turn ended on the server's refusal. What the
/// server publishes travels with its listing (<see cref="ModelsByKind"/>): nothing is remembered between two listings.
/// </remarks>
public class PublishedModelCapabilitiesTests
{
    private const string Published = """
        {"models":[
          {"name":"decision-1b:latest","size":1,"capabilities":["decision"]},
          {"name":"gte-large:latest","size":2,"capabilities":["embedding"]},
          {"name":"coder:7b","size":3,"capabilities":["completion","tools","insert"]},
          {"name":"both-e5:1b","size":4,"capabilities":["completion","embedding"]},
          {"name":"bge-unread:1b","size":5,"capabilities":[]}]}
        """;

    private static (LoopbackHttpServer Server, OllamaClient Client) Ollama(string tags)
    {
        var server = new LoopbackHttpServer(path => path.StartsWith("/api/tags", StringComparison.Ordinal) ? tags : null);
        return (server, new OllamaClient(new InferpalConfig { Provider = "ollama", BaseUrl = server.BaseUrl }));
    }

    [Fact]
    public async Task WhatOllamaPublishes_DecidesWhichListAModelIsOfferedIn()
    {
        var (server, client) = Ollama(Published);
        using var _ = server;
        var listed = await client.ListModelsByKindAsync(CancellationToken.None);

        Assert.Equal(["decision-1b:latest", "gte-large:latest", "coder:7b", "both-e5:1b", "bge-unread:1b"], listed.Listed);
        // Published for both, a model stays a chat model; an empty list says nothing, and the name decides.
        Assert.Equal(["coder:7b", "both-e5:1b"], listed.Chat);
        Assert.Equal(["gte-large:latest", "both-e5:1b", "bge-unread:1b"], listed.Embedding);

        // /bench, /arena and the model advisor read the installed listing: the same split.
        var installed = await client.ListInstalledModelsAsync(CancellationToken.None);
        Assert.Equal(listed.Chat, ModelsByKind.Of(installed).Chat);
    }

    /// <summary>A name typed after <c>/model</c> is judged on a listing read then: nothing in the process listed before.</summary>
    [Fact]
    public async Task TypedModel_IsJudgedOnTheListingReadNow()
    {
        var (server, client) = Ollama(Published);
        using var _ = server;

        Assert.Equal(Strings.SlashModelNotForChat("decision-1b"), await ModelsCommandHandler.RefusedForChatAsync(client, "decision-1b", CancellationToken.None));
        Assert.Equal(Strings.SlashModelIsEmbedding("gte-large"), await ModelsCommandHandler.RefusedForChatAsync(client, "gte-large", CancellationToken.None));
        Assert.Null(await ModelsCommandHandler.RefusedForChatAsync(client, "coder:7b", CancellationToken.None));
        Assert.Null(await ModelsCommandHandler.RefusedForChatAsync(client, "both-e5:1b", CancellationToken.None));

        // A name the server does not list is left to its name, as before.
        Assert.Null(await ModelsCommandHandler.RefusedForChatAsync(client, "elsewhere:8b", CancellationToken.None));
        Assert.Equal(Strings.SlashModelIsEmbedding("nomic-embed-text"),
                     await ModelsCommandHandler.RefusedForChatAsync(client, "nomic-embed-text", CancellationToken.None));
    }

    /// <summary>Reference arm: an Ollama that publishes nothing is split by the names, as before.</summary>
    [Fact]
    public async Task WithoutPublishedCapabilities_TheNameDecides()
    {
        var (server, client) = Ollama("""{"models":[{"name":"gte-large"},{"name":"chat:8b"},{"name":"bge-m3"}]}""");
        using var _ = server;
        var listed = await client.ListModelsByKindAsync(CancellationToken.None);

        Assert.Equal(["gte-large", "chat:8b"], listed.Chat);
        Assert.Equal(["bge-m3"], listed.Embedding);
        Assert.Null(await ModelsCommandHandler.RefusedForChatAsync(client, "gte-large", CancellationToken.None));
    }

    /// <summary>Every choice of a chat model reads what a listing says it can be picked for: "not an embedding" lets a
    /// decision model through.</summary>
    [Fact]
    public void NoProductCode_ChoosesAChatModelAsNotAnEmbedding()
    {
        var root = ConventionCoverageTests.RepoRoot();
        var files = new[] { "Inferpal.Core", "Inferpal", "Inferpal.Host" }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count > 300, $"only {files.Count} product sources read: the projects moved");   // WITNESS

        // shortcut: the spelling "!…IsEmbeddingModel(" only; "== false" or Except(…) would pass — none exists today.
        var negated = new Regex(@"!\s*(?:[\w.]+\.)?IsEmbeddingModel\(", RegexOptions.CultureInvariant);
        var offenders = files.Where(f => negated.IsMatch(ConventionCoverageTests.CodeOnly(f)))
                             .Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.Empty(offenders);
    }
}
