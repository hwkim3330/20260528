using System.IO;
using SharpPcap;
using SharpPcap.LibPcap;

namespace EthernetPacketGenerator.Services;

public class CapturedPacketInfo
{
    public DateTime Timestamp { get; init; }
    public int Length { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string Protocol { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Info { get; init; } = string.Empty;
    public string Summary => Info;
}

public class PacketCaptureService : IDisposable
{
    private ILiveDevice? _device;
    private bool _capturing;

    public event EventHandler<CapturedPacketInfo>? PacketCaptured;

    public bool IsCapturing => _capturing;

    public void StartCapture(ILiveDevice device, string filter = "")
    {
        if (_capturing) StopCapture();

        _device = device;
        _device.Open(DeviceModes.None, 1000);

        if (!string.IsNullOrWhiteSpace(filter))
        {
            try { _device.Filter = filter; } catch { }
        }

        _device.OnPacketArrival += OnPacketArrival;
        _device.StartCapture();
        _capturing = true;
    }

    public void StopCapture()
    {
        if (_device == null || !_capturing) return;
        try
        {
            _device.StopCapture();
            _device.OnPacketArrival -= OnPacketArrival;
            _device.Close();
        }
        catch { }
        _capturing = false;
        _device = null;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            var raw = e.GetPacket();
            var data = raw.Data;
            var parsed = ParsePacket(data);
            var info = new CapturedPacketInfo
            {
                Timestamp = raw.Timeval.Date,
                Length = data.Length,
                Data = data.ToArray(),
                Protocol    = parsed.Protocol,
                Source      = parsed.Source,
                Destination = parsed.Destination,
                Info        = parsed.Info
            };
            PacketCaptured?.Invoke(this, info);
        }
        catch { }
    }

    private record ParsedPacket(string Protocol, string Source, string Destination, string Info);

    private static ParsedPacket ParsePacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14)
            return new("RAW", "-", "-", $"[{data.Length}B] Too short");

        ushort etherType = (ushort)((data[12] << 8) | data[13]);
        string srcMac = $"{data[6]:X2}:{data[7]:X2}:{data[8]:X2}:{data[9]:X2}:{data[10]:X2}:{data[11]:X2}";
        string dstMac = $"{data[0]:X2}:{data[1]:X2}:{data[2]:X2}:{data[3]:X2}:{data[4]:X2}:{data[5]:X2}";

        if (etherType == 0x0800 && data.Length >= 34)
        {
            string srcIp = $"{data[26]}.{data[27]}.{data[28]}.{data[29]}";
            string dstIp = $"{data[30]}.{data[31]}.{data[32]}.{data[33]}";
            byte proto = data[23];
            int ihl = (data[14] & 0x0F) * 4;
            int l4off = 14 + ihl;

            if (proto == 6 && data.Length >= l4off + 20)
            {
                ushort sp = (ushort)((data[l4off] << 8) | data[l4off + 1]);
                ushort dp = (ushort)((data[l4off + 2] << 8) | data[l4off + 3]);
                uint seq = (uint)((data[l4off+4]<<24)|(data[l4off+5]<<16)|(data[l4off+6]<<8)|data[l4off+7]);
                byte flags = data[l4off + 13];
                string flagStr = BuildTcpFlags(flags);
                return new("TCP", $"{srcIp}:{sp}", $"{dstIp}:{dp}",
                    $"{sp} → {dp} [{flagStr}] Seq={seq} Len={data.Length - l4off - ((data[l4off+12]>>4)*4)}");
            }
            if (proto == 17 && data.Length >= l4off + 8)
            {
                ushort sp = (ushort)((data[l4off] << 8) | data[l4off + 1]);
                ushort dp = (ushort)((data[l4off + 2] << 8) | data[l4off + 3]);
                ushort udpLen = (ushort)((data[l4off+4] << 8) | data[l4off+5]);
                return new("UDP", $"{srcIp}:{sp}", $"{dstIp}:{dp}",
                    $"Source port: {sp}  Destination port: {dp}  Length={udpLen}");
            }
            if (proto == 1 && data.Length >= l4off + 4)
            {
                byte type = data[l4off];
                byte code = data[l4off + 1];
                string icmpDesc = (type, code) switch
                {
                    (8, 0) => "Echo (ping) request",
                    (0, 0) => "Echo (ping) reply",
                    (3, _) => $"Destination unreachable (code {code})",
                    (11,_) => $"Time-to-live exceeded (code {code})",
                    _      => $"Type={type} Code={code}"
                };
                return new("ICMP", srcIp, dstIp, icmpDesc);
            }
            return new($"IPv4/{proto}", srcIp, dstIp, $"Protocol {proto}  {srcIp} → {dstIp}");
        }

        if (etherType == 0x0806 && data.Length >= 42)
        {
            ushort op = (ushort)((data[20] << 8) | data[21]);
            string senderIp = $"{data[28]}.{data[29]}.{data[30]}.{data[31]}";
            string targetIp = $"{data[38]}.{data[39]}.{data[40]}.{data[41]}";
            string info = op == 1
                ? $"Who has {targetIp}? Tell {senderIp}"
                : $"{senderIp} is at {srcMac}";
            return new("ARP", senderIp, targetIp, info);
        }

        if (etherType == 0x86DD)
            return new("IPv6", srcMac, dstMac, $"IPv6  {srcMac} → {dstMac}");

        if (etherType == 0x8100 && data.Length >= 18)
        {
            ushort vlan = (ushort)((data[14] << 8) | data[15] & 0x0FFF);
            return new("802.1Q", srcMac, dstMac, $"VLAN ID={vlan & 0x0FFF}");
        }

        return new($"0x{etherType:X4}", srcMac, dstMac, $"EtherType 0x{etherType:X4}  [{data.Length}B]");
    }

    private static string BuildTcpFlags(byte flags)
    {
        var parts = new List<string>(6);
        if ((flags & 0x02) != 0) parts.Add("SYN");
        if ((flags & 0x10) != 0) parts.Add("ACK");
        if ((flags & 0x01) != 0) parts.Add("FIN");
        if ((flags & 0x04) != 0) parts.Add("RST");
        if ((flags & 0x08) != 0) parts.Add("PSH");
        if ((flags & 0x20) != 0) parts.Add("URG");
        return parts.Count > 0 ? string.Join(", ", parts) : "None";
    }

    // pcapng 저장
    public static void SavePcapng(IEnumerable<CapturedPacketInfo> packets, string filePath)
    {
        // pcapng 형식: Section Header Block + Interface Description Block + Enhanced Packet Blocks
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // Section Header Block
        WriteSectionHeaderBlock(bw);
        // Interface Description Block (Ethernet, snaplen=65535)
        WriteInterfaceDescBlock(bw);
        // Enhanced Packet Blocks
        foreach (var pkt in packets)
            WriteEnhancedPacketBlock(bw, pkt);
    }

    private static void WriteSectionHeaderBlock(BinaryWriter bw)
    {
        // Block Type
        bw.Write(0x0A0D0D0A);
        uint blockLen = 28;
        bw.Write(blockLen);
        bw.Write(0x1A2B3C4D); // Byte-Order Magic
        bw.Write((ushort)1);  // Major
        bw.Write((ushort)0);  // Minor
        bw.Write((ulong)0xFFFFFFFFFFFFFFFF); // Section Length unknown
        bw.Write(blockLen);
    }

    private static void WriteInterfaceDescBlock(BinaryWriter bw)
    {
        uint blockLen = 20;
        bw.Write(0x00000001); // Block Type
        bw.Write(blockLen);
        bw.Write((ushort)1);  // LinkType = Ethernet
        bw.Write((ushort)0);  // Reserved
        bw.Write(65535u);     // SnapLen
        bw.Write(blockLen);
    }

    private static void WriteEnhancedPacketBlock(BinaryWriter bw, CapturedPacketInfo pkt)
    {
        int padLen = (4 - (pkt.Data.Length % 4)) % 4;
        uint blockLen = (uint)(32 + pkt.Data.Length + padLen);

        var ts = (ulong)(pkt.Timestamp - DateTime.UnixEpoch).TotalMicroseconds;

        bw.Write(0x00000006); // Block Type = EPB
        bw.Write(blockLen);
        bw.Write(0u);         // Interface ID
        bw.Write((uint)(ts >> 32));  // Timestamp High
        bw.Write((uint)(ts & 0xFFFFFFFF)); // Timestamp Low
        bw.Write((uint)pkt.Data.Length);   // Captured Length
        bw.Write((uint)pkt.Data.Length);   // Original Length
        bw.Write(pkt.Data);
        if (padLen > 0) bw.Write(new byte[padLen]);
        bw.Write(blockLen);
    }

    public void Dispose() => StopCapture();
}
