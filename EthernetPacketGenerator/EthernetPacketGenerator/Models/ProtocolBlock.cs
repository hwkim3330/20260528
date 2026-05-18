using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public abstract class ProtocolBlock : INotifyPropertyChanged
{
    protected byte[] _bytes = Array.Empty<byte>();
    protected bool _suppressNotify;

    public abstract ProtocolType Type { get; }
    public abstract string DisplayName { get; }
    public abstract string IconKey { get; }

    public byte[] Bytes
    {
        get => _bytes;
        protected set
        {
            _bytes = value;
            OnPropertyChanged();
        }
    }

    public int ByteLength => _bytes.Length;
    public int StartOffset { get; set; }

    protected abstract void RecomputeBytes();

    public virtual ValidationResult ValidateAgainst(ProtocolBlock? next)
        => ValidationResult.None;

    public virtual void UpdateDerivedFields(byte[] prefix, byte[] suffix) { }

    public abstract void ImportBytes(byte[] fullPacket, int startOffset);

    protected static ushort ReadUInt16BE(byte[] data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    protected static uint ReadUInt32BE(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    protected static void WriteUInt16BE(byte[] data, int offset, ushort value)
    {
        data[offset]     = (byte)(value >> 8);
        data[offset + 1] = (byte)(value & 0xFF);
    }

    protected static void WriteUInt32BE(byte[] data, int offset, uint value)
    {
        data[offset]     = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)(value & 0xFF);
    }

    protected static byte[] ParseMac(string mac)
    {
        var parts = mac.Replace("-", ":").Split(':');
        if (parts.Length != 6) return new byte[6];
        var result = new byte[6];
        for (int i = 0; i < 6; i++)
            result[i] = Convert.ToByte(parts[i], 16);
        return result;
    }

    protected static string FormatMac(byte[] mac)
    {
        if (mac.Length < 6) return "00:00:00:00:00:00";
        return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
    }

    protected static byte[] ParseIPv4(string ip)
    {
        try
        {
            return System.Net.IPAddress.Parse(ip).GetAddressBytes();
        }
        catch
        {
            return new byte[4];
        }
    }

    protected static string FormatIPv4(byte[] ip)
    {
        if (ip.Length < 4) return "0.0.0.0";
        return string.Join(".", ip.Take(4).Select(b => b.ToString()));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (!_suppressNotify)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
