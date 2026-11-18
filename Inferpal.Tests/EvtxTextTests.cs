using System.Globalization;
using System.IO;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A Windows event log read as a text log (<c>EvtxText</c>), on every system. The fixture is a real export
/// (<c>wevtutil epl</c>) of two crashes of a small .NET program, its machine name replaced at equal length and its
/// checksums recomputed: Windows still reads it.
/// </summary>
public class EvtxTextTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("inferpal-evtx-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { }
    }

    private static byte[] Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return File.ReadAllBytes(Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "documents", "dotnet-crash.evtx"));
    }

    // The two events as Windows recorded them (UTC).
    private static readonly DateTime FirstCrash = new(2026, 11, 18, 21, 10, 56, DateTimeKind.Utc);
    private static readonly DateTime SecondCrash = new(2026, 11, 18, 21, 11, 0, DateTimeKind.Utc);

    private Task<string> Read(object args)
    {
        File.WriteAllBytes(Path.Combine(_root.FullName, "app.evtx"), Fixture());
        return new ReadFileTool(() => _root.FullName).ExecuteAsync(JsonSerializer.SerializeToElement(args), CancellationToken.None);
    }

    [Fact]
    public void ADotNetCrash_ReadsAsAnErrorEntry_WithItsExceptionAndItsStack()
    {
        var read = EvtxText.Extract(Fixture(), CancellationToken.None);

        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("2 event(s)", read.Header);
        var first = FirstCrash.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Contains(first, read.Text);
        Assert.Contains("ERROR .NET Runtime[1026]: Application: OrderService.exe", read.Text);
        Assert.Contains("    Exception Info: System.NullReferenceException: Object reference not set to an instance of an object.", read.Text);
        Assert.Contains("       at OrderService.Checkout.Normalize(String coupon) in C:\\src\\OrderService\\Program.cs:line 13", read.Text);
        Assert.DoesNotContain("BUILD-AGENT", read.Text);   // the machine is not part of an entry
    }

    [Fact]
    public async Task ReadFile_FiltersItsEvents_ByLevelAndTime_AsAnyLog()
    {
        var errors = await Read(new { path = "app.evtx", level = "error" });
        Assert.Equal(2, errors.Split('\n').Count(l => l.Contains("ERROR .NET Runtime[1026]")));
        Assert.Contains("NullReferenceException", errors);

        var since = SecondCrash.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var later = await Read(new { path = "app.evtx", since });
        Assert.Equal(1, later.Split('\n').Count(l => l.Contains("ERROR .NET Runtime[1026]")));
        Assert.Contains(since, later);
    }

    [Fact]
    public async Task ALineOfData_ThatOpensWithALevelOrATime_StaysInItsEvent()
    {
        var text = EvtxText.EntryText(FirstCrash, "INFO", "Backup", "4", ["Started", "Error: disk full", "10:30 retry scheduled"])
                 + EvtxText.EntryText(SecondCrash, "ERROR", "Backup", "9", ["Gave up"]);
        File.WriteAllText(Path.Combine(_root.FullName, "events.log"), text);

        var errors = await new ReadFileTool(() => _root.FullName)
            .ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "events.log", level = "error" }), CancellationToken.None);

        Assert.Contains("Backup[9]: Gave up", errors);          // witness: the filter kept the error entry
        Assert.DoesNotContain("disk full", errors);             // an INFO event's data, whatever its words
        Assert.DoesNotContain("retry scheduled", errors);
    }

    [Fact]
    public void ALogTooLongToKeep_KeepsItsNewestEvents_AndSaysHowManyOlderOnesAreLeftOut()
    {
        var whole = EvtxText.Extract(Fixture(), CancellationToken.None);

        var newest = EvtxText.Extract(Fixture(), CancellationToken.None, maxChars: whole.Text.Length - 10);

        Assert.Contains("The 1 oldest event(s) do not fit", newest.Header);
        Assert.Contains(SecondCrash.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), newest.Text);
        Assert.DoesNotContain(FirstCrash.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), newest.Text);
    }

    [Fact]
    public void ADamagedRecord_IsCountedAndSaid()
    {
        var bytes = Fixture();
        // The second record: the first record's size, past the chunk header, names where it starts.
        var second = 4096 + 512 + BitConverter.ToInt32(bytes, 4096 + 512 + 4);
        Assert.Equal(0x2a, bytes[second]);   // witness: a record starts there
        bytes[second] = 0;

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("1 event(s)", read.Header);
        Assert.Contains("1 damaged record(s) or chunk(s) could not be read", read.Header);
    }

    // ── Damaged, wrapped and hostile logs ─────────────────────────────────────────

    private const int Chunk = 4096;                     // the fixture's one chunk
    private const long OneDay = 864_000_000_000;        // in FILETIME ticks

    private static int SecondRecord(byte[] bytes) => Chunk + 512 + BitConverter.ToInt32(bytes, Chunk + 512 + 4);

    [Fact]
    public void AWrappedLog_IsReadOldestFirst_AndKeepsItsNewestEvents()
    {
        // A full log reuses its chunks: here the newer chunk (a day later, higher record identifiers) comes FIRST in the file.
        var original = Fixture();
        var newer = original.AsSpan(Chunk, 65536).ToArray();
        BitConverter.GetBytes(BitConverter.ToUInt64(newer, 24) + 1000).CopyTo(newer, 24);
        foreach (var record in new[] { 512, SecondRecord(original) - Chunk })
            BitConverter.GetBytes(BitConverter.ToInt64(newer, record + 16) + OneDay).CopyTo(newer, record + 16);
        var wrapped = original.AsSpan(0, Chunk).ToArray().Concat(newer).Concat(original.AsSpan(Chunk, 65536).ToArray()).ToArray();

        var whole = EvtxText.Extract(wrapped, CancellationToken.None);
        var day1 = FirstCrash.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var day2 = FirstCrash.AddDays(1).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.True(whole.Text.IndexOf(day1, StringComparison.Ordinal) < whole.Text.IndexOf(day2, StringComparison.Ordinal), "oldest first");

        var newest = EvtxText.Extract(wrapped, CancellationToken.None, maxChars: whole.Text.Length / 2 + 10);
        Assert.Contains("The 2 oldest event(s) do not fit", newest.Header);
        Assert.Contains(day2, newest.Text);
        Assert.DoesNotContain(day1, newest.Text);
    }

    [Fact]
    public void ATemplateThatNamesItself_IsADamagedRecord_NeverAStackOverflow()
    {
        // One record whose resident template holds an instance of itself.
        var bytes = new byte[Chunk + 65536];
        "ElfFile\0"u8.CopyTo(bytes);
        "ElfChnk\0"u8.CopyTo(bytes.AsSpan(Chunk));
        BitConverter.GetBytes(640u).CopyTo(bytes, Chunk + 48);              // free space: after the one record
        var r = Chunk + 512;
        BitConverter.GetBytes(0x00002a2au).CopyTo(bytes, r);
        BitConverter.GetBytes(128u).CopyTo(bytes, r + 4);
        BitConverter.GetBytes(1UL).CopyTo(bytes, r + 8);
        byte[] binXml =
        [
            0x0F, 1, 1, 0,                                                   // fragment header (536)
            0x0C, 1, 7, 0, 0, 0, 0x26, 0x02, 0, 0,                           // instance of the template at 550, defined here
            0, 0, 0, 0, .. new byte[16], 14, 0, 0, 0,                        // the definition: next, GUID, 14 bytes of data
            0x0F, 1, 1, 0,                                                   // its data: a fragment...
            0x0C, 1, 7, 0, 0, 0, 0x26, 0x02, 0, 0,                           // ...that instantiates itself
        ];
        binXml.CopyTo(bytes, r + 24);

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("1 damaged record(s) or chunk(s) could not be read", read.Header);
    }

    [Fact]
    public void ARecordThatDoesNotDecode_IsNotCountedAsAnEvent()
    {
        var bytes = Fixture();
        bytes[SecondRecord(bytes) + 24] = 0xFF;                              // its binary XML starts with no token

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("1 event(s)", read.Header);
        Assert.Contains("1 damaged record(s)", read.Header);
    }

    [Fact]
    public void AFileCutInsideAChunk_SaysSo()
    {
        var bytes = Fixture().Concat(Fixture().AsSpan(Chunk, 1000).ToArray()).ToArray();

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("2 event(s)", read.Header);
        Assert.Contains("1 damaged record(s) or chunk(s) could not be read", read.Header);
    }

    [Fact]
    public void ARecordPastAChunkHeaderThatLagsBehind_IsRead_WhenItFollowsTheLastOne()
    {
        // A log copied while Windows wrote it: the chunk's free-space offset still points after the first record.
        var bytes = Fixture();
        BitConverter.GetBytes((uint)(SecondRecord(bytes) - Chunk)).CopyTo(bytes, Chunk + 48);

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("2 event(s)", read.Header);
        Assert.DoesNotContain("damaged", read.Header);
    }

    [Fact]
    public void AnEventLog_HasItsOwnSizeLimit_AndTheRefusalNamesIt()
    {
        var big = Path.Combine(_root.FullName, "Security.evtx");
        using (var file = File.Create(big)) file.SetLength(65L * 1024 * 1024);
        var past20 = Path.Combine(_root.FullName, "Application.evtx");
        using (var file = File.Create(past20)) file.SetLength(30L * 1024 * 1024);

        Assert.Equal(Inferpal.Localization.Strings.AttachDocumentTooLarge("Security.evtx", 64),
                     Inferpal.Services.Presentation.MentionController.DocumentRefusal(big));
        Assert.Equal(DocumentProblem.Unreadable, AttachedDocuments.Read(past20).Problem);   // read (zeros), never refused by size
    }

    [Fact]
    public void AValueReferencedThousandsOfTimes_IsCut_NeverExpandedToGigabytes()
    {
        // A crafted record: one Data element that references 6 000 times a value of 10 000 characters — 60 million once
        // expanded. Names and the template are written inline, as Windows writes them.
        var bytes = new byte[Chunk + 65536];
        "ElfFile\0"u8.CopyTo(bytes);
        "ElfChnk\0"u8.CopyTo(bytes.AsSpan(Chunk));
        BitConverter.GetBytes(1UL).CopyTo(bytes, Chunk + 24);
        var w = new List<byte>();
        int Pos() => 512 + 24 + w.Count;                                   // chunk-relative, as offsets are
        void U16(int v) => w.AddRange(BitConverter.GetBytes((ushort)v));
        void U32(int v) => w.AddRange(BitConverter.GetBytes(v));
        void Open(string name)
        {
            w.Add(0x01); U16(0); U32(0);
            U32(Pos() + 4);                                                // the name, defined right after
            U32(0); U16(0); U16(name.Length); w.AddRange(System.Text.Encoding.Unicode.GetBytes(name)); U16(0);
            w.Add(0x02);
        }
        w.AddRange([0x0F, 1, 1, 0]);
        w.Add(0x0C); w.Add(1); U32(1); U32(Pos() + 4);                    // an instance of the template defined here
        U32(0); w.AddRange(new byte[16]);
        var sizeAt = w.Count; U32(0);
        var dataStart = w.Count;
        w.AddRange([0x0F, 1, 1, 0]);
        Open("Event"); Open("EventData"); Open("Data");
        for (var i = 0; i < 6000; i++) { w.Add(0x0D); U16(0); w.Add(0x01); }
        w.AddRange([0x04, 0x04, 0x04, 0x00]);
        var dataSize = BitConverter.GetBytes(w.Count - dataStart);
        for (var i = 0; i < 4; i++) w[sizeAt + i] = dataSize[i];
        var value = System.Text.Encoding.Unicode.GetBytes(new string('v', 10_000));
        U32(1); U16(value.Length); w.Add(0x01); w.Add(0);
        w.AddRange(value);
        var size = 24 + w.Count + 4;
        var r = Chunk + 512;
        BitConverter.GetBytes(0x00002a2au).CopyTo(bytes, r);
        BitConverter.GetBytes(size).CopyTo(bytes, r + 4);
        BitConverter.GetBytes(1UL).CopyTo(bytes, r + 8);
        w.CopyTo(bytes, r + 24);
        BitConverter.GetBytes(512 + size).CopyTo(bytes, Chunk + 48);

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("1 event(s)", read.Header);                        // witness: the record decoded
        Assert.Contains("(value cut)", read.Text);
        Assert.True(read.Text.Length < 200_000, $"{read.Text.Length} characters");
    }

    [Fact]
    public void AChunkHeaderThatCountsNoRecordYet_StillGivesItsRecords()
    {
        var bytes = Fixture();
        BitConverter.GetBytes(512u).CopyTo(bytes, Chunk + 48);             // free space at the first record

        var read = EvtxText.Extract(bytes, CancellationToken.None);

        Assert.Contains("2 event(s)", read.Header);
    }

    [Fact]
    public async Task AFilteredRead_SaysTheLogsHeaderAboveItsEntries_NeverJudgesItAsALine()
    {
        var read = await Read(new { path = "app.evtx", level = "error" });

        Assert.StartsWith("[Windows event log: 2 event(s)", read);
        Assert.Contains("ERROR .NET Runtime[1026]", read);
        Assert.DoesNotContain("before the first entry", read);
    }

    [Fact]
    public void ALogWithNoEvent_SaysSo_NeverAScan()
    {
        var path = Path.Combine(_root.FullName, "empty.evtx");
        var bytes = new byte[4096];
        "ElfFile\0"u8.CopyTo(bytes);
        File.WriteAllBytes(path, bytes);

        Assert.Equal(Inferpal.Localization.Strings.AttachDocumentNothingInIt("empty.evtx"),
                     Inferpal.Services.Presentation.MentionController.DocumentRefusal(path));
        Assert.Equal("it holds no event", AttachedDocuments.WhyNoText(path));
    }

    [Fact]
    public async Task ADocumentReadInPart_IsSaidBySearch_NeverAPlainNoMatch()
    {
        // A table saved as text under .xls, past the documents' text limit: its end is not read, so not searched.
        File.WriteAllText(Path.Combine(_root.FullName, "big.xls"), new string('a', PdfText.MaxChars + 10) + "\nneedle\n");

        var report = await new SearchInFilesTool(() => _root.FullName).ExecuteAsync(
            JsonSerializer.SerializeToElement(new { path = _root.FullName, pattern = "needle" }), CancellationToken.None);

        Assert.Contains("1 document(s) were searched only in part", report);
        Assert.Contains("big.xls", report);
    }

    [Fact]
    public async Task InsideAnArchive_ADocumentSaysWhatItIs_AndATestReportReadsAsItsResult()
    {
        var zip = Path.Combine(_root.FullName, "artifacts.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = archive.CreateEntry("app.evtx").Open()) s.Write(Fixture());
            using (var w = new StreamWriter(archive.CreateEntry("results.xml").Open()))
                w.Write("<testsuite><testcase classname=\"Cart\" name=\"total\"><failure message=\"expected 19\">at Cart.total</failure></testcase></testsuite>");
        }
        var tool = new ReadFileTool(() => _root.FullName);

        var log = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "artifacts.zip!/app.evtx" }), CancellationToken.None);
        var report = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = "artifacts.zip!/results.xml" }), CancellationToken.None);

        Assert.Contains("is a document inside an archive", log);
        Assert.Contains("1. Cart.total — failed", report);
    }

    [Fact]
    public void AFileThatIsNoEventLog_IsUnreadable_NeverAnEmptyLog()
    {
        var read = DocumentText.Extract(new byte[8192], ".evtx");

        Assert.Equal(DocumentProblem.Unreadable, read.Problem);
    }
}
