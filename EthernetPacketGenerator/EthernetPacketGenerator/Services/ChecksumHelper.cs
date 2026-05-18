namespace EthernetPacketGenerator.Services;

public static class ChecksumHelper
{
    public static ushort InternetChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int end = offset + length;

        for (int i = offset; i < end - 1; i += 2)
            sum += (uint)((data[i] << 8) | data[i + 1]);

        if ((length & 1) != 0)
            sum += (uint)(data[end - 1] << 8);

        while (sum >> 16 != 0)
            sum = (sum & 0xFFFF) + (sum >> 16);

        return (ushort)~sum;
    }

    public static ushort PseudoHeaderChecksum(
        byte[] srcIp, byte[] dstIp, byte protocol, byte[] segment)
    {
        int pseudoLen = 12 + segment.Length;
        if (pseudoLen % 2 != 0) pseudoLen++;

        var pseudo = new byte[pseudoLen];
        Array.Copy(srcIp, 0, pseudo, 0, 4);
        Array.Copy(dstIp, 0, pseudo, 4, 4);
        pseudo[8] = 0;
        pseudo[9] = protocol;
        pseudo[10] = (byte)(segment.Length >> 8);
        pseudo[11] = (byte)(segment.Length & 0xFF);
        Array.Copy(segment, 0, pseudo, 12, segment.Length);

        return InternetChecksum(pseudo, 0, pseudoLen);
    }
}
