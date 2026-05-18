using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.Models;

public class IPv4Block : ProtocolBlock
{
    private byte _dscp;
    private byte _ecn;
    private ushort _identification = 0x0001;
    private byte _flags;
    private ushort _fragmentOffset;
    private byte _ttl = 64;
    private byte _protocol = (byte)IpProtocolValue.TCP;
    private byte[] _srcIp = new byte[4];
    private byte[] _dstIp = new byte[4];

    public override ProtocolType Type => ProtocolType.IPv4;
    public override string DisplayName => "IPv4";
    public override string IconKey => "IPv4Icon";

    public IPv4Block() => RecomputeBytes();

    public byte DSCP
    {
        get => _dscp;
        set { _dscp = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public byte ECN
    {
        get => _ecn;
        set { _ecn = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort Identification
    {
        get => _identification;
        set { _identification = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public byte Flags
    {
        get => _flags;
        set { _flags = (byte)(value & 0x07); RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort FragmentOffset
    {
        get => _fragmentOffset;
        set { _fragmentOffset = (ushort)(value & 0x1FFF); RecomputeBytes(); OnPropertyChanged(); }
    }

    public byte TTL
    {
        get => _ttl;
        set { _ttl = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public byte Protocol
    {
        get => _protocol;
        set { _protocol = value; RecomputeBytes(); OnPropertyChanged(); OnPropertyChanged(nameof(ProtocolDisplay)); }
    }

    public string ProtocolDisplay => _protocol switch
    {
        1  => "ICMP (1)",
        6  => "TCP (6)",
        17 => "UDP (17)",
        _  => $"Unknown ({_protocol})"
    };

    public string SrcIP
    {
        get => FormatIPv4(_srcIp);
        set { _srcIp = ParseIPv4(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    public string DstIP
    {
        get => FormatIPv4(_dstIp);
        set { _dstIp = ParseIPv4(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    protected override void RecomputeBytes()
    {
        var b = new byte[20];
        b[0] = 0x45; // Version=4, IHL=5
        b[1] = (byte)((_dscp << 2) | (_ecn & 0x03));
        WriteUInt16BE(b, 2, 20); // placeholder total length
        WriteUInt16BE(b, 4, _identification);
        WriteUInt16BE(b, 6, (ushort)((_flags << 13) | (_fragmentOffset & 0x1FFF)));
        b[8] = _ttl;
        b[9] = _protocol;
        WriteUInt16BE(b, 10, 0); // checksum placeholder
        Array.Copy(_srcIp, 0, b, 12, 4);
        Array.Copy(_dstIp, 0, b, 16, 4);
        Bytes = b;
    }

    public override void UpdateDerivedFields(byte[] prefix, byte[] suffix)
    {
        var b = (byte[])Bytes.Clone();
        ushort totalLen = (ushort)(20 + suffix.Length);
        WriteUInt16BE(b, 2, totalLen);
        WriteUInt16BE(b, 10, 0);
        ushort checksum = ChecksumHelper.InternetChecksum(b, 0, 20);
        WriteUInt16BE(b, 10, checksum);
        Bytes = b;
    }

    public override ValidationResult ValidateAgainst(ProtocolBlock? next)
    {
        if (next == null) return ValidationResult.None;

        var expectedProto = next.Type switch
        {
            ProtocolType.ICMP => (byte)IpProtocolValue.ICMP,
            ProtocolType.TCP  => (byte)IpProtocolValue.TCP,
            ProtocolType.UDP  => (byte)IpProtocolValue.UDP,
            _ => (byte)0
        };

        if (expectedProto != 0 && _protocol != expectedProto)
            return ValidationResult.Warning(
                $"IP Protocol field {_protocol} does not match next protocol {next.DisplayName} (expected {expectedProto})");

        return ValidationResult.None;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 20) return;
        _suppressNotify = true;
        _dscp           = (byte)(fullPacket[startOffset + 1] >> 2);
        _ecn            = (byte)(fullPacket[startOffset + 1] & 0x03);
        _identification = ReadUInt16BE(fullPacket, startOffset + 4);
        ushort flagsFrag = ReadUInt16BE(fullPacket, startOffset + 6);
        _flags          = (byte)(flagsFrag >> 13);
        _fragmentOffset = (ushort)(flagsFrag & 0x1FFF);
        _ttl            = fullPacket[startOffset + 8];
        _protocol       = fullPacket[startOffset + 9];
        _srcIp          = fullPacket.Skip(startOffset + 12).Take(4).ToArray();
        _dstIp          = fullPacket.Skip(startOffset + 16).Take(4).ToArray();
        _suppressNotify = false;
        RecomputeBytes();
        foreach (var prop in new[] { nameof(DSCP), nameof(ECN), nameof(Identification),
            nameof(Flags), nameof(FragmentOffset), nameof(TTL), nameof(Protocol),
            nameof(SrcIP), nameof(DstIP) })
            OnPropertyChanged(prop);
    }

    public byte[] SrcIPBytes => _srcIp;
    public byte[] DstIPBytes => _dstIp;
}
