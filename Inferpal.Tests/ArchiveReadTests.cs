using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Governance;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// An archive is read into without extracting it: <c>list_files</c> lists its entries, <c>read_file</c> reads one
/// (<c>logs.zip!/app/log.txt</c>). Nothing trusted from a header, the ignore files judging each entry as if the archive were
/// a folder, a nested archive named and not opened.
/// </summary>
public sealed class ArchiveReadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("inferpal-archive-").FullName;

    public void Dispose()
    {
        ContentExclusions.ForgetForTests(_root);
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string Zip(string name, params (string Entry, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, bytes) in entries)
            using (var s = zip.CreateEntry(entry).Open()) s.Write(bytes);
        return path;
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private Task<string> Read(string path, int start = 0) =>
        new ReadFileTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(start > 0 ? new { path, start_line = start } : (object)new { path }), default);

    private Task<string> List(string path, string? pattern = null) =>
        new ListFilesTool(() => _root).ExecuteAsync(JsonSerializer.SerializeToElement(pattern is null ? new { path } : (object)new { path, pattern }), default);

    [Fact]
    public async Task AZipEntry_IsReadAsText_WithoutExtractingAnything()
    {
        Zip("logs.zip", ("app/log.txt", Text("INFO start\nERROR NullReferenceException at Billing.cs:42\n")));

        var said = await Read("logs.zip!/app/log.txt");

        Assert.Contains("ERROR NullReferenceException at Billing.cs:42", said);
        Assert.Single(Directory.GetFiles(_root));   // nothing extracted
    }

    [Fact]
    public async Task ATarGzEntry_IsReadToo()
    {
        var path = Path.Combine(_root, "dump.tar.gz");
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "./var/log/service.log") { DataStream = new MemoryStream(Text("service crashed: OutOfMemory\n")) };
            tar.WriteEntry(entry);
        }

        var said = await Read("dump.tar.gz!/var/log/service.log");

        Assert.Contains("service crashed: OutOfMemory", said);
    }

    [Fact]
    public async Task ListFiles_ListsTheEntries_AndHowToReadOne()
    {
        Zip("pkg.nupkg", ("lib/net8.0/Acme.dll", [0, 1, 2, 0]), ("Acme.nuspec", Text("<package/>")), ("README.md", Text("# Acme")));

        var all = await List("pkg.nupkg");
        var md  = await List("pkg.nupkg", "*.md");
        var lib = await List("pkg.nupkg!/lib");

        Assert.Contains("pkg.nupkg!/Acme.nuspec", all);
        Assert.Contains("pkg.nupkg!/lib/net8.0/Acme.dll", all);
        Assert.Contains("read_file", all);
        Assert.Contains("pkg.nupkg!/README.md", md);
        Assert.DoesNotContain("Acme.nuspec", md);
        Assert.Contains("Acme.dll", lib);
        Assert.DoesNotContain("README.md", lib);
    }

    [Fact]
    public async Task AnEntryThatInflatesPastTheLimit_IsRefused_WhateverItsHeaderSays()
    {
        Zip("bomb.zip", ("zeros.txt", new byte[Archives.MaxEntryBytes + 1]));   // a few KB compressed

        var said = await Read("bomb.zip!/zeros.txt");

        Assert.Contains("larger than", said);
    }

    [Fact]
    public async Task ABinaryEntry_ANestedArchive_AndAWayOut_AreNamed_NotRead()
    {
        Zip("mix.zip", ("bin/app.dll", [0, 0, 1, 2, 3]), ("inner.zip", [80, 75, 3, 4]), ("ok.txt", Text("ok")));

        Assert.Contains("binary file", await Read("mix.zip!/bin/app.dll"));
        Assert.Contains("archive inside an archive", await Read("mix.zip!/inner.zip"));
        Assert.Contains("Name an entry", await Read("mix.zip!/../secret.txt"));
        Assert.Contains("holds no entry", await Read("mix.zip!/missing.txt"));
    }

    [Fact]
    public async Task TheIgnoreFiles_JudgeAnEntry_AsIfTheArchiveWereAFolder()
    {
        File.WriteAllText(Path.Combine(_root, ".aiignore"), ".env\n");
        Zip("backup.zip", (".env", Text("API_KEY=secret-7f3a")), ("app.txt", Text("fine")));
        ContentExclusions.Declare(_root, () => ContentExclusions.DefaultFiles);

        var read = await Read("backup.zip!/.env");
        var list = await List("backup.zip");

        Assert.DoesNotContain("secret-7f3a", read);
        Assert.Contains(".aiignore", read);
        Assert.Contains("app.txt", list);   // witness: the listing ran
        Assert.DoesNotContain("backup.zip!/.env", list);
        Assert.Contains("excluded by the ignore files", list);
    }

    [Fact]
    public async Task ReadFile_OnTheArchiveItself_SaysHowToGoIn()
    {
        Zip("logs.zip", ("a.txt", Text("a")));
        var said = await Read("logs.zip");
        Assert.Contains("list_files", said);
        Assert.Contains("logs.zip!/", said);
    }

    [Theory]
    [InlineData("logs.zip!/a/b.txt", "logs.zip", "a/b.txt")]
    [InlineData(@"C:\x\pkg.nupkg!\lib\x.dll", @"C:\x\pkg.nupkg", "lib/x.dll")]
    [InlineData("dump.tar.gz!/", "dump.tar.gz", "")]
    public void APathInAnArchive_IsSplitAtTheArchive(string path, string archive, string inner)
    {
        Assert.True(Archives.TrySplit(path, out var a, out var i));
        Assert.Equal(archive, a);
        Assert.Equal(inner, i);
    }

    [Theory]
    [InlineData("src/Wow!.cs")]
    [InlineData("notes!/x.txt")]
    public void AnExclamationMarkElsewhere_IsNoArchive(string path) => Assert.False(Archives.TrySplit(path, out _, out _));

    [Fact]
    public void AnEntryKeepsItsLeadingDot() => Assert.Equal(".env", Archives.Normalize("./.env"));

    [Fact]
    public async Task ATarThatExpandsPastTheBudget_IsRefused_NotReadOnAndOn()
    {
        var path = Path.Combine(_root, "big.tar.gz");
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionLevel.Fastest))
        using (var tar = new TarWriter(gz))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "zeros.bin") { DataStream = new MemoryStream(new byte[4 * 1024 * 1024]) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "after.txt") { DataStream = new MemoryStream(Text("after")) });
        }
        var kept = Archives.MaxStreamBytes;
        Archives.MaxStreamBytes = 1024 * 1024;
        try
        {
            var said = await Read("big.tar.gz!/after.txt");
            Assert.Contains("expands past", said);
        }
        finally { Archives.MaxStreamBytes = kept; }
    }

    [Fact]
    public void AZipsDeclaredEntryCount_IsReadWithoutOpeningIt()
    {
        var zip = Zip("three.zip", ("a.txt", Text("a")), ("b.txt", Text("b")), ("c.txt", Text("c")));
        Assert.Equal(3, Archives.DeclaredZipEntries(zip));
    }

    [Fact]
    public async Task HostileEntryNames_AreLeftOut()
    {
        Zip("hostile.zip", ("C:/cfg/.env", Text("x")), ("../escape.txt", Text("x")), ("a\nfake line.txt", Text("x")), ("fine.txt", Text("ok")));

        var list = await List("hostile.zip");

        Assert.Contains("fine.txt", list);   // witness
        Assert.DoesNotContain("escape.txt", list);
        Assert.DoesNotContain("cfg", list);
        Assert.DoesNotContain("fake line", list);
    }

    [Fact]
    public async Task ANameWithAJoiner_IsListed_ADirectionControl_IsLeftOutAndSaid()
    {
        Zip("names.zip", ("گزارش\u200Cها.txt", Text("x")), ("invoice\u202Etxt.exe", Text("x")));

        var list = await List("names.zip");

        Assert.Contains("گزارش\u200Cها.txt", list);
        Assert.DoesNotContain("invoice", list);
        Assert.Contains("1 entry left out", list);
    }

    [Fact]
    public async Task ARealFileWhoseNameHoldsAnExclamationMark_IsReadAsThatFile()
    {
        Directory.CreateDirectory(Path.Combine(_root, "odd.zip!"));
        File.WriteAllText(Path.Combine(_root, "odd.zip!", "note.txt"), "real file");

        Assert.Contains("real file", await Read("odd.zip!/note.txt"));
    }
}
