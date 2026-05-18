using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.Models;

public class UdpBlock : ProtocolBlock
{
    private ushort _srcPort;
    private ushort _dstPort;
    private byte[] _payload = Array.Empty<byte>();

    public override ProtocolType Type => ProtocolType.UDP;
    public override string DisplayName => "UDP";
    public override string IconKey => "UdpIcon";

    public UdpBlock() => RecomputeBytes();

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

    protected override void RecomputeBytes()
    {
        var b = new byte[8 + _payload.Length];
        WriteUInt16BE(b, 0, _srcPort);
        WriteUInt16BE(b, 2, _dstPort);
        WriteUInt16BE(b, 4, (ushort)(8 + _payload.Length));
        WriteUInt16BE(b, 6, 0); // checksum placeholder
        if (_payload.Length > 0)
            Array.Copy(_payload, 0, b, 8, _payload.Length);
        Bytes = b;
    }

    public override void UpdateDerivedFields(byte[] prefix, byte[] suffix)
    {
        byte[] srcIp = new byte[4], dstIp = new byte[4];
        for (int i = 0; i <= prefix.Length - 20; i++)
        {
            if ((prefix[i] & 0xF0) == 0x40)
            {
                Array.Copy(prefix, i + 12, srcIp, 0, 4);
                Array.Copy(prefix, i + 16, dstIp, 0, 4);
                break;
            }
        }

        var b = (byte[])Bytes.Clone();
        WriteUInt16BE(b, 4, (ushort)b.Length);
        WriteUInt16BE(b, 6, 0);
        ushort checksum = ChecksumHelper.PseudoHeaderChecksum(
            srcIp, dstIp, (byte)IpProtocolValue.UDP, b);
        WriteUInt16BE(b, 6, checksum);
        Bytes = b;
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        if (fullPacket.Length < startOffset + 8) return;
        _suppressNotify = true;
        _srcPort = ReadUInt16BE(fullPacket, startOffset);
        _dstPort = ReadUInt16BE(fullPacket, startOffset + 2);
        ushort len = ReadUInt16BE(fullPacket, startOffset + 4);
        int payloadLen = len > 8 ? len - 8 : 0;
        _payload = payloadLen > 0 ? fullPacket.Skip(startOffset + 8).Take(payloadLen).ToArray()
                                  : Array.Empty<byte>();
        _suppressNotify = false;
        RecomputeBytes();
        OnPropertyChanged(nameof(SrcPort));
        OnPropertyChanged(nameof(DstPort));
        OnPropertyChanged(nameof(PayloadHex));
    }
}
