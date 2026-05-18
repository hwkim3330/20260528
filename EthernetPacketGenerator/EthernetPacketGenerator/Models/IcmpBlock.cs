using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.Models;

public class IcmpBlock : ProtocolBlock
{
    private byte _type = (byte)Models.IcmpType.EchoRequest;
    private byte _code;
    private uint _restOfHeader;
    private byte[] _data = Array.Empty<byte>();

    public override ProtocolType Type => ProtocolType.ICMP;
    public override string DisplayName => "ICMP";
    public override string IconKey => "IcmpIcon";

    public IcmpBlock() => RecomputeBytes();

    public byte IcmpType
    {
        get => _type;
        set { _type = value; RecomputeBytes(); OnPropertyChanged(); OnPropertyChanged(nameof(TypeDisplay)); }
    }

    public string TypeDisplay => _type switch
    {
        0  => "Echo Reply (0)",
        3  => "Destination Unreachable (3)",
        8  => "Echo Request (8)",
        11 => "Time Exceeded (11)",
        _  => $"Type {_type}"
    };

    public byte Code
    {
        get => _code;
        set { _code = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public uint RestOfHeader
    {
        get => _restOfHeader;
        set { _restOfHeader = value; RecomputeBytes(); OnPropertyChanged(); }
    }

    public string DataHex
    {
        get => BitConverter.ToString(_data).Replace("-", " ");
        set
        {
            try
            {
                var hex = value.Replace(" ", "").Replace("-", "");
                if (hex.Length % 2 != 0) hex = "0" + hex;
                _data = Enumerable.Range(0, hex.Length / 2)
                    .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            catch
            {
                _data = Array.Empty<byte>();
            }
            RecomputeBytes();
            OnPropertyChanged();
        }
    }

    protected override void RecomputeBytes()
    {
        var b = new byte[8 + _data.Length];
        b[0] = _type;
        b[1] = _code;
        WriteUInt16BE(b, 2, 0); // checksum placeholder
        WriteUInt32BE(b, 4, _restOfHeader);
        Array.Copy(_data, 0, b, 8, _data.Length);
        Bytes = b;
    }

    public override void UpdateDerivedFields(byte[] prefix, byte[] suffix)
    {
        var b = (byte[])Bytes.Clone();
        WriteUInt16BE(b, 2, 0);
        ushort checksum = ChecksumHelper.InternetChecksum(b, 0, b.Length);
        WriteUInt16BE(b, 2, checksum);
        Bytes = b;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 8) return;
        _suppressNotify = true;
        _type          = fullPacket[startOffset];
        _code          = fullPacket[startOffset + 1];
        _restOfHeader  = ReadUInt32BE(fullPacket, startOffset + 4);
        int dataLen    = fullPacket.Length - startOffset - 8;
        _data          = dataLen > 0 ? fullPacket.Skip(startOffset + 8).Take(dataLen).ToArray()
                                      : Array.Empty<byte>();
        _suppressNotify = false;
        RecomputeBytes();
        OnPropertyChanged(nameof(IcmpType));
        OnPropertyChanged(nameof(Code));
        OnPropertyChanged(nameof(RestOfHeader));
        OnPropertyChanged(nameof(DataHex));
    }
}
