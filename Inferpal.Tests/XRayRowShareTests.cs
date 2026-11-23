using System.Text.RegularExpressions;
using Inferpal.Config;
using Inferpal.Services.Commands;
using Inferpal.Services.Prompting;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// <c>/xray</c> shares its rows as the panel does: each row's share of the rows listed, so that they add up to the whole —
/// a layer the history already carries is still listed, and counted once in the budget line only.
/// </summary>
[Collection(CultureSerialCollection.Name)]
public class XRayRowShareTests
{
    [Fact]
    public void TheRowsShares_AddUpToTheWhole_WhenTheQuestionsLayersAreInTheHistory()
    {
        var sections = new List<PromptSection>
        {
            new(PromptSectionKind.Base, null, new string('a', 400)),
            new(PromptSectionKind.Persona, "fr", new string('b', 400), Placement: PromptPlacement.Question),
        };
        var md = XRayCommandHandler.Handle(sections, historyTokens: 0, contextWindow: 0, new InferpalConfig(),
                                           questionLayersInHistory: true);
        var shares = Regex.Matches(md, @"\s(\d+)%").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        Assert.Equal([50, 50], shares);
    }
}
