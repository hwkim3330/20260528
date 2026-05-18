using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.ViewModels;

public class ProtocolPaletteItem
{
    public ProtocolType ProtocolType { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string IconKey { get; init; } = string.Empty;
}

public class BlockBuilderViewModel : ViewModelBase
{
    private PacketItem? _currentPacket;
    private ProtocolBlock? _selectedBlock;

    public ObservableCollection<ProtocolBlock> Blocks { get; private set; } = new();
    public ObservableCollection<ValidationResult> ValidationResults { get; } = new();

    public ProtocolBlock? SelectedBlock
    {
        get => _selectedBlock;
        set => SetProperty(ref _selectedBlock, value);
    }

    public ICommand RemoveBlockCommand { get; }
    public ICommand MoveBlockLeftCommand { get; }
    public ICommand MoveBlockRightCommand { get; }

    public IReadOnlyList<ProtocolPaletteItem> PaletteItems { get; } = new List<ProtocolPaletteItem>
    {
        new() { ProtocolType = ProtocolType.Ethernet,   DisplayName = "Ethernet",  Description = "Ethernet II Frame Header", IconKey = "EthernetIcon" },
        new() { ProtocolType = ProtocolType.ARP,        DisplayName = "ARP",       Description = "Address Resolution Protocol", IconKey = "ArpIcon" },
        new() { ProtocolType = ProtocolType.IPv4,       DisplayName = "IPv4",      Description = "Internet Protocol v4 Header", IconKey = "IPv4Icon" },
        new() { ProtocolType = ProtocolType.ICMP,       DisplayName = "ICMP",      Description = "Internet Control Message Protocol", IconKey = "IcmpIcon" },
        new() { ProtocolType = ProtocolType.TCP,        DisplayName = "TCP",       Description = "Transmission Control Protocol", IconKey = "TcpIcon" },
        new() { ProtocolType = ProtocolType.UDP,        DisplayName = "UDP",       Description = "User Datagram Protocol", IconKey = "UdpIcon" },
        new() { ProtocolType = ProtocolType.VLAN,       DisplayName = "VLAN",      Description = "802.1Q VLAN Tag", IconKey = "VlanIcon" },
        new() { ProtocolType = ProtocolType.RawPayload, DisplayName = "Payload",   Description = "Raw byte payload", IconKey = "PayloadIcon" },
    };

    public BlockBuilderViewModel()
    {
        RemoveBlockCommand   = new RelayCommand<ProtocolBlock>(RemoveBlock);
        MoveBlockLeftCommand = new RelayCommand<ProtocolBlock>(MoveLeft);
        MoveBlockRightCommand = new RelayCommand<ProtocolBlock>(MoveRight);
    }

    private PropertyChangedEventHandler? _packetHandler;

    public void SetPacket(PacketItem? packet)
    {
        // Unsubscribe previous
        if (_currentPacket != null && _packetHandler != null)
            _currentPacket.PropertyChanged -= _packetHandler;

        _currentPacket = packet;
        Blocks = packet?.Blocks ?? new ObservableCollection<ProtocolBlock>();
        OnPropertyChanged(nameof(Blocks));
        RefreshValidation(packet);

        if (packet != null)
        {
            _packetHandler = (_, e) =>
            {
                if (e.PropertyName == nameof(PacketItem.ValidationResults))
                    RefreshValidation(packet);
            };
            packet.PropertyChanged += _packetHandler;
        }
        else
        {
            _packetHandler = null;
        }
    }

    public void InsertBlock(ProtocolType type, int index)
    {
        _currentPacket?.InsertBlock(type, index);
    }

    private void RemoveBlock(ProtocolBlock? block)
    {
        if (block == null || _currentPacket == null) return;
        _currentPacket.Blocks.Remove(block);
        if (SelectedBlock == block)
            SelectedBlock = null;
    }

    private void MoveLeft(ProtocolBlock? block)
    {
        if (block == null || _currentPacket == null) return;
        int idx = _currentPacket.Blocks.IndexOf(block);
        if (idx > 0) _currentPacket.Blocks.Move(idx, idx - 1);
    }

    private void MoveRight(ProtocolBlock? block)
    {
        if (block == null || _currentPacket == null) return;
        int idx = _currentPacket.Blocks.IndexOf(block);
        if (idx < _currentPacket.Blocks.Count - 1)
            _currentPacket.Blocks.Move(idx, idx + 1);
    }

    private void RefreshValidation(PacketItem? packet)
    {
        ValidationResults.Clear();
        if (packet == null) return;
        foreach (var r in packet.ValidationResults)
            ValidationResults.Add(r);
    }

    public ValidationSeverity GetBlockSeverity(int blockIndex)
    {
        var result = ValidationResults.FirstOrDefault(r => r.BlockIndex == blockIndex);
        return result?.Severity ?? ValidationSeverity.None;
    }
}
