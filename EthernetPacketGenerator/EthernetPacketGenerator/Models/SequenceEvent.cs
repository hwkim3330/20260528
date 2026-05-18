using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public enum SequenceEventType
{
    Delay,
    RegWrite, RegRead, RegWaitFor,
    FdbWrite, FdbRead, FdbWaitFor, FdbFlush,
    CaptureVerify,   // capture on interface, match filter, timeout → PASS/FAIL
    SerialSend,      // send text/hex on serial port
    SerialVerify     // expect pattern in serial output within timeout → PASS/FAIL
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

    // ── Fdb* ─────────────────────────────────────────────────────────────────
    private string _macAddress  = "00:00:00:00:00:00";
    private bool   _vlanValid   = false;
    private int    _vlanId      = 0;
    private int    _port        = 0;
    private int    _bucket      = 0;
    private int    _slotBitmap  = 1;   // default: Slot 0 = 0b0001

    // ── CaptureVerify ─────────────────────────────────────────────────────────
    private string _captureInterface    = "";
    private string _captureFilter       = "";   // e.g. "udp", "ip:192.168.1.1", "mac:aa:bb"
    private int    _captureExpected     = 1;    // min matching packets to PASS

    // ── SerialSend / SerialVerify ─────────────────────────────────────────────
    private string _serialText   = "";    // text to send or pattern to expect
    private string _serialHex    = "";    // hex bytes to send (alternative to text)

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

    public string CaptureInterface
    {
        get => _captureInterface;
        set { _captureInterface = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string CaptureFilter
    {
        get => _captureFilter;
        set { _captureFilter = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public int CaptureExpected
    {
        get => _captureExpected;
        set { _captureExpected = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string SerialText
    {
        get => _serialText;
        set { _serialText = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    public string SerialHex
    {
        get => _serialHex;
        set { _serialHex = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayLabel)); }
    }

    // ── Display ───────────────────────────────────────────────────────────────
    public string DisplayLabel => EventType switch
    {
        SequenceEventType.Delay          => $"⏱  Delay {DelayMs} ms",
        SequenceEventType.RegWrite       => $"✎  write  0x{Address:X8}  =  0x{Value:X8}",
        SequenceEventType.RegRead        => $"⤷  read   0x{Address:X8}",
        SequenceEventType.RegWaitFor     => $"⏳  wait   0x{Address:X8} & 0x{Mask:X8} == 0x{Expected:X8}  ({TimeoutMs}ms)",
        SequenceEventType.FdbWrite       => $"📋  FDB write  {MacAddress}  Port:{Port}",
        SequenceEventType.FdbRead        => $"🔍  FDB read   {MacAddress}",
        SequenceEventType.FdbWaitFor     => $"⏳  wait   0x{Address:X8} & 0x{Mask:X8} == 0x{Expected:X8}  ({TimeoutMs}ms)",
        SequenceEventType.FdbFlush       => "🗑  FDB flush  (전체 테이블 초기화)",
        SequenceEventType.CaptureVerify  => $"📡  capture [{(string.IsNullOrEmpty(CaptureInterface) ? "any" : CaptureInterface)}]" +
                                            $"{(string.IsNullOrEmpty(CaptureFilter) ? "" : $"  \"{CaptureFilter}\"")}  ×{CaptureExpected}  {TimeoutMs}ms",
        SequenceEventType.SerialSend     => string.IsNullOrEmpty(SerialHex)
                                            ? $"→  serial send \"{SerialText}\""
                                            : $"→  serial send hex [{SerialHex}]",
        SequenceEventType.SerialVerify   => $"⏳  serial expect \"{SerialText}\"  ({TimeoutMs}ms)",
        _                                => "?"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
