using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

public class TreeNode
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public int ByteOffset { get; set; }
    public int ByteLength { get; set; }
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.None;
    public List<TreeNode> Children { get; } = new();
}

public static class ProtocolDecoder
{
    public static List<TreeNode> Decode(byte[] packet)
    {
        var roots = new List<TreeNode>();
        if (packet.Length < 14) return roots;

        int offset = 0;
        var etherNode = DecodeEthernet(packet, ref offset);
        roots.Add(etherNode);

        if (offset >= packet.Length) return roots;

        ushort etherType = (ushort)((packet[12] << 8) | packet[13]);
        switch (etherType)
        {
            case 0x0800:
                roots.Add(DecodeIPv4(packet, ref offset));
                break;
            case 0x0806:
                roots.Add(DecodeARP(packet, ref offset));
                break;
            case 0x8100:
                roots.Add(DecodeVLAN(packet, ref offset));
                // After VLAN, try to decode inner EtherType
                if (offset + 1 < packet.Length)
                {
                    ushort innerType = (ushort)((packet[offset - 2] << 8) | packet[offset - 1]);
                    if (innerType == 0x0800 && offset < packet.Length)
                        roots.Add(DecodeIPv4(packet, ref offset));
                    else if (innerType == 0x0806 && offset < packet.Length)
                        roots.Add(DecodeARP(packet, ref offset));
                }
                break;
            default:
                // EtherType mismatch — try to guess from remaining bytes
                roots.Add(DecodeBestEffort(packet, offset, etherType));
                offset = packet.Length;
                break;
        }

        return roots;
    }

    private static TreeNode DecodeBestEffort(byte[] packet, int offset, ushort etherType)
    {
        int remaining = packet.Length - offset;
        // Try IPv4 heuristic: version nibble == 4 and enough bytes
        if (remaining >= 20 && (packet[offset] & 0xF0) == 0x40)
        {
            var node = DecodeIPv4(packet, ref offset);
            node.Severity = ValidationSeverity.Warning;
            node.Value = $"[EtherType 0x{etherType:X4} mismatch] " + node.Value;
            return node;
        }
        // Try ARP heuristic: 28 bytes, hwType=1
        if (remaining >= 28 && packet[offset] == 0x00 && packet[offset + 1] == 0x01)
        {
            var node = DecodeARP(packet, ref offset);
            node.Severity = ValidationSeverity.Warning;
            node.Value = $"[EtherType 0x{etherType:X4} mismatch] " + node.Value;
            return node;
        }
        return new TreeNode
        {
            Label = $"Unknown (EtherType 0x{etherType:X4})",
            Value = $"{remaining} bytes — cannot decode",
            ByteOffset = offset,
            ByteLength = remaining,
            Severity = ValidationSeverity.Warning
        };
    }

    private static TreeNode DecodeEthernet(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "Ethernet II",
            Value = string.Empty,
            ByteOffset = 0,
            ByteLength = 14
        };

        if (p.Length < 14)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        string dstMac = FormatMac(p, 0);
        string srcMac = FormatMac(p, 6);
        ushort etherType = (ushort)((p[12] << 8) | p[13]);

        node.Children.Add(Field("Destination", dstMac, 0, 6));
        node.Children.Add(Field("Source", srcMac, 6, 6));
        node.Children.Add(Field("Type", $"0x{etherType:X4} ({EtherTypeName(etherType)})", 12, 2));

        offset = 14;
        return node;
    }

    private static TreeNode DecodeIPv4(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "Internet Protocol Version 4",
            ByteOffset = offset,
            ByteLength = 20
        };

        if (p.Length < offset + 20)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        int start = offset;
        byte versionIhl = p[offset];
        byte version = (byte)(versionIhl >> 4);
        byte ihl = (byte)((versionIhl & 0x0F) * 4);
        byte dscp = (byte)(p[offset + 1] >> 2);
        byte ecn = (byte)(p[offset + 1] & 0x03);
        ushort totalLen = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
        ushort id = (ushort)((p[offset + 4] << 8) | p[offset + 5]);
        ushort flagsFrag = (ushort)((p[offset + 6] << 8) | p[offset + 7]);
        byte flags = (byte)(flagsFrag >> 13);
        ushort fragOffset = (ushort)(flagsFrag & 0x1FFF);
        byte ttl = p[offset + 8];
        byte proto = p[offset + 9];
        ushort checksum = (ushort)((p[offset + 10] << 8) | p[offset + 11]);
        string srcIp = $"{p[offset+12]}.{p[offset+13]}.{p[offset+14]}.{p[offset+15]}";
        string dstIp = $"{p[offset+16]}.{p[offset+17]}.{p[offset+18]}.{p[offset+19]}";

        node.Value = $"Src: {srcIp}, Dst: {dstIp}";
        node.ByteLength = ihl;
        node.Children.Add(Field("Version", $"{version}", offset, 1, nibbleHigh: true));
        node.Children.Add(Field("Header Length", $"{ihl} bytes", offset, 1, nibbleLow: true));
        node.Children.Add(Field("DSCP", $"{dscp}", offset + 1, 1));
        node.Children.Add(Field("Total Length", $"{totalLen}", offset + 2, 2));
        node.Children.Add(Field("Identification", $"0x{id:X4}", offset + 4, 2));
        node.Children.Add(Field("Flags", $"0x{flags:X}", offset + 6, 1, nibbleHigh: true));
        node.Children.Add(Field("Fragment Offset", $"{fragOffset}", offset + 6, 2));
        node.Children.Add(Field("Time to Live", $"{ttl}", offset + 8, 1));
        node.Children.Add(Field("Protocol", $"{proto} ({IpProtoName(proto)})", offset + 9, 1));

        // Validate checksum
        bool csValid = ValidateIPv4Checksum(p, offset);
        var csNode = Field("Header Checksum", $"0x{checksum:X4} [{(csValid ? "valid" : "INVALID")}]", offset + 10, 2);
        if (!csValid) csNode.Severity = ValidationSeverity.Error;
        node.Children.Add(csNode);

        node.Children.Add(Field("Source Address", srcIp, offset + 12, 4));
        node.Children.Add(Field("Destination Address", dstIp, offset + 16, 4));

        offset += ihl;

        switch (proto)
        {
            case 1:
                if (offset < p.Length)
                    node.Children.Add(DecodeICMP(p, ref offset));
                break;
            case 6:
                if (offset < p.Length)
                    node.Children.Add(DecodeTCP(p, ref offset));
                break;
            case 17:
                if (offset < p.Length)
                    node.Children.Add(DecodeUDP(p, ref offset));
                break;
        }

        return node;
    }

    private static TreeNode DecodeARP(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "Address Resolution Protocol",
            ByteOffset = offset,
            ByteLength = 28
        };

        if (p.Length < offset + 28)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        ushort hwType = (ushort)((p[offset] << 8) | p[offset + 1]);
        ushort protoType = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
        byte hwLen = p[offset + 4];
        byte protoLen = p[offset + 5];
        ushort op = (ushort)((p[offset + 6] << 8) | p[offset + 7]);
        string senderHw = FormatMac(p, offset + 8);
        string senderProto = $"{p[offset+14]}.{p[offset+15]}.{p[offset+16]}.{p[offset+17]}";
        string targetHw = FormatMac(p, offset + 18);
        string targetProto = $"{p[offset+24]}.{p[offset+25]}.{p[offset+26]}.{p[offset+27]}";

        node.Value = op == 1 ? $"Request: Who has {targetProto}?" : $"Reply: {senderProto} is at {senderHw}";
        node.Children.Add(Field("Hardware Type", $"{hwType}", offset, 2));
        node.Children.Add(Field("Protocol Type", $"0x{protoType:X4}", offset + 2, 2));
        node.Children.Add(Field("HW Address Length", $"{hwLen}", offset + 4, 1));
        node.Children.Add(Field("Proto Address Length", $"{protoLen}", offset + 5, 1));
        node.Children.Add(Field("Operation", op == 1 ? "Request (1)" : op == 2 ? "Reply (2)" : $"{op}", offset + 6, 2));
        node.Children.Add(Field("Sender HW Address", senderHw, offset + 8, 6));
        node.Children.Add(Field("Sender Proto Address", senderProto, offset + 14, 4));
        node.Children.Add(Field("Target HW Address", targetHw, offset + 18, 6));
        node.Children.Add(Field("Target Proto Address", targetProto, offset + 24, 4));

        offset += 28;
        return node;
    }

    private static TreeNode DecodeICMP(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "Internet Control Message Protocol",
            ByteOffset = offset,
            ByteLength = p.Length - offset
        };

        if (p.Length < offset + 8)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        byte type = p[offset];
        byte code = p[offset + 1];
        ushort checksum = (ushort)((p[offset + 2] << 8) | p[offset + 3]);

        node.Value = IcmpTypeName(type, code);
        node.Children.Add(Field("Type", $"{type} ({IcmpTypeName(type, 0)})", offset, 1));
        node.Children.Add(Field("Code", $"{code}", offset + 1, 1));
        node.Children.Add(Field("Checksum", $"0x{checksum:X4}", offset + 2, 2));
        node.Children.Add(Field("Rest of Header", $"0x{p[offset+4]:X2}{p[offset+5]:X2}{p[offset+6]:X2}{p[offset+7]:X2}", offset + 4, 4));

        if (p.Length > offset + 8)
            node.Children.Add(Field("Data", $"{p.Length - offset - 8} bytes", offset + 8, p.Length - offset - 8));

        offset = p.Length;
        return node;
    }

    private static TreeNode DecodeTCP(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "Transmission Control Protocol",
            ByteOffset = offset,
            ByteLength = p.Length - offset
        };

        if (p.Length < offset + 20)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        ushort srcPort = (ushort)((p[offset] << 8) | p[offset + 1]);
        ushort dstPort = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
        uint seqNum = (uint)((p[offset+4] << 24) | (p[offset+5] << 16) | (p[offset+6] << 8) | p[offset+7]);
        uint ackNum = (uint)((p[offset+8] << 24) | (p[offset+9] << 16) | (p[offset+10] << 8) | p[offset+11]);
        byte dataOffset = (byte)((p[offset + 12] >> 4) * 4);
        byte flags = p[offset + 13];
        ushort window = (ushort)((p[offset + 14] << 8) | p[offset + 15]);

        string flagsStr = FormatTcpFlags(flags);
        node.Value = $"{srcPort} → {dstPort} [{flagsStr}] Seq={seqNum}";
        node.ByteLength = dataOffset;
        node.Children.Add(Field("Source Port", $"{srcPort}", offset, 2));
        node.Children.Add(Field("Destination Port", $"{dstPort}", offset + 2, 2));
        node.Children.Add(Field("Sequence Number", $"{seqNum}", offset + 4, 4));
        node.Children.Add(Field("Acknowledgment Number", $"{ackNum}", offset + 8, 4));
        node.Children.Add(Field("Data Offset", $"{dataOffset} bytes", offset + 12, 1, nibbleHigh: true));
        node.Children.Add(Field("Flags", flagsStr, offset + 13, 1));
        node.Children.Add(Field("Window Size", $"{window}", offset + 14, 2));

        int payloadStart = offset + dataOffset;
        int payloadLen = p.Length - payloadStart;
        if (payloadLen > 0)
            node.Children.Add(Field("Payload", $"{payloadLen} bytes", payloadStart, payloadLen));

        offset = p.Length;
        return node;
    }

    private static TreeNode DecodeUDP(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "User Datagram Protocol",
            ByteOffset = offset,
            ByteLength = p.Length - offset
        };

        if (p.Length < offset + 8)
        {
            node.Severity = ValidationSeverity.Error;
            node.Value = "Truncated";
            return node;
        }

        ushort srcPort = (ushort)((p[offset] << 8) | p[offset + 1]);
        ushort dstPort = (ushort)((p[offset + 2] << 8) | p[offset + 3]);
        ushort length = (ushort)((p[offset + 4] << 8) | p[offset + 5]);
        ushort checksum = (ushort)((p[offset + 6] << 8) | p[offset + 7]);

        node.Value = $"{srcPort} → {dstPort}";
        node.Children.Add(Field("Source Port", $"{srcPort}", offset, 2));
        node.Children.Add(Field("Destination Port", $"{dstPort}", offset + 2, 2));
        node.Children.Add(Field("Length", $"{length}", offset + 4, 2));
        node.Children.Add(Field("Checksum", $"0x{checksum:X4}", offset + 6, 2));

        int payloadLen = length - 8;
        if (payloadLen > 0)
            node.Children.Add(Field("Payload", $"{payloadLen} bytes", offset + 8, payloadLen));

        offset = p.Length;
        return node;
    }

    private static TreeNode DecodeVLAN(byte[] p, ref int offset)
    {
        var node = new TreeNode
        {
            Label = "802.1Q Virtual LAN",
            ByteOffset = offset,
            ByteLength = 4
        };

        if (p.Length < offset + 4)
        {
            node.Severity = ValidationSeverity.Warning;
            node.Value = "Truncated";
            return node;
        }

        ushort tci = (ushort)((p[offset] << 8) | p[offset + 1]);
        byte priority = (byte)(tci >> 13);
        bool dei = (tci & 0x1000) != 0;
        ushort vlanId = (ushort)(tci & 0x0FFF);
        ushort etherType = (ushort)((p[offset + 2] << 8) | p[offset + 3]);

        node.Value = $"VLAN ID: {vlanId}";
        node.Children.Add(Field("Priority", $"{priority}", offset, 1, nibbleHigh: true));
        node.Children.Add(Field("DEI", dei ? "1" : "0", offset, 1));
        node.Children.Add(Field("VLAN ID", $"{vlanId}", offset, 2));
        node.Children.Add(Field("Type", $"0x{etherType:X4}", offset + 2, 2));

        offset += 4;
        return node;
    }

    private static TreeNode Field(string label, string value, int byteOffset, int byteLength,
        bool nibbleHigh = false, bool nibbleLow = false)
        => new() { Label = label, Value = value, ByteOffset = byteOffset, ByteLength = byteLength };

    private static string FormatMac(byte[] p, int offset)
    {
        if (p.Length < offset + 6) return "??:??:??:??:??:??";
        return string.Join(":", Enumerable.Range(offset, 6).Select(i => p[i].ToString("X2")));
    }

    private static string EtherTypeName(ushort t) => t switch
    {
        0x0800 => "IPv4",
        0x0806 => "ARP",
        0x8100 => "802.1Q VLAN",
        0x86DD => "IPv6",
        _ => "Unknown"
    };

    private static string IpProtoName(byte p) => p switch
    {
        1  => "ICMP",
        6  => "TCP",
        17 => "UDP",
        _  => "Unknown"
    };

    private static string IcmpTypeName(byte type, byte code) => type switch
    {
        0  => "Echo Reply",
        3  => "Destination Unreachable",
        8  => "Echo Request",
        11 => "Time Exceeded",
        _  => $"Type {type}"
    };

    private static string FormatTcpFlags(byte flags)
    {
        var parts = new List<string>();
        if ((flags & 0x20) != 0) parts.Add("URG");
        if ((flags & 0x10) != 0) parts.Add("ACK");
        if ((flags & 0x08) != 0) parts.Add("PSH");
        if ((flags & 0x04) != 0) parts.Add("RST");
        if ((flags & 0x02) != 0) parts.Add("SYN");
        if ((flags & 0x01) != 0) parts.Add("FIN");
        return parts.Count > 0 ? string.Join("|", parts) : "None";
    }

    private static bool ValidateIPv4Checksum(byte[] p, int offset)
    {
        if (p.Length < offset + 20) return false;
        var header = p.Skip(offset).Take(20).ToArray();
        ushort stored = (ushort)((header[10] << 8) | header[11]);
        header[10] = 0;
        header[11] = 0;
        ushort computed = ChecksumHelper.InternetChecksum(header, 0, 20);
        return computed == stored;
    }
}
