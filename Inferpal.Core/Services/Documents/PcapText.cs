using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace Inferpal.Services.Documents;

/// <summary>
/// Reads a network capture (<c>.pcap</c>, <c>.pcapng</c>) as what someone looking for a failure wants from it, problems
/// first: TCP resets, connections nobody answered, retransmissions, ICMP unreachable; then the busiest conversations, the DNS
/// questions and their answers, the HTTP exchanges in clear text. TLS and QUIC are said encrypted with what they show in
/// clear (the server name, the version offered) — their content is never guessed. No package, no Wireshark.
/// </summary>
/// <remarks>
/// ⚠ Every length read from the file is bounded by what remains of it before use: a capture is data from anywhere. HTTP is
/// read from the first line and the <c>Host</c> header of one segment; TCP streams are not reassembled, IP fragments not
/// rebuilt (said).
/// </remarks>
internal static class PcapText
{
    /// <summary>How many lines each list keeps; past it, the rest is counted and said.</summary>
    internal const int MaxConversations = 40, MaxDns = 150, MaxHttp = 150, MaxProblems = 100;

    /// <summary>How many conversations are followed: past it (a port scan, a SYN flood) packets are only counted, so a capture's
    /// size bounds the memory read.</summary>
    internal const int MaxTracked = 20_000;

    /// <summary>The largest packet a record may claim: past it the record is damaged (no link carries more).</summary>
    private const int MaxPacket = 256 * 1024;

    private readonly record struct Packet(long Ticks, ReadOnlyMemory<byte> Data, int Link, int Length);

    /// <summary>The text of the capture in <paramref name="bytes"/>; its header says what it is and what is not read.</summary>
    internal static DocumentExtraction Extract(byte[] bytes, CancellationToken ct, int maxChars = PdfText.MaxChars)
    {
        if (bytes.Length < 4) throw new InvalidDataException("not a network capture");
        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        var analysis = new Analysis();
        var (damaged, cut) = magic == 0x0A0D0D0A ? ReadPcapng(bytes, analysis, ct) : ReadPcap(bytes, analysis, ct);
        if (analysis.Packets == 0 && damaged == 0 && !cut) return new(string.Empty, 0, 0, DocumentProblem.NoText);
        var text = analysis.Render(maxChars, out var textCut);

        var header = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"[Network capture: {analysis.Packets} packet(s)"));
        if (analysis.Packets > 0)
        {
            var first = new DateTime(analysis.FirstTicks, DateTimeKind.Utc);
            header.Append(CultureInfo.InvariantCulture,
                $" over {Seconds(analysis.LastTicks - analysis.FirstTicks)} s from {first:yyyy-MM-dd HH:mm:ss} UTC");
            header.Append(", link ").Append(string.Join(", ", analysis.Links.Select(LinkName)));
        }
        header.Append('.');
        header.Append(" Times are seconds since the first packet. Read from the packets alone: TLS and QUIC payloads are encrypted and"
                      + " not read; TCP streams are not reassembled (HTTP is read from the first line of a segment).");
        if (analysis.Untracked > 0)
            header.Append(CultureInfo.InvariantCulture,
                $" Past {MaxTracked} conversations, {analysis.Untracked} packet(s) of later ones are counted only (DNS is still read).");
        if (analysis.Fragments > 0)
            header.Append(CultureInfo.InvariantCulture, $" {analysis.Fragments} IP fragment(s) not rebuilt.");
        if (analysis.Other.Count > 0)
            header.Append(" Not read: ").Append(string.Join(", ", analysis.Other.OrderByDescending(o => o.Value)
                .Select(o => string.Create(CultureInfo.InvariantCulture, $"{o.Value} {o.Key}")))).Append('.');
        if (damaged > 0) header.Append(CultureInfo.InvariantCulture, $" {damaged} damaged block(s) or record(s) could not be read.");
        if (cut) header.Append(" The file ends inside a packet: a capture copied while it was written.");
        if (textCut) header.Append(" The text is cut at its end: open less of the capture or filter it first.");
        header.Append(']');
        return new(text, 0, 0, DocumentProblem.None, textCut ? 1 : 0, header.ToString());
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The packets of a classic pcap: either byte order, micro- or nanosecond times.</summary>
    private static (int Damaged, bool Cut) ReadPcap(byte[] bytes, Analysis analysis, CancellationToken ct)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var (little, nano) = magic switch
        {
            0xA1B2C3D4 => (true, false),
            0xA1B23C4D => (true, true),
            0xD4C3B2A1 => (false, false),
            0x4D3CB2A1 => (false, true),
            _ => throw new InvalidDataException("not a network capture"),
        };
        if (bytes.Length < 24) throw new InvalidDataException("a capture header cut short");
        var link = (int)(U32(bytes, 20, little) & 0xFFFF);
        var at = 24;
        while (at < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (at + 16 > bytes.Length) return (0, true);
            var seconds = U32(bytes, at, little);
            var fraction = U32(bytes, at + 4, little);
            var included = U32(bytes, at + 8, little);
            var original = U32(bytes, at + 12, little);
            if (included > MaxPacket) return (1, false);   // not a record: nothing after it can be trusted
            if (at + 16 + included > bytes.Length) return (0, true);
            var ticks = DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond
                      + (nano ? fraction / 100 : fraction * 10L);
            analysis.Add(new(ticks, bytes.AsMemory(at + 16, (int)included), link, (int)Math.Min(original, int.MaxValue)));
            at += 16 + (int)included;
        }
        return (0, false);
    }

    /// <summary>The packets of a pcapng: its sections, each with its byte order and interfaces (link, time resolution).</summary>
    private static (int Damaged, bool Cut) ReadPcapng(byte[] bytes, Analysis analysis, CancellationToken ct)
    {
        var interfaces = new List<(int Link, long Units, int SnapLength)>();
        var little = true;
        var damaged = 0;
        var at = 0;
        while (at < bytes.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (at + 12 > bytes.Length) return (damaged, true);
            var type = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at)) == 0x0A0D0D0A ? 0x0A0D0D0Au : U32(bytes, at, little);
            if (type == 0x0A0D0D0A)
            {
                // A new section: its own byte order (read from its magic) and its own interfaces.
                var order = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 8));
                if (order == 0x1A2B3C4D) little = true;
                else if (order == 0x4D3C2B1A) little = false;
                else return (damaged + 1, false);
                interfaces.Clear();
            }
            var length = U32(bytes, at + 4, little);
            if (length < 12 || length % 4 != 0 || length > int.MaxValue) return (damaged + 1, false);
            if (at + length > bytes.Length) return (damaged, true);
            var body = bytes.AsSpan(at + 8, (int)length - 12);
            try
            {
                switch (type)
                {
                    case 1:   // interface description
                        if (body.Length < 8) { damaged++; break; }
                        interfaces.Add(((int)U16(body, 0, little), Resolution(body[8..], little), (int)U32(body, 4, little)));
                        break;
                    case 6:   // enhanced packet
                    {
                        var id = (int)U32(body, 0, little);
                        var captured = U32(body, 12, little);
                        if (id >= interfaces.Count || captured > MaxPacket || 20 + captured > body.Length) { damaged++; break; }
                        var stamp = ((ulong)U32(body, 4, little) << 32) | U32(body, 8, little);
                        analysis.Add(new(Ticks(stamp, interfaces[id].Units), bytes.AsMemory(at + 8 + 20, (int)captured),
                                        interfaces[id].Link, (int)Math.Min(U32(body, 16, little), int.MaxValue)));
                        break;
                    }
                    case 3:   // simple packet: interface 0, no time
                    {
                        if (interfaces.Count == 0 || body.Length < 4) { damaged++; break; }
                        var original = U32(body, 0, little);
                        var captured = (int)Math.Min(Math.Min(original, (uint)(body.Length - 4)),
                                                     interfaces[0].SnapLength > 0 ? (uint)interfaces[0].SnapLength : uint.MaxValue);
                        if (captured > MaxPacket) { damaged++; break; }
                        var last = analysis.Packets > 0 ? analysis.LastSeen : DateTime.UnixEpoch.Ticks;
                        analysis.Add(new(last, bytes.AsMemory(at + 8 + 4, captured), interfaces[0].Link, (int)Math.Min(original, int.MaxValue)));
                        break;
                    }
                    case 2:   // packet block (obsolete)
                    {
                        var id = (int)U16(body, 0, little);
                        var captured = U32(body, 12, little);
                        if (id >= interfaces.Count || captured > MaxPacket || 20 + captured > body.Length) { damaged++; break; }
                        var stamp = ((ulong)U32(body, 4, little) << 32) | U32(body, 8, little);
                        analysis.Add(new(Ticks(stamp, interfaces[id].Units), bytes.AsMemory(at + 8 + 20, (int)captured),
                                        interfaces[id].Link, (int)Math.Min(U32(body, 16, little), int.MaxValue)));
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                Diagnostics.Swallow("PcapText.Block", ex);
                damaged++;
            }
            at += (int)length;
        }
        return (damaged, false);
    }

    /// <summary>The time units per second an interface's <c>if_tsresol</c> option gives; microseconds without it.</summary>
    private static long Resolution(ReadOnlySpan<byte> options, bool little)
    {
        while (options.Length >= 4)
        {
            var code = U16(options, 0, little);
            var length = U16(options, 2, little);
            if (code == 0 || 4 + length > options.Length) break;
            if (code == 9 && length >= 1)
            {
                var value = options[4];
                var power = value & 0x7F;
                // Past these, a unit is shorter than a tick's ten-millionth: no capture uses one.
                if ((value & 0x80) == 0 && power <= 12) return (long)Math.Pow(10, power);
                if ((value & 0x80) != 0 && power <= 40) return 1L << power;
                return 1_000_000;
            }
            options = options[Math.Min(options.Length, 4 + ((length + 3) & ~3))..];
        }
        return 1_000_000;
    }

    private static long Ticks(ulong stamp, long units)
    {
        var whole = stamp / (ulong)units;
        var rest = stamp % (ulong)units;
        var seconds = (long)Math.Min(whole, (ulong)(DateTime.MaxValue.Ticks / TimeSpan.TicksPerSecond) - 62_135_596_800UL);
        return DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond
             + (long)(rest * (ulong)TimeSpan.TicksPerSecond / (ulong)units);
    }

    private static string LinkName(int link) => link switch
    {
        1 => "Ethernet",
        0 or 108 => "loopback",
        12 or 14 or 101 or 228 or 229 => "raw IP",
        113 => "Linux cooked",
        276 => "Linux cooked v2",
        _ => string.Create(CultureInfo.InvariantCulture, $"type {link} (not read)"),
    };

    // ── Packets ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class Conversation
    {
        public required string Protocol;
        public required string From;   // the side that spoke first (or sent the SYN)
        public required string To;
        public int Packets;
        public long Bytes;
        public long First, Last;
        public string? Label;
        public bool Syn, SynAck;
        public int SynCount;
        public long LastSyn = long.MinValue;
        public (long Ticks, string By)? Reset;
        public int Retransmissions;
        /// <summary>Each data segment seen, by direction, sequence and length, with when: made on first use.</summary>
        public Dictionary<(bool Forward, uint Seq, int Length), long>? Segments;
        public Queue<(long Ticks, string Line)>? PendingHttp;
    }

    private sealed class Analysis
    {
        private readonly Dictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Client, ushort Id, string Name), (long Ticks, string Question)> _dnsAsked = new();
        private readonly List<(long Ticks, string Line)> _dns = [], _http = [], _icmp = [];
        /// <summary>The most entries each list holds; past it they are counted only (said with the list).</summary>
        private const int MaxHeld = 50_000;
        private int _dnsBeyond, _icmpBeyond, _httpBeyond, _askedBeyond;

        private static void Hold(List<(long, string)> list, ref int beyond, (long, string) entry)
        {
            if (list.Count < MaxHeld) list.Add(entry);
            else beyond++;
        }
        private long _origin = long.MaxValue;
        public int Packets, Fragments, Untracked;
        /// <summary>Packets read down to TCP, UDP or ICMP: without one, "no problem" would be a conclusion drawn from nothing.</summary>
        public int Judged;
        public long FirstTicks = long.MaxValue, LastTicks = long.MinValue, LastSeen;
        public readonly SortedSet<int> Links = [];
        public readonly Dictionary<string, int> Other = new(StringComparer.Ordinal);

        private void Count(string what) => Other[what] = Other.GetValueOrDefault(what) + 1;

        public void Add(Packet packet)
        {
            Packets++;
            LastSeen = packet.Ticks;
            FirstTicks = Math.Min(FirstTicks, packet.Ticks);
            LastTicks = Math.Max(LastTicks, packet.Ticks);
            Links.Add(packet.Link);
            _origin = Math.Min(_origin, packet.Ticks);
            var data = packet.Data.Span;
            int at, version;
            var outgoing = false;
            switch (packet.Link)
            {
                case 1:
                {
                    if (data.Length < 14) { Count("short frames"); return; }
                    var type = BinaryPrimitives.ReadUInt16BigEndian(data[12..]);
                    at = 14;
                    for (var tags = 0; (type is 0x8100 or 0x88A8) && tags < 2 && data.Length >= at + 4; tags++)
                    {
                        type = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 2)..]);
                        at += 4;
                    }
                    if (type == 0x0800) version = 4;
                    else if (type == 0x86DD) version = 6;
                    else { Count(type == 0x0806 ? "ARP" : type < 0x0600 ? "802.3 frames" : $"EtherType 0x{type:X4}"); return; }
                    break;
                }
                case 0 or 108:
                {
                    if (data.Length < 4) { Count("short frames"); return; }
                    var family = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), BinaryPrimitives.ReadUInt32BigEndian(data));
                    at = 4;
                    if (family == 2) version = 4;
                    else if (family is 24 or 28 or 30 or 10) version = 6;
                    else { Count("loopback frames of another family"); return; }
                    break;
                }
                case 12 or 14 or 101 or 228 or 229:
                    at = 0;
                    version = data.Length > 0 ? data[0] >> 4 : 0;
                    break;
                case 113 or 276:
                {
                    var size = packet.Link == 113 ? 16 : 20;
                    if (data.Length < size) { Count("short frames"); return; }
                    var type = BinaryPrimitives.ReadUInt16BigEndian(packet.Link == 113 ? data[14..] : data);
                    outgoing = (packet.Link == 113 ? BinaryPrimitives.ReadUInt16BigEndian(data) : data[10]) == 4;
                    at = size;
                    if (type == 0x0800) version = 4;
                    else if (type == 0x86DD) version = 6;
                    else { Count(type == 0x0806 ? "ARP" : $"protocol 0x{type:X4}"); return; }
                    break;
                }
                default:
                    Count($"packets of link type {packet.Link}");
                    return;
            }
            Ip(packet, data[at..], version, outgoing);
        }

        private void Ip(Packet packet, ReadOnlySpan<byte> ip, int version, bool outgoing)
        {
            string source, destination;
            int protocol;
            ReadOnlySpan<byte> payload;
            if (version == 4)
            {
                if (ip.Length < 20 || ip[0] >> 4 != 4) { Count("damaged IP packets"); return; }
                var headerLength = (ip[0] & 0x0F) * 4;
                var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
                if (headerLength < 20 || ip.Length < headerLength) { Count("damaged IP packets"); return; }
                var flags = BinaryPrimitives.ReadUInt16BigEndian(ip[6..]);
                protocol = ip[9];
                source = new IPAddress(ip.Slice(12, 4)).ToString();
                destination = new IPAddress(ip.Slice(16, 4)).ToString();
                // A capture truncated by its snap length keeps fewer bytes than the header says.
                var end = total >= headerLength ? Math.Min(total, ip.Length) : ip.Length;
                payload = ip[headerLength..end];
                if ((flags & 0x1FFF) != 0) { Fragments++; return; }   // a later fragment: no transport header
                if ((flags & 0x2000) != 0) Fragments++;               // the first of several: its header is read
            }
            else if (version == 6)
            {
                if (ip.Length < 40) { Count("damaged IP packets"); return; }
                source = new IPAddress(ip.Slice(8, 16)).ToString();
                destination = new IPAddress(ip.Slice(24, 16)).ToString();
                protocol = ip[6];
                var length = BinaryPrimitives.ReadUInt16BigEndian(ip[4..]);
                // 0: a jumbogram or a segment the system offloads (BIG TCP) — the captured length is what there is.
                payload = ip[40..(length == 0 ? ip.Length : Math.Min(ip.Length, 40 + length))];
                for (var hops = 0; protocol is 0 or 43 or 60 or 44 && hops < 8; hops++)
                {
                    if (payload.Length < 8) { Count("damaged IP packets"); return; }
                    if (protocol == 44)
                    {
                        Fragments++;
                        if ((BinaryPrimitives.ReadUInt16BigEndian(payload[2..]) & 0xFFF8) != 0) return;
                        protocol = payload[0];
                        payload = payload[8..];
                        continue;
                    }
                    var size = (payload[1] + 1) * 8;
                    if (payload.Length < size) { Count("damaged IP packets"); return; }
                    protocol = payload[0];
                    payload = payload[size..];
                }
            }
            else { Count("packets that are not IP"); return; }

            // On Linux's "any" interface a loopback packet is captured going out and again coming in: one is enough.
            if (outgoing && IPAddress.TryParse(destination, out var to) && IPAddress.IsLoopback(to)) { Count("outgoing loopback copies"); return; }

            switch (protocol)
            {
                case 6: Tcp(packet, source, destination, payload, version); break;
                case 17: Udp(packet, source, destination, payload, version); break;
                case 1 or 58: Icmp(packet, source, destination, payload, protocol == 58); break;
                default: Count(protocol switch { 2 => "IGMP", 47 => "GRE", 50 => "IPsec ESP", 89 => "OSPF", _ => $"IP protocol {protocol}" }); break;
            }
        }

        private static string Endpoint(string address, int port, int version) =>
            version == 6 ? $"[{address}]:{port}" : $"{address}:{port}";

        private Conversation? ConversationOf(string protocol, string source, string destination, long ticks, bool forwardIfNew)
        {
            var key = protocol + " " + (string.CompareOrdinal(source, destination) < 0 ? source + " " + destination : destination + " " + source);
            if (!_conversations.TryGetValue(key, out var conversation))
            {
                if (_conversations.Count >= MaxTracked) { Untracked++; return null; }
                conversation = new Conversation
                {
                    Protocol = protocol, From = forwardIfNew ? source : destination, To = forwardIfNew ? destination : source,
                    First = ticks, Last = ticks,
                };
                _conversations[key] = conversation;
            }
            return conversation;
        }

        private void Tcp(Packet packet, string sourceIp, string destinationIp, ReadOnlySpan<byte> tcp, int version)
        {
            if (tcp.Length < 20) { Count("damaged TCP segments"); return; }
            if ((tcp[12] >> 4) * 4 is >= 20 and var size && size <= tcp.Length) Judged++;
            var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(tcp);
            var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(tcp[2..]);
            var seq = BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]);
            var offset = (tcp[12] >> 4) * 4;
            var flags = tcp[13];
            if (offset < 20 || offset > tcp.Length) { Count("damaged TCP segments"); return; }
            var payload = tcp[offset..];
            bool syn = (flags & 0x02) != 0, ack = (flags & 0x10) != 0, rst = (flags & 0x04) != 0;
            var source = Endpoint(sourceIp, sourcePort, version);
            var destination = Endpoint(destinationIp, destinationPort, version);
            // The side that sends a SYN without ACK opened the connection; otherwise the lower port is taken for the server.
            var forwardIfNew = syn && !ack || !syn && sourcePort > destinationPort;
            if (ConversationOf("TCP", source, destination, packet.Ticks, forwardIfNew) is not { } c) return;
            var forward = c.From == source;
            c.Packets++;
            c.Bytes += packet.Length;
            c.Last = Math.Max(c.Last, packet.Ticks);
            // A SYN seen again within a millisecond is the same SYN captured twice, not sent again.
            var synAgain = syn && !ack && packet.Ticks - c.LastSyn >= TimeSpan.TicksPerMillisecond;
            if (syn && !ack) { c.Syn = true; if (synAgain || c.SynCount == 0) c.SynCount++; c.LastSyn = packet.Ticks; }
            if (syn && ack) c.SynAck = true;
            if (rst && c.Reset is null) c.Reset = (packet.Ticks, source);
            // A segment that carries data and repeats one already seen in its direction is a retransmission. One byte is
            // left out: a keep-alive probe repeats its byte by design.
            var repeated = false;
            if (payload.Length > 1)
            {
                c.Segments ??= [];
                var segment = (forward, seq, payload.Length);
                if (c.Segments.TryGetValue(segment, out var before))
                {
                    repeated = true;
                    // Seen again within a millisecond: the same packet captured twice (an "any" interface, a bridge and its
                    // port), not sent twice.
                    if (packet.Ticks - before >= TimeSpan.TicksPerMillisecond) c.Retransmissions++;
                }
                c.Segments[segment] = packet.Ticks;
            }
            if (synAgain && c.SynCount > 1) c.Retransmissions++;
            // A retransmitted request is the same request: read once, timed from the first time it was sent.
            if (payload.Length == 0 || repeated) return;
            Application(packet, c, forward, payload, sourcePort, destinationPort);
        }

        private void Application(Packet packet, Conversation c, bool forward, ReadOnlySpan<byte> payload, int sourcePort, int destinationPort)
        {
            // TLS: a record header — content type 20 to 23, version 3.x. A ClientHello shows the server name in clear.
            if (payload.Length >= 5 && payload[0] is >= 20 and <= 23 && payload[1] == 3)
            {
                if (payload[0] == 22 && payload.Length > 9 && payload[5] == 1 && ClientHello(payload) is { } hello) c.Label = hello;
                else c.Label ??= "TLS (encrypted)";
                return;
            }
            if (HttpRequest(payload) is { } request)
            {
                c.Label ??= "HTTP";
                (c.PendingHttp ??= new()).Enqueue((packet.Ticks, request));
                return;
            }
            if (payload.StartsWith("HTTP/1."u8))
            {
                c.Label ??= "HTTP";
                var status = FirstLine(payload, 120);
                status = status.Length > 9 ? status[9..] : status;
                // Interim (100 Continue): the request waits for its final status. 101 is final: the connection changes protocol.
                if (status.StartsWith('1') && !status.StartsWith("101", StringComparison.Ordinal)) return;
                if (c.PendingHttp is not null && c.PendingHttp.TryDequeue(out var asked))
                    Hold(_http, ref _httpBeyond, (asked.Ticks, $"{asked.Line} → {status} ({Millis(packet.Ticks - asked.Ticks)})"));
                else Hold(_http, ref _httpBeyond, (packet.Ticks, $"(response to a request before the capture) → {status}"));
                return;
            }
            if (sourcePort == 53 || destinationPort == 53) c.Label ??= "DNS over TCP";
        }

        private void Udp(Packet packet, string sourceIp, string destinationIp, ReadOnlySpan<byte> udp, int version)
        {
            if (udp.Length < 8) { Count("damaged UDP datagrams"); return; }
            Judged++;
            var sourcePort = BinaryPrimitives.ReadUInt16BigEndian(udp);
            var destinationPort = BinaryPrimitives.ReadUInt16BigEndian(udp[2..]);
            var payload = udp[8..];
            var source = Endpoint(sourceIp, sourcePort, version);
            var destination = Endpoint(destinationIp, destinationPort, version);
            var dns = sourcePort == 53 || destinationPort == 53;
            // Read before the cap of conversations: a resolver picks a port per question, so a long ordinary capture opens
            // a conversation for each.
            if (dns) Dns(packet, payload, source, destination);
            if (ConversationOf("UDP", source, destination, packet.Ticks, sourcePort > destinationPort || sourcePort == destinationPort) is not { } c)
                return;
            c.Packets++;
            c.Bytes += packet.Length;
            c.Last = Math.Max(c.Last, packet.Ticks);
            if (dns) c.Label ??= "DNS";
            else if (sourcePort == 443 || destinationPort == 443) c.Label ??= "QUIC (encrypted)";
            else if (sourcePort is 5353 || destinationPort is 5353) c.Label ??= "mDNS";
            else if (sourcePort is 67 or 68 || destinationPort is 67 or 68) c.Label ??= "DHCP";
            else if (sourcePort == 123 || destinationPort == 123) c.Label ??= "NTP";
        }

        private void Icmp(Packet packet, string source, string destination, ReadOnlySpan<byte> icmp, bool v6)
        {
            if (icmp.Length < 8) { Count("damaged ICMP messages"); return; }
            Judged++;
            var type = icmp[0];
            var code = icmp[1];
            var unreachable = v6 ? type == 1 : type == 3;
            if (!unreachable) { Count(v6 ? "ICMPv6 other than unreachable" : "ICMP other than unreachable"); return; }
            var what = v6
                ? code switch { 0 => "no route", 1 => "prohibited", 3 => "address unreachable", 4 => "port unreachable", _ => $"code {code}" }
                : code switch { 0 => "network unreachable", 1 => "host unreachable", 3 => "port unreachable", 13 => "prohibited", _ => $"code {code}" };
            var about = string.Empty;
            // The packet it answers is quoted after the header: its destination names what could not be reached.
            var inner = icmp[8..];
            if (!v6 && inner.Length >= 28 && inner[0] >> 4 == 4)
            {
                var length = (inner[0] & 0x0F) * 4;
                if (inner.Length >= length + 4)
                    about = $" for {(inner[9] == 17 ? "UDP" : inner[9] == 6 ? "TCP" : "IP")} "
                          + Endpoint(new IPAddress(inner.Slice(16, 4)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(inner[(length + 2)..]), 4);
            }
            else if (v6 && inner.Length >= 44)
                about = $" for {(inner[6] == 17 ? "UDP" : inner[6] == 6 ? "TCP" : "IP")} "
                      + Endpoint(new IPAddress(inner.Slice(24, 16)).ToString(), BinaryPrimitives.ReadUInt16BigEndian(inner[42..]), 6);
            Hold(_icmp, ref _icmpBeyond, (packet.Ticks, $"ICMP {what}{about}, sent by {source} to {destination}"));
        }

        /// <summary>A DNS message; its client is the sender of a question and the receiver of an answer (a server forwarding
        /// from port 53 to port 53 included).</summary>
        private void Dns(Packet packet, ReadOnlySpan<byte> dns, string source, string destination)
        {
            if (dns.Length < 12) return;
            var id = BinaryPrimitives.ReadUInt16BigEndian(dns);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(dns[2..]);
            var questions = BinaryPrimitives.ReadUInt16BigEndian(dns[4..]);
            var answers = BinaryPrimitives.ReadUInt16BigEndian(dns[6..]);
            var at = 12;
            if (questions == 0 || DnsName(dns, ref at) is not { } name || at + 4 > dns.Length) return;
            var type = DnsType(BinaryPrimitives.ReadUInt16BigEndian(dns[at..]));
            at += 4;
            for (var i = 1; i < questions; i++)
                if (DnsName(dns, ref at) is null || (at += 4) > dns.Length) return;
            var question = $"{type} {name}";
            var client = (flags & 0x8000) == 0 ? source : destination;
            if ((flags & 0x8000) == 0)
            {
                if (_dnsAsked.Count < MaxHeld || _dnsAsked.ContainsKey((client, id, name))) _dnsAsked[(client, id, name)] = (packet.Ticks, question);
                else _askedBeyond++;
                return;
            }
            var rcode = flags & 0x0F;
            var told = new List<string>();
            for (var i = 0; i < answers && told.Count < 4; i++)
            {
                if (DnsName(dns, ref at) is null || at + 10 > dns.Length) break;
                var answerType = BinaryPrimitives.ReadUInt16BigEndian(dns[at..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(dns[(at + 8)..]);
                at += 10;
                if (at + length > dns.Length) break;
                var record = dns.Slice(at, length);
                if (answerType == 1 && length == 4) told.Add(new IPAddress(record).ToString());
                else if (answerType == 28 && length == 16) told.Add(new IPAddress(record).ToString());
                else if (answerType == 5) { var inner = at; if (DnsName(dns, ref inner) is { } alias) told.Add("alias " + alias); }
                else if (answerType is 2 or 12) { var inner = at; if (DnsName(dns, ref inner) is { } target) told.Add($"{DnsType(answerType)} {target}"); }
                else if (answerType == 15 && length > 2) { var inner = at + 2; if (DnsName(dns, ref inner) is { } exchange) told.Add("MX " + exchange); }
                else told.Add($"a {DnsType(answerType)} record");   // its data is not read, but it is an answer
                at += length;
            }
            if (answers > told.Count && told.Count > 0) told.Add($"{answers - told.Count} more");
            var answer = rcode switch
            {
                0 when told.Count == 0 && (flags & 0x0200) != 0 => "truncated: the answer is sent again over TCP",
                0 => told.Count > 0 ? string.Join(", ", told) : "no record",
                2 => "SERVFAIL",
                3 => "NXDOMAIN (no such name)",
                5 => "REFUSED",
                _ => $"error code {rcode}",
            };
            if (_dnsAsked.Remove((client, id, name), out var asked))
                Hold(_dns, ref _dnsBeyond, (asked.Ticks, $"{asked.Question} → {answer} ({Millis(packet.Ticks - asked.Ticks)})"));
            else Hold(_dns, ref _dnsBeyond, (packet.Ticks, $"{question} → {answer} (its question is not in the capture)"));
        }

        /// <summary>The text: problems, conversations, DNS, HTTP — each list capped and the cap said.</summary>
        public string Render(int maxChars, out bool cut)
        {
            var origin = _origin == long.MaxValue ? 0 : _origin;
            var sb = new StringBuilder();
            string At(long ticks) => "+" + Seconds(ticks - origin) + " s";

            var problems = new List<(long Ticks, string Line)>();
            foreach (var c in _conversations.Values.Where(c => c.Protocol == "TCP"))
            {
                var name = $"{c.From} → {c.To}";
                if (c.Reset is { } reset)
                    problems.Add((reset.Ticks, c.Syn && !c.SynAck && reset.By == c.To
                        ? $"connection refused: {c.To} answered the SYN of {c.From} with a reset"
                        : $"reset by {reset.By} in {name}, {Seconds(reset.Ticks - c.First)} s after its first packet"));
                else if (c.Syn && !c.SynAck)
                    problems.Add((c.First, $"no answer to {c.SynCount} SYN(s) from {c.From} to {c.To}"));
                if (c.Retransmissions > 0)
                    problems.Add((c.First, $"{c.Retransmissions} retransmission(s) in {name}"));
            }
            problems.AddRange(_icmp);
            problems.AddRange(_dnsAsked.Values.Select(q => (q.Ticks, $"DNS question with no answer in the capture: {q.Question}")));
            // The same problem again (a client retrying a DNS server that is not there) is one line with its count.
            var grouped = problems.GroupBy(p => p.Line, StringComparer.Ordinal)
                .Select(g => (Ticks: g.Min(p => p.Ticks), Line: g.Count() == 1 ? g.Key
                              : $"{g.Key} — {g.Count()} times, the last at {At(g.Max(p => p.Ticks))}"))
                .OrderBy(p => p.Ticks).Select(p => $"{At(p.Ticks)}  {p.Line}").ToList();
            Section(sb, "Problems", grouped, MaxProblems, _icmpBeyond + _askedBeyond,
                    Judged > 0 ? "none: no reset, no unanswered connection or DNS question, no retransmission, no ICMP unreachable"
                               : "not judged: no packet of the capture was read down to TCP, UDP or ICMP");

            var conversations = _conversations.Values.OrderByDescending(c => c.Bytes).ThenBy(c => c.First)
                .Select(c => $"{c.Protocol} {c.From} → {c.To}  {c.Packets} packet(s), {Size(c.Bytes)}, {Seconds(c.Last - c.First)} s"
                           + (c.Label is { } label ? "  " + label : string.Empty)
                           + (c.Reset is not null ? "  [reset]" : string.Empty))
                .ToList();
            Section(sb, "Conversations, largest first", conversations, MaxConversations, 0, "none");
            if (_dns.Count > 0) Section(sb, "DNS", _dns.OrderBy(d => d.Ticks).Select(d => $"{At(d.Ticks)}  {d.Line}").ToList(), MaxDns, _dnsBeyond, "none");
            var http = _http.Concat(_conversations.Values.SelectMany(c => (c.PendingHttp ?? new())
                                       .Select(p => (p.Ticks, $"{p.Line} → (no response in the capture)"))))
                            .OrderBy(h => h.Item1).Select(h => $"{At(h.Item1)}  {h.Item2}").ToList();
            if (http.Count > 0) Section(sb, "HTTP in clear text", http, MaxHttp, _httpBeyond, "none");

            cut = sb.Length > maxChars;
            return cut ? sb.ToString(0, maxChars) : sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, List<string> lines, int cap, int beyond, string none)
        {
            sb.Append("## ").Append(title).Append('\n');
            if (lines.Count == 0) sb.Append(none).Append('\n');
            foreach (var line in lines.Take(cap)) sb.Append(line).Append('\n');
            if (lines.Count + beyond > cap) sb.Append(CultureInfo.InvariantCulture, $"… and {lines.Count + beyond - Math.Min(cap, lines.Count)} more, not listed.\n");
            sb.Append('\n');
        }
    }

    // ── Application layers ───────────────────────────────────────────────────────────────────────────

    private static readonly byte[][] Methods =
        [.. new[] { "GET ", "POST ", "PUT ", "DELETE ", "HEAD ", "PATCH ", "OPTIONS ", "CONNECT ", "TRACE " }.Select(Encoding.ASCII.GetBytes)];

    /// <summary>"GET http://host/path" from a request's first segment, or null when it does not start one.</summary>
    private static string? HttpRequest(ReadOnlySpan<byte> payload)
    {
        byte[]? method = null;
        foreach (var candidate in Methods)
            if (payload.StartsWith(candidate)) { method = candidate; break; }
        if (method is null) return null;
        var line = FirstLine(payload, 400);
        var parts = line.Split(' ');
        if (parts.Length < 3 || !parts[^1].StartsWith("HTTP/", StringComparison.Ordinal)) return null;
        var target = parts[1];
        var head = Encoding.ASCII.GetString(payload[..Math.Min(payload.Length, 4096)]);
        var hostAt = head.IndexOf("\r\nHost:", StringComparison.OrdinalIgnoreCase);
        if (hostAt >= 0 && target.StartsWith('/'))
        {
            var end = head.IndexOfAny(['\r', '\n'], hostAt + 7);
            // Printable characters only: the header is data from the capture, and a line break in it would write a line of
            // this text.
            var host = new string((end > 0 ? head[(hostAt + 7)..end] : head[(hostAt + 7)..]).Trim()
                                  .Select(ch => ch < ' ' || ch > '~' ? '?' : ch).Take(200).ToArray());
            target = "http://" + host + target;
        }
        return $"{parts[0]} {(target.Length > 200 ? target[..199] + "…" : target)}";
    }

    /// <summary>The first line of a payload, as ASCII, at most <paramref name="max"/> characters.</summary>
    private static string FirstLine(ReadOnlySpan<byte> payload, int max)
    {
        var end = payload.IndexOf((byte)'\n');
        var line = payload[..(end < 0 ? Math.Min(payload.Length, max) : Math.Min(end, max))];
        var text = Encoding.ASCII.GetString(line).TrimEnd('\r');
        return new string(text.Select(c => c < ' ' || c > '~' ? '?' : c).ToArray());
    }

    /// <summary>What a TLS ClientHello shows in clear: the server name and the newest version it offers.</summary>
    private static string? ClientHello(ReadOnlySpan<byte> record)
    {
        var at = 5 + 4 + 2 + 32;
        if (record.Length < at + 1) return null;
        at += 1 + record[at];                                                    // session id
        if (record.Length < at + 2) return null;
        at += 2 + BinaryPrimitives.ReadUInt16BigEndian(record[at..]);             // cipher suites
        if (record.Length < at + 1) return null;
        at += 1 + record[at];                                                    // compression
        if (record.Length < at + 2) return "TLS (encrypted)";
        var end = Math.Min(record.Length, at + 2 + BinaryPrimitives.ReadUInt16BigEndian(record[at..]));
        at += 2;
        string? name = null;
        var tls13 = false;
        while (at + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(record[at..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[(at + 2)..]);
            var body = record.Slice(at + 4, Math.Min(length, end - at - 4));
            if (type == 0 && body.Length >= 5 && body[2] == 0)
            {
                var nameLength = BinaryPrimitives.ReadUInt16BigEndian(body[3..]);
                if (5 + nameLength <= body.Length) name = FirstLine(body.Slice(5, nameLength), 255);
            }
            else if (type == 43 && body.Length >= 1)
                for (var i = 1; i + 1 < body.Length && i <= body[0]; i += 2)
                    tls13 |= body[i] == 3 && body[i + 1] == 4;
            at += 4 + length;
        }
        return "TLS (encrypted)" + (name is not null ? $", server name {name}" : string.Empty) + (tls13 ? ", offers TLS 1.3" : string.Empty);
    }

    /// <summary>A DNS name at <paramref name="at"/>, compression pointers followed (bounded); <paramref name="at"/> moves past it.</summary>
    private static string? DnsName(ReadOnlySpan<byte> dns, ref int at)
    {
        var labels = new List<string>();
        var position = at;
        var jumped = false;
        for (var steps = 0; steps < 128; steps++)
        {
            if (position >= dns.Length) return null;
            var length = dns[position];
            if (length == 0)
            {
                if (!jumped) at = position + 1;
                return labels.Count == 0 ? "." : string.Join('.', labels);
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= dns.Length) return null;
                if (!jumped) at = position + 2;
                jumped = true;
                position = ((length & 0x3F) << 8) | dns[position + 1];
                continue;
            }
            if (position + 1 + length > dns.Length) return null;
            labels.Add(FirstLine(dns.Slice(position + 1, length), 63));
            position += 1 + length;
        }
        return null;
    }

    private static string DnsType(ushort type) => type switch
    {
        1 => "A", 2 => "NS", 5 => "CNAME", 6 => "SOA", 12 => "PTR", 15 => "MX", 16 => "TXT", 28 => "AAAA", 33 => "SRV",
        65 => "HTTPS", 255 => "ANY",
        _ => string.Create(CultureInfo.InvariantCulture, $"type {type}"),
    };

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static string Seconds(long ticks) =>
        (ticks / (double)TimeSpan.TicksPerSecond).ToString("0.000", CultureInfo.InvariantCulture);

    private static string Millis(long ticks) =>
        (ticks / (double)TimeSpan.TicksPerMillisecond).ToString("0.#", CultureInfo.InvariantCulture) + " ms";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} kB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
    };

    private static uint U32(ReadOnlySpan<byte> bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);

    private static ushort U16(ReadOnlySpan<byte> bytes, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[at..]) : BinaryPrimitives.ReadUInt16BigEndian(bytes[at..]);
}
