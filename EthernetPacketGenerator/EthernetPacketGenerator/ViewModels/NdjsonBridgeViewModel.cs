using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using Microsoft.Win32;

namespace EthernetPacketGenerator.ViewModels;

public sealed class NdjsonPreviewRow : ViewModelBase
{
    public int LineNumber { get; init; }
    public string State { get; init; } = "";
    public string Name { get; init; } = "";
    public string InterfaceName { get; init; } = "";
    public int Length { get; init; }
    public int RepeatCount { get; init; }
    public int IntervalMs { get; init; }
    public string Summary { get; init; } = "";
    public string Error { get; init; } = "";
    public PacketItem? Packet { get; init; }
}

public sealed class NdjsonBridgeViewModel : ViewModelBase
{
    private readonly NdjsonPacketImportService _importService = new();
    private readonly PacketListViewModel _packetList;
    private readonly SendViewModel _send;
    private readonly CaptureViewModel _capture;
    private readonly TestCaseManagerViewModel _testCases;
    private string _ndjsonText = "";
    private string _addressText = "self";
    private string _resolvedAddress = "";
    private string _resultText = "Result: not checked";
    private string _txInterfaceName = "";
    private string _rxInterfaceNames = "";
    private string _status = "Enter self/local or a peer MAC address, then load capture data.";

    public ObservableCollection<NdjsonPreviewRow> Rows { get; } = new();

    public string NdjsonText
    {
        get => _ndjsonText;
        set => SetProperty(ref _ndjsonText, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    public string ResolvedAddress
    {
        get => _resolvedAddress;
        private set => SetProperty(ref _resolvedAddress, value);
    }

    public string ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public string TxInterfaceName
    {
        get => _txInterfaceName;
        private set => SetProperty(ref _txInterfaceName, value);
    }

    public string RxInterfaceNames
    {
        get => _rxInterfaceNames;
        private set => SetProperty(ref _rxInterfaceNames, value);
    }

    public int ValidCount => Rows.Count(r => r.Packet != null);
    public int ErrorCount => Rows.Count(r => r.Packet == null);
    public CaptureViewModel Capture => _capture;
    public TestCaseManagerViewModel TestCases => _testCases;

    public ICommand OpenFileCommand { get; }
    public ICommand ParseCommand { get; }
    public ICommand ApplyCaptureAddressCommand { get; }
    public ICommand ClearCaptureAddressCommand { get; }
    public ICommand ReplaceSendListCommand { get; }
    public ICommand AppendSendListCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand SendListCommand => _send.SendListCommand;
    public ICommand StartCaptureCommand => _capture.StartCommand;
    public ICommand StopCaptureCommand => _capture.StopCommand;
    public ICommand ClearCaptureCommand => _capture.ClearCommand;
    public ICommand RefreshCaptureInterfacesCommand => _capture.RefreshInterfacesCommand;
    public ICommand ValidateCaptureCommand => _testCases.ValidateCaptureCommand;
    public ICommand ApplyScenarioSheetCommand => _testCases.ApplyScenarioSheetCommand;

    public NdjsonBridgeViewModel(
        PacketListViewModel packetList,
        SendViewModel send,
        CaptureViewModel capture,
        TestCaseManagerViewModel testCases)
    {
        _packetList = packetList;
        _send = send;
        _capture = capture;
        _testCases = testCases;

        OpenFileCommand = new RelayCommand(OpenFile);
        ParseCommand = new RelayCommand(Parse);
        ApplyCaptureAddressCommand = new RelayCommand(ApplyCaptureAddress);
        ClearCaptureAddressCommand = new RelayCommand(ClearCaptureAddress);
        ReplaceSendListCommand = new RelayCommand(() => LoadIntoPacketList(replace: true), () => ValidCount > 0);
        AppendSendListCommand = new RelayCommand(() => LoadIntoPacketList(replace: false), () => ValidCount > 0);
        ClearCommand = new RelayCommand(Clear);
        ApplyCaptureAddress();
    }

    private void ApplyCaptureAddress()
    {
        var clean = (AddressText ?? "").Trim();
        UpdateTxRxSummary();

        // IP address → apply ip: filter directly
        if (System.Net.IPAddress.TryParse(clean, out var ip) &&
            !clean.Equals("self", StringComparison.OrdinalIgnoreCase) &&
            !clean.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            ResolvedAddress = ip.ToString();
            _capture.DisplayFilter = $"ip:{ip}";
            EvaluateCaptureResultByIp(ip.ToString());
            return;
        }

        var mac = ResolveAddress(clean);
        ResolvedAddress = mac;

        if (string.IsNullOrWhiteSpace(mac) || mac == "00:00:00:00:00:00")
        {
            _capture.DisplayFilter = string.Empty;
            ResultText = "Result: FAIL - no address";
            Status = "No capture address resolved. Select a capture NIC or enter a peer MAC address.";
            return;
        }

        _capture.DisplayFilter = $"mac:{mac.ToLowerInvariant()}";
        EvaluateCaptureResult(mac);
    }

    private void EvaluateCaptureResultByIp(string ip)
    {
        var packets = _capture.GetPacketsSnapshot(5000);
        var matches = packets
            .Where(p => p.Source.Contains(ip, StringComparison.OrdinalIgnoreCase) ||
                        p.Destination.Contains(ip, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count > 0)
        {
            ResultText = $"Result: PASS - {matches.Count} packet(s) matched IP {ip}";
            Status = $"PASS. {matches.Count} packet(s) contain IP {ip} as source or destination.";
        }
        else
        {
            ResultText = $"Result: FAIL - no packets matched IP {ip}";
            Status = $"FAIL. No captured packets contain IP {ip}. Make sure capture is running.";
        }
    }

    private void ClearCaptureAddress()
    {
        AddressText = "";
        ResolvedAddress = "";
        ResultText = "Result: not checked";
        TxInterfaceName = "";
        RxInterfaceNames = "";
        _capture.DisplayFilter = string.Empty;
        Status = "Capture address filter cleared.";
    }

    private string ResolveAddress(string? value)
    {
        var clean = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean) ||
            clean.Equals("self", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return GetDefaultRxMac() ?? GetSelectedCaptureMac() ?? "00:00:00:00:00:00";
        }

        // IP address → resolve to MAC
        if (System.Net.IPAddress.TryParse(clean, out var ip))
        {
            // Check if it's one of our own local IPs
            var localMac = GetLocalMacForIp(ip);
            if (localMac != null) return localMac;

            // Try ARP table lookup for remote IPs
            var arpMac = ResolveIpToMacViaArp(ip);
            if (arpMac != null) return arpMac;

            Status = $"Could not resolve {clean} to MAC. Ping the device first, then try again.";
            return "00:00:00:00:00:00";
        }

        return NormalizeMac(clean);
    }

    private static string? GetLocalMacForIp(System.Net.IPAddress ip)
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.Equals(ip))
                {
                    var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                    if (bytes.Length == 6)
                        return string.Join(":", bytes.Select(b => b.ToString("X2")));
                }
            }
        }
        return null;
    }

    private static string? ResolveIpToMacViaArp(System.Net.IPAddress ip)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("arp", $"-a {ip}")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains(ip.ToString())) continue;
                var match = System.Text.RegularExpressions.Regex.Match(
                    line, @"([0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2}[-:][0-9a-fA-F]{2})");
                if (match.Success)
                    return match.Value.Replace("-", ":").ToUpperInvariant();
            }
        }
        catch { }
        return null;
    }

    private void EvaluateCaptureResult(string mac)
    {
        var selected = GetSelectedInterfaces();
        var tx = selected.FirstOrDefault();
        var rx = selected.Skip(1).ToList();
        var packets = _capture.GetPacketsSnapshot(5000);

        var matches = packets
            .Where(p => p.DstMac.Equals(mac, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var txHits = tx == null
            ? new List<CaptureRow>()
            : matches.Where(p => InterfaceMatches(p.InterfaceName, tx.Name)).ToList();

        var rxHits = rx.Count == 0
            ? matches.Where(p => tx == null || !InterfaceMatches(p.InterfaceName, tx.Name)).ToList()
            : matches.Where(p => rx.Any(i => InterfaceMatches(p.InterfaceName, i.Name))).ToList();

        var unexpectedRx = rx.Count == 0
            ? new List<CaptureRow>()
            : matches.Where(p => !rx.Any(i => InterfaceMatches(p.InterfaceName, i.Name)) &&
                                 (tx == null || !InterfaceMatches(p.InterfaceName, tx.Name))).ToList();

        var rxSummary = rxHits
            .GroupBy(p => p.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{ShortName(g.Key)}:{g.Count()}")
            .ToList();

        var txOnlyWarning = rxHits.Count == 0 && txHits.Count > 0
            ? " TX only capture is ignored for pass."
            : "";

        if (rxHits.Count > 0 && unexpectedRx.Count == 0)
        {
            ResultText = $"Result: PASS - RX matched {mac} ({string.Join(" | ", rxSummary)})";
            Status = $"PASS. Destination {mac} was captured on RX interface(s).{txOnlyWarning}";
        }
        else
        {
            var observed = matches.Count == 0
                ? "no matching packet"
                : string.Join(", ", matches.Select(p => ShortName(p.InterfaceName)).Distinct(StringComparer.OrdinalIgnoreCase));
            ResultText = $"Result: FAIL - expected RX match for {mac}, observed {observed}";
            Status = $"FAIL. Destination {mac} was not captured on RX interface(s).{txOnlyWarning}";
        }
    }

    private List<CaptureInterfaceItem> GetSelectedInterfaces() =>
        _capture.Interfaces.Where(i => i.IsSelected).ToList();

    private void UpdateTxRxSummary()
    {
        var selected = GetSelectedInterfaces();
        var tx = selected.FirstOrDefault();
        var rx = selected.Skip(1).ToList();
        TxInterfaceName = tx == null ? "(none)" : ShortName(tx.Name);
        RxInterfaceNames = rx.Count == 0 ? "(all non-TX captures)" : string.Join(", ", rx.Select(i => ShortName(i.Name)));
    }

    private string? GetDefaultRxMac()
    {
        var selected = GetSelectedInterfaces();
        var rx = selected.Skip(1).FirstOrDefault();
        var bytes = rx?.Device.MacAddress?.GetAddressBytes();
        return bytes is { Length: 6 } ? string.Join(":", bytes.Select(b => b.ToString("X2"))) : null;
    }

    private string? GetSelectedCaptureMac()
    {
        var mac = _capture.Interfaces
            .Where(i => i.IsSelected)
            .Select(i => i.Device.MacAddress?.GetAddressBytes())
            .FirstOrDefault(bytes => bytes is { Length: 6 });

        return mac == null ? null : string.Join(":", mac.Select(b => b.ToString("X2")));
    }

    private static string NormalizeMac(string value)
    {
        var hex = value.Trim().Replace("-", ":").ToUpperInvariant();
        if (hex.Contains(':'))
            return hex;

        var compact = hex.Replace(":", "");
        return compact.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => compact.Substring(i * 2, 2)))
            : hex;
    }

    private static bool InterfaceMatches(string observed, string expected)
    {
        if (observed.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        if (observed.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
        if (expected.Contains(observed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string ShortName(string name)
    {
        if (name.Length <= 26) return name;
        var trimmed = name.Replace("\\Device\\NPF_", "", StringComparison.OrdinalIgnoreCase);
        return trimmed.Length <= 26 ? trimmed : trimmed[..26] + "...";
    }

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "NDJSON / JSONL (*.ndjson;*.jsonl)|*.ndjson;*.jsonl|JSON / Text (*.json;*.txt)|*.json;*.txt|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        NdjsonText = File.ReadAllText(dlg.FileName);
        Parse();
    }

    private void Parse()
    {
        Rows.Clear();
        foreach (var result in _importService.ParseLines(NdjsonText))
        {
            Rows.Add(new NdjsonPreviewRow
            {
                LineNumber = result.LineNumber,
                State = result.Ok ? "OK" : "ERR",
                Name = result.Name,
                InterfaceName = result.InterfaceName,
                Length = result.Length,
                RepeatCount = result.RepeatCount,
                IntervalMs = result.IntervalMs,
                Summary = result.Summary,
                Error = result.Error,
                Packet = result.Packet
            });
        }

        RefreshCounts();
        Status = $"Parsed {Rows.Count} lines. Valid {ValidCount}, errors {ErrorCount}.";
    }

    private void LoadIntoPacketList(bool replace)
    {
        if (replace)
            _packetList.Sequence.Clear();

        var added = 0;
        foreach (var row in Rows.Where(r => r.Packet != null))
        {
            for (var i = 0; i < row.RepeatCount; i++)
            {
                var packet = ClonePacket(row.Packet!);
                if (row.RepeatCount > 1)
                    packet.Name = $"{row.Name}-{i + 1}";

                _packetList.Sequence.Add(new SequenceItem(packet));
                added++;

                if (row.IntervalMs > 0 && i < row.RepeatCount - 1)
                    _packetList.Sequence.Add(new SequenceItem(new SequenceEvent { DelayMs = row.IntervalMs }));
            }
        }

        _packetList.SelectedSequenceItem = _packetList.Sequence.LastOrDefault();
        Status = $"{added} packets loaded into Send List. Use Packet Generator/Scenario Lab or press Send List here.";
    }

    private static PacketItem ClonePacket(PacketItem source)
    {
        var clone = new PacketItem { Name = source.Name };
        foreach (var iface in source.OutgoingInterfaceNames)
            clone.OutgoingInterfaceNames.Add(iface);

        var raw = source.FullBytes;
        if (raw.Length >= 14)
        {
            var eth = new EthernetBlock();
            eth.ImportBytes(raw, 0);
            clone.Blocks.Add(eth);

            if (raw.Length > 14)
            {
                var payload = new RawPayloadBlock();
                payload.SetBytes(raw.Skip(14).ToArray());
                clone.Blocks.Add(payload);
            }
        }
        else
        {
            var payload = new RawPayloadBlock();
            payload.SetBytes(raw);
            clone.Blocks.Add(payload);
        }

        clone.Invalidate();
        return clone;
    }

    private void Clear()
    {
        NdjsonText = "";
        Rows.Clear();
        RefreshCounts();
        ClearCaptureAddress();
    }

    private void RefreshCounts()
    {
        OnPropertyChanged(nameof(ValidCount));
        OnPropertyChanged(nameof(ErrorCount));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
