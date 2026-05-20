using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public enum SequenceItemKind { Packet, Event }

public enum PacketTestResult { None, Running, Pass, Fail }

public class SequenceItem : INotifyPropertyChanged
{
    public SequenceItemKind Kind   { get; }
    public PacketItem?      Packet { get; }
    public SequenceEvent?   Event  { get; }

    // 0-based index set by the list when collection changes
    private int _index;
    public int Index
    {
        get => _index;
        set { _index = value; OnPropertyChanged(); }
    }

    // Check state for "Send Selected" — only meaningful for Packet rows
    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            _isChecked = value;
            OnPropertyChanged();
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                System.Windows.Input.CommandManager.InvalidateRequerySuggested,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    // Packet 행 전용 테스트 결과
    private PacketTestResult _testResult = PacketTestResult.None;
    public PacketTestResult TestResult
    {
        get => _testResult;
        set { _testResult = value; OnPropertyChanged(); OnPropertyChanged(nameof(TestResultText)); }
    }

    public string TestResultText => _testResult switch
    {
        PacketTestResult.Running => "⏳",
        PacketTestResult.Pass    => "Done",
        PacketTestResult.Fail    => "Fail",
        _                        => ""
    };

    // Flat properties for direct ListView column binding (avoids nested path refresh issues)
    public string DisplayName        => Packet?.Name            ?? EventName;
    public string DisplaySrcMac      => Packet?.SrcMac          ?? EventTarget;
    public string DisplayDstMac      => Packet?.DstMac          ?? EventValue;
    public string DisplayProtocol    => Packet?.ProtocolSummary ?? EventParameters;
    public string DisplayDescription => Packet?.PacketDescription ?? EventDescription;
    public string DisplayInterface   => Packet?.OutgoingInterfaceDisplay ?? "";

    private string EventName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Event?.Label)) return Event.Label;
            return Event?.EventType switch
            {
                SequenceEventType.Delay          => "Delay",
                SequenceEventType.RegWrite        => "Reg Write",
                SequenceEventType.RegRead         => "Reg Read",
                SequenceEventType.RegVerify          => "Reg Verify",
                SequenceEventType.FdbWrite        => "FDB Write",
                SequenceEventType.FdbWriteBucket  => "FDB Write(Bucket)",
                SequenceEventType.FdbRead         => "FDB Read",
                SequenceEventType.FdbReadBucket   => "FDB Read(Bucket)",
                SequenceEventType.FdbWaitFor      => "FDB Wait",
                SequenceEventType.FdbFlush        => "FDB Initialize",
                SequenceEventType.RxVerify        => "RX Verify",
                _ => Event?.DisplayLabel ?? ""
            };
        }
    }

    private string EventTarget => Event?.EventType switch
    {
        SequenceEventType.RegWrite or SequenceEventType.RegRead or SequenceEventType.RegVerify
            => $"Addr 0x{Event.Address:X8}",
        SequenceEventType.FdbWrite or SequenceEventType.FdbRead or SequenceEventType.FdbWaitFor
            => Event.MacAddress,
        SequenceEventType.FdbWriteBucket
            => Event.MacAddress,
        SequenceEventType.FdbReadBucket
            => $"B:{Event.Bucket} S:0x{Event.SlotBitmap:X}",
        SequenceEventType.Delay
            => $"{Event.DelayMs} ms",
        SequenceEventType.RxVerify
            => string.IsNullOrWhiteSpace(Event.ExpectedDstMac)
                ? $"Timeout {Event.TimeoutMs} ms"
                : $"DA={Event.ExpectedDstMac}",
        _ => ""
    };

    private string EventValue => Event?.EventType switch
    {
        SequenceEventType.RegWrite    => $"Value 0x{Event.Value:X8}",
        SequenceEventType.RegVerify      => $"Expected 0x{Event.Expected:X8}",
        SequenceEventType.FdbWrite or SequenceEventType.FdbWaitFor
            => $"Port 0b{Convert.ToString(Event.Port, 2).PadLeft(6, '0')}",
        SequenceEventType.FdbWriteBucket
            => $"Port 0b{Convert.ToString(Event.Port, 2).PadLeft(6, '0')}",
        SequenceEventType.FdbRead     => "Read by MAC",
        SequenceEventType.FdbReadBucket => "Read by bucket",
        SequenceEventType.FdbFlush    => "All entries",
        SequenceEventType.RxVerify
            => string.IsNullOrWhiteSpace(Event.ExpectedDstMac)
                ? ""
                : $"Timeout {Event.TimeoutMs} ms",
        _ => ""
    };

    private string EventParameters => Event?.EventType switch
    {
        SequenceEventType.RegVerify      => $"Mask 0x{Event.Mask:X8} / {Event.TimeoutMs}ms",
        SequenceEventType.FdbWaitFor  => Event.VlanValid ? $"VLAN {Event.VlanId} / {Event.TimeoutMs}ms" : $"{Event.TimeoutMs}ms",
        SequenceEventType.FdbWrite or SequenceEventType.FdbRead
            => Event.VlanValid ? $"VLAN {Event.VlanId}" : "No VLAN",
        SequenceEventType.FdbWriteBucket
            => $"B:{Event.Bucket} S:0x{Event.SlotBitmap:X}",
        SequenceEventType.FdbReadBucket
            => "",
        SequenceEventType.Delay       => "Timer",
        SequenceEventType.RegWrite    => "Register write",
        SequenceEventType.RegRead     => "Register read",
        SequenceEventType.FdbFlush    => "FDB table",
        SequenceEventType.RxVerify    => "RX Verify",
        _ => ""
    };

    private string EventDescription => Event?.EventType switch
    {
        SequenceEventType.Delay
            => $"Wait for {Event.DelayMs} ms before next step.",
        SequenceEventType.RegWrite
            => $"Write 0x{Event.Value:X8} to register 0x{Event.Address:X8}.",
        SequenceEventType.RegRead
            => $"Read register 0x{Event.Address:X8}.",
        SequenceEventType.RegVerify
            => $"Poll 0x{Event.Address:X8} until (value & 0x{Event.Mask:X8}) == 0x{Event.Expected:X8}.",
        SequenceEventType.FdbWrite
            => $"Write MAC {Event.MacAddress} to port bitmap 0b{Convert.ToString(Event.Port, 2).PadLeft(6, '0')}.",
        SequenceEventType.FdbWriteBucket
            => $"Write MAC {Event.MacAddress} port 0b{Convert.ToString(Event.Port, 2).PadLeft(6, '0')} → B:{Event.Bucket} S:0x{Event.SlotBitmap:X}.",
        SequenceEventType.FdbRead
            => $"Read FDB entry for MAC {Event.MacAddress}.",
        SequenceEventType.FdbReadBucket
            => string.IsNullOrWhiteSpace(Event.FdbExpectedMac)
                ? $"Read FDB entry at bucket {Event.Bucket} slot 0x{Event.SlotBitmap:X}."
                : $"Verify bucket {Event.Bucket} slot 0x{Event.SlotBitmap:X}: exp MAC={Event.FdbExpectedMac}.",
        SequenceEventType.FdbWaitFor
            => $"Wait for FDB entry {Event.MacAddress} port 0b{Convert.ToString(Event.Port, 2).PadLeft(6, '0')} (timeout {Event.TimeoutMs}ms).",
        SequenceEventType.FdbFlush
            => "Flush the FDB table.",
        SequenceEventType.RxVerify
            => string.IsNullOrWhiteSpace(Event.ExpectedDstMac)
                ? $"Verify received packet DA (prev FdbWrite MAC), timeout {Event.TimeoutMs} ms."
                : $"Verify received packet DA={Event.ExpectedDstMac}, timeout {Event.TimeoutMs} ms.",
        _ => Event?.DisplayLabel ?? ""
    };

    public SequenceItem(PacketItem packet)
    {
        Kind   = SequenceItemKind.Packet;
        Packet = packet;
        packet.PropertyChanged += OnPacketChanged;
    }

    public SequenceItem(SequenceEvent ev)
    {
        Kind  = SequenceItemKind.Event;
        Event = ev;
        ev.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(DisplaySrcMac));
            OnPropertyChanged(nameof(DisplayDstMac));
            OnPropertyChanged(nameof(DisplayProtocol));
            OnPropertyChanged(nameof(DisplayDescription));
        };
    }

    private void OnPacketChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplaySrcMac));
        OnPropertyChanged(nameof(DisplayDstMac));
        OnPropertyChanged(nameof(DisplayProtocol));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(DisplayInterface));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
