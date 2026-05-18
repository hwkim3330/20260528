using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace EthernetPacketGenerator.Services;

/// <summary>
/// packet_agent.py 와 동일한 JSON 프로파일 형식으로 이더넷 프레임을 빌드한다.
/// </summary>
public static class LabPacketService
{
    public static (byte[] Frame, JsonObject Decoded) BuildFrame(JsonObject profile, int? sequence = null)
    {
        var protocol = (profile["protocol"]?.GetValue<string>() ?? "udp").ToLowerInvariant();

        byte[] frame = protocol switch
        {
            "udp"  => EthHdr(profile, 0x0800).Add(IPv4(profile, 17, Udp(profile, sequence))),
            "icmp" => EthHdr(profile, 0x0800).Add(IPv4(profile, 1,  Icmp(profile, sequence))),
            "arp"  => EthHdr(profile, 0x0806).Add(Arp(profile)),
            "raw"  => EthHdr(profile, ParseHex(profile["etherType"]?.GetValue<string>() ?? "0x88b5"))
                          .Add(PayloadBytes(profile, sequence)),
            _      => throw new ArgumentException($"unsupported protocol: {protocol}")
        };

        if (frame.Length < 60) frame = frame.Pad(60 - frame.Length);
        var target = profile["targetFrameLength"]?.GetValue<int>();
        if (target > frame.Length) frame = frame.Pad(target.Value - frame.Length);

        return (frame, DecodeFrame(frame));
    }

    // ── Layer builders ────────────────────────────────────────────────────────

    private static byte[] EthHdr(JsonObject p, ushort etherType)
    {
        var dst = MacBytes(p["dstMac"]!.GetValue<string>());
        var src = MacBytes(p["srcMac"]!.GetValue<string>());
        var vlan = p["vlan"] as JsonObject;
        if (vlan != null && (vlan["enabled"]?.GetValue<bool>() ?? false))
        {
            var pri = vlan["priority"]?.GetValue<int>() ?? 0;
            var dei = (vlan["dei"]?.GetValue<bool>() ?? false) ? 1 : 0;
            var vid = vlan["id"]?.GetValue<int>() ?? 1;
            var tci = (ushort)((pri << 13) | (dei << 12) | (vid & 0xFFF));
            return dst.Add(src).Add(U16(0x8100)).Add(U16(tci)).Add(U16(etherType));
        }
        return dst.Add(src).Add(U16(etherType));
    }

    private static byte[] IPv4(JsonObject p, byte proto, byte[] payload)
    {
        var ip  = p["ipv4"] as JsonObject ?? new JsonObject();
        var src = IpBytes(ip["src"]!.GetValue<string>());
        var dst = IpBytes(ip["dst"]!.GetValue<string>());
        var ttl = (byte)(ip["ttl"]?.GetValue<int>() ?? 64);
        var tos = (byte)(ip["tos"]?.GetValue<int>() ?? 0);
        var id  = (ushort)(ip["id"]?.GetValue<int>() ?? Random.Shared.Next(0xFFFF));
        var ff  = (ushort)(ip["flagsFragment"]?.GetValue<int>() ?? 0x4000);
        var tot = (ushort)(20 + payload.Length);

        var h = new byte[20];
        h[0] = 0x45; h[1] = tos;
        U16(tot).CopyTo(h, 2); U16(id).CopyTo(h, 4); U16(ff).CopyTo(h, 6);
        h[8] = ttl; h[9] = proto;
        src.CopyTo(h, 12); dst.CopyTo(h, 16);
        var cs = Checksum(h); h[10] = (byte)(cs >> 8); h[11] = (byte)cs;
        return h.Add(payload);
    }

    private static byte[] Udp(JsonObject p, int? seq)
    {
        var u    = p["udp"] as JsonObject ?? new JsonObject();
        var data = PayloadBytes(p, seq);
        var sp   = (ushort)(u["srcPort"]?.GetValue<int>() ?? 40000);
        var dp   = (ushort)(u["dstPort"]?.GetValue<int>() ?? 50000);
        var len  = (ushort)(8 + data.Length);
        var hdr  = U16(sp).Add(U16(dp)).Add(U16(len)).Add(new byte[2]);
        var ip   = p["ipv4"] as JsonObject ?? new JsonObject();
        var pseudo = IpBytes(ip["src"]!.GetValue<string>())
            .Add(IpBytes(ip["dst"]!.GetValue<string>()))
            .Add(new byte[] { 0, 17 }).Add(U16(len));
        var cs = Checksum(pseudo.Add(hdr).Add(data));
        if (cs == 0) cs = 0xFFFF;
        hdr[6] = (byte)(cs >> 8); hdr[7] = (byte)cs;
        return hdr.Add(data);
    }

    private static byte[] Icmp(JsonObject p, int? seq)
    {
        var ic   = p["icmp"] as JsonObject ?? new JsonObject();
        var data = PayloadBytes(p, seq);
        var type = (byte)(ic["type"]?.GetValue<int>() ?? 8);
        var code = (byte)(ic["code"]?.GetValue<int>() ?? 0);
        var id   = (ushort)(ic["id"]?.GetValue<int>() ?? 0x2026);
        var sq   = (ushort)(ic["seq"]?.GetValue<int>() ?? 1);
        var hdr  = new byte[] { type, code, 0, 0 }.Add(U16(id)).Add(U16(sq));
        var cs   = Checksum(hdr.Add(data));
        hdr[2] = (byte)(cs >> 8); hdr[3] = (byte)cs;
        return hdr.Add(data);
    }

    private static byte[] Arp(JsonObject p)
    {
        var a     = p["arp"] as JsonObject ?? new JsonObject();
        var op    = (ushort)(a["operation"]?.GetValue<int>() ?? 1);
        var smac  = MacBytes(a["senderMac"]?.GetValue<string>() ?? p["srcMac"]?.GetValue<string>() ?? "00:00:00:00:00:00");
        var sip   = IpBytes(a["senderIp"]!.GetValue<string>());
        var tmac  = MacBytes(a["targetMac"]?.GetValue<string>() ?? "00:00:00:00:00:00");
        var tip   = IpBytes(a["targetIp"]!.GetValue<string>());
        return U16(1).Add(U16(0x0800)).Add(new byte[] { 6, 4 }).Add(U16(op))
            .Add(smac).Add(sip).Add(tmac).Add(tip);
    }

    // ── Payload ───────────────────────────────────────────────────────────────

    private static byte[] PayloadBytes(JsonObject p, int? sequence)
    {
        var pl = p["payload"];
        if (pl is JsonValue jv) return Encoding.UTF8.GetBytes(jv.GetValue<string>());
        if (pl is not JsonObject po) return Encoding.UTF8.GetBytes("ethernet-packet-lab");

        var mode = po["mode"]?.GetValue<string>() ?? "text";
        var size = po["size"]?.GetValue<int>() ?? 32;
        var seq  = sequence ?? po["start"]?.GetValue<int>() ?? 1;

        return mode switch
        {
            "hex"       => Convert.FromHexString((po["data"]?.GetValue<string>() ?? "").Replace(" ", "")),
            "counter"   => Enumerable.Range(0, size).Select(i => (byte)(i % 256)).ToArray(),
            "random"    => RandomBytes(size),
            "repeat"    => Enumerable.Repeat((byte)(po["byte"]?.GetValue<int>() ?? 0), size).ToArray(),
            "sequence"  => Encoding.UTF8.GetBytes(
                               (po["template"]?.GetValue<string>() ?? "KETI_TEST_SEQ_{seq:000000}")
                               .Replace("{seq:000000}", seq.ToString("D6"))
                               .Replace("{seq}", seq.ToString())),
            "benchmark" => BenchmarkPayload(size, seq),
            _           => Encoding.UTF8.GetBytes(po["data"]?.GetValue<string>() ?? "ethernet-packet-lab")
        };
    }

    private static byte[] BenchmarkPayload(int size, int seq)
    {
        // "KETI" + seq(4B BE) + txTimestampNs(8B BE)
        var hdr = new byte[16];
        hdr[0] = (byte)'K'; hdr[1] = (byte)'E'; hdr[2] = (byte)'T'; hdr[3] = (byte)'I';
        U32((uint)seq).CopyTo(hdr, 4);
        // High-resolution epoch ns: anchor to wall time + stopwatch offset
        U64((ulong)HighResNs()).CopyTo(hdr, 8);
        if (size <= 16) return hdr[..size];
        return hdr.Add(new byte[size - 16]);
    }

    // ── Decoder (Python decode_frame 호환) ────────────────────────────────────

    public static JsonObject DecodeFrame(byte[] f)
    {
        if (f.Length < 14)
            return new JsonObject { ["length"] = f.Length, ["error"] = "truncated" };

        var et = (ushort)((f[12] << 8) | f[13]);
        var d  = new JsonObject
        {
            ["length"]   = f.Length,
            ["ethernet"] = new JsonObject
            {
                ["dstMac"]    = MacStr(f[0..6]),
                ["srcMac"]    = MacStr(f[6..12]),
                ["etherType"] = $"0x{et:x4}"
            }
        };

        int off = 14;
        if (et == 0x8100 && f.Length >= 18)
        {
            var tci = (ushort)((f[14] << 8) | f[15]);
            var inner = (ushort)((f[16] << 8) | f[17]);
            d["vlan"] = new JsonObject
            {
                ["priority"] = (tci >> 13) & 7,
                ["dei"]      = (tci & 0x1000) != 0,
                ["id"]       = tci & 0xFFF,
                ["etherType"]= $"0x{inner:x4}"
            };
            et = inner; off = 18;
        }

        if (et == 0x0800 && f.Length >= off + 20)
        {
            var ihl  = (f[off] & 0xF) * 4;
            var proto = f[off + 9];
            d["ipv4"] = new JsonObject
            {
                ["src"]      = $"{f[off+12]}.{f[off+13]}.{f[off+14]}.{f[off+15]}",
                ["dst"]      = $"{f[off+16]}.{f[off+17]}.{f[off+18]}.{f[off+19]}",
                ["ttl"]      = f[off + 8],
                ["protocol"] = proto,
                ["totalLength"] = (f[off+2] << 8) | f[off+3]
            };
            var l4 = off + ihl;
            if (proto == 17 && f.Length >= l4 + 8)
            {
                var sp = (f[l4] << 8) | f[l4+1];
                var dp = (f[l4+2] << 8) | f[l4+3];
                d["udp"] = new JsonObject
                {
                    ["srcPort"] = sp, ["dstPort"] = dp,
                    ["length"]  = (f[l4+4] << 8) | f[l4+5],
                    ["checksum"]= $"0x{(f[l4+6] << 8 | f[l4+7]):x4}"
                };
                if (f.Length >= l4 + 8 + 16)
                {
                    var pl = f[(l4+8)..];
                    if (pl[0]=='K' && pl[1]=='E' && pl[2]=='T' && pl[3]=='I')
                    {
                        var sq = (pl[4]<<24)|(pl[5]<<16)|(pl[6]<<8)|pl[7];
                        ulong ts = 0;
                        for (int i = 0; i < 8; i++) ts = (ts << 8) | pl[8+i];
                        d["benchmark"] = new JsonObject { ["seq"] = sq, ["txTimestampNs"] = (long)ts };
                    }
                }
            }
            else if (proto == 1 && f.Length >= l4 + 8)
            {
                d["icmp"] = new JsonObject
                {
                    ["type"] = f[l4], ["code"] = f[l4+1],
                    ["checksum"] = $"0x{(f[l4+2]<<8|f[l4+3]):x4}",
                    ["id"]  = (f[l4+4]<<8)|f[l4+5],
                    ["seq"] = (f[l4+6]<<8)|f[l4+7]
                };
            }
        }
        else if (et == 0x0806 && f.Length >= off + 28)
        {
            var a = f[off..];
            d["arp"] = new JsonObject
            {
                ["operation"] = (a[6]<<8)|a[7],
                ["senderMac"] = MacStr(a[8..14]),
                ["senderIp"]  = $"{a[14]}.{a[15]}.{a[16]}.{a[17]}",
                ["targetMac"] = MacStr(a[18..24]),
                ["targetIp"]  = $"{a[24]}.{a[25]}.{a[26]}.{a[27]}"
            };
        }
        return d;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] MacBytes(string mac)
        => mac.Replace("-", ":").Split(':').Select(s => Convert.ToByte(s, 16)).ToArray();

    private static string MacStr(byte[] b)
        => string.Join(":", b.Select(x => x.ToString("x2")));

    private static byte[] IpBytes(string ip)
        => ip.Split('.').Select(byte.Parse).ToArray();

    private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)(v & 0xFF) };

    private static byte[] U32(uint v) => new[]
        { (byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v };

    private static byte[] U64(ulong v) => new[]
        { (byte)(v>>56),(byte)(v>>48),(byte)(v>>40),(byte)(v>>32),
          (byte)(v>>24),(byte)(v>>16),(byte)(v>>8),(byte)v };

    private static byte[] RandomBytes(int n) { var b = new byte[n]; Random.Shared.NextBytes(b); return b; }

    private static ushort Checksum(byte[] data)
    {
        uint s = 0;
        int i = 0;
        while (i + 1 < data.Length) { s += (uint)((data[i] << 8) + data[i+1]); i += 2; }
        if (i < data.Length) s += (uint)(data[i] << 8);
        while (s >> 16 != 0) s = (s & 0xFFFF) + (s >> 16);
        return (ushort)~s;
    }

    private static ushort ParseHex(string s)
        => Convert.ToUInt16(s.Replace("0x","").Replace("0X",""), 16);

    // 고해상도 nanosecond 타임스탬프 (Python time.time_ns() 호환)
    private static readonly long   _epochNs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
    private static readonly long   _startTick = Stopwatch.GetTimestamp();
    private static readonly double _tickToNs  = 1_000_000_000.0 / Stopwatch.Frequency;

    public static long HighResNs()
        => _epochNs + (long)((Stopwatch.GetTimestamp() - _startTick) * _tickToNs);
}

// byte[] 연결 확장
internal static class ByteArrayExtensions
{
    public static byte[] Add(this byte[] a, byte[] b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result, 0);
        b.CopyTo(result, a.Length);
        return result;
    }

    public static byte[] Pad(this byte[] a, int count)
    {
        var result = new byte[a.Length + count];
        a.CopyTo(result, 0);
        return result;
    }
}
