using System.Collections.Concurrent;
using System.IO.Ports;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PacketDotNet;
using SharpPcap;

var server   = GetArg(args, "--server")    ?? Environment.GetEnvironmentVariable("PACKET_LAB_SERVER")    ?? "ws://127.0.0.1:8080";
var workerId = GetArg(args, "--worker-id") ?? Environment.GetEnvironmentVariable("PACKET_LAB_WORKER_ID") ?? "local";

Console.WriteLine($"[Worker] id={workerId}  server={server}");
var worker = new PacketLabWorker(NormalizeWsUrl(server, workerId), workerId);
await worker.RunAsync();

static string? GetArg(string[] args, string name) {
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
    return null;
}

static string NormalizeWsUrl(string server, string workerId) {
    var b = server.TrimEnd('/');
    if (b.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) b = "ws://"  + b[7..];
    if (b.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) b = "wss://" + b[8..];
    if (!b.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !b.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        b = "ws://" + b;
    return $"{b}/ws/worker?workerId={Uri.EscapeDataString(workerId)}";
}

// ─────────────────────────────────────────────────────────────────────────────

sealed class PacketLabWorker
{
    private readonly Uri    _uri;
    private readonly string _id;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // Capture state
    private readonly ConcurrentQueue<CaptureRecord> _captures = new();
    private readonly List<ILiveDevice> _captureDevices = new();
    private int _captureNo;

    // Serial state
    private SerialPort? _serial;
    private readonly StringBuilder _serialBuf = new();
    private readonly object _serialLock = new();

    private ClientWebSocket? _ws;

    public PacketLabWorker(string uri, string id) { _uri = new Uri(uri); _id = id; }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                await ws.ConnectAsync(_uri, CancellationToken.None);
                Console.WriteLine($"[Worker] connected → {_uri}");
                await SendAsync(new { type = "hello", payload = new { workerId = _id, machine = Environment.MachineName, user = Environment.UserName, interfaces = GetInterfaces() } });
                await ReceiveLoop(ws);
            }
            catch (Exception ex) { Console.WriteLine($"[Worker] disconnected: {ex.Message}"); }
            await Task.Delay(2000);
        }
    }

    // ── WebSocket receive loop ────────────────────────────────────────────────

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buf = new byte[256 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do {
                result = await ws.ReceiveAsync(buf, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, result.Count);
            } while (!result.EndOfMessage);
            await HandleMessage(Encoding.UTF8.GetString(ms.ToArray()));
        }
    }

    private async Task HandleMessage(string raw)
    {
        JsonObject? msg;
        try { msg = JsonNode.Parse(raw) as JsonObject; } catch { return; }
        if (msg?["type"]?.GetValue<string>() != "command") return;

        var id      = msg["id"]?.GetValue<string>() ?? "";
        var command = msg["command"]?.GetValue<string>() ?? "";
        var payload = msg["payload"] as JsonObject ?? new JsonObject();

        try
        {
            var data = command.ToLowerInvariant() switch
            {
                // ── Capture ──────────────────────────────────────────────────
                "getinterfaces" => SerializeNode(new { interfaces = GetInterfaces() }),
                "startcapture"  => StartCapture(payload),
                "stopcapture"   => StopCapture(),
                "clearcapture"  => ClearCapture(),
                "getcaptures"   => GetCaptures(payload),
                "sendhex"       => SendHex(payload),
                "status"        => SerializeNode(new { workerId = _id, capturing = _captureDevices.Count > 0, captureCount = _captures.Count }),
                // ── Packet build / send ───────────────────────────────────────
                "build"         => BuildPacket(payload),
                "send"          => await SendPackets(payload),
                // ── Serial ───────────────────────────────────────────────────
                "seriallist"    => SerialList(),
                "serialstatus"  => SerialStatus(),
                "serialopen"    => SerialOpen(payload),
                "serialclose"   => SerialClose(),
                "serialwrite"   => SerialWrite(payload),
                "serialread"    => SerialRead(),
                "serialclear"   => SerialClear(),
                "serialcontrol" => SerialControl(payload),
                _ => throw new InvalidOperationException($"Unknown command: {command}")
            };
            await SendAsync(new { type = "reply", replyTo = id, ok = true, data });
        }
        catch (Exception ex)
        {
            await SendAsync(new { type = "reply", replyTo = id, ok = false, error = ex.Message });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── Packet Build / Send ──────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private JsonNode? BuildPacket(JsonObject p)
    {
        var bytes   = BuildFrame(p);
        var hexFull = BitConverter.ToString(bytes).Replace("-", "");
        var decoded = DecodeFrame(bytes);
        // Return both "stdout" wrapper (GitHub compat) and flat fields
        var result  = new { frameHex = hexFull, frameBytes = bytes.Length, decoded, stdout = new { frameHex = hexFull, decoded } };
        return SerializeNode(result);
    }

    private async Task<JsonNode?> SendPackets(JsonObject p)
    {
        var iface      = p["interface"]?.GetValue<string>() ?? "";
        var count      = Math.Clamp(p["count"]?.GetValue<int>() ?? 1, 1, 1_000_000);
        var intervalMs = Math.Clamp(p["intervalMs"]?.GetValue<int>() ?? 0, 0, 60_000);

        var bytes  = BuildFrame(p);
        var dev    = FindDevice(iface) ?? throw new InvalidOperationException($"Interface not found: {iface}");

        dev.Open(DeviceModes.None);
        try
        {
            for (var i = 0; i < count; i++)
            {
                dev.SendPacket(bytes);
                if (intervalMs > 0 && i < count - 1) await Task.Delay(intervalMs);
            }
        }
        finally { dev.Close(); }

        return SerializeNode(new { framesSent = count, bytesSent = count * bytes.Length, frameBytes = bytes.Length });
    }

    // Build a raw ethernet frame from a JSON profile
    private byte[] BuildFrame(JsonObject p)
    {
        var protocol   = p["protocol"]?.GetValue<string>()?.ToLowerInvariant() ?? "raw";
        var dstMacStr  = NormalizeMac(p["dstMac"]?.GetValue<string>() ?? "FF:FF:FF:FF:FF:FF");
        var srcMacStr  = NormalizeMac(p["srcMac"]?.GetValue<string>() ?? "02:00:00:00:00:01");
        var dstMacBytes = ParseMac(dstMacStr);
        var srcMacBytes = ParseMac(srcMacStr);
        var payload     = BuildPayload(p["payload"] as JsonObject, protocol, p["count"]?.GetValue<int>() ?? 1);

        byte[] frame = protocol switch
        {
            "udp"  => BuildUdp(srcMacBytes, dstMacBytes, p, payload),
            "icmp" => BuildIcmp(srcMacBytes, dstMacBytes, p, payload),
            "arp"  => BuildArp(srcMacBytes, dstMacBytes, p),
            _      => BuildRaw(srcMacBytes, dstMacBytes, payload)
        };

        // VLAN tagging
        var vlan = p["vlan"] as JsonObject;
        if (vlan?["enabled"]?.GetValue<bool>() == true)
        {
            var vid  = vlan["id"]?.GetValue<int>() ?? 100;
            var prio = vlan["priority"]?.GetValue<int>() ?? 0;
            frame = InsertVlan(frame, vid, prio);
        }

        return frame;
    }

    // UDP: 14-byte eth + 20-byte IP + 8-byte UDP + payload
    private byte[] BuildUdp(byte[] src, byte[] dst, JsonObject p, byte[] payload)
    {
        var srcIp  = ParseIp(p["srcIp"]?.GetValue<string>() ?? "192.168.1.1");
        var dstIp  = ParseIp(p["dstIp"]?.GetValue<string>() ?? "192.168.1.2");
        var sport  = (ushort)(p["srcPort"]?.GetValue<int>() ?? (p["udp"] as JsonObject)?["srcPort"]?.GetValue<int>() ?? 12345);
        var dport  = (ushort)(p["dstPort"]?.GetValue<int>() ?? (p["udp"] as JsonObject)?["dstPort"]?.GetValue<int>() ?? 50000);
        var ipLen  = (ushort)(20 + 8 + payload.Length);
        var udpLen = (ushort)(8 + payload.Length);

        var buf = new byte[14 + ipLen];
        // Ethernet header
        Array.Copy(dst, 0, buf, 0, 6);
        Array.Copy(src, 0, buf, 6, 6);
        buf[12] = 0x08; buf[13] = 0x00; // EtherType IPv4

        // IP header
        buf[14] = 0x45; buf[15] = 0x00;
        W16(buf, 16, ipLen);
        W16(buf, 18, 0x1234); // ID
        buf[20] = 0x40; buf[21] = 0x00; // DF, frag=0
        buf[22] = 64;   buf[23] = 17;   // TTL, proto=UDP
        Array.Copy(srcIp, 0, buf, 26, 4);
        Array.Copy(dstIp, 0, buf, 30, 4);
        W16(buf, 24, IpChecksum(buf, 14, 20));

        // UDP header
        W16(buf, 34, sport); W16(buf, 36, dport); W16(buf, 38, udpLen);
        // UDP checksum (optional, set 0 for simplicity)
        W16(buf, 40, 0);

        Array.Copy(payload, 0, buf, 42, payload.Length);
        // Update UDP checksum
        W16(buf, 40, UdpChecksum(buf, 26, 30, 34, 8 + payload.Length));
        return buf;
    }

    // ICMP Echo Request
    private byte[] BuildIcmp(byte[] src, byte[] dst, JsonObject p, byte[] payload)
    {
        var srcIp = ParseIp(p["srcIp"]?.GetValue<string>() ?? "192.168.1.1");
        var dstIp = ParseIp(p["dstIp"]?.GetValue<string>() ?? "192.168.1.2");
        var icmpLen = 8 + payload.Length;
        var ipLen   = (ushort)(20 + icmpLen);

        var buf = new byte[14 + ipLen];
        Array.Copy(dst, 0, buf, 0, 6); Array.Copy(src, 0, buf, 6, 6);
        buf[12] = 0x08; buf[13] = 0x00;
        buf[14] = 0x45; W16(buf, 16, ipLen);
        W16(buf, 18, 0x1235); buf[20] = 0x40; buf[22] = 64; buf[23] = 1; // proto=ICMP
        Array.Copy(srcIp, 0, buf, 26, 4); Array.Copy(dstIp, 0, buf, 30, 4);
        W16(buf, 24, IpChecksum(buf, 14, 20));
        buf[34] = 8; buf[35] = 0; // type=EchoRequest, code=0
        W16(buf, 36, 1); W16(buf, 38, 1); // id, seq
        Array.Copy(payload, 0, buf, 42, payload.Length);
        W16(buf, 36, Checksum(buf, 34, icmpLen));
        return buf;
    }

    // ARP Request
    private byte[] BuildArp(byte[] src, byte[] dst, JsonObject p)
    {
        var srcIp = ParseIp(p["srcIp"]?.GetValue<string>() ?? "192.168.1.1");
        var dstIp = ParseIp(p["dstIp"]?.GetValue<string>() ?? "192.168.1.2");
        var buf = new byte[42];
        Array.Copy(dst, 0, buf, 0, 6); Array.Copy(src, 0, buf, 6, 6);
        buf[12] = 0x08; buf[13] = 0x06; // ARP
        buf[14] = 0; buf[15] = 1;       // HW Ethernet
        buf[16] = 0x08; buf[17] = 0x00; // Proto IPv4
        buf[18] = 6; buf[19] = 4;       // HW/Proto addr lengths
        buf[20] = 0; buf[21] = 1;       // Operation: Request
        Array.Copy(src, 0, buf, 22, 6);
        Array.Copy(srcIp, 0, buf, 28, 4);
        // Target HW = 0, Target IP = dstIp
        Array.Copy(dstIp, 0, buf, 38, 4);
        return buf;
    }

    private byte[] BuildRaw(byte[] src, byte[] dst, byte[] payload)
    {
        var buf = new byte[14 + payload.Length];
        Array.Copy(dst, 0, buf, 0, 6); Array.Copy(src, 0, buf, 6, 6);
        buf[12] = 0x60; buf[13] = 0x00; // custom EtherType
        Array.Copy(payload, 0, buf, 14, payload.Length);
        return buf;
    }

    private byte[] InsertVlan(byte[] frame, int vid, int prio)
    {
        var buf = new byte[frame.Length + 4];
        Array.Copy(frame, 0, buf, 0, 12);       // dst+src mac
        buf[12] = 0x81; buf[13] = 0x00;         // 802.1Q
        var tci = (ushort)(((prio & 7) << 13) | (vid & 0xFFF));
        buf[14] = (byte)(tci >> 8); buf[15] = (byte)(tci & 0xFF);
        Array.Copy(frame, 12, buf, 16, frame.Length - 12);
        return buf;
    }

    private byte[] BuildPayload(JsonObject? p, string protocol, int frameNo)
    {
        if (p == null) return Encoding.UTF8.GetBytes("KETI");
        var mode = p["mode"]?.GetValue<string>() ?? "text";
        var data = p["data"]?.GetValue<string>() ?? "";
        var size = p["size"]?.GetValue<int>() ?? 64;

        return mode switch
        {
            "text"     => Encoding.UTF8.GetBytes(data.Length > 0 ? data : "KETI Packet Lab"),
            "hex"      => ParseHex(data),
            "sequence" => BuildSeqPayload(size, frameNo),
            "random"   => BuildRandomPayload(size),
            "repeat"   => BuildRepeatPayload(size, data.Length > 0 ? Convert.ToByte(data[..2], 16) : (byte)0xAB),
            _          => Encoding.UTF8.GetBytes("KETI")
        };
    }

    private static byte[] BuildSeqPayload(int size, int no)
    {
        var b = new byte[Math.Max(8, size)];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(i & 0xFF);
        var ts = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()); Array.Copy(ts, 0, b, 0, Math.Min(8, b.Length));
        return b;
    }

    private static byte[] BuildRandomPayload(int size) { var b = new byte[Math.Max(4, size)]; Random.Shared.NextBytes(b); return b; }
    private static byte[] BuildRepeatPayload(int size, byte val) { var b = new byte[Math.Max(4, size)]; Array.Fill(b, val); return b; }

    // ── Checksums ─────────────────────────────────────────────────────────────

    private static ushort IpChecksum(byte[] buf, int offset, int len)
    {
        uint s = 0;
        for (var i = 0; i < len; i += 2) s += (uint)((buf[offset + i] << 8) | buf[offset + i + 1]);
        while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
        return (ushort)~s;
    }

    private static ushort Checksum(byte[] buf, int offset, int len)
    {
        uint s = 0;
        for (var i = 0; i < len - 1; i += 2) s += (uint)((buf[offset + i] << 8) | buf[offset + i + 1]);
        if (len % 2 != 0) s += (uint)(buf[offset + len - 1] << 8);
        while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
        return (ushort)~s;
    }

    private static ushort UdpChecksum(byte[] buf, int srcIpOff, int dstIpOff, int udpOff, int udpLen)
    {
        // Pseudo-header + UDP
        uint s = 0;
        s += (uint)((buf[srcIpOff] << 8) | buf[srcIpOff + 1]);
        s += (uint)((buf[srcIpOff + 2] << 8) | buf[srcIpOff + 3]);
        s += (uint)((buf[dstIpOff] << 8) | buf[dstIpOff + 1]);
        s += (uint)((buf[dstIpOff + 2] << 8) | buf[dstIpOff + 3]);
        s += 17; s += (uint)udpLen;
        for (var i = 0; i < udpLen - 1; i += 2) s += (uint)((buf[udpOff + i] << 8) | buf[udpOff + i + 1]);
        if (udpLen % 2 != 0) s += (uint)(buf[udpOff + udpLen - 1] << 8);
        while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
        var r = (ushort)~s;
        return r == 0 ? (ushort)0xFFFF : r;
    }

    private static void W16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)(v & 0xFF); }

    // ── Decode ────────────────────────────────────────────────────────────────

    private static Dictionary<string, object?> DecodeFrame(byte[] bytes)
    {
        var r = new Dictionary<string, object?>();
        r["length"] = bytes.Length;
        if (bytes.Length < 14) { r["raw"] = ToHex(bytes, bytes.Length); return r; }

        var dstMac = FormatMac(bytes[..6]);
        var srcMac = FormatMac(bytes[6..12]);
        var etype  = (ushort)((bytes[12] << 8) | bytes[13]);
        int l3Off  = 14;

        if (etype == 0x8100 && bytes.Length >= 18)
        {
            var tci = (ushort)((bytes[14] << 8) | bytes[15]);
            etype   = (ushort)((bytes[16] << 8) | bytes[17]);
            l3Off   = 18;
            r["vlan"] = new Dictionary<string, object?> { ["id"] = tci & 0xFFF, ["priority"] = tci >> 13 };
        }

        r["ethernet"] = new Dictionary<string, object?> {
            ["srcMac"]    = srcMac,
            ["dstMac"]    = dstMac,
            ["etherType"] = $"0x{etype:X4}"
        };

        switch (etype)
        {
            case 0x0800: DecodeIpv4(bytes, l3Off, r); break;
            case 0x0806: DecodeArp(bytes, l3Off, r);  break;
            case 0x86DD: DecodeIpv6(bytes, l3Off, r); break;
        }
        return r;
    }

    private static void DecodeIpv4(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 20) return;
        var ihl   = (b[off] & 0x0F) * 4;
        var proto = b[off + 9];
        r["ipv4"] = new Dictionary<string, object?> {
            ["src"]      = $"{b[off+12]}.{b[off+13]}.{b[off+14]}.{b[off+15]}",
            ["dst"]      = $"{b[off+16]}.{b[off+17]}.{b[off+18]}.{b[off+19]}",
            ["protocol"] = (int)proto,
            ["ttl"]      = (int)b[off+8],
            ["length"]   = (b[off+2] << 8) | b[off+3]
        };
        var l4 = off + ihl;
        switch (proto)
        {
            case 6:  DecodeTcp(b, l4, r);  break;
            case 17: DecodeUdp(b, l4, r);  break;
            case 1:  DecodeIcmp(b, l4, r); break;
        }
    }

    private static void DecodeUdp(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 8) return;
        r["udp"] = new Dictionary<string, object?> {
            ["srcPort"] = (b[off]   << 8) | b[off+1],
            ["dstPort"] = (b[off+2] << 8) | b[off+3],
            ["length"]  = (b[off+4] << 8) | b[off+5]
        };
    }

    private static void DecodeTcp(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 20) return;
        var fl = b[off + 13];
        var flags = new List<string>();
        if ((fl & 0x01) != 0) flags.Add("FIN");
        if ((fl & 0x02) != 0) flags.Add("SYN");
        if ((fl & 0x04) != 0) flags.Add("RST");
        if ((fl & 0x08) != 0) flags.Add("PSH");
        if ((fl & 0x10) != 0) flags.Add("ACK");
        if ((fl & 0x20) != 0) flags.Add("URG");
        r["tcp"] = new Dictionary<string, object?> {
            ["srcPort"] = (b[off]   << 8) | b[off+1],
            ["dstPort"] = (b[off+2] << 8) | b[off+3],
            ["seq"]     = (long)((uint)((b[off+4] << 24) | (b[off+5] << 16) | (b[off+6] << 8) | b[off+7])),
            ["ack"]     = (long)((uint)((b[off+8] << 24) | (b[off+9] << 16) | (b[off+10] << 8) | b[off+11])),
            ["flags"]   = flags,
            ["window"]  = (b[off+14] << 8) | b[off+15]
        };
    }

    private static void DecodeIcmp(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 4) return;
        r["icmp"] = new Dictionary<string, object?> {
            ["type"] = (int)b[off],
            ["code"] = (int)b[off+1],
            ["seq"]  = b.Length >= off + 8 ? (b[off+6] << 8) | b[off+7] : 0
        };
    }

    private static void DecodeArp(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 28) return;
        r["arp"] = new Dictionary<string, object?> {
            ["operation"] = (b[off+6] << 8) | b[off+7],
            ["senderMac"] = FormatMac(b[(off+8)..(off+14)]),
            ["senderIp"]  = $"{b[off+14]}.{b[off+15]}.{b[off+16]}.{b[off+17]}",
            ["targetMac"] = FormatMac(b[(off+18)..(off+24)]),
            ["targetIp"]  = $"{b[off+24]}.{b[off+25]}.{b[off+26]}.{b[off+27]}"
        };
    }

    private static void DecodeIpv6(byte[] b, int off, Dictionary<string, object?> r)
    {
        if (b.Length < off + 40) return;
        r["ipv6"] = new Dictionary<string, object?> {
            ["src"]        = FormatIpv6(b, off + 8),
            ["dst"]        = FormatIpv6(b, off + 24),
            ["nextHeader"] = (int)b[off + 6]
        };
    }

    private static string FormatIpv6(byte[] b, int off)
    {
        var p = new string[8];
        for (var i = 0; i < 8; i++) p[i] = $"{b[off + i*2]:x2}{b[off + i*2 + 1]:x2}";
        return string.Join(":", p);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── Capture ──────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private JsonNode? StartCapture(JsonObject payload)
    {
        StopCapture();
        ClearCapture();
        var names = payload["interfaces"]?.AsArray()
            .Select(n => n?.GetValue<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList()
            ?? new List<string>();

        var nics = NetworkInterface.GetAllNetworkInterfaces();
        var allDevs = CaptureDeviceList.Instance.OfType<ILiveDevice>().ToList();
        var devs = names.Count == 0 ? allDevs :
            allDevs.Where(d => names.Any(n => DeviceMatchesName(d, n, nics))).ToList();
        if (devs.Count == 0) throw new InvalidOperationException("No capture interfaces matched.");

        foreach (var dev in devs) { dev.OnPacketArrival += OnPacket; dev.Open(DeviceModes.Promiscuous, 1000); dev.StartCapture(); _captureDevices.Add(dev); }
        return SerializeNode(new { started = devs.Count, interfaces = devs.Select(DeviceKey).ToList() });
    }

    private JsonNode? StopCapture()
    {
        foreach (var dev in _captureDevices.ToList())
        { try { dev.StopCapture(); } catch { } try { dev.OnPacketArrival -= OnPacket; } catch { } try { dev.Close(); } catch { } }
        var n = _captureDevices.Count; _captureDevices.Clear();
        return SerializeNode(new { stopped = n });
    }

    private JsonNode? ClearCapture()
    {
        while (_captures.TryDequeue(out _)) { } _captureNo = 0;
        return SerializeNode(new { cleared = true });
    }

    private JsonNode? GetCaptures(JsonObject p)
    {
        var limit  = p["limit"]?.GetValue<int>() ?? 500;
        var rows   = _captures.Reverse().Take(limit).Reverse().ToList();
        return SerializeNode(new { rows });
    }

    private JsonNode? SendHex(JsonObject p)
    {
        var iface = p["interface"]?.GetValue<string>() ?? "";
        var bytes = ParseHex(p["hex"]?.GetValue<string>() ?? "");
        var dev   = FindDevice(iface) ?? throw new InvalidOperationException($"Interface not found: {iface}");
        dev.Open(DeviceModes.None); dev.SendPacket(bytes); dev.Close();
        return SerializeNode(new { sent = bytes.Length });
    }

    private void OnPacket(object sender, PacketCapture e)
    {
        var raw   = e.GetPacket();
        var iface = sender is ILiveDevice d ? DeviceKey(d) : "unknown";
        var rec   = DecodeCapture(raw, iface);
        _captures.Enqueue(rec);
        while (_captures.Count > 10_000 && _captures.TryDequeue(out _)) { }
        _ = SendAsync(new { type = "event", payload = new { kind = "capture", record = rec } });
    }

    private CaptureRecord DecodeCapture(RawCapture raw, string iface)
    {
        var no       = Interlocked.Increment(ref _captureNo);
        var frameHex = BitConverter.ToString(raw.Data).Replace("-", "").ToLowerInvariant();
        var decoded  = DecodeFrame(raw.Data);
        var ts       = raw.Timeval.Date != DateTime.MinValue ? raw.Timeval.Date.ToString("o") : DateTime.UtcNow.ToString("o");
        return new CaptureRecord(no, ts, iface, raw.Data.Length, frameHex, decoded);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── Serial ───────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private JsonNode? SerialList()
    {
        var ttys = SerialPort.GetPortNames().OrderBy(p => p).Select(p => new { path = p, name = p, usbProduct = (string?)null, manufacturer = (string?)null, usbId = (string?)null, serial = (string?)null, driver = (string?)null }).ToList();
        return SerializeNode(new { ttys });
    }

    private JsonNode? SerialStatus()
    {
        var isOpen = _serial?.IsOpen ?? false;
        string output;
        lock (_serialLock) { output = _serialBuf.ToString(); }
        return SerializeNode(new { isOpen, portName = _serial?.PortName ?? "", terminalOutput = output, isConnected = isOpen, connectionStatus = isOpen ? "connected" : "disconnected" });
    }

    private JsonNode? SerialOpen(JsonObject p)
    {
        SerialClose();
        var portName = p["path"]?.GetValue<string>() ?? p["port"]?.GetValue<string>() ?? p["portName"]?.GetValue<string>() ?? "";
        var baud     = p["baudRate"]?.GetValue<int>() ?? 115200;
        var data     = p["dataBits"]?.GetValue<int>() ?? 8;
        var stopStr  = p["stopBits"]?.GetValue<string>() ?? "1";
        var parStr   = p["parity"]?.GetValue<string>() ?? "none";

        var stop = stopStr switch { "1.5" => StopBits.OnePointFive, "2" => StopBits.Two, _ => StopBits.One };
        var par  = parStr.ToLower() switch { "odd" => Parity.Odd, "even" => Parity.Even, "mark" => Parity.Mark, "space" => Parity.Space, _ => Parity.None };

        _serial = new SerialPort(portName, baud, par, data, stop) { ReadTimeout = SerialPort.InfiniteTimeout, WriteTimeout = 1000, Encoding = Encoding.UTF8, NewLine = "\n" };
        lock (_serialLock) { _serialBuf.Clear(); }
        _serial.DataReceived += OnSerialData;
        _serial.Open();
        return SerializeNode(new { ok = true, sessionId = portName, session = portName, portName, baudRate = baud });
    }

    private JsonNode? SerialClose()
    {
        if (_serial == null) return SerializeNode(new { closed = false });
        _serial.DataReceived -= OnSerialData;
        try { if (_serial.IsOpen) _serial.Close(); } catch { }
        _serial.Dispose(); _serial = null;
        return SerializeNode(new { closed = true });
    }

    private JsonNode? SerialWrite(JsonObject p)
    {
        if (_serial == null || !_serial.IsOpen) throw new InvalidOperationException("Serial port not open");
        var text    = p["text"]?.GetValue<string>();
        var hexData = p["hex"]?.GetValue<string>() ?? p["hexData"]?.GetValue<string>() ?? p["data"]?.GetValue<string>();

        if (hexData != null) { var bytes = ParseHex(hexData); _serial.Write(bytes, 0, bytes.Length); return SerializeNode(new { written = bytes.Length }); }
        if (text != null) { _serial.Write(text); return SerializeNode(new { written = text.Length }); }
        throw new InvalidOperationException("Provide 'hex', 'hexData', or 'text'");
    }

    private JsonNode? SerialRead()
    {
        string output; lock (_serialLock) { output = _serialBuf.ToString(); }
        return SerializeNode(new { output });
    }

    private JsonNode? SerialClear()
    {
        lock (_serialLock) { _serialBuf.Clear(); }
        return SerializeNode(new { cleared = true });
    }

    private JsonNode? SerialControl(JsonObject p)
    {
        if (_serial == null || !_serial.IsOpen) throw new InvalidOperationException("Serial port not open");
        if (p.ContainsKey("rts")) _serial.RtsEnable = p["rts"]!.GetValue<bool>();
        if (p.ContainsKey("dtr")) _serial.DtrEnable = p["dtr"]!.GetValue<bool>();
        if (p.ContainsKey("break")) { if (p["break"]!.GetValue<bool>()) _serial.BreakState = true; else _serial.BreakState = false; }
        return SerializeNode(new { ok = true });
    }

    private void OnSerialData(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serial == null) return;
        string data;
        try { data = _serial.ReadExisting(); } catch { return; }
        lock (_serialLock) { _serialBuf.Append(data); if (_serialBuf.Length > 65536) _serialBuf.Remove(0, _serialBuf.Length - 65536); }
        var hex = BitConverter.ToString(Encoding.UTF8.GetBytes(data)).Replace("-", "").ToLowerInvariant();
        _ = SendAsync(new { type = "event", payload = new { kind = "serial", rxType = "rx", hex, session = _serial?.PortName ?? "" } });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── Interface helpers ────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private List<object> GetInterfaces()
    {
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        return CaptureDeviceList.Instance.OfType<ILiveDevice>().Select(dev =>
        {
            var mac = dev.MacAddress?.GetAddressBytes();
            var nic = mac?.Length == 6 ? nics.FirstOrDefault(n => n.GetPhysicalAddress().GetAddressBytes().SequenceEqual(mac)) : null;
            return (object)new
            {
                key = DeviceKey(dev), name = nic?.Name ?? DeviceKey(dev), description = dev.Description ?? "",
                deviceName = dev.Name, mac = FormatMac(dev.MacAddress?.GetAddressBytes()),
                state = (nic?.OperationalStatus ?? OperationalStatus.Unknown).ToString().ToLower(),
                ipv4 = nic?.GetIPProperties().UnicastAddresses
                          .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                          .Select(a => new { local = a.Address.ToString(), prefixLength = a.PrefixLength })
                          .ToList()
            };
        }).ToList();
    }

    private ILiveDevice? FindDevice(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CaptureDeviceList.Instance.OfType<ILiveDevice>().FirstOrDefault();
        var nics = NetworkInterface.GetAllNetworkInterfaces();
        return CaptureDeviceList.Instance.OfType<ILiveDevice>().FirstOrDefault(d => DeviceMatchesName(d, value, nics));
    }

    private static bool DeviceMatchesName(ILiveDevice d, string value, NetworkInterface[] nics)
    {
        if (DeviceKey(d).Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        if (d.Name.Equals(value, StringComparison.OrdinalIgnoreCase)) return true;
        if ((d.Description ?? "").Contains(value, StringComparison.OrdinalIgnoreCase)) return true;
        var mac = d.MacAddress?.GetAddressBytes();
        if (mac?.Length == 6) {
            var nic = nics.FirstOrDefault(n => n.GetPhysicalAddress().GetAddressBytes().SequenceEqual(mac));
            if (nic?.Name?.Equals(value, StringComparison.OrdinalIgnoreCase) == true) return true;
        }
        return false;
    }

    private static string DeviceKey(ILiveDevice dev)
    {
        var desc = dev.Description ?? dev.Name ?? "";
        var idx  = desc.LastIndexOf('{');
        if (idx > 0) desc = desc[..idx].TrimEnd(' ', '\\', '_');
        return string.IsNullOrWhiteSpace(desc) ? (dev.Name ?? "unknown") : desc;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── WS send ──────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private async Task SendAsync(object obj)
    {
        var ws = _ws; if (ws == null || ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, _json));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ── Utilities ────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────

    private JsonNode? SerializeNode<T>(T obj) => JsonSerializer.SerializeToNode(obj, _json);

    private static byte[] ParseMac(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "");
        if (hex.Length != 12) throw new ArgumentException($"Invalid MAC: {mac}");
        return Enumerable.Range(0, 6).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
    }

    private static string NormalizeMac(string value)
    {
        var text = value.Trim().Replace("-", ":").ToUpperInvariant();
        if (text.Contains(':')) return text;
        return text.Length == 12 ? string.Join(":", Enumerable.Range(0, 6).Select(i => text.Substring(i * 2, 2))) : text;
    }

    private static string FormatMac(byte[]? b)
        => b?.Length == 6 ? string.Join(":", b.Select(x => x.ToString("X2"))) : "";

    private static string FormatMac(PhysicalAddress? a) => FormatMac(a?.GetAddressBytes());

    private static byte[] ParseIp(string ip)
        => ip.Split('.').Select(byte.Parse).ToArray();

    private static byte[] ParseHex(string hex)
    {
        var clean = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (clean.Length % 2 != 0) throw new InvalidOperationException("Hex length must be even.");
        return Enumerable.Range(0, clean.Length / 2).Select(i => Convert.ToByte(clean.Substring(i * 2, 2), 16)).ToArray();
    }

    private static string ToHex(byte[] data, int max)
        => string.Join(" ", data.Take(Math.Min(max, data.Length)).Select(b => b.ToString("X2")));
}

public sealed record CaptureRecord(int No, string Timestamp, string Interface, int Length, string FrameHex, object Decoded);
