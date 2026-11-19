using Inferpal.Services.Presentation;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A <c>query_database</c> step, as the run shows it: what it read (its query, its table) and how many rows came back —
/// read on the count line the reader writes last, never by parsing the rows.
/// </summary>
public class QueryDatabaseStepTests
{
    /// <summary>The line the tool puts above every answer (<c>QueryDatabaseTool</c>).</summary>
    private const string Head = "PostgreSQL database shop on localhost:5432 (from appsettings.Development.json › ConnectionStrings:Shop):\n";

    [Fact]
    public void TheStep_IsNamedByItsQuery_OrItsTable()
    {
        Assert.Equal("SELECT id FROM orders",
            RunSummary.Subject("{\"connection\":\".env#DATABASE_URL\",\"action\":\"query\",\"sql\":\"SELECT id FROM orders\"}"));
        Assert.Equal("orders", RunSummary.Subject("{\"connection\":\".env#DATABASE_URL\",\"action\":\"describe\",\"table\":\"orders\"}"));
        // A connection argument is never a step's subject: another tool's (an MCP server's) may hold a connection string.
        Assert.Equal(string.Empty, RunSummary.Subject("{\"connection\":\"Server=db;Password=s3cret\"}"));
    }

    [Theory]
    [InlineData(Head + "id,name\n1,Ann\n2,Bob\n(2 rows)", "2")]
    [InlineData(Head + "id,name\n1,Ann\n(1 row)", "1")]
    [InlineData(Head + "id,name\n(no rows)\n(0 rows)", "0")]
    // The rows are never parsed: a quoted cell with line breaks, an empty cell, two results — the count line says it.
    [InlineData(Head + "note\n\"first\n\nsecond\"\n\nb\n\nid\n7\n(4 rows)", "4")]
    [InlineData(Head + "id\n1\n(1 cell(s) cut at 2000 characters, marked …)\n(1 row)", "1")]
    [InlineData(Head + "id\n1\n2\n(more rows than the 2 shown, all this answer holds: aggregate, filter, or page with a limit and an offset)", "2+")]
    [InlineData("Note: the argument was clamped.\n\n" + Head + "id\n1\n(1 row)", "1")]
    [InlineData(Head + "id,name\r\n1,Ann\r\n2,Bob\r\n(2 rows)\r\n", "2")]
    public void TheRowsShown_AreReadOnTheCountLine(string output, string expected)
    {
        Assert.Equal((expected, false), RunSummary.StepStatus("query_database", output));
    }

    [Theory]
    [InlineData("Error: postgres database app (from .env › DATABASE_URL) answered: relation \"x\" does not exist")]
    [InlineData("Cancelled by user.")]
    [InlineData(Head + "The query returned no columns.")]
    [InlineData(Head + "(no table or view named orders — list them with action tables)")]
    [InlineData(Head + "## Columns\nname,type\nid,integer\n\n## Keys\n(none)")]
    [InlineData(Head + "table_schema,table_name,table_type\npublic,orders,BASE TABLE")]
    // A step reloaded in VS Code keeps the start of its output only: no count rather than a wrong one.
    [InlineData(Head + "id\n1\n2\n…(truncated)")]
    [InlineData("")]
    public void AnythingButAQuerysRows_ShowsNoCount(string output)
    {
        Assert.Null(RunSummary.StepStatus("query_database", output));
    }
}
