namespace EthernetPacketGenerator.Services;

/// <summary>
/// Calculates the on-wire transmission time for Ethernet frames.
///
/// Full Ethernet frame structure on the wire (802.3):
///   Preamble          7 bytes  (0x55 x7)
///   SFD               1 byte   (0xD5)
///   Destination MAC   6 bytes
///   Source MAC        6 bytes
///   EtherType/Length  2 bytes
///   Payload           variable (minimum 46 bytes to reach 64-byte minimum frame)
///   FCS/CRC           4 bytes
///   IFG               12 bytes (Inter-Frame Gap — idle time between frames)
///
/// Minimum frame size (excluding preamble/SFD/IFG): 64 bytes
/// Minimum total wire bytes per frame: 8 + 64 + 12 = 84 bytes
/// </summary>
public static class EthernetTiming
{
    private const int PreambleSfd = 8;   // 7 preamble + 1 SFD
    private const int Fcs         = 4;   // Frame Check Sequence
    private const int Ifg         = 12;  // Inter-Frame Gap
    private const int MinFrame    = 64;  // minimum Ethernet frame (header+payload+FCS)

    /// <summary>
    /// Returns the total on-wire byte count for a frame whose logical payload
    /// (everything from dst MAC through application data, excluding FCS) is
    /// <paramref name="payloadBytes"/> bytes long.
    /// </summary>
    public static int WireBytes(int payloadBytes)
    {
        // Frame body = payload + FCS; must be at least MinFrame bytes
        int frameBody = Math.Max(payloadBytes + Fcs, MinFrame);
        return PreambleSfd + frameBody + Ifg;
    }

    /// <summary>
    /// Calculates wire transmission time in microseconds at a given link speed.
    /// </summary>
    /// <param name="payloadBytes">Logical packet size (bytes)</param>
    /// <param name="linkSpeedMbps">Link speed in Mbit/s (default 1000 = 1 Gbps)</param>
    public static double WireTimeUs(int payloadBytes, double linkSpeedMbps = 1000.0)
    {
        double bits = WireBytes(payloadBytes) * 8.0;
        return bits / linkSpeedMbps;   // µs  (Mbps = bits/µs)
    }

    /// <summary>
    /// Calculates wire transmission time in milliseconds.
    /// </summary>
    public static double WireTimeMs(int payloadBytes, double linkSpeedMbps = 1000.0)
        => WireTimeUs(payloadBytes, linkSpeedMbps) / 1000.0;
}
