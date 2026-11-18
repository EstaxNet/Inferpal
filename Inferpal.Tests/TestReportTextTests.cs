using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A test report read as its result (<c>TestReportText</c>): the failed tests with their messages and stacks first, never
/// hundreds of green results before the first red; <c>start_line</c> still reads the XML itself.
/// </summary>
public class TestReportTextTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("inferpal-testreport-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { }
    }

    private Task<string> Read(string name, string content, object args)
    {
        File.WriteAllText(Path.Combine(_root.FullName, name), content);
        return new ReadFileTool(() => _root.FullName).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    // The shape VSTest writes (namespace, definitions before results, the counters at the end).
    private const string Trx = """
        <?xml version="1.0" encoding="utf-8"?>
        <TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testName="Orders.Tests.OrderTests.Discount_case_001" outcome="Passed" />
            <UnitTestResult testName="Orders.Tests.OrderTests.Discount_OnExactlyOneHundred_IsTenPercent" outcome="Failed">
              <Output>
                <ErrorInfo>
                  <Message>Assert.Equal() Failure: Values differ
        Expected: 10
        Actual:   0</Message>
                  <StackTrace>   at Orders.Tests.OrderTests.Discount_OnExactlyOneHundred_IsTenPercent() in C:\src\OrderTests.cs:line 299</StackTrace>
                </ErrorInfo>
              </Output>
            </UnitTestResult>
            <UnitTestResult testName="Orders.Tests.OrderTests.Payment_Sandbox" outcome="NotExecuted" />
            <UnitTestResult testName="Orders.Tests.OrderTests.Slow_Import" outcome="Timeout" />
            <UnitTestResult testName="Orders.Tests.OrderTests.Logs_A_Warning" outcome="Warning" />
            <UnitTestResult testName="Orders.Tests.OrderTests.Before_The_Abort" outcome="PassedButRunAborted" />
          </Results>
          <ResultSummary outcome="Failed"><Counters total="4" passed="1" failed="1" /></ResultSummary>
        </TestRun>
        """;

    [Fact]
    public async Task ATrx_ReadsAsItsResult_TheFailedTestsWithTheirMessagesAndStacks()
    {
        var read = await Read("orders.trx", Trx, new { path = "orders.trx" });

        Assert.Contains("6 test(s) — 3 passed, 1 failed, 1 in error, 1 not run", read);   // a warning passed
        Assert.Contains("1. Orders.Tests.OrderTests.Discount_OnExactlyOneHundred_IsTenPercent — failed", read);
        Assert.Contains("Expected: 10", read);
        Assert.Contains("OrderTests.cs:line 299", read);
        Assert.Contains("2. Orders.Tests.OrderTests.Slow_Import — timeout", read);
        Assert.DoesNotContain("Discount_case_001", read);   // a green test is counted, never listed
    }

    [Fact]
    public async Task AskedForLines_ItIsTheXmlItHolds()
    {
        var read = await Read("orders.trx", Trx, new { path = "orders.trx", start_line = 1 });

        Assert.Contains("<TestRun", read);
        Assert.DoesNotContain("test(s) —", read);
    }

    private const string JUnit = """
        <?xml version="1.0" encoding="utf-8"?>
        <testsuites><testsuite name="pytest" errors="1" failures="1" skipped="1" tests="4">
          <testcase classname="test_cart" name="test_total" time="0.001" />
          <testcase classname="test_cart" name="test_empty_cart_is_zero" time="0.001">
            <failure message="assert 0 == None">def test_empty_cart_is_zero():
        &gt;       assert total([]) == None
        E       assert 0 == None
        test_cart.py:18: AssertionError</failure>
          </testcase>
          <testcase classname="test_cart" name="test_reserve_uses_stock" time="0.001">
            <error message="failed on setup with &quot;ConnectionError: inventory service unreachable&quot;">E       ConnectionError: inventory service unreachable</error>
          </testcase>
          <testcase classname="test_cart" name="test_payment" time="0.000"><skipped message="needs the payment sandbox" /></testcase>
        </testsuite></testsuites>
        """;

    [Fact]
    public async Task AJUnitReport_IsKnownByItsRoot_ItsErrorsCountedApartFromItsFailures()
    {
        var read = await Read("results.xml", JUnit, new { path = "results.xml" });

        Assert.Contains("4 test(s) — 1 passed, 1 failed, 1 in error, 1 not run", read);
        Assert.Contains("1. test_cart.test_empty_cart_is_zero — failed", read);
        Assert.Contains("test_cart.py:18: AssertionError", read);
        Assert.Contains("2. test_cart.test_reserve_uses_stock — error", read);
        Assert.Contains("ConnectionError: inventory service unreachable", read);
    }

    [Fact]
    public async Task AnyOtherXml_ReadsAsItIs()
    {
        var read = await Read("app.config.xml", "<configuration><appSettings /></configuration>", new { path = "app.config.xml" });

        Assert.Contains("<configuration>", read);
        Assert.DoesNotContain("Test report", read);
    }

    [Fact]
    public void AReportWithNoRedTest_SaysSo_WithItsCounts()
    {
        var read = TestReportText.Summarize("ok.xml",
            "<testsuite tests=\"2\"><testcase classname=\"a\" name=\"one\"/><testcase classname=\"a\" name=\"two\"/></testsuite>", 8000);

        Assert.Contains("2 test(s) — 2 passed, 0 failed", read);
        Assert.Contains("No test failed.", read);
    }

    [Fact]
    public void AFailureWrittenOnOneHugeLine_IsCut_AndTheReadStaysWithinItsBudget()
    {
        var xml = $"<testsuite><testcase classname=\"Snap\" name=\"matches\"><failure message=\"{new string('x', 1_000_000)}\" /></testcase></testsuite>";

        var read = TestReportText.Summarize("snap.xml", xml, 8000)!;

        Assert.Contains("1. Snap.matches — failed", read);
        Assert.Contains("more characters)", read);
        Assert.True(read.Length <= 8000, $"{read.Length} characters");
    }

    [Fact]
    public void AReportTooLargeToParse_IsPagedAsItsXml()
    {
        var xml = "<testsuite>" + new string(' ', 9_000_000) + "<testcase classname=\"A\" name=\"b\"><failure message=\"m\" /></testcase></testsuite>";

        Assert.Null(TestReportText.Summarize("huge.xml", xml, 8000));
    }

    [Fact]
    public void FailuresPastTheBudget_AreCounted_NeverLeftOutInSilence()
    {
        var xml = new StringBuilder("<testsuite>");
        for (var i = 1; i <= 60; i++)
            xml.Append($"<testcase classname=\"Suite\" name=\"case_{i:00}\"><failure message=\"value {i} was wrong\">at Suite.case_{i:00}</failure></testcase>");
        xml.Append("</testsuite>");

        var read = TestReportText.Summarize("big.xml", xml.ToString(), 2000)!;

        var shown = read.Split('\n').Count(l => l.Contains(" — failed"));
        Assert.InRange(shown, 1, 59);
        Assert.Contains($"{60 - shown} more failed test(s) are not shown here", read);
        Assert.True(read.Length <= 2000, $"{read.Length} characters");
    }
}
