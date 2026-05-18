using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public class PacketItem : INotifyPropertyChanged
{
    private string _name = "Packet";
    private HashSet<string> _outgoingInterfaceNames = new();  // empty = use default (all IsActive)
    private byte[]? _cachedBytes;
    private List<ValidationResult> _validationResults = new();
    private bool _isComputing;
    private bool _pendingInvalidate;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 이 패킷을 전송할 인터페이스 이름 집합.
    /// 비어있으면 Default 동작 (IsActive 전체, 없으면 IsDefault 하나).
    /// </summary>
    public HashSet<string> OutgoingInterfaceNames
    {
        get => _outgoingInterfaceNames;
        set { _outgoingInterfaceNames = value; OnPropertyChanged(); OnPropertyChanged(nameof(OutgoingInterfaceDisplay)); }
    }

    /// <summary>PacketList Interface 컬럼 표시용 요약 문자열.</summary>
    public string OutgoingInterfaceDisplay =>
        _outgoingInterfaceNames.Count == 0
            ? "(Default)"
            : string.Join(", ", _outgoingInterfaceNames);

    public void ToggleOutgoingInterface(string shortName)
    {
        if (!_outgoingInterfaceNames.Remove(shortName))
            _outgoingInterfaceNames.Add(shortName);
        OnOutgoingInterfaceChanged();
    }

    public void OnOutgoingInterfaceChanged()
    {
        OnPropertyChanged(nameof(OutgoingInterfaceNames));
        OnPropertyChanged(nameof(OutgoingInterfaceDisplay));
    }

    public ObservableCollection<ProtocolBlock> Blocks { get; } = new();

    public List<ValidationResult> ValidationResults
    {
        get => _validationResults;
        private set { _validationResults = value; OnPropertyChanged(); }
    }

    public PacketItem()
    {
        Blocks.CollectionChanged += (_, e) =>
        {
            // Subscribe new blocks, unsubscribe removed blocks
            if (e.NewItems != null)
                foreach (ProtocolBlock b in e.NewItems)
                    b.PropertyChanged += OnBlockPropertyChanged;
            if (e.OldItems != null)
                foreach (ProtocolBlock b in e.OldItems)
                    b.PropertyChanged -= OnBlockPropertyChanged;
            Invalidate();
        };
    }

    private void OnBlockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isComputing)
            _pendingInvalidate = true;  // defer until ComputeFullBytes finishes
        else
            Invalidate();
    }

    public byte[] FullBytes
    {
        get
        {
            if (_cachedBytes == null)
                _cachedBytes = ComputeFullBytes();
            return _cachedBytes;
        }
    }

    public int TotalLength => FullBytes.Length;

    // ── Summary properties for packet list display ──
    public string SrcMac => (Blocks.FirstOrDefault(b => b is EthernetBlock) as EthernetBlock)?.SrcMac ?? "-";
    public string DstMac => (Blocks.FirstOrDefault(b => b is EthernetBlock) as EthernetBlock)?.DstMac ?? "-";

    public string ProtocolSummary
    {
        get
        {
            var parts = new List<string>();
            foreach (var b in Blocks)
            {
                string desc = b.Type switch
                {
                    ProtocolType.Ethernet   => "ETH",
                    ProtocolType.ARP        => "ARP",
                    ProtocolType.VLAN       => "VLAN",
                    ProtocolType.IPv4       => "IPv4",
                    ProtocolType.ICMP       => "ICMP",
                    ProtocolType.TCP        => "TCP",
                    ProtocolType.UDP        => "UDP",
                    ProtocolType.RawPayload => "RAW",
                    _ => b.Type.ToString()
                };
                parts.Add(desc);
            }
            return parts.Count > 0 ? string.Join(" / ", parts) + $"  [{TotalLength}B]" : "-";
        }
    }

    public string PacketDescription
    {
        get
        {
            var eth = Blocks.FirstOrDefault(b => b is EthernetBlock) as EthernetBlock;
            var ipv4 = Blocks.FirstOrDefault(b => b is IPv4Block) as IPv4Block;
            var tcp  = Blocks.FirstOrDefault(b => b is TcpBlock)  as TcpBlock;
            var udp  = Blocks.FirstOrDefault(b => b is UdpBlock)  as UdpBlock;
            var arp  = Blocks.FirstOrDefault(b => b is ArpBlock)  as ArpBlock;

            if (arp != null)
                return $"ARP {(arp.Operation == 1 ? "Request" : "Reply")}  {arp.SenderProtoAddr} → {arp.TargetProtoAddr}";
            if (ipv4 != null)
            {
                string l4 = "";
                if (tcp != null)  l4 = $"  TCP {tcp.SrcPort}→{tcp.DstPort} [{BuildTcpFlagStr(tcp)}]";
                else if (udp != null) l4 = $"  UDP {udp.SrcPort}→{udp.DstPort}";
                return $"{ipv4.SrcIP} → {ipv4.DstIP}{l4}";
            }
            if (eth != null)
                return $"{eth.SrcMac} → {eth.DstMac}";
            return $"{TotalLength} bytes";
        }
    }

    private static string BuildTcpFlagStr(TcpBlock t)
    {
        var f = new List<string>();
        if (t.FlagSYN) f.Add("SYN");
        if (t.FlagACK) f.Add("ACK");
        if (t.FlagFIN) f.Add("FIN");
        if (t.FlagRST) f.Add("RST");
        if (t.FlagPSH) f.Add("PSH");
        return f.Count > 0 ? string.Join(",", f) : "None";
    }

    public void Invalidate()
    {
        if (_isComputing) { _pendingInvalidate = true; return; }
        _cachedBytes = null;
        OnPropertyChanged(nameof(FullBytes));
        OnPropertyChanged(nameof(TotalLength));
        FireSummaryChanged();
        Validate();
    }

    private void FireSummaryChanged()
    {
        OnPropertyChanged(nameof(TotalLength));
        OnPropertyChanged(nameof(SrcMac));
        OnPropertyChanged(nameof(DstMac));
        OnPropertyChanged(nameof(ProtocolSummary));
        OnPropertyChanged(nameof(PacketDescription));
        OnPropertyChanged(nameof(Name));
    }

    private byte[] ComputeFullBytes()
    {
        if (Blocks.Count == 0) return Array.Empty<byte>();

        _isComputing = true;
        _pendingInvalidate = false;
        byte[] raw;
        try
        {
            var snapshot = Blocks.ToList();

            int offset = 0;
            foreach (var block in snapshot)
            {
                block.StartOffset = offset;
                offset += block.ByteLength;
            }

            raw = new byte[offset];
            int pos = 0;
            foreach (var block in snapshot)
            {
                Array.Copy(block.Bytes, 0, raw, pos, block.ByteLength);
                pos += block.ByteLength;
            }

            pos = 0;
            foreach (var block in snapshot)
            {
                var prefix = raw.Take(pos).ToArray();
                var suffix = raw.Skip(pos + block.ByteLength).ToArray();
                block.UpdateDerivedFields(prefix, suffix);
                Array.Copy(block.Bytes, 0, raw, pos, Math.Min(block.ByteLength, block.Bytes.Length));
                pos += block.ByteLength;
            }
        }
        finally
        {
            _isComputing = false;
        }

        // Fire deferred notifications after derived fields (checksums etc.) are written
        if (_pendingInvalidate)
        {
            _pendingInvalidate = false;
            _cachedBytes = raw;
            FireSummaryChanged();
        }

        return raw;
    }

    public void ImportFromBytes(byte[] data)
    {
        // Reverse-parse: distribute bytes back to blocks
        int offset = 0;
        foreach (var block in Blocks)
        {
            if (offset >= data.Length) break;
            block.ImportBytes(data, offset);
            offset += block.ByteLength;
        }
        Invalidate();
    }

    private void Validate()
    {
        var results = new List<ValidationResult>();
        var blocks = Blocks.ToList();

        for (int i = 0; i < blocks.Count; i++)
        {
            var next = i + 1 < blocks.Count ? blocks[i + 1] : null;
            var result = blocks[i].ValidateAgainst(next);
            if (result.Severity != ValidationSeverity.None)
            {
                results.Add(new ValidationResult
                {
                    Severity = result.Severity,
                    Message = result.Message,
                    BlockIndex = i
                });
            }
        }

        ValidationResults = results;
    }

    public static ProtocolBlock CreateBlock(ProtocolType type) => type switch
    {
        ProtocolType.Ethernet   => new EthernetBlock(),
        ProtocolType.ARP        => new ArpBlock(),
        ProtocolType.IPv4       => new IPv4Block(),
        ProtocolType.ICMP       => new IcmpBlock(),
        ProtocolType.TCP        => new TcpBlock(),
        ProtocolType.UDP        => new UdpBlock(),
        ProtocolType.VLAN       => new VlanBlock(),
        ProtocolType.RawPayload => new RawPayloadBlock(),
        _ => new RawPayloadBlock()
    };

    public void InsertBlock(ProtocolType type, int index)
    {
        var block = CreateBlock(type);

        int actualIndex;
        if (index < 0 || index >= Blocks.Count)
        {
            actualIndex = Blocks.Count;
            Blocks.Add(block);
        }
        else
        {
            actualIndex = index;
            Blocks.Insert(index, block);
        }

        AutoUpdateEncapsulation(actualIndex);
    }

    private void AutoUpdateEncapsulation(int insertedIndex)
    {
        if (insertedIndex <= 0 || Blocks.Count < 2) return;

        var prev = Blocks[insertedIndex - 1];
        var curr = Blocks[insertedIndex];

        if (prev is EthernetBlock eth)
        {
            eth.EtherType = curr.Type switch
            {
                ProtocolType.IPv4 => (ushort)EtherTypeValue.IPv4,
                ProtocolType.ARP  => (ushort)EtherTypeValue.ARP,
                ProtocolType.VLAN => (ushort)EtherTypeValue.VLAN,
                ProtocolType.IPv6 => (ushort)EtherTypeValue.IPv6,
                _ => eth.EtherType
            };
        }
        else if (prev is IPv4Block ip)
        {
            ip.Protocol = curr.Type switch
            {
                ProtocolType.ICMP => (byte)IpProtocolValue.ICMP,
                ProtocolType.TCP  => (byte)IpProtocolValue.TCP,
                ProtocolType.UDP  => (byte)IpProtocolValue.UDP,
                _ => ip.Protocol
            };
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
