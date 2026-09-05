using Inferpal.Localization;
using Inferpal.Services.Rag;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// "Nothing found" and "the semantic half never ran" came out as the same words.
/// </summary>
/// <remarks>
/// <c>ProjectIndexService.SearchAsync</c> skips its vector side when the embedding is null - the
/// embedding model was never pulled, the backend is down, the breaker is open - and both tools then
/// answered "No relevant code found for …". A flat negative: a model reading it concludes the code
/// does not exist and stops looking, and the user sees an assistant that cannot find anything in
/// their own repository. The 1.6.8 class: an absent capability rendered as a result.
/// </remarks>
public class SearchDegradationTests
{
    [Fact]
    public void AnEmbeddingThatCameBack_MeansTheFullSearchRan()
        => Assert.Equal(SemanticSide.Ran, SearchDegradation.Classify(true, [0.1f, 0.2f]));

    /// <summary>Setting off: the keyword result is what the user asked for. They are still reminded,
    /// otherwise they conclude the code is absent.</summary>
    [Fact]
    public void NoEmbeddingBecauseTheUserTurnedItOff_IsNotAFailure()
        => Assert.Equal(SemanticSide.DisabledByUser, SearchDegradation.Classify(false, null));

    /// <summary>An open breaker or a missing model is NOT a user choice. Confusing the two would
    /// tell a failure "you turned it off".</summary>
    [Fact]
    public void NoEmbeddingWhileItWasRequested_IsAFailure()
        => Assert.Equal(SemanticSide.EmbeddingUnavailable, SearchDegradation.Classify(true, null));

    /// <summary>An empty array is not an embedding: it is the fallback of a provider that answered
    /// without computing anything.</summary>
    [Fact]
    public void AnEmptyEmbedding_CountsAsUnavailable()
        => Assert.Equal(SemanticSide.EmbeddingUnavailable, SearchDegradation.Classify(true, []));

    /// <summary>Ordinary path: nothing is added. A channel that speaks when all is well stops being
    /// read, and here it would pollute the model's context on every fruitless search.</summary>
    [Fact]
    public void WhenTheFullSearchRan_TheMessageIsUntouched()
        => Assert.Equal("nothing", SearchDegradation.Explain("nothing", SemanticSide.Ran, "nomic-embed-text"));

    [Fact]
    public void ADegradedSearch_KeepsTheResultAndAddsWhatWasNotLookedAt()
    {
        var off = SearchDegradation.Explain("nothing", SemanticSide.DisabledByUser, "nomic-embed-text");
        Assert.StartsWith("nothing", off);
        Assert.Contains(Strings.SearchKeywordOnlySemanticOff, off);

        var down = SearchDegradation.Explain("nothing", SemanticSide.EmbeddingUnavailable, "my-model");
        Assert.StartsWith("nothing", down);
        Assert.Contains("my-model", down);   // the failure NAMES the model that did not answer
        Assert.NotEqual(off, down);          // and the two causes do not come out as the same words
    }
}
