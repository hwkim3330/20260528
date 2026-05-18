using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using PacketDotNet;
using SharpPcap;

namespace EthernetPacketGenerator.ViewModels;

public class CaptureViewModel : ViewModelBase
{
    public ObservableCollection<CaptureInterfaceItem> Interfaces { get; } = new();
    public ObservableCollection<CaptureRow> Packets { get; } = new();
    public ICollectionView FilteredPackets { get; }

    public ObservableCollection<CaptureInterfaceFilterItem> InterfaceFilters { get; } = new();
    private readonly HashSet<string> _knownFilterInterfaces = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ILiveDevice> _activeDevices = new();
    private int _seqNo;
    private DateTime _captureStart;

    private CaptureRow? _selectedPacket;
    public CaptureRow? SelectedPacket
    {
        get => _selectedPacket;
        set
        {
            if (SetProperty(ref _selectedPacket, value))
            {
                OnPropertyChanged(nameof(SelectedPacketDetailText));
                OnPropertyChanged(nameof(SelectedPacketHexDump));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string SelectedPacketDetailText => SelectedPacket?.DetailText ?? "Select a packet to inspect decoded fields.";
    public string SelectedPacketHexDump => SelectedPacket?.HexDump ?? string.Empty;

    private string _displayFilter = string.Empty;
    public string DisplayFilter
    {
        get => _displayFilter;
        set
        {
            if (SetProperty(ref _displayFilter, value))
                FilteredPackets.Refresh();
        }
    }

    private bool _isCapturing;
    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            SetProperty(ref _isCapturing, value);
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                CommandManager.InvalidateRequerySuggested,
                DispatcherPriority.Background);
        }
    }

    private int _totalPackets;
    public int TotalPackets
    {
        get => _totalPackets;
        private set => SetProperty(ref _totalPackets, value);
    }

    private string _protocolSummary = "ARP 0  IPv4 0  IPv6 0  TCP 0  UDP 0  ICMP 0";
    public string ProtocolSummary
    {
        get => _protocolSummary;
        private set => SetProperty(ref _protocolSummary, value);
    }

    private string _statusText = "Ready. Select one or more interfaces, then press Start.";
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand RefreshInterfacesCommand { get; }
    public ICommand SetFilterCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand CopyDetailCommand { get; }
    public ICommand CopyHexCommand { get; }

    public CaptureViewModel()
    {
        FilteredPackets = CollectionViewSource.GetDefaultView(Packets);
        FilteredPackets.Filter = FilterPacket;

        StartCommand = new RelayCommand(Start, () => !IsCapturing && Interfaces.Any(i => i.IsSelected));
        StopCommand = new RelayCommand(Stop, () => IsCapturing);
        ClearCommand = new RelayCommand(Clear);
        RefreshInterfacesCommand = new RelayCommand(LoadInterfaces, () => !IsCapturing);
        SetFilterCommand = new RelayCommand(value => DisplayFilter = value?.ToString() ?? string.Empty);
        ClearFilterCommand = new RelayCommand(() => DisplayFilter = string.Empty);
        CopyDetailCommand = new RelayCommand(
            () => System.Windows.Clipboard.SetText(SelectedPacketDetailText),
            () => SelectedPacket != null);
        CopyHexCommand = new RelayCommand(
            () => System.Windows.Clipboard.SetText(SelectedPacketHexDump),
            () => SelectedPacket != null);

        LoadInterfaces();
    }

    private void LoadInterfaces()
    {
        Interfaces.Clear();

        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var dev in CaptureDeviceList.Instance)
            {
                var mac = dev.MacAddress?.GetAddressBytes();
                var nic = mac?.Length == 6
                    ? nics.FirstOrDefault(n => n.GetPhysicalAddress().GetAddressBytes().SequenceEqual(mac))
                    : null;

                var name = nic?.Name ?? dev.Name;
                var state = nic?.OperationalStatus == OperationalStatus.Up ? "up" : "down";
                var desc = string.IsNullOrWhiteSpace(dev.Description) ? dev.Name : dev.Description;

                Interfaces.Add(new CaptureInterfaceItem
                {
                    Device = dev,
                    Name = name,
                    Description = desc,
                    State = state,
                    DisplayName = $"{name}  {state.ToUpperInvariant()}  {dev.MacAddress}"
                });
            }

            foreach (var item in Interfaces)
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CaptureInterfaceItem.IsSelected))
                        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                            CommandManager.InvalidateRequerySuggested,
                            DispatcherPriority.Background);
                };

            foreach (var item in Interfaces.Where(i => i.State == "up").Take(1))
                item.IsSelected = true;

            StatusText = $"Loaded {Interfaces.Count} capture interfaces.";
        }
        catch (Exception ex)
        {
            StatusText = $"Interface load failed: {ex.Message}";
        }
    }

    private void Start()
    {
        var selected = Interfaces.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        try
        {
            _seqNo = 0;
            _captureStart = DateTime.Now;
            _activeDevices.Clear();

            foreach (var item in selected)
            {
                var dev = item.Device;
                dev.OnPacketArrival += OnPacketArrival;
                dev.Open(DeviceModes.Promiscuous, 1000);
                dev.StartCapture();
                _activeDevices.Add(dev);
            }

            IsCapturing = true;
            StatusText = $"Capturing on {selected.Count} interface(s): {string.Join(", ", selected.Select(i => i.Name))}";
        }
        catch (Exception ex)
        {
            Stop();
            StatusText = $"Capture start failed: {ex.Message}";
        }
    }

    public void StartCapture(IEnumerable<string>? interfaceNames)
    {
        if (IsCapturing) Stop();

        var names = interfaceNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in Interfaces)
            item.IsSelected = names == null || names.Count == 0
                ? item.IsSelected
                : names.Contains(item.Name) || names.Contains(item.Description) || names.Contains(item.Device.Name);

        Start();
    }

    public void StopCapture() => Stop();

    public void ClearCapture() => Clear();

    public List<CaptureRow> GetPacketsSnapshot(int max = 500) =>
        Packets.Reverse().Take(max).Reverse().ToList();

    public List<object> GetInterfaceSnapshot() =>
        Interfaces.Select(i => new
        {
            i.Name,
            i.Description,
            i.DisplayName,
            i.State,
            i.IsSelected
        }).Cast<object>().ToList();

    private void Stop()
    {
        foreach (var dev in _activeDevices.ToList())
        {
            try { dev.StopCapture(); } catch { }
            try { dev.OnPacketArrival -= OnPacketArrival; } catch { }
            try { dev.Close(); } catch { }
        }

        _activeDevices.Clear();
        IsCapturing = false;
        StatusText = $"Stopped. {TotalPackets} packets captured.";
    }

    private void Clear()
    {
        Packets.Clear();
        SelectedPacket = null;
        TotalPackets = 0;
        _seqNo = 0;
        InterfaceFilters.Clear();
        _knownFilterInterfaces.Clear();
        UpdateProtocolSummary();
        StatusText = "Cleared.";
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        var raw = e.GetPacket();
        var iface = ResolveInterfaceName(sender as ILiveDevice);
        var row = ParseRow(raw, iface);

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Packets.Count >= 10000) Packets.RemoveAt(0);
            Packets.Add(row);
            TotalPackets++;
            UpdateProtocolSummary();

            if (!string.IsNullOrEmpty(iface) && _knownFilterInterfaces.Add(iface))
            {
                var filterItem = new CaptureInterfaceFilterItem { InterfaceName = iface };
                filterItem.PropertyChanged += (_, _) => FilteredPackets.Refresh();
                InterfaceFilters.Add(filterItem);
            }
        }, DispatcherPriority.Background);
    }

    private void UpdateProtocolSummary()
    {
        var grouped = Packets
            .GroupBy(p => p.Protocol)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        int Get(string key) => grouped.TryGetValue(key, out var count) ? count : 0;
        ProtocolSummary = $"ARP {Get("ARP")}  IPv4 {Get("IPv4")}  IPv6 {Get("IPv6")}  TCP {Get("TCP")}  UDP {Get("UDP")}  ICMP {Get("ICMP")}";
    }

    private string ResolveInterfaceName(ILiveDevice? dev)
    {
        if (dev == null) return "unknown";
        return Interfaces.FirstOrDefault(i => ReferenceEquals(i.Device, dev))?.Name
               ?? dev.Description
               ?? dev.Name;
    }

    private bool FilterPacket(object obj)
    {
        if (obj is not CaptureRow row) return false;

        // Interface filter: if any items exist, hide rows whose interface is unchecked
        if (InterfaceFilters.Count > 0)
        {
            var match = InterfaceFilters.FirstOrDefault(f =>
                string.Equals(f.InterfaceName, row.InterfaceName, StringComparison.OrdinalIgnoreCase));
            if (match != null && !match.IsChecked) return false;
        }

        var filter = DisplayFilter.Trim().ToLowerInvariant();
        if (filter.Length == 0) return true;

        foreach (var token in filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith("mac:"))
            {
                var value = token[4..];
                if (!row.SrcMac.ToLowerInvariant().Contains(value) && !row.DstMac.ToLowerInvariant().Contains(value))
                    return false;
            }
            else if (token.StartsWith("ip:"))
            {
                var value = token[3..];
                if (!row.Source.ToLowerInvariant().Contains(value) && !row.Destination.ToLowerInvariant().Contains(value))
                    return false;
            }
            else if (token.StartsWith("port:"))
            {
                var value = token[5..];
                if (!row.Source.ToLowerInvariant().Contains($":{value}") &&
                    !row.Destination.ToLowerInvariant().Contains($":{value}") &&
                    !row.Info.ToLowerInvariant().Contains($"port: {value}") &&
                    !row.Info.ToLowerInvariant().Contains($"port {value}"))
                    return false;
            }
            else if (!row.SearchText.Contains(token))
            {
                return false;
            }
        }

        return true;
    }

    private CaptureRow ParseRow(RawCapture raw, string interfaceName)
    {
        int no = Interlocked.Increment(ref _seqNo);
        double elapsed = (DateTime.Now - _captureStart).TotalSeconds;
        string srcMac = string.Empty;
        string dstMac = string.Empty;
        string source = string.Empty;
        string destination = string.Empty;
        string protocol = "Ethernet";
        string info = string.Empty;
        int length = raw.Data.Length;
        var detail = new StringBuilder();

        detail.AppendLine($"Frame {no}");
        detail.AppendLine($"  Interface: {interfaceName}");
        detail.AppendLine($"  Arrival Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.ffff}");
        detail.AppendLine($"  Time Since Start: {elapsed:F6} s");
        detail.AppendLine($"  Length: {length} bytes");

        try
        {
            var packet = Packet.ParsePacket(raw.LinkLayerType, raw.Data);
            if (packet is EthernetPacket eth)
            {
                srcMac = FormatMac(eth.SourceHardwareAddress?.ToString());
                dstMac = FormatMac(eth.DestinationHardwareAddress?.ToString());
                source = srcMac;
                destination = dstMac;

                detail.AppendLine();
                detail.AppendLine("Ethernet II");
                detail.AppendLine($"  Destination: {dstMac}");
                detail.AppendLine($"  Source: {srcMac}");
                detail.AppendLine($"  Type: 0x{(ushort)eth.Type:X4} ({eth.Type})");

                if (eth.PayloadPacket is ArpPacket arp)
                {
                    protocol = "ARP";
                    source = arp.SenderProtocolAddress?.ToString() ?? srcMac;
                    destination = arp.TargetProtocolAddress?.ToString() ?? dstMac;
                    info = $"Who has {destination}? Tell {source}";

                    detail.AppendLine();
                    detail.AppendLine("Address Resolution Protocol");
                    detail.AppendLine($"  Operation: {arp.Operation}");
                    detail.AppendLine($"  Sender MAC: {FormatMac(arp.SenderHardwareAddress?.ToString())}");
                    detail.AppendLine($"  Sender IP: {source}");
                    detail.AppendLine($"  Target MAC: {FormatMac(arp.TargetHardwareAddress?.ToString())}");
                    detail.AppendLine($"  Target IP: {destination}");
                }
                else if (eth.PayloadPacket is IPv4Packet ipv4)
                {
                    var srcIp = ipv4.SourceAddress?.ToString() ?? "?";
                    var dstIp = ipv4.DestinationAddress?.ToString() ?? "?";
                    protocol = "IPv4";
                    source = srcIp;
                    destination = dstIp;
                    info = $"{srcIp} -> {dstIp}";

                    detail.AppendLine();
                    detail.AppendLine("Internet Protocol Version 4");
                    detail.AppendLine($"  Source: {srcIp}");
                    detail.AppendLine($"  Destination: {dstIp}");
                    detail.AppendLine($"  Protocol: {ipv4.Protocol}");
                    detail.AppendLine($"  TTL: {ipv4.TimeToLive}");

                    if (ipv4.PayloadPacket is UdpPacket udp)
                    {
                        protocol = "UDP";
                        source = $"{srcIp}:{udp.SourcePort}";
                        destination = $"{dstIp}:{udp.DestinationPort}";
                        info = $"{source} -> {destination}  Len={udp.Length}";

                        detail.AppendLine();
                        detail.AppendLine("User Datagram Protocol");
                        detail.AppendLine($"  Source Port: {udp.SourcePort}");
                        detail.AppendLine($"  Destination Port: {udp.DestinationPort}");
                        detail.AppendLine($"  Length: {udp.Length}");
                    }
                    else if (ipv4.PayloadPacket is TcpPacket tcp)
                    {
                        protocol = "TCP";
                        source = $"{srcIp}:{tcp.SourcePort}";
                        destination = $"{dstIp}:{tcp.DestinationPort}";
                        var flags = TcpFlags(tcp);
                        info = $"{source} -> {destination}" + (flags.Length > 0 ? $" [{flags}]" : string.Empty);

                        detail.AppendLine();
                        detail.AppendLine("Transmission Control Protocol");
                        detail.AppendLine($"  Source Port: {tcp.SourcePort}");
                        detail.AppendLine($"  Destination Port: {tcp.DestinationPort}");
                        detail.AppendLine($"  Sequence Number: {tcp.SequenceNumber}");
                        detail.AppendLine($"  Acknowledgment Number: {tcp.AcknowledgmentNumber}");
                        detail.AppendLine($"  Flags: {flags}");
                    }
                    else if (ipv4.PayloadPacket is IcmpV4Packet icmp)
                    {
                        protocol = "ICMP";
                        info = $"{srcIp} -> {dstIp}  Type={icmp.TypeCode}";
                        detail.AppendLine();
                        detail.AppendLine("Internet Control Message Protocol");
                        detail.AppendLine($"  Type/Code: {icmp.TypeCode}");
                    }
                }
                else if (eth.PayloadPacket is IPv6Packet ipv6)
                {
                    protocol = "IPv6";
                    source = ipv6.SourceAddress?.ToString() ?? srcMac;
                    destination = ipv6.DestinationAddress?.ToString() ?? dstMac;
                    info = $"{source} -> {destination}";

                    detail.AppendLine();
                    detail.AppendLine("Internet Protocol Version 6");
                    detail.AppendLine($"  Source: {source}");
                    detail.AppendLine($"  Destination: {destination}");
                    detail.AppendLine($"  Next Header: {ipv6.NextHeader}");
                    detail.AppendLine($"  Hop Limit: {ipv6.HopLimit}");
                }
            }
        }
        catch (Exception ex)
        {
            info = $"Decode failed: {ex.Message}";
            detail.AppendLine();
            detail.AppendLine(info);
        }

        return new CaptureRow
        {
            No = no,
            Time = elapsed.ToString("F6"),
            InterfaceName = interfaceName,
            SrcMac = srcMac,
            DstMac = dstMac,
            Source = string.IsNullOrEmpty(source) ? srcMac : source,
            Destination = string.IsNullOrEmpty(destination) ? dstMac : destination,
            Protocol = protocol,
            Length = length,
            Info = info,
            DetailText = detail.ToString(),
            HexDump = BuildHexDump(raw.Data),
            RawData = raw.Data.ToArray()
        };
    }

    private static string FormatMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var hex = value.Replace(":", "").Replace("-", "").ToUpperInvariant();
        return hex.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
            : value;
    }

    private static string TcpFlags(TcpPacket tcp)
    {
        var flags = new List<string>();
        try
        {
            if (tcp.Synchronize) flags.Add("SYN");
            if (tcp.Acknowledgment) flags.Add("ACK");
            if (tcp.Finished) flags.Add("FIN");
            if (tcp.Reset) flags.Add("RST");
            if (tcp.Push) flags.Add("PSH");
            if (tcp.Urgent) flags.Add("URG");
        }
        catch { }
        return string.Join(", ", flags);
    }

    private static string BuildHexDump(byte[] data)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            var len = Math.Min(16, data.Length - offset);
            var hex = new StringBuilder();
            var ascii = new StringBuilder();

            for (int i = 0; i < 16; i++)
            {
                if (i < len)
                {
                    var b = data[offset + i];
                    hex.Append($"{b:X2} ");
                    ascii.Append(b >= 32 && b <= 126 ? (char)b : '.');
                }
                else
                {
                    hex.Append("   ");
                    ascii.Append(' ');
                }
            }

            sb.AppendLine($"{offset:X4}  {hex} {ascii}");
        }
        return sb.ToString();
    }
}
