namespace EthernetPacketGenerator.Models;

public class VlanBlock : ProtocolBlock
{
    private byte _priority;
    private bool _dei;
    private ushort _vlanId = 1;
    private ushort _etherType = (ushort)EtherTypeValue.IPv4;

    public override ProtocolType Type => ProtocolType.VLAN;
    public override string DisplayName => "802.1Q VLAN";
    public override string IconKey => "VlanIcon";

    public VlanBlock() => RecomputeBytes();

    public byte Priority
    {
        get => _priority;
        set { _priority = (byte)(value & 0x07); RecomputeBytes(); OnPropertyChanged(); }
    }

    public bool DEI
    {
        get => _dei;
        set { _dei = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort VlanId
    {
        get => _vlanId;
        set { _vlanId = (ushort)(value & 0x0FFF); RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort EtherType
    {
        get => _etherType;
        set { _etherType = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    protected override void RecomputeBytes()
    {
        var b = new byte[4];
        ushort tci = (ushort)((_priority << 13) | (_dei ? 0x1000 : 0) | (_vlanId & 0x0FFF));
        WriteUInt16BE(b, 0, tci);
        WriteUInt16BE(b, 2, _etherType);
        Bytes = b;
    }

    public override ValidationResult ValidateAgainst(ProtocolBlock? next)
    {
        if (next == null) return ValidationResult.None;
        var expectedEtherType = next.Type switch
        {
            ProtocolType.IPv4 => (ushort)EtherTypeValue.IPv4,
            ProtocolType.ARP  => (ushort)EtherTypeValue.ARP,
            _ => (ushort)0
        };
        if (expectedEtherType != 0 && _etherType != expectedEtherType)
            return ValidationResult.Warning(
                $"VLAN EtherType 0x{_etherType:X4} does not match next protocol {next.DisplayName}");
        return ValidationResult.None;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 4) return;
        _suppressNotify = true;
        ushort tci  = ReadUInt16BE(fullPacket, startOffset);
        _priority   = (byte)(tci >> 13);
        _dei        = (tci & 0x1000) != 0;
        _vlanId     = (ushort)(tci & 0x0FFF);
        _etherType  = ReadUInt16BE(fullPacket, startOffset + 2);
        _suppressNotify = false;
        RecomputeBytes();
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(DEI));
        OnPropertyChanged(nameof(VlanId));
        OnPropertyChanged(nameof(EtherType));
    }
}
