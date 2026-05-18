using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using SharpPcap;

namespace EthernetPacketGenerator.ViewModels;

public class PacketFlowMonitorViewModel : ViewModelBase
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly ManagerServerClient       _server  = new();
    private readonly PacketFlowMonitorService  _monitor = new();
    private CancellationTokenSource?           _cts;

    // ── Manager Server ────────────────────────────────────────────────────────
    private string _managerServerUrl    = "http://localhost:8080";
    private string _managerServerStatus = "Disconnected";
    private bool   _isServerConnected;
    private bool   _saveResultToServer;
    private string _currentTestId  = string.Empty;
    private string _currentMacroId = string.Empty;

    public string ManagerServerUrl
    {
        get => _managerServerUrl;
        set { SetProperty(ref _managerServerUrl, value); _server.BaseUrl = value; }
    }
    public string ManagerServerStatus
    {
        get => _managerServerStatus;
        set => SetProperty(ref _managerServerStatus, value);
    }
    public bool IsServerConnected
    {
        get => _isServerConnected;
        set => SetProperty(ref _isServerConnected, value);
    }
    public bool SaveResultToServer
    {
        get => _saveResultToServer;
        set => SetProperty(ref _saveResultToServer, value);
    }
    public string CurrentTestId
    {
        get => _currentTestId;
        set => SetProperty(ref _currentTestId, value);
    }

    // ── Interface Mapping ─────────────────────────────────────────────────────
    public ObservableCollection<PfmInterfaceItem>       AvailableInterfaces    { get; } = new();
    public ObservableCollection<PfmSelectableInterface> SelectableRxInterfaces { get; } = new();

    private PfmInterfaceItem? _selTx;
    public PfmInterfaceItem? SelectedTxInterface
    {
        get => _selTx;
        set => SetProperty(ref _selTx, value);
    }

    // ── Packet Match Filter ───────────────────────────────────────────────────
    private string _dstMac     = "C8:4D:44:25:2D:37";
    private string _srcMac     = string.Empty;
    private string _etherType  = "0x0800";
    private string _signature  = "KETI_FDB_TEST";
    private string _udpSrcPort = string.Empty;
    private string _udpDstPort = string.Empty;

    public string DstMac    { get => _dstMac;     set => SetProperty(ref _dstMac,     value); }
    public string SrcMac    { get => _srcMac;     set => SetProperty(ref _srcMac,     value); }
    public string EtherType { get => _etherType;  set => SetProperty(ref _etherType,  value); }
    public string Signature { get => _signature;  set => SetProperty(ref _signature,  value); }
    public string UdpSrcPort{ get => _udpSrcPort; set => SetProperty(ref _udpSrcPort, value); }
    public string UdpDstPort{ get => _udpDstPort; set => SetProperty(ref _udpDstPort, value); }

    // ── Flow Expectation ──────────────────────────────────────────────────────
    public ObservableCollection<string> FlowModes { get; } = new()
        { "TX Only", "Forwarding Check", "FDB Static Unicast", "Flooding Check", "Custom" };

    private string _flowMode           = "FDB Static Unicast";
    private int    _expectedOutputPort = 1;
    private int    _expectedPacketCount = 100;
    private int    _captureTimeoutSec  = 5;
    private bool   _strictCountMode;
    private bool   _checkTxObserved   = true;
    private bool   _checkUnexpectedPorts = true;
    private bool   _expectPort1 = true;
    private bool   _expectPort2 = true;
    private bool   _expectPort3 = true;

    public string FlowMode           { get => _flowMode;            set => SetProperty(ref _flowMode,            value); }
    public int ExpectedOutputPort    { get => _expectedOutputPort;  set => SetProperty(ref _expectedOutputPort,  value); }
    public int ExpectedPacketCount   { get => _expectedPacketCount; set => SetProperty(ref _expectedPacketCount, value); }
    public int CaptureTimeoutSec     { get => _captureTimeoutSec;   set => SetProperty(ref _captureTimeoutSec,   value); }
    public bool StrictCountMode      { get => _strictCountMode;     set => SetProperty(ref _strictCountMode,     value); }
    public bool CheckTxObserved      { get => _checkTxObserved;     set => SetProperty(ref _checkTxObserved,     value); }
    public bool CheckUnexpectedPorts { get => _checkUnexpectedPorts;set => SetProperty(ref _checkUnexpectedPorts,value); }
    public bool ExpectPort1          { get => _expectPort1;         set => SetProperty(ref _expectPort1,         value); }
    public bool ExpectPort2          { get => _expectPort2;         set => SetProperty(ref _expectPort2,         value); }
    public bool ExpectPort3          { get => _expectPort3;         set => SetProperty(ref _expectPort3,         value); }

    // ── State ─────────────────────────────────────────────────────────────────
    private bool   _isMonitoring;
    private string _finalResult = string.Empty;
    private string _finalReason = string.Empty;
    private string _logsText    = string.Empty;

    public bool IsMonitoring
    {
        get => _isMonitoring;
        set => SetProperty(ref _isMonitoring, value);
    }
    public string FinalResult
    {
        get => _finalResult;
        set => SetProperty(ref _finalResult, value);
    }
    public string FinalReason
    {
        get => _finalReason;
        set => SetProperty(ref _finalReason, value);
    }
    public string LogsText
    {
        get => _logsText;
        set => SetProperty(ref _logsText, value);
    }

    // ── Collections ───────────────────────────────────────────────────────────
    public ObservableCollection<PacketFlowResultRow>     SummaryRows   { get; } = new();
    public ObservableCollection<CapturedPacketDetailRow> DetailPackets { get; } = new();
    public ObservableCollection<PacketFlowAutoTestRow>   AutoTestRows  { get; } = new();

    private CapturedPacketDetailRow? _selectedPacket;
    private string _selectedPacketDetailText = string.Empty;
    private string _selectedPacketHexPreview = string.Empty;

    public CapturedPacketDetailRow? SelectedPacket
    {
        get => _selectedPacket;
        set { SetProperty(ref _selectedPacket, value); UpdatePacketDetail(value); }
    }
    public string SelectedPacketDetailText
    {
        get => _selectedPacketDetailText;
        set => SetProperty(ref _selectedPacketDetailText, value);
    }
    public string SelectedPacketHexPreview
    {
        get => _selectedPacketHexPreview;
        set => SetProperty(ref _selectedPacketHexPreview, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand RefreshInterfacesCommand  { get; }
    public ICommand CheckManagerServerCommand { get; }
    public ICommand StartMonitorCommand       { get; }
    public ICommand StopMonitorCommand        { get; }
    public ICommand ClearResultCommand        { get; }
    public ICommand UploadResultCommand       { get; }
    public ICommand RunTxSanityCheckCommand   { get; }
    public ICommand RunFdbAutoTestCommand     { get; }
    public ICommand RunFloodingCheckCommand   { get; }

    // ── Constructor ───────────────────────────────────────────────────────────
    public PacketFlowMonitorViewModel()
    {
        _server.BaseUrl = _managerServerUrl;

        RefreshInterfacesCommand  = new RelayCommand(RefreshInterfaces);
        CheckManagerServerCommand = new RelayCommand(async () => await CheckServerAsync());
        StartMonitorCommand       = new RelayCommand(async () => await StartMonitorAsync(), () => !IsMonitoring);
        StopMonitorCommand        = new RelayCommand(StopMonitor, () => IsMonitoring);
        ClearResultCommand        = new RelayCommand(ClearResult);
        UploadResultCommand       = new RelayCommand(async () => await UploadResultAsync());
        RunTxSanityCheckCommand   = new RelayCommand(async () => await RunTxSanityCheckAsync(), () => !IsMonitoring);
        RunFdbAutoTestCommand     = new RelayCommand(async () => await RunFdbAutoTestAsync(),   () => !IsMonitoring);
        RunFloodingCheckCommand   = new RelayCommand(async () => await RunFloodingCheckAsync(), () => !IsMonitoring);

        RefreshInterfaces();
    }

    // ── Interface loading ─────────────────────────────────────────────────────
    private void RefreshInterfaces()
    {
        AvailableInterfaces.Clear();
        SelectableRxInterfaces.Clear();
        var (devices, error) = NetworkInterfaceService.GetInterfaces();
        if (error != null)
        {
            AppendLog($"Interface error: {error}");
            return;
        }
        foreach (var dev in devices)
        {
            string friendly = dev.Description ?? dev.Name;
            string display  = $"{dev.Name} - {friendly}";
            AvailableInterfaces.Add(new PfmInterfaceItem { Device = dev, DisplayName = display });
            SelectableRxInterfaces.Add(new PfmSelectableInterface { Device = dev, DisplayName = display });
        }
        AppendLog($"Refreshed: {AvailableInterfaces.Count} interface(s) found.");
    }

    // ── Server health check ───────────────────────────────────────────────────
    private async Task CheckServerAsync()
    {
        ManagerServerStatus = "Checking...";
        bool ok = await _server.CheckHealthAsync();
        IsServerConnected   = ok;
        ManagerServerStatus = ok ? "Connected" : "Disconnected";
        AppendLog($"Server check: {ManagerServerStatus}");
    }

    // ── Get selected RX interfaces (in selection order, up to 3) ─────────────
    private List<PfmSelectableInterface> GetSelectedRxInterfaces()
        => SelectableRxInterfaces.Where(x => x.IsSelected).Take(3).ToList();

    // ── Start monitor ─────────────────────────────────────────────────────────
    public async Task StartMonitorAsync()
    {
        var rxSelected = GetSelectedRxInterfaces();
        if (SelectedTxInterface == null && rxSelected.Count == 0)
        {
            AppendLog("FAIL: No capture interface selected.");
            FinalResult = "FAIL";
            FinalReason = "Capture interface is not selected.";
            return;
        }

        IsMonitoring = true;
        FinalResult  = string.Empty;
        FinalReason  = string.Empty;
        SummaryRows.Clear();
        DetailPackets.Clear();

        // Register with server
        if (SaveResultToServer && IsServerConnected)
        {
            var startReq  = BuildStartRequest(rxSelected);
            var startResp = await _server.StartPacketFlowAsync(startReq);
            CurrentTestId = startResp?.TestId ?? string.Empty;
            AppendLog($"Server: test started [{CurrentTestId}]");
        }

        // Prepare summary rows
        var summaryMap = new Dictionary<string, PacketFlowResultRow>();
        void AddSummaryRow(string role, string port, string iface)
        {
            var row = new PacketFlowResultRow { Role = role, Port = port, InterfaceName = iface, Status = "Capturing..." };
            SummaryRows.Add(row);
            summaryMap[$"{role}:{port}"] = row;
        }
        if (SelectedTxInterface != null) AddSummaryRow("TX", "-", SelectedTxInterface.DisplayName);
        for (int i = 0; i < rxSelected.Count; i++)
            AddSummaryRow("RX", $"Port {i + 1}", rxSelected[i].DisplayName);

        // Build config
        var config = new PacketFlowMonitorConfig
        {
            TxDevice           = SelectedTxInterface?.Device,
            Port1Device        = rxSelected.Count > 0 ? rxSelected[0].Device : null,
            Port2Device        = rxSelected.Count > 1 ? rxSelected[1].Device : null,
            Port3Device        = rxSelected.Count > 2 ? rxSelected[2].Device : null,
            TxInterfaceName    = SelectedTxInterface?.DisplayName    ?? string.Empty,
            Port1InterfaceName = rxSelected.Count > 0 ? rxSelected[0].DisplayName : string.Empty,
            Port2InterfaceName = rxSelected.Count > 1 ? rxSelected[1].DisplayName : string.Empty,
            Port3InterfaceName = rxSelected.Count > 2 ? rxSelected[2].DisplayName : string.Empty,
            DstMac            = DstMac,
            SrcMac            = SrcMac,
            EtherType         = EtherType,
            Signature         = Signature,
            UdpSrcPort        = UdpSrcPort,
            UdpDstPort        = UdpDstPort,
            CaptureTimeoutSec = CaptureTimeoutSec,
            MaxDetailPackets  = 1000
        };

        _cts = new CancellationTokenSource();
        var result = await _monitor.RunAsync(config, (row, state) =>
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                string key = $"{state.Role}:{(state.SwitchPort.HasValue ? $"Port {state.SwitchPort}" : "-")}";
                if (summaryMap.TryGetValue(key, out var sumRow))
                {
                    sumRow.MatchedCount       = state.MatchedCount;
                    sumRow.TotalCapturedCount = state.TotalCapturedCount;
                }
                if (DetailPackets.Count < 1000)
                    DetailPackets.Add(row);
            });
        }, _cts.Token);

        // Final summary update
        foreach (var (key, row) in summaryMap)
        {
            var state = key.StartsWith("TX:") ? result.TxState
                : key.Contains("Port 1") ? result.Port1State
                : key.Contains("Port 2") ? result.Port2State
                : result.Port3State;
            if (state != null)
            {
                row.MatchedCount       = state.MatchedCount;
                row.TotalCapturedCount = state.TotalCapturedCount;
                row.Status             = "Done";
            }
        }

        // Judge
        int txMatch = result.TxState?.MatchedCount    ?? 0;
        int p1Match = result.Port1State?.MatchedCount ?? 0;
        int p2Match = result.Port2State?.MatchedCount ?? 0;
        int p3Match = result.Port3State?.MatchedCount ?? 0;

        var (finalRes, finalReason) = JudgeResult(txMatch, p1Match, p2Match, p3Match);
        FinalResult = finalRes;
        FinalReason = finalReason;
        AppendLog($"Result: {finalRes}{(finalReason.Length > 0 ? $" — {finalReason}" : "")}");

        foreach (var row in SummaryRows) row.Status = "Done";

        IsMonitoring = false;

        if (SaveResultToServer && IsServerConnected)
            await UploadResultCoreAsync(result, finalRes, finalReason);
    }

    // ── Stop ──────────────────────────────────────────────────────────────────
    private void StopMonitor()
    {
        _monitor.Stop();
        _cts?.Cancel();
        AppendLog("Capture stopped by user.");
    }

    // ── Clear ─────────────────────────────────────────────────────────────────
    private void ClearResult()
    {
        SummaryRows.Clear();
        DetailPackets.Clear();
        AutoTestRows.Clear();
        FinalResult   = string.Empty;
        FinalReason   = string.Empty;
        CurrentTestId = string.Empty;
        AppendLog("Cleared.");
    }

    // ── Upload result ─────────────────────────────────────────────────────────
    private async Task UploadResultAsync()
    {
        if (!IsServerConnected) { AppendLog("Server not connected."); return; }
        var req = BuildResultRequest(FinalResult, FinalReason);
        bool ok = await _server.UploadPacketFlowResultAsync(req);
        AppendLog(ok ? "Uploaded to server." : "Upload failed.");
    }

    private async Task UploadResultCoreAsync(PacketFlowMonitorResult result, string finalRes, string reason)
    {
        var req = BuildResultRequest(finalRes, reason);
        req.Results.Clear();
        void AddPortResult(string role, int? port, FlowInterfaceState? st)
        {
            if (st == null) return;
            req.Results.Add(new PacketFlowPortResultDto
            {
                Role               = role,
                Port               = port,
                InterfaceName      = st.InterfaceName,
                MatchedCount       = st.MatchedCount,
                TotalCapturedCount = st.TotalCapturedCount,
                Status             = "OK"
            });
        }
        AddPortResult("TX", null, result.TxState);
        AddPortResult("RX", 1, result.Port1State);
        AddPortResult("RX", 2, result.Port2State);
        AddPortResult("RX", 3, result.Port3State);
        bool ok = await _server.UploadPacketFlowResultAsync(req);
        AppendLog(ok ? "Result uploaded to server." : "Server upload failed.");
    }

    // ── Macro: TX Sanity Check ────────────────────────────────────────────────
    public async Task RunTxSanityCheckAsync()
    {
        FlowMode = "TX Only";
        AppendLog("== TX Sanity Check ==");
        await StartMonitorAsync();
        AutoTestRows.Add(new PacketFlowAutoTestRow
        {
            Step         = AutoTestRows.Count + 1,
            TestType     = "TX Sanity",
            ExpectedMode = "TX Only",
            ExpectedPort = "-",
            TxMatch      = $"{SummaryRows.FirstOrDefault(r => r.Role == "TX")?.MatchedCount ?? 0}",
            Port1Match   = "-", Port2Match = "-", Port3Match = "-",
            Result       = FinalResult,
            Uploaded     = (SaveResultToServer && IsServerConnected) ? "Yes" : "No",
            Reason       = FinalReason
        });
    }

    // ── Macro: FDB Auto Test ──────────────────────────────────────────────────
    public async Task RunFdbAutoTestAsync()
    {
        string? macroId = null;
        if (SaveResultToServer && IsServerConnected)
        {
            var mReq = new PacketFlowMacroStartRequest
            {
                MacroType         = "FDB_AUTO_TEST",
                DstMac            = DstMac,
                Ports             = new() { 1, 2, 3 },
                PacketCount       = ExpectedPacketCount,
                CaptureTimeoutSec = CaptureTimeoutSec,
                CheckTxObserved   = CheckTxObserved
            };
            var mResp = await _server.StartPacketFlowMacroAsync(mReq);
            macroId = mResp?.MacroId;
            _currentMacroId = macroId ?? string.Empty;
        }

        for (int port = 1; port <= 3; port++)
        {
            AppendLog($"== FDB Auto Test: Step {port}/3 — Expected Port {port} ==");
            FlowMode           = "FDB Static Unicast";
            ExpectedOutputPort = port;
            await StartMonitorAsync();

            int txM = SummaryRows.FirstOrDefault(r => r.Role == "TX")?.MatchedCount    ?? 0;
            int p1M = SummaryRows.FirstOrDefault(r => r.Port == "Port 1")?.MatchedCount ?? 0;
            int p2M = SummaryRows.FirstOrDefault(r => r.Port == "Port 2")?.MatchedCount ?? 0;
            int p3M = SummaryRows.FirstOrDefault(r => r.Port == "Port 3")?.MatchedCount ?? 0;

            AutoTestRows.Add(new PacketFlowAutoTestRow
            {
                Step         = port,
                TestType     = "FDB Static",
                ExpectedMode = "FDB Static Unicast",
                ExpectedPort = $"Port {port}",
                TxMatch      = $"{txM}",
                Port1Match   = $"{p1M}",
                Port2Match   = $"{p2M}",
                Port3Match   = $"{p3M}",
                Result       = FinalResult,
                Uploaded     = (macroId != null) ? "Yes" : "No",
                Reason       = FinalReason
            });

            if (macroId != null)
            {
                await _server.UploadMacroStepResultAsync(macroId, new PacketFlowMacroStepResultRequest
                {
                    Step               = port,
                    FlowMode           = "FDB_STATIC_UNICAST",
                    ExpectedOutputPort = port,
                    TxMatch            = txM,
                    Port1Match         = p1M,
                    Port2Match         = p2M,
                    Port3Match         = p3M,
                    Result             = FinalResult,
                    Reason             = FinalReason
                });
            }
            SummaryRows.Clear();
        }
        AppendLog("== FDB Auto Test: Complete ==");
    }

    // ── Macro: Flooding Check ─────────────────────────────────────────────────
    public async Task RunFloodingCheckAsync()
    {
        FlowMode  = "Flooding Check";
        ExpectPort1 = true; ExpectPort2 = true; ExpectPort3 = true;
        AppendLog("== Flooding Check ==");
        await StartMonitorAsync();
        AutoTestRows.Add(new PacketFlowAutoTestRow
        {
            Step         = AutoTestRows.Count + 1,
            TestType     = "Flooding",
            ExpectedMode = "Flooding Check",
            ExpectedPort = "Multiple",
            TxMatch      = $"{SummaryRows.FirstOrDefault(r => r.Role == "TX")?.MatchedCount ?? 0}",
            Port1Match   = $"{SummaryRows.FirstOrDefault(r => r.Port == "Port 1")?.MatchedCount ?? 0}",
            Port2Match   = $"{SummaryRows.FirstOrDefault(r => r.Port == "Port 2")?.MatchedCount ?? 0}",
            Port3Match   = $"{SummaryRows.FirstOrDefault(r => r.Port == "Port 3")?.MatchedCount ?? 0}",
            Result       = FinalResult,
            Uploaded     = (SaveResultToServer && IsServerConnected) ? "Yes" : "No",
            Reason       = FinalReason
        });
    }

    // ── PASS/FAIL judge ───────────────────────────────────────────────────────
    public (string Result, string Reason) JudgeResult(int txM, int p1M, int p2M, int p3M)
    {
        int expPortMatch = ExpectedOutputPort == 1 ? p1M : ExpectedOutputPort == 2 ? p2M : p3M;
        switch (FlowMode)
        {
            case "TX Only":
                return txM > 0 ? ("PASS", "") : ("FAIL", "TX packet was not observed.");

            case "Forwarding Check":
                if (CheckTxObserved && txM == 0) return ("FAIL", "TX packet was not observed.");
                if (expPortMatch == 0) return ("FAIL", $"Expected output port (Port {ExpectedOutputPort}) did not receive matched packet.");
                if (CheckUnexpectedPorts)
                {
                    if (ExpectedOutputPort != 1 && p1M > 0) return ("FAIL", "Unexpected forwarding on Port 1.");
                    if (ExpectedOutputPort != 2 && p2M > 0) return ("FAIL", "Unexpected forwarding on Port 2.");
                    if (ExpectedOutputPort != 3 && p3M > 0) return ("FAIL", "Unexpected forwarding on Port 3.");
                }
                return ("PASS", "");

            case "FDB Static Unicast":
                if (CheckTxObserved && txM == 0) return ("FAIL", "TX packet was not observed.");
                if (expPortMatch == 0) return ("FAIL", $"Expected output port (Port {ExpectedOutputPort}) did not receive matched packet.");
                if (ExpectedOutputPort != 1 && p1M > 0) return ("FAIL", "Unexpected forwarding detected on Port 1.");
                if (ExpectedOutputPort != 2 && p2M > 0) return ("FAIL", "Unexpected forwarding detected on Port 2.");
                if (ExpectedOutputPort != 3 && p3M > 0) return ("FAIL", "Unexpected forwarding detected on Port 3.");
                return ("PASS", "");

            case "Flooding Check":
                if (CheckTxObserved && txM == 0) return ("FAIL", "TX packet was not observed.");
                if (ExpectPort1 && p1M == 0) return ("FAIL", "Flooding expected but Port 1 did not receive matched packet.");
                if (ExpectPort2 && p2M == 0) return ("FAIL", "Flooding expected but Port 2 did not receive matched packet.");
                if (ExpectPort3 && p3M == 0) return ("FAIL", "Flooding expected but Port 3 did not receive matched packet.");
                return ("PASS", "");

            default:
                return ("PASS", "");
        }
    }

    // ── Build request helpers ─────────────────────────────────────────────────
    private PacketFlowStartRequest BuildStartRequest(List<PfmSelectableInterface> rxSelected) => new()
    {
        FlowMode             = FlowMode,
        DstMac               = DstMac,
        SrcMac               = SrcMac,
        EtherType            = EtherType,
        Signature            = Signature,
        UdpSrcPort           = UdpSrcPort,
        UdpDstPort           = UdpDstPort,
        ExpectedOutputPort   = ExpectedOutputPort,
        ExpectedPorts        = GetExpectedPortsList(),
        PacketCount          = ExpectedPacketCount,
        CaptureTimeoutSec    = CaptureTimeoutSec,
        StrictCountMode      = StrictCountMode,
        CheckTxObserved      = CheckTxObserved,
        CheckUnexpectedPorts = CheckUnexpectedPorts,
        Interfaces           = new()
        {
            ["tx"]    = SelectedTxInterface?.DisplayName ?? string.Empty,
            ["port1"] = rxSelected.Count > 0 ? rxSelected[0].DisplayName : string.Empty,
            ["port2"] = rxSelected.Count > 1 ? rxSelected[1].DisplayName : string.Empty,
            ["port3"] = rxSelected.Count > 2 ? rxSelected[2].DisplayName : string.Empty,
        }
    };

    private PacketFlowResultRequest BuildResultRequest(string result, string reason) => new()
    {
        TestId             = CurrentTestId,
        FlowMode           = FlowMode,
        DstMac             = DstMac,
        ExpectedOutputPort = ExpectedOutputPort,
        PacketCount        = ExpectedPacketCount,
        CaptureTimeoutSec  = CaptureTimeoutSec,
        Result             = result,
        Reason             = reason,
        Results            = SummaryRows.Select(r => new PacketFlowPortResultDto
        {
            Role               = r.Role,
            Port               = r.Port == "-" ? null : (int?)int.Parse(r.Port.Replace("Port ", "")),
            InterfaceName      = r.InterfaceName,
            MatchedCount       = r.MatchedCount,
            TotalCapturedCount = r.TotalCapturedCount,
            Status             = "OK"
        }).ToList()
    };

    private List<int> GetExpectedPortsList()
    {
        var list = new List<int>();
        if (ExpectPort1) list.Add(1);
        if (ExpectPort2) list.Add(2);
        if (ExpectPort3) list.Add(3);
        return list;
    }

    // ── Packet detail / hex preview ───────────────────────────────────────────
    private void UpdatePacketDetail(CapturedPacketDetailRow? row)
    {
        if (row == null || row.RawData.Length == 0)
        {
            SelectedPacketDetailText = string.Empty;
            SelectedPacketHexPreview = string.Empty;
            return;
        }
        SelectedPacketDetailText = BuildDetailText(row);
        SelectedPacketHexPreview = BuildHexDump(row.RawData);
    }

    private static string BuildDetailText(CapturedPacketDetailRow row)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Frame: {row.Length} bytes  [{row.Time}]");
        sb.AppendLine($"  Role: {row.Role}  Port: {row.Port}  Interface: {row.InterfaceName}");
        sb.AppendLine($"  Match: {(row.IsMatch ? "YES" : "no")}  Direction: {row.Direction}");
        sb.AppendLine("Ethernet II");
        sb.AppendLine($"  Destination: {row.DstMac}");
        sb.AppendLine($"  Source:      {row.SrcMac}");
        sb.AppendLine($"  EtherType:   {row.EtherTypeStr}");
        if (row.Protocol.Contains("IPv4") || row.Protocol.Contains("UDP"))
        {
            sb.AppendLine("Internet Protocol");
            sb.AppendLine($"  Source:      {row.Source}");
            sb.AppendLine($"  Destination: {row.Destination}");
        }
        if (row.Protocol.Contains("UDP"))
            sb.AppendLine("User Datagram Protocol");
        sb.AppendLine($"Payload Info: {row.Info}");
        return sb.ToString();
    }

    private static string BuildHexDump(byte[] data)
    {
        const int bpr = 16;
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i += bpr)
        {
            sb.Append($"{i:X4}  ");
            int count = Math.Min(bpr, data.Length - i);
            for (int j = 0; j < bpr; j++)
            {
                sb.Append(j < count ? $"{data[i + j]:X2} " : "   ");
                if (j == 7) sb.Append(' ');
            }
            sb.Append("  ");
            for (int j = 0; j < count; j++)
            {
                char c = (char)data[i + j];
                sb.Append(c >= 0x20 && c < 0x7F ? c : '.');
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ── Logging ───────────────────────────────────────────────────────────────
    public void AppendLog(string msg)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        LogsText += $"[{ts}] {msg}\n";
    }
}
