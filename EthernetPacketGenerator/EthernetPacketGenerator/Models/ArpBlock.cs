namespace EthernetPacketGenerator.Models;

public class ArpBlock : ProtocolBlock
{
    private ushort _hardwareType = 1;
    private ushort _protocolType = 0x0800;
    private byte _hwAddrLen = 6;
    private byte _protoAddrLen = 4;
    private ushort _operation = (ushort)ArpOperation.Request;
    private byte[] _senderHw = new byte[6];
    private byte[] _senderProto = new byte[4];
    private byte[] _targetHw = new byte[6];
    private byte[] _targetProto = new byte[4];

    public override ProtocolType Type => ProtocolType.ARP;
    public override string DisplayName => "ARP";
    public override string IconKey => "ArpIcon";

    public ArpBlock() => RecomputeBytes();

    public ushort HardwareType
    {
        get => _hardwareType;
        set { _hardwareType = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort ProtocolTypeField
    {
        get => _protocolType;
        set { _protocolType = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public ushort Operation
    {
        get => _operation;
        set { _operation = value; RecomputeBytes(); OnPropertyChanged(); OnPropertyChanged(nameof(OperationDisplay)); }
    }

    public string OperationDisplay => _operation == 1 ? "Request (1)" : _operation == 2 ? "Reply (2)" : $"{_operation}";

    public string SenderHwAddr
    {
        get => FormatMac(_senderHw);
        set { _senderHw = ParseMac(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    public string SenderProtoAddr
    {
        get => FormatIPv4(_senderProto);
        set { _senderProto = ParseIPv4(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    public string TargetHwAddr
    {
        get => FormatMac(_targetHw);
        set { _targetHw = ParseMac(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    public string TargetProtoAddr
    {
        get => FormatIPv4(_targetProto);
        set { _targetProto = ParseIPv4(value); RecomputeBytes(); OnPropertyChanged(); }
    }

    protected override void RecomputeBytes()
    {
        var b = new byte[28];
        WriteUInt16BE(b, 0, _hardwareType);
        WriteUInt16BE(b, 2, _protocolType);
        b[4] = _hwAddrLen;
        b[5] = _protoAddrLen;
        WriteUInt16BE(b, 6, _operation);
        Array.Copy(_senderHw,    0, b, 8,  6);
        Array.Copy(_senderProto, 0, b, 14, 4);
        Array.Copy(_targetHw,    0, b, 18, 6);
        Array.Copy(_targetProto, 0, b, 24, 4);
        Bytes = b;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 28) return;
        _suppressNotify = true;
        _hardwareType  = ReadUInt16BE(fullPacket, startOffset);
        _protocolType  = ReadUInt16BE(fullPacket, startOffset + 2);
        _hwAddrLen     = fullPacket[startOffset + 4];
        _protoAddrLen  = fullPacket[startOffset + 5];
        _operation     = ReadUInt16BE(fullPacket, startOffset + 6);
        _senderHw      = fullPacket.Skip(startOffset + 8).Take(6).ToArray();
        _senderProto   = fullPacket.Skip(startOffset + 14).Take(4).ToArray();
        _targetHw      = fullPacket.Skip(startOffset + 18).Take(6).ToArray();
        _targetProto   = fullPacket.Skip(startOffset + 24).Take(4).ToArray();
        _suppressNotify = false;
        RecomputeBytes();
        OnPropertyChanged(nameof(HardwareType));
        OnPropertyChanged(nameof(ProtocolTypeField));
        OnPropertyChanged(nameof(Operation));
        OnPropertyChanged(nameof(SenderHwAddr));
        OnPropertyChanged(nameof(SenderProtoAddr));
        OnPropertyChanged(nameof(TargetHwAddr));
        OnPropertyChanged(nameof(TargetProtoAddr));
    }
}
