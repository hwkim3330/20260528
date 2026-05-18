using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public enum SequenceItemKind { Packet, Event }

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

    // Flat properties for direct ListView column binding (avoids nested path refresh issues)
    public string DisplayName        => Packet?.Name            ?? EventName;
    public string DisplaySrcMac      => Packet?.SrcMac          ?? EventTarget;
    public string DisplayDstMac      => Packet?.DstMac          ?? EventValue;
    public string DisplayProtocol    => Packet?.ProtocolSummary ?? EventParameters;
    public string DisplayDescription => Packet?.PacketDescription ?? EventDescription;
    public string DisplayInterface   => Packet?.OutgoingInterfaceDisplay ?? "";

    private string EventName => Event?.EventType switch
    {
        SequenceEventType.Delay => "Delay",
        SequenceEventType.RegWrite => "Reg Write",
        SequenceEventType.RegRead => "Reg Read",
        SequenceEventType.RegWaitFor => "Reg Wait",
        SequenceEventType.FdbWrite => "FDB Write",
        SequenceEventType.FdbRead => "FDB Read",
        SequenceEventType.FdbWaitFor => "FDB Wait",
        SequenceEventType.FdbFlush => "FDB Flush",
        _ => Event?.DisplayLabel ?? ""
    };

    private string EventTarget => Event?.EventType switch
    {
        SequenceEventType.RegWrite or SequenceEventType.RegRead or SequenceEventType.RegWaitFor or SequenceEventType.FdbWaitFor
            => $"Addr 0x{Event.Address:X8}",
        SequenceEventType.FdbWrite or SequenceEventType.FdbRead
            => Event.MacAddress,
        SequenceEventType.Delay
            => $"{Event.DelayMs} ms",
        _ => ""
    };

    private string EventValue => Event?.EventType switch
    {
        SequenceEventType.RegWrite => $"Value 0x{Event.Value:X8}",
        SequenceEventType.RegWaitFor or SequenceEventType.FdbWaitFor => $"Expected 0x{Event.Expected:X8}",
        SequenceEventType.FdbWrite => $"Port 0b{Convert.ToString(Event.Port, 2).PadLeft(4, '0')}",
        SequenceEventType.FdbRead => "Read by MAC",
        SequenceEventType.FdbFlush => "All entries",
        _ => ""
    };

    private string EventParameters => Event?.EventType switch
    {
        SequenceEventType.RegWaitFor or SequenceEventType.FdbWaitFor => $"Mask 0x{Event.Mask:X8} / {Event.TimeoutMs}ms",
        SequenceEventType.FdbWrite or SequenceEventType.FdbRead => Event.VlanValid ? $"VLAN {Event.VlanId}" : "No VLAN",
        SequenceEventType.Delay => "Timer",
        SequenceEventType.RegWrite => "Register write",
        SequenceEventType.RegRead => "Register read",
        SequenceEventType.FdbFlush => "FDB table",
        _ => ""
    };

    private string EventDescription => Event?.EventType switch
    {
        SequenceEventType.Delay => $"Wait for {Event.DelayMs} ms before next step.",
        SequenceEventType.RegWrite => $"Write 0x{Event.Value:X8} to register 0x{Event.Address:X8}.",
        SequenceEventType.RegRead => $"Read register 0x{Event.Address:X8}.",
        SequenceEventType.RegWaitFor => $"Poll 0x{Event.Address:X8} until (value & 0x{Event.Mask:X8}) == 0x{Event.Expected:X8}.",
        SequenceEventType.FdbWrite => $"Write MAC {Event.MacAddress} to port bitmap 0b{Convert.ToString(Event.Port, 2).PadLeft(4, '0')}.",
        SequenceEventType.FdbRead => $"Read FDB entry for MAC {Event.MacAddress}.",
        SequenceEventType.FdbWaitFor => $"Wait for FDB/register condition at 0x{Event.Address:X8}.",
        SequenceEventType.FdbFlush => "Flush the FDB table.",
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
