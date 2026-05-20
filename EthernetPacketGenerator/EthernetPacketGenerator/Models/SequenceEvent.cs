using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public enum SequenceEventType
{
    Delay,
    RegWrite, RegRead, RegVerify,
    FdbWrite, FdbWriteBucket, FdbRead, FdbReadBucket, FdbWaitFor, FdbFlush,
    RxVerify
}

public class SequenceEvent : INotifyPropertyChanged
{
    private SequenceEventType _eventType = SequenceEventType.Delay;

    // ── Delay ────────────────────────────────────────────────────────────────
    private int  _delayMs = 100;

    // ── Reg* (절대 주소 사용) ─────────────────────────────────────────────────
    private uint _address  = 0;
    private uint _value    = 0;
    private uint _mask     = 0xFFFFFFFF;
    private uint _expected = 0;
    private int  _timeoutMs = 1000;

    // ── User-defined label ────────────────────────────────────────────────────
    private string _label = string.Empty;

    // ── Fdb* ─────────────────────────────────────────────────────────────────
    private string _macAddress     = "00:00:00:00:00:00";
    private bool   _vlanValid      = false;
    private int    _vlanId         = 0;
    private int    _port           = 0;
    private int    _bucket         = 0;
    private int    _slotBitmap     = 1;   // default: Slot 0 = 0b0001
    private string _fdbExpectedMac = string.Empty;

    // ── RxVerify ─────────────────────────────────────────────────────────────
    // ExpectedPort: 패킷이 수신되어야 하는 포트 인덱스 (포트-MAC 매핑 기준)
    // ExpectedDstMac: 캡처에서 확인할 DA. 비어있으면 직전 FdbWrite의 MacAddress 사용
    private int    _expectedPort    = -1;
    private string _expectedDstMac  = string.Empty;

    public int    ExpectedPort   { get => _expectedPort;   set { _expectedPort   = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); } }
    public string ExpectedDstMac { get => _expectedDstMac; set { _expectedDstMac = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); } }

    public string Label
    {
        get => _label;
        set { _label = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string FdbExpectedMac
    {
        get => _fdbExpectedMac;
        set { _fdbExpectedMac = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    // ── Properties ───────────────────────────────────────────────────────────
    public SequenceEventType EventType
    {
        get => _eventType;
        set { _eventType = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int DelayMs
    {
        get => _delayMs;
        set { _delayMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public uint Address
    {
        get => _address;
        set { _address = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public uint Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public uint Mask
    {
        get => _mask;
        set { _mask = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public uint Expected
    {
        get => _expected;
        set { _expected = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int TimeoutMs
    {
        get => _timeoutMs;
        set { _timeoutMs = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string MacAddress
    {
        get => _macAddress;
        set { _macAddress = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public bool VlanValid
    {
        get => _vlanValid;
        set { _vlanValid = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int VlanId
    {
        get => _vlanId;
        set { _vlanId = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int Port
    {
        get => _port;
        set { _port = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int Bucket
    {
        get => _bucket;
        set { _bucket = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int SlotBitmap
    {
        get => _slotBitmap;
        set { _slotBitmap = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    // ── Display ───────────────────────────────────────────────────────────────
    public string DisplayLabel => EventType switch
    {
        SequenceEventType.Delay          => $"⏱  Delay {DelayMs} ms",
        SequenceEventType.RegWrite       => $"✎  write  0x{Address:X8}  =  0x{Value:X8}",
        SequenceEventType.RegRead        => $"⤷  read   0x{Address:X8}",
        SequenceEventType.RegVerify         => $"✅  verify 0x{Address:X8} & 0x{Mask:X8} == 0x{Expected:X8}  ({TimeoutMs}ms)",
        SequenceEventType.FdbWrite       => $"📋  FDB write  {MacAddress}  Port:0b{Convert.ToString(Port, 2).PadLeft(6, '0')}",
        SequenceEventType.FdbWriteBucket => $"📋  FDB write(bucket)  {MacAddress}  B:{Bucket} S:0x{SlotBitmap:X}",
        SequenceEventType.FdbRead        => $"🔍  FDB read   {MacAddress}",
        SequenceEventType.FdbReadBucket  => string.IsNullOrWhiteSpace(FdbExpectedMac)
            ? $"🔍  FDB read(bucket)  B:{Bucket} S:0x{SlotBitmap:X}"
            : $"✅  FDB verify(bucket)  B:{Bucket} S:0x{SlotBitmap:X}  exp:{FdbExpectedMac}",
        SequenceEventType.FdbWaitFor     => $"⏳  FDB wait  {MacAddress}  Port:0b{Convert.ToString(Port, 2).PadLeft(6, '0')}  ({TimeoutMs}ms)",
        SequenceEventType.FdbFlush       => "🗑  FDB Initialize  (전체 테이블 초기화)",
        SequenceEventType.RxVerify       => $"📥  RX Verify  DA={ExpectedDstMac}  ExpPort={ExpectedPort}  timeout={TimeoutMs}ms",
        _                                => "?"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
