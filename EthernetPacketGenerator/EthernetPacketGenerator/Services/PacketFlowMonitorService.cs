using System.Text;
using SharpPcap;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

// ── Configuration ─────────────────────────────────────────────────────────────

public class PacketFlowMonitorConfig
{
    public ILiveDevice? TxDevice    { get; init; }
    public ILiveDevice? Port1Device { get; init; }
    public ILiveDevice? Port2Device { get; init; }
    public ILiveDevice? Port3Device { get; init; }

    public string TxInterfaceName    { get; init; } = string.Empty;
    public string Port1InterfaceName { get; init; } = string.Empty;
    public string Port2InterfaceName { get; init; } = string.Empty;
    public string Port3InterfaceName { get; init; } = string.Empty;

    // Parsed filter values (set by service from string fields)
    internal byte[]? DstMacBytes { get; set; }
    internal byte[]? SrcMacBytes { get; set; }
    internal ushort  EtherTypeFilter { get; set; }   // 0 = any
    internal byte[]? SignatureBytes  { get; set; }
    internal ushort  UdpSrcPortFilter { get; set; }  // 0 = any
    internal ushort  UdpDstPortFilter { get; set; }

    // Raw string fields used by ViewModel
    public string DstMac    { get; init; } = string.Empty;
    public string SrcMac    { get; init; } = string.Empty;
    public string EtherType { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public string UdpSrcPort{ get; init; } = string.Empty;
    public string UdpDstPort{ get; init; } = string.Empty;

    public int  CaptureTimeoutSec { get; init; } = 5;
    public int  MaxDetailPackets  { get; init; } = 1000;
}

// ── Per-interface capture state ───────────────────────────────────────────────

public class FlowInterfaceState
{
    public string Role          { get; init; } = string.Empty;  // TX / RX
    public int?   SwitchPort    { get; init; }                  // null for TX
    public string InterfaceName { get; init; } = string.Empty;
    public ILiveDevice? Device  { get; init; }

    private int _matched;
    private int _total;
    public int MatchedCount       => _matched;
    public int TotalCapturedCount => _total;

    private readonly object _listLock = new();
    public List<CapturedPacketDetailRow> Packets { get; } = new();

    public void AddPacket(CapturedPacketDetailRow row, bool countAsMatched)
    {
        System.Threading.Interlocked.Increment(ref _total);
        if (countAsMatched) System.Threading.Interlocked.Increment(ref _matched);
        lock (_listLock)
        {
            if (Packets.Count < 1000)
                Packets.Add(row);
        }
    }
}

// ── Result ────────────────────────────────────────────────────────────────────

public class PacketFlowMonitorResult
{
    public FlowInterfaceState? TxState    { get; init; }
    public FlowInterfaceState? Port1State { get; init; }
    public FlowInterfaceState? Port2State { get; init; }
    public FlowInterfaceState? Port3State { get; init; }
    public bool WasCancelled { get; init; }
}

// ── Service ───────────────────────────────────────────────────────────────────

public class PacketFlowMonitorService
{
    private volatile bool _stopRequested;

    public void Stop() => _stopRequested = true;

    public async Task<PacketFlowMonitorResult> RunAsync(
        PacketFlowMonitorConfig config,
        Action<CapturedPacketDetailRow, FlowInterfaceState> onPacket,
        CancellationToken ct = default)
    {
        _stopRequested = false;

        // Parse filter
        ParseConfig(config);

        // Build active interface states
        var states = BuildStates(config);
        if (states.Count == 0)
            return new PacketFlowMonitorResult { WasCancelled = true };

        int counter = 0;

        // Open and start each device
        var openedDevices = new List<ILiveDevice>();
        foreach (var st in states)
        {
            if (st.Device == null) continue;
            try
            {
                var dev = st.Device;
                dev.Open(DeviceModes.None, 500);
                openedDevices.Add(dev);

                // Capture the loop variable
                var capturedState = st;
                dev.OnPacketArrival += (_, e) =>
                {
                    try
                    {
                        var raw  = e.GetPacket();
                        var data = raw.Data;
                        bool match = MatchesFilter(data, config);
                        int  no   = System.Threading.Interlocked.Increment(ref counter);
                        var  row  = BuildDetailRow(no, raw.Timeval.Date, capturedState, data, match);
                        capturedState.AddPacket(row, match);
                        onPacket(row, capturedState);
                    }
                    catch { }
                };
                dev.StartCapture();
            }
            catch { /* device open failed - skip */ }
        }

        // Wait for timeout, stop signal, or cancellation
        var deadline = DateTime.UtcNow.AddSeconds(config.CaptureTimeoutSec);
        while (DateTime.UtcNow < deadline && !_stopRequested && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, CancellationToken.None);
        }

        // Stop and close all devices
        foreach (var dev in openedDevices)
        {
            try { dev.StopCapture(); } catch { }
            try { dev.Close();       } catch { }
        }

        var txSt    = states.FirstOrDefault(s => s.Role == "TX");
        var p1State = states.FirstOrDefault(s => s.SwitchPort == 1);
        var p2State = states.FirstOrDefault(s => s.SwitchPort == 2);
        var p3State = states.FirstOrDefault(s => s.SwitchPort == 3);

        return new PacketFlowMonitorResult
        {
            TxState    = txSt,
            Port1State = p1State,
            Port2State = p2State,
            Port3State = p3State,
            WasCancelled = ct.IsCancellationRequested
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static List<FlowInterfaceState> BuildStates(PacketFlowMonitorConfig config)
    {
        var list = new List<FlowInterfaceState>();
        if (config.TxDevice != null)
            list.Add(new FlowInterfaceState { Role = "TX",  SwitchPort = null, InterfaceName = config.TxInterfaceName,    Device = config.TxDevice });
        if (config.Port1Device != null)
            list.Add(new FlowInterfaceState { Role = "RX",  SwitchPort = 1,    InterfaceName = config.Port1InterfaceName, Device = config.Port1Device });
        if (config.Port2Device != null)
            list.Add(new FlowInterfaceState { Role = "RX",  SwitchPort = 2,    InterfaceName = config.Port2InterfaceName, Device = config.Port2Device });
        if (config.Port3Device != null)
            list.Add(new FlowInterfaceState { Role = "RX",  SwitchPort = 3,    InterfaceName = config.Port3InterfaceName, Device = config.Port3Device });
        return list;
    }

    private static void ParseConfig(PacketFlowMonitorConfig cfg)
    {
        cfg.DstMacBytes       = ParseMac(cfg.DstMac);
        cfg.SrcMacBytes       = ParseMac(cfg.SrcMac);
        cfg.EtherTypeFilter   = ParseHex16(cfg.EtherType);
        cfg.SignatureBytes     = string.IsNullOrWhiteSpace(cfg.Signature) ? null : Encoding.ASCII.GetBytes(cfg.Signature);
        cfg.UdpSrcPortFilter  = ParsePort(cfg.UdpSrcPort);
        cfg.UdpDstPortFilter  = ParsePort(cfg.UdpDstPort);
    }

    private static byte[]? ParseMac(string mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var parts = mac.Split(':', '-');
        if (parts.Length != 6) return null;
        try { return parts.Select(p => Convert.ToByte(p, 16)).ToArray(); }
        catch { return null; }
    }

    private static ushort ParseHex16(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ushort.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : (ushort)0;
    }

    private static ushort ParsePort(string s)
        => ushort.TryParse(s?.Trim(), out var v) ? v : (ushort)0;

    // ── Packet match filter ───────────────────────────────────────────────────

    private static bool MatchesFilter(ReadOnlySpan<byte> data, PacketFlowMonitorConfig cfg)
    {
        if (data.Length < 14) return false;

        // Dst MAC
        if (cfg.DstMacBytes != null)
            for (int i = 0; i < 6; i++) if (data[i] != cfg.DstMacBytes[i]) return false;

        // Src MAC
        if (cfg.SrcMacBytes != null)
            for (int i = 0; i < 6; i++) if (data[6 + i] != cfg.SrcMacBytes[i]) return false;

        // EtherType
        if (cfg.EtherTypeFilter != 0)
        {
            ushort et = (ushort)((data[12] << 8) | data[13]);
            if (et != cfg.EtherTypeFilter) return false;
        }

        // UDP ports
        if (cfg.UdpSrcPortFilter != 0 || cfg.UdpDstPortFilter != 0)
        {
            ushort et = (ushort)((data[12] << 8) | data[13]);
            if (et != 0x0800 || data.Length < 34) return false;
            byte proto = data[23];
            if (proto != 17) return false;
            int ihl  = (data[14] & 0x0F) * 4;
            int l4   = 14 + ihl;
            if (data.Length < l4 + 8) return false;
            if (cfg.UdpSrcPortFilter != 0 && (ushort)((data[l4] << 8) | data[l4 + 1]) != cfg.UdpSrcPortFilter) return false;
            if (cfg.UdpDstPortFilter != 0 && (ushort)((data[l4 + 2] << 8) | data[l4 + 3]) != cfg.UdpDstPortFilter) return false;
        }

        // Signature (ASCII search in payload)
        if (cfg.SignatureBytes != null && cfg.SignatureBytes.Length > 0)
            if (!ContainsSequence(data, cfg.SignatureBytes)) return false;

        return true;
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { found = false; break; }
            if (found) return true;
        }
        return false;
    }

    // ── Build detail row ──────────────────────────────────────────────────────

    private static CapturedPacketDetailRow BuildDetailRow(
        int no, DateTime ts, FlowInterfaceState st, ReadOnlySpan<byte> data, bool isMatch)
    {
        string dstMac = data.Length >= 6
            ? $"{data[0]:X2}:{data[1]:X2}:{data[2]:X2}:{data[3]:X2}:{data[4]:X2}:{data[5]:X2}"
            : string.Empty;
        string srcMac = data.Length >= 12
            ? $"{data[6]:X2}:{data[7]:X2}:{data[8]:X2}:{data[9]:X2}:{data[10]:X2}:{data[11]:X2}"
            : string.Empty;
        string etStr = data.Length >= 14
            ? $"0x{(ushort)((data[12] << 8) | data[13]):X4}"
            : string.Empty;

        var (proto, src, dst, info) = DecodePacketBrief(data);

        return new CapturedPacketDetailRow
        {
            No            = no,
            Time          = ts.ToString("HH:mm:ss.ffffff"),
            Role          = st.Role,
            Port          = st.SwitchPort.HasValue ? $"Port {st.SwitchPort}" : "-",
            InterfaceName = st.InterfaceName,
            IsMatch       = isMatch,
            Direction     = st.Role == "TX" ? "Outgoing Observed" : "Received",
            Protocol      = proto,
            Source        = src,
            Destination   = dst,
            Length        = data.Length,
            DstMac        = dstMac,
            SrcMac        = srcMac,
            EtherTypeStr  = etStr,
            Info          = info,
            RawData       = data.ToArray()
        };
    }

    private static (string Proto, string Src, string Dst, string Info) DecodePacketBrief(ReadOnlySpan<byte> data)
    {
        if (data.Length < 14) return ("RAW", "-", "-", $"[{data.Length}B]");

        ushort et = (ushort)((data[12] << 8) | data[13]);
        string sm = $"{data[6]:X2}:{data[7]:X2}:{data[8]:X2}:{data[9]:X2}:{data[10]:X2}:{data[11]:X2}";
        string dm = $"{data[0]:X2}:{data[1]:X2}:{data[2]:X2}:{data[3]:X2}:{data[4]:X2}:{data[5]:X2}";

        if (et == 0x0800 && data.Length >= 34)
        {
            string si = $"{data[26]}.{data[27]}.{data[28]}.{data[29]}";
            string di = $"{data[30]}.{data[31]}.{data[32]}.{data[33]}";
            byte proto = data[23];
            int ihl = (data[14] & 0x0F) * 4;
            int l4  = 14 + ihl;
            if (proto == 17 && data.Length >= l4 + 8)
            {
                ushort sp = (ushort)((data[l4] << 8) | data[l4 + 1]);
                ushort dp = (ushort)((data[l4 + 2] << 8) | data[l4 + 3]);
                string sig = ExtractAsciiSnippet(data, l4 + 8, 32);
                return ("IPv4/UDP", $"{si}:{sp}", $"{di}:{dp}", $"{sp}→{dp}{(sig.Length > 0 ? $" [{sig}]" : "")}");
            }
            return ($"IPv4/0x{proto:X2}", si, di, $"{si}→{di}");
        }
        if (et == 0x0806) return ("ARP", sm, dm, "ARP");
        if (et == 0x86DD) return ("IPv6", sm, dm, "IPv6");
        return ($"0x{et:X4}", sm, dm, $"EtherType 0x{et:X4} [{data.Length}B]");
    }

    private static string ExtractAsciiSnippet(ReadOnlySpan<byte> data, int offset, int maxLen)
    {
        if (offset >= data.Length) return string.Empty;
        var sb = new StringBuilder();
        int end = Math.Min(offset + maxLen, data.Length);
        for (int i = offset; i < end; i++)
        {
            char c = (char)data[i];
            if (c >= 0x20 && c < 0x7F) sb.Append(c);
            else if (sb.Length > 0) break;
        }
        return sb.ToString();
    }
}
