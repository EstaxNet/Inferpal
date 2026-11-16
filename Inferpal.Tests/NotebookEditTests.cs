using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// One cell of a notebook changed, the rest written back as Jupyter writes it. The fixture is written by Python exactly as
/// nbformat writes a notebook (<c>json.dumps(nb, sort_keys=True, indent=1, ensure_ascii=False)</c> plus a line break).
/// </summary>
public sealed class NotebookEditTests
{
    private static string Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "notebooks", "analysis.ipynb"));
    }

    [Fact]
    public void AnUntouchedNotebook_IsWrittenBack_ByteForByte() =>
        Assert.Equal(Fixture(), NotebookEdit.Write(System.Text.Json.Nodes.JsonNode.Parse(Fixture())!, Fixture()));

    [Fact]
    public void ReplacingACellByItsId_ChangesItsLinesOnly_AndClearsItsOutputs()
    {
        var before = Fixture();

        var (change, refusal) = NotebookEdit.Apply(before, "293a4b5c", "replace", null,
            "orders = pd.read_csv(\"data/orders.csv\")\ntotal = orders[\"total_eur\"].sum()\nprint(f\"{total:,.2f} EUR\")");

        Assert.Null(refusal);
        Assert.Equal(3, change!.Number);
        Assert.Contains("orders[\"amount\"]", change.OldSource);
        var removed = before.Split('\n').Except(change.Text.Split('\n')).ToList();
        var added = change.Text.Split('\n').Except(before.Split('\n')).ToList();
        // The old source line, the old output (its ename, evalue, type, traceback and the lines around them), the count.
        Assert.Contains(removed, l => l.Contains("orders[\\\"amount\\\"]"));
        Assert.Contains(removed, l => l.Contains("KeyError"));
        Assert.Contains(added, l => l.Contains("total_eur"));
        Assert.DoesNotContain(removed, l => l.Contains("image/png") || l.Contains("Analyse des ventes") || l.Contains("\"tags\""));
        using var doc = JsonDocument.Parse(change.Text);   // still a notebook
        Assert.Equal(4, doc.RootElement.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public void InsertingAfterACell_GivesTheNewCellAFreshId_AndAnEmptyOutput()
    {
        var (change, _) = NotebookEdit.Apply(Fixture(), "3", "insert_after", "code", "monthly = 1\nmonthly");

        using var doc = JsonDocument.Parse(change!.Text);
        var cell = doc.RootElement.GetProperty("cells")[3];
        Assert.Equal(4, change.Number);
        Assert.Equal(8, cell.GetProperty("id").GetString()!.Length);
        Assert.Equal(0, cell.GetProperty("outputs").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, cell.GetProperty("execution_count").ValueKind);
        Assert.Equal(["monthly = 1\n", "monthly"], cell.GetProperty("source").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public void DeletingACell_RemovesItAlone()
    {
        var (change, _) = NotebookEdit.Apply(Fixture(), "6d7e8f90", "delete", null, null);
        Assert.DoesNotContain("image/png", change!.Text);
        Assert.Contains("Analyse des ventes", change.Text);
        Assert.Equal("orders.plot()", change.OldSource);
    }

    [Fact]
    public void AnUnknownCell_IsRefused_WithTheListOfCells()
    {
        var (change, refusal) = NotebookEdit.Apply(Fixture(), "nope", "replace", null, "x");
        Assert.Null(change);
        Assert.Contains("3 (id 293a4b5c) code", refusal);
    }

    [Fact]
    public void AnEmptyNotebook_GetsItsFirstCell()
    {
        var (change, refusal) = NotebookEdit.Apply("{\n \"cells\": [],\n \"metadata\": {},\n \"nbformat\": 4,\n \"nbformat_minor\": 5\n}\n", "1", "insert_after", "code", "print(1)");
        Assert.Null(refusal);
        Assert.Contains("print(1)", change!.Text);
    }

    [Fact]
    public void ANotebookNotInJupytersLayout_IsSaidSo()
    {
        Assert.True(NotebookEdit.InJupytersLayout(Fixture()));
        Assert.False(NotebookEdit.InJupytersLayout(Fixture().Replace("\n \"", "\n  \"")));   // a two-space indent
    }

    [Fact]
    public void ASourceIsSplit_AsPythonSplitsLines()
    {
        var (change, _) = NotebookEdit.Apply(Fixture(), "1", "replace", null, "a\r\nb\rc");
        using var doc = JsonDocument.Parse(change!.Text);
        Assert.Equal(["a\r\n", "b\r", "c"], doc.RootElement.GetProperty("cells")[0].GetProperty("source").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public void ACrlfNotebook_KeepsItsLineBreaks()
    {
        var crlf = Fixture().Replace("\n", "\r\n");
        var (change, _) = NotebookEdit.Apply(crlf, "1", "replace", null, "# Title");
        Assert.DoesNotContain("\n", change!.Text.Replace("\r\n", ""));
        Assert.EndsWith("\r\n", change.Text);
    }
}
