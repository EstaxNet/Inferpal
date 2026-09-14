using System.IO;
using System.Text;
using System.Text.Json;
using Inferpal.Services.Lsp;
using Nerdbank.Streams;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// The language-server transport reads the same Content-Length framing as the FIM sidecar pair.
/// </summary>
public class LspJsonRpcFramingTests
{
    /// <summary>Witness: the harness delivers a plain frame, so a red below means the BOM.</summary>
    [Fact]
    public async Task APlainFrame_DeliversTheResponse()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        using var rpc = new LspJsonRpc(server, server);

        var pending = rpc.SendRequestAsync("initialize", new { }, CancellationToken.None, TimeSpan.FromSeconds(10));
        await AnswerAsync(client, bom: false);

        Assert.Equal(42, (await pending)?.GetInt32());
    }

    /// <summary>
    /// A UTF-8 BOM ahead of the first frame does not tear the channel down. The reader casts bytes to
    /// chars, so the BOM arrives as three chars (EF BB BF), never as U+FEFF: the check it carried
    /// compared against U+FEFF and could not match. The first header no longer started with the
    /// marker, the frame declared no length, the channel was closed, and every request to that
    /// language server was cancelled.
    /// </summary>
    [Fact]
    public async Task ABomAheadOfTheFirstFrame_StillDeliversTheResponse()
    {
        var (client, server) = FullDuplexStream.CreatePair();
        using var rpc = new LspJsonRpc(server, server);

        var pending = rpc.SendRequestAsync("initialize", new { }, CancellationToken.None, TimeSpan.FromSeconds(10));
        await AnswerAsync(client, bom: true);

        Assert.Equal(42, (await pending)?.GetInt32());
    }

    /// <summary>Reads the request the transport wrote, and answers it with <c>42</c>.</summary>
    private static async Task AnswerAsync(Stream client, bool bom)
    {
        var request = await ReadFrameAsync(client).WaitAsync(TimeSpan.FromSeconds(10));
        var id      = JsonDocument.Parse(request).RootElement.GetProperty("id").GetInt32();

        if (bom) await client.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF });
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":42}");
        await client.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await client.WriteAsync(body);
        await client.FlushAsync();
    }

    private static async Task<string> ReadFrameAsync(Stream stream)
    {
        var line   = new StringBuilder();
        var length = -1;
        var one    = new byte[1];

        while (true)
        {
            Assert.True(await stream.ReadAsync(one.AsMemory(0, 1)) > 0, "stream closed inside the headers");
            if (one[0] != (byte)'\n') { if (one[0] != (byte)'\r') line.Append((char)one[0]); continue; }

            var text = line.ToString();
            line.Clear();
            if (text.Length == 0) break;

            const string marker = "Content-Length:";
            if (text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                length = int.Parse(text[marker.Length..].Trim());
        }

        Assert.True(length > 0, "no body announced by the headers");
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read, length - read));
            Assert.True(n > 0, "stream closed before the announced body");
            read += n;
        }
        return Encoding.UTF8.GetString(body);
    }
}
