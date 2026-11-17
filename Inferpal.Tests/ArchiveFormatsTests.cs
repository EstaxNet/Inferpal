using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Documents;
using Inferpal.Services.Tools;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// 7z, RAR, the bzip2 and xz tars and a lone compressed file, read in place like a zip. Fixtures made by other tools: 7-Zip
/// 24.07 (the 7z, tar.xz, tar.bz2, .gz, .bz2, .xz; password <c>K3y!</c>) and RAR files from SharpCompress's public test set
/// (MIT; password <c>test</c>). The expected bytes are 7-Zip's own extraction, never what this reader produces.
/// </summary>
public sealed class ArchiveFormatsTests
{
    private const string Log = "secret log line\nERROR at Billing.cs:42\n";

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Inferpal.Tests", "Fixtures", "archives", name);
    }

    private static string Text(string archive, string entry)
    {
        var (bytes, problem, locked) = Archives.Read(Fixture(archive), entry, default);
        Assert.False(locked, $"{archive}!/{entry} asked for a password");
        Assert.True(bytes is not null, $"{archive}!/{entry}: {problem}");
        return Encoding.UTF8.GetString(bytes!);
    }

    [Theory]
    [InlineData("solid.7z")]       // LZMA2, solid
    [InlineData("ppmd.7z")]        // PPMd, not solid
    [InlineData("src.tar.xz")]
    [InlineData("src.tar.bz2")]
    public void EachFormat_ListsItsEntries_AndReadsOne(string archive)
    {
        Assert.True(Archives.Handles(archive));
        var (entries, problem) = Archives.List(Fixture(archive), "", out var more, default);
        Assert.True(entries is not null, problem);
        Assert.False(more);
        Assert.Equal(["app/app.log", "docs/a.txt", "docs/b.txt"], entries!.Select(e => e.Path));

        Assert.Equal(Log, Text(archive, "app/app.log"));
        Assert.Equal("second\n", Text(archive, "docs/b.txt"));   // the last one: a solid archive decodes the others first
    }

    [Theory]
    [InlineData("app.log.gz")]
    [InlineData("app.log.bz2")]
    [InlineData("app.log.xz")]
    public void ALoneCompressedFile_IsAnArchiveOfOneEntry(string archive)
    {
        var (entries, _) = Archives.List(Fixture(archive), "", out _, default);
        var entry = Assert.Single(entries!);
        Assert.Equal("app.log", entry.Path);
        Assert.Equal(Log, Text(archive, "app.log"));
    }

    [Theory]
    [InlineData("rar4.rar")]
    [InlineData("rar5solid.rar")]
    public void ARar_ReadsLike7Zip(string archive)
    {
        var (entries, problem) = Archives.List(Fixture(archive), "", out _, default);
        Assert.True(entries is not null, problem);
        Assert.Contains(entries!, e => e.Path == "jpg/test.jpg");
        foreach (var (entry, sha) in new[]
                 {
                     ("jpg/test.jpg", "b251c7501fb0f55dd4a92feabe0a6f5733bc40a02679498155fae9b30138fc53"),
                     ("exe/test.exe", "8557928804f57ecc340b3bb38b095a3607474ec8deb0076f316fcfe02b562106"),
                 })
        {
            var (bytes, why, _) = Archives.Read(Fixture(archive), entry, default);
            Assert.True(bytes is not null, $"{archive}!/{entry}: {why}");
            Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(bytes!)).ToLowerInvariant());
        }
    }

    [Fact]
    public void ARar5_Small_ReadsItsText() => Assert.Equal("hello from RAR!\n", Text("rar5.rar", "payload.txt"));

    [Fact]
    public void ASolidArchive_PastTheBudget_IsRefused_BeforeDecodingAnything()
    {
        var kept = Archives.MaxStreamBytes;
        try
        {
            Archives.MaxStreamBytes = 10;   // the two entries before docs/b.txt declare more than that
            var (bytes, problem, _) = Archives.Read(Fixture("solid.7z"), "docs/b.txt", default);
            Assert.Null(bytes);
            Assert.Contains("solid archive", problem);
            Assert.Equal(Log, Text("solid.7z", "app/app.log"));   // the first entry: nothing to decode before it
        }
        finally { Archives.MaxStreamBytes = kept; }
    }

    [Fact]
    public void ALoneCompressedBomb_StopsAtTheEntryLimit()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-bomb-").FullName;
        try
        {
            var bomb = Path.Combine(root, "zeros.log.gz");
            using (var gz = new GZipStream(File.Create(bomb), CompressionLevel.SmallestSize))
            {
                var zeros = new byte[1 << 20];
                for (var i = 0; i < 64; i++) gz.Write(zeros);   // 64 MB out of a few dozen kilobytes
            }
            var (bytes, problem, _) = Archives.Read(bomb, "zeros.log", default);
            Assert.Null(bytes);
            Assert.Contains("larger than 8 MB", problem);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ── Passwords, through read_file and its card ────────────────────────────────────────────────────────────────

    private sealed class Typist(params string[] typed) : Inferpal.Services.Execution.ApprovalServiceBase(new Inferpal.Config.InferpalConfig(), () => null)
    {
        private readonly Queue<string> _typed = new(typed);
        public int Cards { get; private set; }
        public override bool CanAskForInput => true;
        protected override Task<Inferpal.Services.Execution.ApprovalDecision> PromptUserAsync(string message, Inferpal.Services.CodeActions.DiffInfo? diff, CancellationToken ct) =>
            Task.FromResult(Inferpal.Services.Execution.ApprovalDecision.Deny);
        protected override Task<Inferpal.Services.Presentation.InputReply?> PromptInputAsync(Inferpal.Services.Presentation.InputCardModel card, CancellationToken ct)
        {
            Cards++;
            return Task.FromResult<Inferpal.Services.Presentation.InputReply?>(_typed.Count == 0
                ? new(Inferpal.Services.Mcp.McpInputAction.Cancel, new Dictionary<string, Inferpal.Services.Mcp.McpInputValue>())
                : new(Inferpal.Services.Mcp.McpInputAction.Accept, new Dictionary<string, Inferpal.Services.Mcp.McpInputValue>
                      { [Inferpal.Services.Presentation.InputCard.PasswordKey] = new(Text: _typed.Dequeue()) }));
        }
        protected override void ShowInputReceipt(Inferpal.Services.Presentation.InputReceipt receipt) { }
    }

    /// <summary>A copy under a fresh name: the passwords typed are kept per archive path for the process.</summary>
    private static (string Root, string Name) Copy(string fixture)
    {
        var root = Directory.CreateTempSubdirectory("inferpal-archivefmt-").FullName;
        var name = "backup" + Path.GetExtension(fixture);
        File.Copy(Fixture(fixture), Path.Combine(root, name));
        return (root, name);
    }

    private static Task<string> Read(string root, string path, Typist approval) =>
        new ReadFileTool(() => root, approval: approval).ExecuteAsync(JsonSerializer.SerializeToElement(new { path }), default);

    [Theory]
    [InlineData("enc.7z", "K3y!", "app/app.log", "ERROR at Billing.cs:42")]
    [InlineData("enchdr.7z", "K3y!", "app/app.log", "ERROR at Billing.cs:42")]   // its names are encrypted too
    [InlineData("rar5enc.rar", "test", "payload.txt", "hello from RAR!")]
    public async Task AProtectedArchive_OpensOnTheCard_AfterAWrongPassword(string fixture, string password, string entry, string expected)
    {
        var (root, name) = Copy(fixture);
        try
        {
            var typist = new Typist("wrong", password);
            var said = await Read(root, $"{name}!/{entry}", typist);
            Assert.Contains(expected, said);
            Assert.DoesNotContain(password, said);
            Assert.Equal(2, typist.Cards);   // the card shown again after the wrong try

            var again = await Read(root, $"{name}!/{entry}", new Typist());   // kept for this archive
            Assert.Contains(expected, again);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task EncryptedNames_AreSaid_UntilThePasswordIsTyped_ThenListed()
    {
        var (root, name) = Copy("enchdr.7z");
        try
        {
            var list = new ListFilesTool(() => root);
            var before = await list.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = name }), default);
            Assert.Contains("protected by a password", before);

            Assert.Contains("ERROR at Billing.cs:42", await Read(root, $"{name}!/app/app.log", new Typist("K3y!")));
            var after = await list.ExecuteAsync(JsonSerializer.SerializeToElement(new { path = name }), default);
            Assert.Contains($"{name}!/docs/b.txt", after);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>⚠ Stored and AES-encrypted: no decoder to fail, and 7z's AES has no password check — only the CRC tells a wrong
    /// key from the right one.</summary>
    [Fact]
    public void AWrongPassword_OnAStoredEntry_IsWrong_NeverNoise()
    {
        var (root, name) = Copy("encstored.7z");
        try
        {
            var archive = Path.Combine(root, name);
            Assert.False(Archives.TryPassword(archive, "app/app.log", "wrong"));
            Assert.True(Archives.TryPassword(archive, "app/app.log", "K3y!"));
            Assert.Equal(Log, Encoding.UTF8.GetString(Archives.Read(archive, "app/app.log", default).Bytes!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The extension picks the reader, never the signature: a zip named <c>.7z</c> would skip the zip's own guards.</summary>
    [Fact]
    public void AZipNamed7z_IsNotReadAsAZip()
    {
        var root = Directory.CreateTempSubdirectory("inferpal-archivefmt-").FullName;
        try
        {
            var archive = Path.Combine(root, "renamed.7z");
            File.Copy(Fixture("big.zip"), archive);
            var (entries, problem) = Archives.List(archive, "", out _, default);
            Assert.Null(entries);
            Assert.Contains("could not be read", problem);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task AWrongPassword_NeverGivesGarbage()
    {
        var (root, name) = Copy("enc.7z");
        try
        {
            var said = await Read(root, $"{name}!/app/app.log", new Typist("nope", "x", "K3y"));
            Assert.DoesNotContain("ERROR", said);
            Assert.Contains("stays closed", said);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
