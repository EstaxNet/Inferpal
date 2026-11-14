using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A green pytest run narrowed by <c>path</c> is compared with what pytest itself collects for the whole suite: fewer tests
/// ran ⇒ a "Not run:" note, read as part of the suite by the result bar and <c>/tdd</c>. A count that could not be made
/// reads as part too — nothing proved the whole. The outputs below are pytest 9.2's, as it printed them.
/// </summary>
public sealed class PytestPartOfTheSuiteTests
{
    private const string RanTwo = """
        platform win32 -- Python 3.14.5, pytest-9.2.0, pluggy-1.7.0
        rootdir: C:\proj
        configfile: pytest.ini
        collected 2 items

        tests\test_a.py ..                                                       [100%]

        ============================== 2 passed in 0.04s ==============================
        """;

    private const string CollectedThree = """
        tests/test_a.py::test_one
        tests/test_a.py::test_two
        tests/test_b.py::test_three

        3 tests collected in 0.01s
        """;

    [Fact]
    public void FewerTestsThanTheSuite_AreNamedAsNotRun_AndReadAsPart()
    {
        var note = RunTestsTool.OtherPytestTests("tests/test_a.py", RanTwo, CollectedThree, 0);

        Assert.Equal("Note: Only tests/test_a.py ran: 2 of the 3 tests pytest collects. Not run: 1 other test — omit 'path' "
                     + "to run the whole suite.\n\n", note);
        Assert.True(RunTestsTool.RanPartOfTheSuite(note + "✓ Tests passed (pytest): 2 passed", filter: null));
    }

    [Fact]
    public void ARunThatTookEveryTest_SaysNothing()
    {
        // Reference arm: the path held the whole suite.
        Assert.Null(RunTestsTool.OtherPytestTests("tests", RanTwo.Replace("collected 2 items", "collected 3 items"), CollectedThree, 0));
    }

    [Fact]
    public void UnderAddoptsVerbose_BothCountsAreRead()
    {
        // pytest 9.2 with addopts = -v: the run's count after "collecting ...", the suite's framed in "=".
        var note = RunTestsTool.OtherPytestTests("tests/test_a.py",
            "collecting ... collected 2 items\n\ntests/test_a.py::test_one PASSED [ 50%]\n\n=== 2 passed in 0.03s ===",
            "\n========================= 3 tests collected in 0.03s ==========================\n", 0);

        Assert.Contains("ran: 2 of the 3 tests pytest collects. Not run: 1 other test", note);
    }

    [Fact]
    public void ARunThatPrintsNoCount_ReadsAsPart()
    {
        // pytest 9.2 with addopts = -q prints no "collected N items" for the run: nothing proved the whole.
        var note = RunTestsTool.OtherPytestTests("tests/test_a.py", "2 passed in 0.01s\n", CollectedThree, 0);

        Assert.Contains("could not be counted", note);
        Assert.True(RunTestsTool.RanPartOfTheSuite(note + "✓ Tests passed (pytest): 2 passed", filter: null));
    }

    [Theory]
    [InlineData("tests/test_b.py::test_three\n\n3 tests collected, 1 error in 0.12s\n", 2)]   // a module that does not import
    [InlineData("", -1)]                                                                       // killed at its budget
    public void ACountThatCouldNotBeMade_IsSaid_AndReadsAsPart(string collect, int exit)
    {
        var note = RunTestsTool.OtherPytestTests("tests/test_a.py", RanTwo, collect, exit);

        Assert.NotNull(note);
        Assert.Contains("could not be counted", note);
        Assert.True(RunTestsTool.RanPartOfTheSuite(note + "✓ Tests passed (pytest): 2 passed", filter: null));
    }
}
