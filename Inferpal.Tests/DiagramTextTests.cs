using System.IO;
using System.IO.Compression;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A diagram read as text (<c>DiagramText</c>): its shapes and its links, page by page — draw.io, plain or compressed as
/// draw.io saves it, and Visio.
/// </summary>
public class DiagramTextTests
{
    // A page as draw.io writes it: an HTML label, a shape wrapped in a UserObject, a label cell on an edge, a shape with no
    // text, and a container.
    private const string Model = """
        <mxGraphModel><root>
          <mxCell id="0" /><mxCell id="1" parent="0" />
          <mxCell id="10" value="Backend" style="swimlane" vertex="1" parent="1" />
          <mxCell id="2" value="Order API" vertex="1" parent="10" />
          <mxCell id="3" value="&lt;b&gt;Orders&lt;/b&gt;&lt;br&gt;database" style="html=1" vertex="1" parent="1" />
          <UserObject label="Web shop" id="7"><mxCell vertex="1" parent="1" /></UserObject>
          <mxCell id="4" value="reads" edge="1" source="2" target="3" parent="1" />
          <mxCell id="5" vertex="1" parent="1" />
          <mxCell id="6" edge="1" source="7" target="2" parent="1" />
          <mxCell id="8" value="HTTPS" style="edgeLabel" vertex="1" parent="6" />
        </root></mxGraphModel>
        """;

    private static string Expected(DocumentExtraction read)
    {
        Assert.Equal(DocumentProblem.None, read.Problem);
        return read.Text;
    }

    private static void AssertThePage(string text)
    {
        Assert.Contains("## Architecture", text);
        Assert.Contains("- Order API (in Backend)", text);
        Assert.Contains("- Orders database", text);
        Assert.Contains("- Web shop", text);
        Assert.Contains("- Order API → Orders database: reads", text);
        Assert.Contains("- Web shop → Order API: HTTPS", text);
        Assert.Contains("(1 shape(s) with no text are not listed.)", text);
        Assert.DoesNotContain("- HTTPS", text);   // an edge's label is not a shape
    }

    [Fact]
    public void ADrawIoPage_ReadsAsItsShapesAndLinks()
    {
        var file = $"<mxfile><diagram name=\"Architecture\" id=\"a\">{Model}</diagram></mxfile>";

        var read = DocumentText.Extract(Encoding.UTF8.GetBytes(file), ".drawio");

        AssertThePage(Expected(read));
        Assert.Contains("draw.io", read.Header);
    }

    [Fact]
    public void ACompressedDrawIoPage_ReadsTheSame()
    {
        // As draw.io compresses a page: URL-encoded, raw deflate, base64.
        var buffer = new MemoryStream();
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(Uri.EscapeDataString(Model)));
        var file = $"<mxfile><diagram name=\"Architecture\" id=\"a\">{Convert.ToBase64String(buffer.ToArray())}</diagram></mxfile>";

        AssertThePage(Expected(DocumentText.Extract(Encoding.UTF8.GetBytes(file), ".drawio")));
    }

    [Fact]
    public void AVisioPage_ReadsItsShapes_AndItsConnectorsAsLinks()
    {
        const string ns = "http://schemas.microsoft.com/office/visio/2012/main";
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml);
            }
            Part("visio/pages/pages.xml", $"<Pages xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><Page ID=\"0\" NameU=\"Architecture\" Name=\"Architecture\"><Rel r:id=\"rId1\" /></Page></Pages>");
            Part("visio/pages/_rels/pages.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"page1.xml\" /></Relationships>");
            Part("visio/pages/page1.xml", $"""
                <PageContents xmlns="{ns}"><Shapes>
                  <Shape ID="1" NameU="Process" Type="Shape"><Cell N="PinX" V="1" /><Text><cp IX="0" />Order API</Text></Shape>
                  <Shape ID="2" NameU="Database" Type="Shape"><Text>Orders database</Text></Shape>
                  <Shape ID="4" NameU="Rectangle" Type="Shape" />
                  <Shape ID="3" NameU="Dynamic connector" Type="Shape"><Text>reads</Text></Shape>
                </Shapes><Connects>
                  <Connect FromSheet="3" FromCell="BeginX" FromPart="9" ToSheet="1" ToCell="PinX" ToPart="3" />
                  <Connect FromSheet="3" FromCell="EndX" FromPart="12" ToSheet="2" ToCell="PinX" ToPart="3" />
                </Connects></PageContents>
                """);
        }

        var text = Expected(DocumentText.Extract(buffer.ToArray(), ".vsdx"));

        Assert.Contains("## Architecture", text);
        Assert.Contains("- Order API", text);
        Assert.Contains("- Orders database", text);
        Assert.Contains("- Order API → Orders database: reads", text);
        Assert.Contains("(1 shape(s) with no text are not listed.)", text);
        Assert.DoesNotContain("- reads", text);   // the connector is a link, not a shape
    }

    [Fact]
    public async Task TheAgent_ReadsADrawIoFileAsItsShapes_AndItsXmlWithStartLine()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-diagram-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "arch.drawio"), $"<mxfile><diagram name=\"Architecture\" id=\"a\">{Model}</diagram></mxfile>");
            var tool = new Inferpal.Services.Tools.ReadFileTool(() => root.FullName);

            var shapes = await tool.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "arch.drawio" }), CancellationToken.None);
            var xml = await tool.ExecuteAsync(System.Text.Json.JsonSerializer.SerializeToElement(new { path = "arch.drawio", start_line = 1 }), CancellationToken.None);

            AssertThePage(shapes);
            Assert.Contains("read its XML with start_line=1", shapes);
            Assert.Contains("<mxfile>", xml);                                   // the file stays a text file it can edit
        }
        finally { try { root.Delete(recursive: true); } catch { } }
    }

    [Fact]
    public void AVisioPage_IsNamedThroughItsRelationship_NeverByItsPosition()
    {
        const string ns = "http://schemas.microsoft.com/office/visio/2012/main";
        const string rns = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Part(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml);
            }
            // Pages reordered: the first page listed lives in page2.xml.
            Part("visio/pages/pages.xml", $"<Pages xmlns=\"{ns}\" xmlns:r=\"{rns}\"><Page NameU=\"Network\"><Rel r:id=\"rId2\" /></Page><Page NameU=\"Database\"><Rel r:id=\"rId1\" /></Page></Pages>");
            Part("visio/pages/_rels/pages.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"page1.xml\" /><Relationship Id=\"rId2\" Target=\"page2.xml\" /></Relationships>");
            Part("visio/pages/page1.xml", $"<PageContents xmlns=\"{ns}\"><Shapes><Shape ID=\"1\"><Text>Orders table</Text></Shape></Shapes></PageContents>");
            Part("visio/pages/page2.xml", $"<PageContents xmlns=\"{ns}\"><Shapes><Shape ID=\"1\"><Text>Firewall</Text></Shape></Shapes></PageContents>");
        }

        var text = Expected(DocumentText.Extract(buffer.ToArray(), ".vsdx"));

        Assert.Contains("## Network\nShapes:\n- Firewall", text);
        Assert.Contains("## Database\nShapes:\n- Orders table", text);
    }

    [Fact]
    public void ALabelOfManyOpenBrackets_ReadsInOnePass()
    {
        var label = string.Concat(Enumerable.Repeat("&lt;", 200_000)) + "x";
        var file = $"<mxfile><diagram name=\"p\"><mxGraphModel><root><mxCell id=\"2\" value=\"{label}\" style=\"html=1\" vertex=\"1\" /></root></mxGraphModel></diagram></mxfile>";

        var read = DocumentText.Extract(Encoding.UTF8.GetBytes(file), ".drawio");

        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("<<<x", read.Text);
    }

    [Fact]
    public void APlainLabel_KeepsItsAngleBrackets_OnlyAnHtmlOneIsReadAsHtml()
    {
        var drawio = "<mxfile><diagram name=\"Classes\"><mxGraphModel><root>"
                   + "<mxCell id=\"2\" value=\"List&lt;Order&gt; orders\" vertex=\"1\" />"
                   + "<mxCell id=\"3\" value=\"if x &lt; 10 and y &gt; 5\" vertex=\"1\" />"
                   + "</root></mxGraphModel></diagram></mxfile>";
        var text = Expected(DocumentText.Extract(Encoding.UTF8.GetBytes(drawio), ".drawio"));

        Assert.Contains("- List<Order> orders", text);
        Assert.Contains("- if x < 10 and y > 5", text);
    }

    [Fact]
    public void ADiagramWithNothingWritten_GivesNoText_NeverAnEmptyDiagram()
    {
        var read = DocumentText.Extract(Encoding.UTF8.GetBytes("<mxfile><diagram name=\"p\"><mxGraphModel><root><mxCell id=\"0\"/></root></mxGraphModel></diagram></mxfile>"), ".drawio");

        Assert.Equal(DocumentProblem.NoText, read.Problem);
    }
}
