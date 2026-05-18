using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.Models;

public class TcpBlock : ProtocolBlock
{
    private ushort _srcPort;
    private ushort _dstPort;
    private uint _seqNum;
    private uint _ackNum;
    private byte _flags;
    private ushort _windowSize = 65535;
    private ushort _urgentPtr;
    private byte[] _payload = Array.Empty<byte>();

    public override ProtocolType Type => ProtocolType.TCP;
    public override string DisplayName => "TCP";
    public override string IconKey => "TcpIcon";

    public TcpBlock() => RecomputeBytes();

    public ushort SrcPort
    {
        get => _srcPort;
        set { _srcPort = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort DstPort
    {
        get => _dstPort;
        set { _dstPort = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public uint SeqNum
    {
        get => _seqNum;
        set { _seqNum = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public uint AckNum
    {
        get => _ackNum;
        set { _ackNum = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public bool FlagSYN
    {
        get => (_flags & 0x02) != 0;
        set { SetFlag(0x02, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public bool FlagACK
    {
        get => (_flags & 0x10) != 0;
        set { SetFlag(0x10, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public bool FlagFIN
    {
        get => (_flags & 0x01) != 0;
        set { SetFlag(0x01, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public bool FlagRST
    {
        get => (_flags & 0x04) != 0;
        set { SetFlag(0x04, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public bool FlagPSH
    {
        get => (_flags & 0x08) != 0;
        set { SetFlag(0x08, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public bool FlagURG
    {
        get => (_flags & 0x20) != 0;
        set { SetFlag(0x20, value); OnPropertyChanged(); OnPropertyChanged(nameof(FlagsDisplay)); }
    }

    public string FlagsDisplay
    {
        get
        {
            var flags = new List<string>();
            if (FlagURG) flags.Add("URG");
            if (FlagACK) flags.Add("ACK");
            if (FlagPSH) flags.Add("PSH");
            if (FlagRST) flags.Add("RST");
            if (FlagSYN) flags.Add("SYN");
            if (FlagFIN) flags.Add("FIN");
            return flags.Count > 0 ? string.Join("|", flags) : "None";
        }
    }

    public ushort WindowSize
    {
        get => _windowSize;
        set { _windowSize = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort UrgentPtr
    {
        get => _urgentPtr;
        set { _urgentPtr = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public string PayloadHex
    {
        get => BitConverter.ToString(_payload).Replace("-", " ");
        set
        {
            try
            {
                var hex = value.Replace(" ", "").Replace("-", "");
                if (hex.Length % 2 != 0) hex = "0" + hex;
                _payload = Enumerable.Range(0, hex.Length / 2)
                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            catch { _payload = Array.Empty<byte>(); }
            RecomputeBytes();
            OnPropertyChanged();
        }
    }

    private void SetFlag(byte mask, bool set)
    {
        if (set) _flags |= mask;
        else _flags &= (byte)~mask;
        RecomputeBytes();
    }

    protected override void RecomputeBytes()
    {
        var b = new byte[20 + _payload.Length];
        WriteUInt16BE(b, 0, _srcPort);
        WriteUInt16BE(b, 2, _dstPort);
        WriteUInt32BE(b, 4, _seqNum);
        WriteUInt32BE(b, 8, _ackNum);
        b[12] = 0x50; // data offset = 5 (20 bytes), reserved = 0
        b[13] = _flags;
        WriteUInt16BE(b, 14, _windowSize);
        WriteUInt16BE(b, 16, 0); // checksum placeholder
        WriteUInt16BE(b, 18, _urgentPtr);
        if (_payload.Length > 0)
            Array.Copy(_payload, 0, b, 20, _payload.Length);
        Bytes = b;
    }

    public override void UpdateDerivedFields(byte[] prefix, byte[] suffix)
    {
        // Find IPv4 source/dest from prefix
        byte[] srcIp = new byte[4], dstIp = new byte[4];
        for (int i = 0; i <= prefix.Length - 20; i++)
        {
            if ((prefix[i] & 0xF0) == 0x40) // IPv4
            {
                Array.Copy(prefix, i + 12, srcIp, 0, 4);
                Array.Copy(prefix, i + 16, dstIp, 0, 4);
                break;
            }
        }

        var b = (byte[])Bytes.Clone();
        WriteUInt16BE(b, 16, 0);
        ushort checksum = ChecksumHelper.PseudoHeaderChecksum(
            srcIp, dstIp, (byte)IpProtocolValue.TCP, b);
        WriteUInt16BE(b, 16, checksum);
        Bytes = b;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 20) return;
        _suppressNotify = true;
        _srcPort    = ReadUInt16BE(fullPacket, startOffset);
        _dstPort    = ReadUInt16BE(fullPacket, startOffset + 2);
        _seqNum     = ReadUInt32BE(fullPacket, startOffset + 4);
        _ackNum     = ReadUInt32BE(fullPacket, startOffset + 8);
        byte dataOffset = (byte)((fullPacket[startOffset + 12] >> 4) * 4);
        _flags      = fullPacket[startOffset + 13];
        _windowSize = ReadUInt16BE(fullPacket, startOffset + 14);
        _urgentPtr  = ReadUInt16BE(fullPacket, startOffset + 18);
        int payloadStart = startOffset + dataOffset;
        int payloadLen   = fullPacket.Length - payloadStart;
        _payload = payloadLen > 0 ? fullPacket.Skip(payloadStart).Take(payloadLen).ToArray()
                                  : Array.Empty<byte>();
        _suppressNotify = false;
        RecomputeBytes();
        foreach (var p in new[] { nameof(SrcPort), nameof(DstPort), nameof(SeqNum),
            nameof(AckNum), nameof(WindowSize), nameof(UrgentPtr), nameof(PayloadHex),
            nameof(FlagSYN), nameof(FlagACK), nameof(FlagFIN), nameof(FlagRST),
            nameof(FlagPSH), nameof(FlagURG), nameof(FlagsDisplay) })
            OnPropertyChanged(p);
    }
}
