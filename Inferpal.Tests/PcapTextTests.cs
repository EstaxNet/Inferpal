using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text;
using Inferpal.Services.Documents;
using Xunit;

namespace Inferpal.Tests;

/// <summary>
/// A network capture (pari 169) reads as what someone looking for a failure wants from it: problems first (a refused
/// connection, an unanswered one, retransmissions, ICMP unreachable, an unanswered DNS question), then the conversations,
/// DNS and HTTP — TLS said encrypted with only what it shows in clear. Every length in the file is bounded before use.
/// </summary>
public class PcapTextTests
{
    // ── A capture written by hand: frames, then a pcap or pcapng file around them ──

    private static readonly DateTime Start = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private sealed record Frame(double At, byte[] Data);

    private static byte[] Ipv4(string source, string destination, byte protocol, byte[] payload, ushort flags = 0)
    {
        var ip = new byte[20 + payload.Length];
        ip[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)ip.Length);
        BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(6), flags);
        ip[8] = 64;
        ip[9] = protocol;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(ip, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(ip, 16);
        payload.CopyTo(ip, 20);
        return ip;
    }

    private static byte[] Ethernet(byte[] ip, ushort type = 0x0800)
    {
        var frame = new byte[14 + ip.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(12), type);
        ip.CopyTo(frame, 14);
        return frame;
    }

    private const byte Syn = 0x02, Ack = 0x10, Rst = 0x04, Psh = 0x08;

    private static byte[] Tcp(int sourcePort, int destinationPort, uint seq, byte flags, byte[]? payload = null)
    {
        payload ??= [];
        var tcp = new byte[20 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), seq);
        tcp[12] = 5 << 4;
        tcp[13] = flags;
        payload.CopyTo(tcp, 20);
        return tcp;
    }

    private static byte[] Udp(int sourcePort, int destinationPort, byte[] payload)
    {
        var udp = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(udp, (ushort)sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(2), (ushort)destinationPort);
        BinaryPrimitives.WriteUInt16BigEndian(udp.AsSpan(4), (ushort)udp.Length);
        payload.CopyTo(udp, 8);
        return udp;
    }

    private static Frame TcpFrame(double at, string from, int fromPort, string to, int toPort, uint seq, byte flags, string? payload = null) =>
        new(at, Ethernet(Ipv4(from, to, 6, Tcp(fromPort, toPort, seq, flags, payload is null ? null : Encoding.ASCII.GetBytes(payload)))));

    private static byte[] DnsName(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return [.. bytes];
    }

    private static byte[] Dns(ushort id, string name, bool response, int rcode = 0, string? address = null)
    {
        var message = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)((response ? 0x8180 : 0x0100) | rcode));
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)(address is null ? 0 : 1));
        message.AddRange(header);
        message.AddRange(DnsName(name));
        message.AddRange(new byte[] { 0, 1, 0, 1 });   // A, IN
        if (address is not null)
        {
            message.AddRange(new byte[] { 0xC0, 12, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4 });   // pointer to the question's name
            message.AddRange(IPAddress.Parse(address).GetAddressBytes());
        }
        return [.. message];
    }

    private static byte[] ClientHello(string serverName)
    {
        var name = Encoding.ASCII.GetBytes(serverName);
        var sni = new List<byte> { 0, 0 };
        var list = new List<byte>();
        list.AddRange(new byte[] { (byte)((name.Length + 3) >> 8), (byte)(name.Length + 3), 0, (byte)(name.Length >> 8), (byte)name.Length });
        list.AddRange(name);
        sni.AddRange(new byte[] { (byte)(list.Count >> 8), (byte)list.Count });
        sni.AddRange(list);
        var versions = new byte[] { 0, 43, 0, 3, 2, 3, 4 };
        var extensions = sni.Concat(versions).ToArray();
        var body = new List<byte> { 3, 3 };
        body.AddRange(new byte[32]);                      // random
        body.Add(0);                                      // session id
        body.AddRange(new byte[] { 0, 2, 0x13, 0x01 });   // one cipher suite
        body.AddRange(new byte[] { 1, 0 });               // no compression
        body.AddRange(new byte[] { (byte)(extensions.Length >> 8), (byte)extensions.Length });
        body.AddRange(extensions);
        var handshake = new List<byte> { 1, 0, (byte)(body.Count >> 8), (byte)body.Count };
        handshake.AddRange(body);
        var record = new List<byte> { 22, 3, 1, (byte)(handshake.Count >> 8), (byte)handshake.Count };
        record.AddRange(handshake);
        return [.. record];
    }

    private static byte[] Pcap(IEnumerable<Frame> frames, bool little = true, bool nano = false, int link = 1)
    {
        using var stream = new MemoryStream();
        void U32(uint v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); stream.Write(b); }
        void U16(ushort v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v); stream.Write(b); }
        U32(nano ? 0xA1B23C4D : 0xA1B2C3D4); U16(2); U16(4); U32(0); U32(0); U32(65535); U32((uint)link);
        foreach (var f in frames)
        {
            var at = Start.AddSeconds(f.At) - DateTime.UnixEpoch;
            U32((uint)at.TotalSeconds);
            U32(nano ? (uint)(at.Ticks % TimeSpan.TicksPerSecond * 100) : (uint)(at.Ticks % TimeSpan.TicksPerSecond / 10));
            U32((uint)f.Data.Length); U32((uint)f.Data.Length);
            stream.Write(f.Data);
        }
        return stream.ToArray();
    }

    private static byte[] Pcapng(IEnumerable<Frame> frames, bool little = true, int link = 1)
    {
        using var stream = new MemoryStream();
        byte[] W32(uint v) { var b = new byte[4]; if (little) BinaryPrimitives.WriteUInt32LittleEndian(b, v); else BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
        byte[] W16(ushort v) { var b = new byte[2]; if (little) BinaryPrimitives.WriteUInt16LittleEndian(b, v); else BinaryPrimitives.WriteUInt16BigEndian(b, v); return b; }
        void Block(uint type, byte[] body)
        {
            var padded = (body.Length + 3) & ~3;
            var length = (uint)(12 + padded);
            stream.Write(W32(type)); stream.Write(W32(length)); stream.Write(body); stream.Write(new byte[padded - body.Length]); stream.Write(W32(length));
        }
        Block(0x0A0D0D0A, [.. W32(0x1A2B3C4D), .. W16(1), .. W16(0), .. new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }]);
        // Nanosecond times (if_tsresol = 9), then the end of options.
        Block(1, [.. W16((ushort)link), .. W16(0), .. W32(0), .. W16(9), .. W16(1), 9, 0, 0, 0, .. W16(0), .. W16(0)]);
        foreach (var f in frames)
        {
            var nanos = (ulong)((Start.AddSeconds(f.At) - DateTime.UnixEpoch).Ticks * 100);
            Block(6, [.. W32(0), .. W32((uint)(nanos >> 32)), .. W32((uint)nanos), .. W32((uint)f.Data.Length), .. W32((uint)f.Data.Length), .. f.Data]);
        }
        return stream.ToArray();
    }

    /// <summary>A morning's trouble: an HTTP 500, a DNS answer and an NXDOMAIN, a refused connection, one nobody answers, a
    /// retransmission, a TLS connection, and a DNS server that is not there.</summary>
    private static List<Frame> Trouble() =>
    [
        new(0.000, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(50000, 53, Dns(7, "api.example.com", false))))),
        new(0.012, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 17, Udp(53, 50000, Dns(7, "api.example.com", true, address: "93.184.216.34"))))),
        new(0.020, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(50001, 53, Dns(8, "nope.example.com", false))))),
        new(0.030, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 17, Udp(53, 50001, Dns(8, "nope.example.com", true, rcode: 3))))),
        TcpFrame(0.100, "10.0.0.5", 51000, "93.184.216.34", 80, 1000, Syn),
        TcpFrame(0.110, "93.184.216.34", 80, "10.0.0.5", 51000, 5000, Syn | Ack),
        TcpFrame(0.111, "10.0.0.5", 51000, "93.184.216.34", 80, 1001, Ack),
        TcpFrame(0.112, "10.0.0.5", 51000, "93.184.216.34", 80, 1001, Psh | Ack, "GET /orders/42 HTTP/1.1\r\nHost: api.example.com\r\n\r\n"),
        TcpFrame(0.400, "10.0.0.5", 51000, "93.184.216.34", 80, 1001, Psh | Ack, "GET /orders/42 HTTP/1.1\r\nHost: api.example.com\r\n\r\n"),
        TcpFrame(0.447, "93.184.216.34", 80, "10.0.0.5", 51000, 5001, Psh | Ack, "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\n\r\n"),
        TcpFrame(1.000, "10.0.0.5", 51001, "10.0.0.9", 5432, 1, Syn),
        TcpFrame(1.001, "10.0.0.9", 5432, "10.0.0.5", 51001, 0, Rst | Ack),
        TcpFrame(2.000, "10.0.0.5", 51002, "10.0.0.77", 6379, 1, Syn),
        TcpFrame(3.000, "10.0.0.5", 51002, "10.0.0.77", 6379, 1, Syn),
        new(4.000, Ethernet(Ipv4("10.0.0.5", "93.184.216.34", 6, Tcp(51003, 443, 9, Psh | Ack, ClientHello("shop.example.com"))))),
        new(5.000, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 1,
            [3, 3, 0, 0, 0, 0, 0, 0, .. Ipv4("10.0.0.5", "10.0.0.2", 17, Udp(50002, 53, new byte[8]))]))),
        new(5.500, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(50003, 53, Dns(9, "lost.example.com", false))))),
        new(6.000, Ethernet(new byte[28], 0x0806)),   // ARP
    ];

    private static (string Header, string Text) Read(byte[] capture, string extension = ".pcap")
    {
        var read = DocumentText.Extract(capture, extension);
        Assert.Equal(DocumentProblem.None, read.Problem);
        return (read.Header!, read.Text);
    }

    [Fact]
    public void ACapture_ReadsProblemsFirst_ThenConversationsDnsAndHttp()
    {
        var (header, text) = Read(Pcap(Trouble()));

        Assert.StartsWith("[Network capture: 18 packet(s) over 6.000 s from 2026-11-23 10:00:00 UTC, link Ethernet.", header);
        Assert.Contains("TLS and QUIC payloads are encrypted and not read", header);
        Assert.Contains("Not read: 1 ARP.", header);

        var problems = text[..text.IndexOf("## Conversations", StringComparison.Ordinal)];
        Assert.StartsWith("## Problems\n", problems);
        Assert.Contains("1 retransmission(s) in 10.0.0.5:51000 → 93.184.216.34:80", problems);
        Assert.Contains("+1.001 s  connection refused: 10.0.0.9:5432 answered the SYN of 10.0.0.5:51001 with a reset", problems);
        Assert.Contains("+2.000 s  no answer to 2 SYN(s) from 10.0.0.5:51002 to 10.0.0.77:6379", problems);
        Assert.Contains("+5.000 s  ICMP port unreachable for UDP 10.0.0.2:53, sent by 10.0.0.1 to 10.0.0.5", problems);
        Assert.Contains("+5.500 s  DNS question with no answer in the capture: A lost.example.com", problems);

        Assert.Contains("TCP 10.0.0.5:51003 → 93.184.216.34:443  1 packet(s)", text);
        Assert.Contains("TLS (encrypted), server name shop.example.com, offers TLS 1.3", text);
        Assert.Contains("+0.000 s  A api.example.com → 93.184.216.34 (12 ms)", text);
        Assert.Contains("+0.020 s  A nope.example.com → NXDOMAIN (no such name) (10 ms)", text);
        // The retransmitted request is the same request: one exchange, timed from the first time it was sent.
        Assert.Contains("+0.112 s  GET http://api.example.com/orders/42 → 500 Internal Server Error (335 ms)", text);
        Assert.DoesNotContain("+0.400 s  GET", text);
    }

    [Fact]
    public void Pcapng_AndBothByteOrders_ReadTheSame()
    {
        var reference = Read(Pcap(Trouble())).Text;
        Assert.Equal(reference, Read(Pcap(Trouble(), little: false)).Text);
        Assert.Equal(reference, Read(Pcap(Trouble(), nano: true)).Text);
        Assert.Equal(reference, Read(Pcapng(Trouble()), ".pcapng").Text);
        Assert.Equal(reference, Read(Pcapng(Trouble(), little: false), ".pcapng").Text);
    }

    [Fact]
    public void RawIpAndIpv6_AreRead()
    {
        var ipv6 = new byte[40 + 8 + 12 + 17 + 4];
        ipv6[0] = 0x60;
        var dns = Udp(50000, 53, Dns(1, "v6.example.com", false));
        BinaryPrimitives.WriteUInt16BigEndian(ipv6.AsSpan(4), (ushort)dns.Length);
        ipv6[6] = 17;
        IPAddress.Parse("fd00::5").GetAddressBytes().CopyTo(ipv6, 8);
        IPAddress.Parse("fd00::1").GetAddressBytes().CopyTo(ipv6, 24);
        var packet = ipv6[..40].Concat(dns).ToArray();

        var (header, text) = Read(Pcap([new(0, packet)], link: 101));

        Assert.Contains("link raw IP", header);
        Assert.Contains("UDP [fd00::5]:50000 → [fd00::1]:53", text);
        Assert.Contains("DNS question with no answer in the capture: A v6.example.com", text);
    }

    [Fact]
    public void TheSameProblemAgain_IsOneLineWithItsCount()
    {
        var unreachable = Trouble()[15];
        var (_, text) = Read(Pcap([unreachable, unreachable with { At = 6 }, unreachable with { At = 7.5 }]));
        Assert.Contains("+0.000 s  ICMP port unreachable for UDP 10.0.0.2:53, sent by 10.0.0.1 to 10.0.0.5 — 3 times, the last at +2.500 s\n", text);
        Assert.Single(text.Split('\n'), l => l.Contains("ICMP port unreachable", StringComparison.Ordinal));
    }

    [Fact]
    public void AHostHeader_CannotWriteALineOfTheText()
    {
        var (_, text) = Read(Pcap([TcpFrame(0, "10.0.0.5", 51000, "10.0.0.1", 80, 1, Psh | Ack,
            "GET / HTTP/1.1\r\nHost: x\n## Problems\nnone\r\n\r\n")]));
        Assert.Single(text.Split('\n'), l => l == "## Problems");
        Assert.Contains("GET http://x/ → (no response in the capture)", text);   // the host ends at its line
    }

    [Fact]
    public void ThePacketCapturedTwice_IsNoRetransmission_ButOneSentAgainIs()
    {
        var request = TcpFrame(0.100, "10.0.0.5", 51000, "10.0.0.1", 80, 1, Psh | Ack, "GET / HTTP/1.1\r\n\r\n");
        var twice = Read(Pcap([request, request with { At = 0.1002 }])).Text;
        Assert.StartsWith("## Problems\nnone", twice);
        Assert.Single(twice.Split('\n'), l => l.Contains("GET /", StringComparison.Ordinal));
        Assert.Contains("1 retransmission(s)", Read(Pcap([request, request with { At = 0.300 }])).Text);
    }

    [Fact]
    public void AnInterimStatus_LeavesTheRequestToItsFinalOne()
    {
        var (_, text) = Read(Pcap([
            TcpFrame(0.0, "10.0.0.5", 51000, "10.0.0.1", 80, 1, Psh | Ack, "POST /upload HTTP/1.1\r\nHost: h\r\nExpect: 100-continue\r\n\r\n"),
            TcpFrame(0.010, "10.0.0.1", 80, "10.0.0.5", 51000, 1, Psh | Ack, "HTTP/1.1 100 Continue\r\n\r\n"),
            TcpFrame(0.250, "10.0.0.1", 80, "10.0.0.5", 51000, 26, Psh | Ack, "HTTP/1.1 413 Payload Too Large\r\n\r\n"),
        ]));
        Assert.Contains("POST http://h/upload → 413 Payload Too Large (250 ms)", text);
        Assert.DoesNotContain("before the capture", text);
    }

    [Fact]
    public void ATruncatedDnsAnswer_SaysItWentOverTcp()
    {
        var answer = Dns(4, "big.example.com", true);
        answer[2] |= 0x02;   // TC
        var (_, text) = Read(Pcap([
            new(0, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(50000, 53, Dns(4, "big.example.com", false))))),
            new(0.005, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 17, Udp(53, 50000, answer)))),
        ]));
        Assert.Contains("A big.example.com → truncated: the answer is sent again over TCP (5 ms)", text);
    }

    [Fact]
    public void AScan_IsCountedPastTheConversationsFollowed_NeverHeld()
    {
        var frames = Enumerable.Range(0, PcapText.MaxTracked + 30)
            .Select(i => TcpFrame(i * 0.0001, "10.0.0.5", 40000, "10.0.0.1", 1 + i % 60000, (uint)i, Syn)).ToList();
        var (header, text) = Read(Pcap(frames));
        Assert.Contains($"[Network capture: {PcapText.MaxTracked + 30} packet(s)", header);
        Assert.Contains($"Past {PcapText.MaxTracked} conversations, 30 packet(s) of later ones are counted only", header);
        Assert.Contains($"… and {PcapText.MaxTracked - PcapText.MaxProblems} more, not listed.", text);
    }

    [Fact]
    public void AnAnswerOfAnotherType_IsAnAnswer_NeverNoRecord()
    {
        var question = Dns(3, "5.0.0.10.in-addr.arpa", false);
        question[^3] = 12;   // PTR
        var answer = Dns(3, "5.0.0.10.in-addr.arpa", true, address: "10.0.0.5");
        var at = answer.Length - 16;          // the answer record: pointer, type, class, ttl, length, data
        answer[12 + DnsName("5.0.0.10.in-addr.arpa").Length + 1] = 12;   // the question's type: PTR
        answer[at + 3] = 12;                  // the record's type: PTR
        var name = DnsName("db.local");
        answer = [.. answer[..(at + 10)], 0, (byte)name.Length, .. name];
        BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(at + 10), (ushort)name.Length);
        var (_, text) = Read(Pcap([
            new(0, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(50000, 53, question)))),
            new(0.003, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 17, Udp(53, 50000, answer)))),
        ]));
        Assert.Contains("PTR 5.0.0.10.in-addr.arpa → PTR db.local (3 ms)", text);
    }

    [Fact]
    public void AForwarderFromPort53ToPort53_PairsItsQuestionAndAnswer()
    {
        var (_, text) = Read(Pcap([
            new(0, Ethernet(Ipv4("10.0.0.1", "8.8.8.8", 17, Udp(53, 53, Dns(5, "api.example.com", false))))),
            new(0.020, Ethernet(Ipv4("8.8.8.8", "10.0.0.1", 17, Udp(53, 53, Dns(5, "api.example.com", true, address: "93.184.216.34"))))),
        ]));
        Assert.Contains("A api.example.com → 93.184.216.34 (20 ms)", text);
        Assert.DoesNotContain("no answer", text);
    }

    [Fact]
    public void NothingReadDownToTcpOrUdp_IsNotJudgedClean()
    {
        var (header, text) = Read(Pcap([new(0, new byte[40])], link: 127));
        Assert.Contains("link type 127 (not read)", header);
        Assert.StartsWith("## Problems\nnot judged:", text);
    }

    [Fact]
    public void ASynCapturedTwice_IsOneSyn_AndAnUpgradeIsAnAnswer()
    {
        var syn = TcpFrame(0, "10.0.0.5", 51000, "10.0.0.1", 6379, 1, Syn);
        var (_, text) = Read(Pcap([syn, syn with { At = 0.00001 }]));
        Assert.Contains("no answer to 1 SYN(s)", text);
        Assert.DoesNotContain("retransmission", text);

        var (_, upgraded) = Read(Pcap([
            TcpFrame(0, "10.0.0.5", 51000, "10.0.0.1", 80, 1, Psh | Ack, "GET /ws HTTP/1.1\r\nHost: h\r\nUpgrade: websocket\r\n\r\n"),
            TcpFrame(0.004, "10.0.0.1", 80, "10.0.0.5", 51000, 1, Psh | Ack, "HTTP/1.1 101 Switching Protocols\r\n\r\n"),
        ]));
        Assert.Contains("GET http://h/ws → 101 Switching Protocols (4 ms)", upgraded);
    }

    [Fact]
    public void DnsIsRead_PastTheConversationsFollowed()
    {
        var frames = Enumerable.Range(0, PcapText.MaxTracked)
            .Select(i => TcpFrame(i * 0.0001, "10.0.0.5", 40000, "10.0.0.1", 1 + i, 1, Syn | Ack)).ToList();
        frames.Add(new(3, Ethernet(Ipv4("10.0.0.5", "10.0.0.1", 17, Udp(61000, 53, Dns(1, "late.example.com", false))))));
        frames.Add(new(3.01, Ethernet(Ipv4("10.0.0.1", "10.0.0.5", 17, Udp(53, 61000, Dns(1, "late.example.com", true, rcode: 3))))));
        var (header, text) = Read(Pcap(frames));
        Assert.Contains("2 packet(s) of later ones are counted only (DNS is still read)", header);
        Assert.Contains("A late.example.com → NXDOMAIN (no such name) (10 ms)", text);
    }

    [Fact]
    public void ACleanCapture_SaysItHasNoProblem()
    {
        var (_, text) = Read(Pcap(Trouble().Take(4)));
        Assert.StartsWith("## Problems\nnone: no reset", text);
    }

    [Fact]
    public void EveryListIsCapped_AndTheCapSaid()
    {
        var frames = Enumerable.Range(0, PcapText.MaxConversations + 5)
            .Select(i => TcpFrame(i * 0.01, "10.0.0.5", 40000 + i, "10.0.0.1", 8080, 1, Syn | Ack)).ToList();
        var (_, text) = Read(Pcap(frames));
        Assert.Contains("… and 5 more, not listed.", text);
    }

    [Fact]
    public void ARecordThatClaimsTooMuch_OrAFileCutShort_IsSaid_NeverReadPast()
    {
        var whole = Pcap(Trouble());

        // Cut inside the last packet: what came before is read, and the cut is said.
        var cut = Read(whole[..^10]);
        Assert.Contains("17 packet(s)", cut.Header);
        Assert.Contains("The file ends inside a packet", cut.Header);

        // A record whose length is past anything a link carries: nothing after it is trusted.
        var lying = Pcap(Trouble().Take(2));
        BinaryPrimitives.WriteUInt32LittleEndian(lying.AsSpan(24 + 8), 0x7FFFFFF0);
        var read = DocumentText.Extract(lying, ".pcap");
        Assert.Equal(DocumentProblem.None, read.Problem);
        Assert.Contains("1 damaged block(s) or record(s)", read.Header);
        Assert.Contains("0 packet(s)", read.Header);

        // A pcapng block whose length runs past the file.
        var ng = Pcapng(Trouble());
        var lastBlock = 12 + 20 + ((Trouble()[^1].Data.Length + 3) & ~3);
        BinaryPrimitives.WriteUInt32LittleEndian(ng.AsSpan(ng.Length - lastBlock + 4), 0x0FFFFFF0);
        Assert.Contains("ends inside a packet", DocumentText.Extract(ng, ".pcapng").Header);
    }

    [Fact]
    public void NotACapture_IsUnreadable_AndAnEmptyOne_HoldsNoPacket()
    {
        Assert.Equal(DocumentProblem.Unreadable, DocumentText.Extract(Encoding.ASCII.GetBytes("hello, not a capture"), ".pcap").Problem);
        Assert.Equal(DocumentProblem.NoText, DocumentText.Extract(Pcap([]), ".pcap").Problem);
        Assert.Equal("it holds no packet", AttachedDocuments.NoTextKind("x.pcapng"));
        Assert.True(DocumentText.ToolsRead("trace.PCAPNG"));
        Assert.Equal(64L * 1024 * 1024, DocumentText.MaxBytesOf("x.pcap"));
    }
}
