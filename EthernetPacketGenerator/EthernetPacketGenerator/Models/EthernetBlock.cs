namespace EthernetPacketGenerator.Models;

public class EthernetBlock : ProtocolBlock
{
    private byte[] _dstMac = new byte[6];
    private byte[] _srcMac = new byte[6];
    private ushort _etherType = (ushort)EtherTypeValue.IPv4;

    public override ProtocolType Type => ProtocolType.Ethernet;
    public override string DisplayName => "Ethernet II";
    public override string IconKey => "EthernetIcon";

    public EthernetBlock()
    {
        _dstMac = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        _srcMac = new byte[6];
        RecomputeBytes();
    }

    public string DstMac
    {
        get => FormatMac(_dstMac);
        set
        {
            _dstMac = ParseMac(value);
            RecomputeBytes();
            OnPropertyChanged();
        }
    }

    public string SrcMac
    {
        get => FormatMac(_srcMac);
        set
        {
            _srcMac = ParseMac(value);
            RecomputeBytes();
            OnPropertyChanged();
        }
    }

    public ushort EtherType
    {
        get => _etherType;
        set
        {
            _etherType = value;
            RecomputeBytes();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EtherTypeDisplay));
        }
    }

    public string EtherTypeDisplay => $"0x{_etherType:X4}";

    protected override void RecomputeBytes()
    {
        var b = new byte[14];
        Array.Copy(_dstMac, 0, b, 0, 6);
        Array.Copy(_srcMac, 0, b, 6, 6);
        WriteUInt16BE(b, 12, _etherType);
        Bytes = b;
    }

    public override ValidationResult ValidateAgainst(ProtocolBlock? next)
    {
        if (next == null) return ValidationResult.None;

        var expectedEtherType = next.Type switch
        {
            ProtocolType.IPv4 => (ushort)EtherTypeValue.IPv4,
            ProtocolType.ARP  => (ushort)EtherTypeValue.ARP,
            ProtocolType.VLAN => (ushort)EtherTypeValue.VLAN,
            ProtocolType.IPv6 => (ushort)EtherTypeValue.IPv6,
            _ => (ushort)0
        };

        if (expectedEtherType != 0 && _etherType != expectedEtherType)
            return ValidationResult.Warning(
                $"EtherType 0x{_etherType:X4} does not match next protocol {next.DisplayName} (expected 0x{expectedEtherType:X4})");

        return ValidationResult.None;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 14) return;
        _suppressNotify = true;
        _dstMac = fullPacket.Skip(startOffset).Take(6).ToArray();
        _srcMac = fullPacket.Skip(startOffset + 6).Take(6).ToArray();
        _etherType = ReadUInt16BE(fullPacket, startOffset + 12);
        _suppressNotify = false;
        RecomputeBytes();
        OnPropertyChanged(nameof(DstMac));
        OnPropertyChanged(nameof(SrcMac));
        OnPropertyChanged(nameof(EtherType));
        OnPropertyChanged(nameof(EtherTypeDisplay));
    }
}
