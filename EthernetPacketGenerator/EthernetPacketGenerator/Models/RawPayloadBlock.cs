namespace EthernetPacketGenerator.Models;

public class RawPayloadBlock : ProtocolBlock
{
    public override ProtocolType Type => ProtocolType.RawPayload;
    public override string DisplayName => "Payload";
    public override string IconKey => "PayloadIcon";

    public RawPayloadBlock()
    {
        _bytes = Array.Empty<byte>();
    }

    public string DataHex
    {
        get => BitConverter.ToString(_bytes).Replace("-", " ");
        set
        {
            try
            {
                var hex = value.Replace(" ", "").Replace("-", "");
                if (hex.Length % 2 != 0) hex = "0" + hex;
                _bytes = hex.Length == 0
                    ? Array.Empty<byte>()
                    : Enumerable.Range(0, hex.Length / 2)
                        .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                        .ToArray();
            }
            catch { _bytes = Array.Empty<byte>(); }
            OnPropertyChanged();
            OnPropertyChanged(nameof(Bytes));
            OnPropertyChanged(nameof(ByteLength));
        }
    }

    protected override void RecomputeBytes() { }

    public void SetBytes(byte[] data)
    {
        _bytes = data;
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(DataHex));
    }

    public override void ImportBytes(byte[] fullPacket, int startOffset)
    {
        int len = fullPacket.Length - startOffset;
        _bytes = len > 0 ? fullPacket.Skip(startOffset).Take(len).ToArray() : Array.Empty<byte>();
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(DataHex));
    }
}
