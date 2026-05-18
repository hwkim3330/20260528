using System.ComponentModel;
using System.Runtime.CompilerServices;
using SharpPcap;

namespace EthernetPacketGenerator.Models;

// ── UI display items ──────────────────────────────────────────────────────────

public class PfmInterfaceItem
{
    public ILiveDevice? Device      { get; init; }
    public string       DisplayName { get; init; } = string.Empty;
    public override string ToString() => DisplayName;
}

public class PfmSelectableInterface : INotifyPropertyChanged
{
    private bool _isSelected;

    public ILiveDevice? Device      { get; init; }
    public string       DisplayName { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public override string ToString() => DisplayName;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class PacketFlowResultRow : INotifyPropertyChanged
{
    private int    _matchedCount;
    private int    _totalCapturedCount;
    private string _status = string.Empty;
    private string _reason = string.Empty;

    public string Role              { get; init; } = string.Empty;
    public string Port              { get; init; } = string.Empty;
    public string InterfaceName     { get; init; } = string.Empty;

    public int MatchedCount
    {
        get => _matchedCount;
        set { _matchedCount = value; OnPropertyChanged(); }
    }
    public int TotalCapturedCount
    {
        get => _totalCapturedCount;
        set { _totalCapturedCount = value; OnPropertyChanged(); }
    }
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }
    public string Reason
    {
        get => _reason;
        set { _reason = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class CapturedPacketDetailRow
{
    public int    No            { get; init; }
    public string Time          { get; init; } = string.Empty;
    public string Role          { get; init; } = string.Empty;   // TX / RX
    public string Port          { get; init; } = string.Empty;   // Port 1 / -
    public string InterfaceName { get; init; } = string.Empty;
    public bool   IsMatch       { get; init; }
    public string MatchMark     => IsMatch ? "✓" : "";
    public string Direction     { get; init; } = string.Empty;
    public string Protocol      { get; init; } = string.Empty;
    public string Source        { get; init; } = string.Empty;
    public string Destination   { get; init; } = string.Empty;
    public int    Length        { get; init; }
    public string DstMac        { get; init; } = string.Empty;
    public string SrcMac        { get; init; } = string.Empty;
    public string EtherTypeStr  { get; init; } = string.Empty;
    public string Info          { get; init; } = string.Empty;
    public byte[] RawData       { get; init; } = Array.Empty<byte>();
}

public class PacketFlowAutoTestRow
{
    public int    Step          { get; init; }
    public string TestType      { get; init; } = string.Empty;
    public string ExpectedMode  { get; init; } = string.Empty;
    public string ExpectedPort  { get; init; } = string.Empty;
    public string TxMatch       { get; init; } = string.Empty;
    public string Port1Match    { get; init; } = string.Empty;
    public string Port2Match    { get; init; } = string.Empty;
    public string Port3Match    { get; init; } = string.Empty;
    public string Result        { get; init; } = string.Empty;
    public string Uploaded      { get; init; } = string.Empty;
    public string Reason        { get; init; } = string.Empty;
}

// ── Wireshark-style capture row ───────────────────────────────────────────────

public class CaptureRow
{
    public int    No       { get; init; }
    public string Time     { get; init; } = string.Empty;
    public string InterfaceName { get; init; } = string.Empty;
    public string SrcMac   { get; init; } = string.Empty;
    public string DstMac   { get; init; } = string.Empty;
    public string Source   { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public int    Length   { get; init; }
    public string Info     { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string HexDump { get; init; } = string.Empty;
    public byte[] RawData { get; init; } = Array.Empty<byte>();
    public string HexPreview => RawData.Length == 0
        ? string.Empty
        : string.Join(" ", RawData.Take(Math.Min(32, RawData.Length)).Select(b => b.ToString("X2")));
    public string SearchText =>
        $"{No} {Time} {InterfaceName} {SrcMac} {DstMac} {Source} {Destination} {Protocol} {Length} {Info} {HexPreview}".ToLowerInvariant();
}

public class CaptureInterfaceItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public ILiveDevice Device { get; init; } = null!;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class CaptureInterfaceFilterItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public string InterfaceName { get; init; } = string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ── HTTP DTO types ────────────────────────────────────────────────────────────

public class ManagerHealthResponse
{
    public bool   Ok      { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Time    { get; set; } = string.Empty;
}

public class PacketFlowStartRequest
{
    public string              FlowMode             { get; set; } = string.Empty;
    public string              DstMac               { get; set; } = string.Empty;
    public string              SrcMac               { get; set; } = string.Empty;
    public string              EtherType            { get; set; } = string.Empty;
    public string              Signature            { get; set; } = string.Empty;
    public string              UdpSrcPort           { get; set; } = string.Empty;
    public string              UdpDstPort           { get; set; } = string.Empty;
    public int                 ExpectedOutputPort   { get; set; }
    public List<int>           ExpectedPorts        { get; set; } = new();
    public int                 PacketCount          { get; set; }
    public int                 CaptureTimeoutSec    { get; set; }
    public bool                StrictCountMode      { get; set; }
    public bool                CheckTxObserved      { get; set; }
    public bool                CheckUnexpectedPorts { get; set; }
    public Dictionary<string,string> Interfaces     { get; set; } = new();
}

public class PacketFlowStartResponse
{
    public bool   Ok     { get; set; }
    public string TestId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

public class PacketFlowPortResultDto
{
    public string Role               { get; set; } = string.Empty;
    public int?   Port               { get; set; }
    public string InterfaceName      { get; set; } = string.Empty;
    public int    MatchedCount       { get; set; }
    public int    TotalCapturedCount { get; set; }
    public string Status             { get; set; } = string.Empty;
}

public class PacketFlowResultRequest
{
    public string                       TestId            { get; set; } = string.Empty;
    public string                       Type              { get; set; } = "PACKET_FLOW_MONITOR";
    public string                       FlowMode          { get; set; } = string.Empty;
    public string                       DstMac            { get; set; } = string.Empty;
    public int                          ExpectedOutputPort{ get; set; }
    public int                          PacketCount       { get; set; }
    public int                          CaptureTimeoutSec { get; set; }
    public List<PacketFlowPortResultDto>Results           { get; set; } = new();
    public string                       Result            { get; set; } = string.Empty;
    public string                       Reason            { get; set; } = string.Empty;
}

public class PacketFlowMacroStartRequest
{
    public string    MacroType         { get; set; } = "FDB_AUTO_TEST";
    public string    DstMac            { get; set; } = string.Empty;
    public List<int> Ports             { get; set; } = new() { 1, 2, 3 };
    public int       PacketCount       { get; set; }
    public int       CaptureTimeoutSec { get; set; }
    public bool      CheckTxObserved   { get; set; }
}

public class PacketFlowMacroStepDto
{
    public int    Step               { get; set; }
    public string FlowMode           { get; set; } = string.Empty;
    public int    ExpectedOutputPort { get; set; }
    public string Status             { get; set; } = "pending";
}

public class PacketFlowMacroStartResponse
{
    public bool                        Ok      { get; set; }
    public string                      MacroId { get; set; } = string.Empty;
    public List<PacketFlowMacroStepDto>Steps   { get; set; } = new();
}

public class PacketFlowMacroStepResultRequest
{
    public int    Step               { get; set; }
    public string FlowMode           { get; set; } = string.Empty;
    public int    ExpectedOutputPort { get; set; }
    public int    TxMatch            { get; set; }
    public int    Port1Match         { get; set; }
    public int    Port2Match         { get; set; }
    public int    Port3Match         { get; set; }
    public string Result             { get; set; } = string.Empty;
    public string Reason             { get; set; } = string.Empty;
}
