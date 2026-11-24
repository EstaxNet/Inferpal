using System.IO;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A query plan reads first as where its time goes (pari 151): each operator by its OWN share, the full scans, the estimates
/// far from what was read, the engine's warnings and missing indexes, then the tree. Fixtures: real plans from the probe's
/// engines (PostgreSQL 17 on Northwind, MariaDB 11.8, SQL Server LocalDB), estimated and measured.
/// </summary>
public class SqlPlanTextTests
{
    private static string Read(string name) =>
        SqlPlanText.Summarize(name, File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "sqlplan", name)), 12000)!;

    [Fact]
    public void APostgresPlan_RanksEachOperatorByItsOwnShare_NeverTheRootByItsTotal()
    {
        var text = Read("pg-explain.json");
        Assert.StartsWith("Query plan pg-explain.json (PostgreSQL, the planner's estimates only: nothing was run): total cost 75.77.", text);
        // The Sort at the root holds the whole cost in its total; its own share is small.
        Assert.Contains("1. Seq Scan on order_details d — cost 33.55 (44%)", text);
        // An estimated plan says what a scan returns, never what it reads: named by its share of the cost.
        Assert.Contains("Full scan: Seq Scan on order_details d, 44% of the plan's cost (an estimated plan does not say how many rows it reads; about 2,155 returned).", text);
        Assert.DoesNotContain("Seq Scan on customers c, ", text[text.IndexOf("Worth a look", StringComparison.Ordinal)..text.IndexOf("Plan:", StringComparison.Ordinal)]);
        Assert.Contains("\nPlan:\n  Sort, ~91 rows\n    Aggregate", text);
    }

    [Fact]
    public void AMeasuredPostgresPlan_RanksByTime_AndSaysWhatItsFilterRemoved()
    {
        var text = Read("pg-analyze.json");
        Assert.Contains("(PostgreSQL, with what the run measured)", text);
        Assert.Contains("planning 3.466 ms, execution 0.63 ms", text);
        Assert.Contains("Where the time went", text);
        Assert.Contains("Seq Scan on orders o — ", text);
        Assert.Contains("753 rows removed by it", text);
    }

    [Fact]
    public void AMariaDbPlan_ReadsItsTablesAndItsSort()
    {
        var text = Read("mariadb-analyze.json");
        Assert.Contains("(MariaDB, with what the run measured): total cost 21.024; execution 5.432 ms.", text);
        Assert.Contains("1. ALL on o — 3.018 ms", text);
        Assert.Contains("Full table scan: o, 20,000 rows read.", text);
        Assert.Contains("filesort by sum(o.total) desc", text);
        Assert.Contains("eq_ref on c using PRIMARY, 1 row (estimated 1)", text);
        Assert.Contains("Full table scan: o, 19,793 rows read (estimated).", Read("mariadb-explain.json"));
    }

    [Fact]
    public void ASqlServerPlan_NamesItsMissingIndex_AndChargesNoParentForAnUnmeasuredChild()
    {
        var actual = Read("mssql-actual.sqlplan");
        Assert.Contains("(SQL Server, with what the run measured): total cost 0.217.", actual);
        // The Sort's child (a Compute Scalar) has no counters: the Sort is not charged the whole subtree.
        Assert.Contains("1. Clustered Index Scan on p_orders using PK__p_orders__3213E83FCC0472DE — 2 ms", actual);
        Assert.Contains("Full scan: Clustered Index Scan on p_orders using PK__p_orders__3213E83FCC0472DE, 20,000 rows read.", actual);
        Assert.Contains("Missing index suggested by SQL Server (estimated impact 74%): p_orders (customer) include (country, total).", actual);
        Assert.Contains("(SQL Server, the planner's estimates only", Read("mssql-estimated.sqlplan"));
    }

    [Fact]
    public void AnEstimateFarFromWhatWasRead_IsNamed()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "Nested Loop", "Join Type": "Inner", "Total Cost": 120, "Plan Rows": 5, "Actual Rows": 48000,
                         "Actual Loops": 1, "Actual Total Time": 900,
              "Plans": [ { "Node Type": "Seq Scan", "Relation Name": "events", "Alias": "events", "Total Cost": 100, "Plan Rows": 5,
                           "Actual Rows": 48000, "Actual Loops": 1, "Actual Total Time": 850 } ] } }]
            """;
        var text = SqlPlanText.Summarize("slow.json", plan, 8000)!;
        Assert.Contains("Estimate off 9600x: Seq Scan on events — estimated 5 rows, got 48,000.", text);
        Assert.Contains("1. Seq Scan on events — 850 ms (94%)", text);
    }

    [Fact]
    public void AQueryResultHoldingAPlan_ReadsAsOne_AndAnyOtherCellDoesNot()
    {
        Assert.StartsWith("Query plan (PostgreSQL",
            SqlPlanText.FromQueryResult(File.ReadAllText(Path.Combine(ConversationPersistenceSilenceTests.RepoRoot(), "Inferpal.Tests", "Fixtures", "sqlplan", "pg-explain.json")), 8000));
        Assert.Null(SqlPlanText.FromQueryResult("""{ "name": "a" }""", 8000));
        Assert.Null(SqlPlanText.FromQueryResult("42", 8000));
    }

    [Fact]
    public void AnyOtherJsonOrXml_IsReadAsItsLines()
    {
        Assert.Null(SqlPlanText.Summarize("data.json", """[{ "Plan": "monthly", "price": 9 }]""", 8000));
        Assert.Null(SqlPlanText.Summarize("package.json", """{ "name": "shop" }""", 8000));
        Assert.Null(SqlPlanText.Summarize("plan.sqlplan", "<root/>", 8000));
        Assert.Null(SqlPlanText.Summarize("plan.txt", "[{\"Plan\": {}}]", 8000));
    }

    [Fact]
    public void ALoopsInnerSide_CostedPerLoop_IsChargedEveryLoop_NotLeftToTheLoop()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "Nested Loop", "Join Type": "Inner", "Total Cost": 8350, "Plan Rows": 1000,
              "Plans": [
                { "Node Type": "Seq Scan", "Parent Relationship": "Outer", "Relation Name": "a", "Total Cost": 15, "Plan Rows": 1000 },
                { "Node Type": "Index Scan", "Parent Relationship": "Inner", "Relation Name": "b", "Index Name": "b_pk", "Total Cost": 8.3, "Plan Rows": 1 } ] } }]
            """;
        var text = SqlPlanText.Summarize("loop.json", plan, 8000)!;
        Assert.DoesNotContain("1. Nested Loop", text);
        Assert.Contains("1. Index Scan on b using b_pk", text);
    }

    [Fact]
    public void AParallelWorkersTime_IsWallTime_NotTheirSum()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "Gather", "Total Cost": 100, "Plan Rows": 300, "Actual Rows": 300, "Actual Loops": 1, "Actual Total Time": 100,
              "Plans": [ { "Node Type": "Parallel Seq Scan", "Parent Relationship": "Outer", "Relation Name": "t", "Total Cost": 90,
                           "Plan Rows": 100, "Actual Rows": 100, "Actual Loops": 3, "Actual Total Time": 95 } ] } }]
            """;
        var text = SqlPlanText.Summarize("par.json", plan, 8000)!;
        Assert.Contains("1. Parallel Seq Scan on t — 95 ms (95%)", text);
        Assert.Contains("2. Gather — 5 ms (5%)", text);
    }

    [Fact]
    public void ACtesBody_IsChargedOnce()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "CTE Scan", "CTE Name": "c", "Total Cost": 50, "Plan Rows": 10, "Actual Rows": 10, "Actual Loops": 1, "Actual Total Time": 910,
              "Plans": [ { "Node Type": "Seq Scan", "Parent Relationship": "InitPlan", "Subplan Name": "CTE c", "Relation Name": "big",
                           "Total Cost": 40, "Plan Rows": 10, "Actual Rows": 10, "Actual Loops": 1, "Actual Total Time": 900 } ] } }]
            """;
        var text = SqlPlanText.Summarize("cte.json", plan, 8000)!;
        Assert.Contains("1. CTE c: Seq Scan on big — 900 ms (99%)", text);
        Assert.Contains("2. CTE Scan of c — 10 ms (1%)", text);
    }

    [Fact]
    public void AnOperatorThatNeverRan_IsNoWrongEstimate()
    {
        const string plan = """
            [{ "Plan": { "Node Type": "Index Scan", "Relation Name": "t", "Total Cost": 10, "Plan Rows": 500,
                         "Actual Rows": 0, "Actual Loops": 0, "Actual Total Time": 0 } }]
            """;
        var text = SqlPlanText.Summarize("never.json", plan, 8000)!;
        Assert.DoesNotContain("Estimate off", text);
    }

    [Fact]
    public void AManyPartitionPlan_StaysWithinItsBudget_AndSaysWhatItLeftOut()
    {
        var scans = string.Join(",", Enumerable.Range(0, 300).Select(i =>
            $"{{ \"Node Type\": \"Seq Scan\", \"Parent Relationship\": \"Member\", \"Relation Name\": \"events_p{i}\", \"Total Cost\": 10, \"Plan Rows\": 5000, \"Actual Rows\": 5000, \"Actual Loops\": 1, \"Actual Total Time\": 3, \"Filter\": \"{new string('x', 5000)}\" }}"));
        var plan = $"[{{ \"Plan\": {{ \"Node Type\": \"Append\", \"Total Cost\": 3000, \"Plan Rows\": 1500000, \"Actual Rows\": 1500000, \"Actual Loops\": 1, \"Actual Total Time\": 950, \"Plans\": [{scans}] }} }}]";
        var text = SqlPlanText.Summarize("parts.json", plan, 7000)!;
        Assert.True(text.Length <= 7000, text.Length.ToString());
        Assert.Contains("more]", text);
    }

    [Fact]
    public void APlanCutToFit_EndsOnItsCutNote_NeverOnTheRawNoteAfterIt()
    {
        // Ranked operators and "worth a look" past the budget: the text is cut at a line, and says it once.
        var scans = string.Join(",", Enumerable.Range(0, 300).Select(i =>
            $"{{ \"Node Type\": \"Seq Scan\", \"Parent Relationship\": \"Member\", \"Relation Name\": \"events_p{i}\", \"Total Cost\": 10, \"Plan Rows\": 5000, \"Actual Rows\": 5000, \"Actual Loops\": 1, \"Actual Total Time\": 3, \"Filter\": \"{new string('x', 5000)}\" }}"));
        var plan = $"[{{ \"Plan\": {{ \"Node Type\": \"Append\", \"Total Cost\": 3000, \"Plan Rows\": 1500000, \"Actual Rows\": 1500000, \"Actual Loops\": 1, \"Actual Total Time\": 950, \"Plans\": [{scans}] }} }}]";
        var text = SqlPlanText.Summarize("parts.json", plan, 1500)!;
        Assert.EndsWith("\n[Cut to fit: read_file with start_line pages the raw plan.]", text);
        Assert.DoesNotContain("[The raw plan:", text);
        Assert.True(text.Length <= 1500, text.Length.ToString());

        // A whole plan still points at the raw file.
        Assert.EndsWith("\n[The raw plan: read_file with start_line.]", Read("pg-explain.json"));
    }
}
