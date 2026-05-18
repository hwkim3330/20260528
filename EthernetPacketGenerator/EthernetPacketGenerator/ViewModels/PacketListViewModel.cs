using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.ViewModels;

/// <summary>PacketList Interface 팝업의 체크박스 항목 — 특정 패킷+인터페이스 조합</summary>
public class PacketInterfaceCheckItem : INotifyPropertyChanged
{
    private bool _isChecked;
    public string ShortName { get; }
    public PacketItem Packet { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();
            if (value) Packet.OutgoingInterfaceNames.Add(ShortName);
            else       Packet.OutgoingInterfaceNames.Remove(ShortName);
            Packet.OnOutgoingInterfaceChanged();
        }
    }

    public PacketInterfaceCheckItem(PacketItem packet, string shortName)
    {
        Packet    = packet;
        ShortName = shortName;
        _isChecked = packet.OutgoingInterfaceNames.Contains(shortName);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class PacketListViewModel : ViewModelBase
{
    private PacketItem? _selectedPacket;
    private SequenceItem? _selectedSequenceItem;
    private ObservableCollection<InterfaceEntry> _interfaceEntries = new();

    // Flat sequence: packets + events interleaved
    public ObservableCollection<SequenceItem> Sequence { get; } = new();

    /// <summary>SendViewModel.InterfaceEntries 참조 — PacketListView의 Interface 드롭다운용</summary>
    public ObservableCollection<InterfaceEntry> InterfaceEntries
    {
        get => _interfaceEntries;
        set
        {
            _interfaceEntries.CollectionChanged -= OnInterfaceEntriesChanged;
            SetProperty(ref _interfaceEntries, value ?? new());
            _interfaceEntries.CollectionChanged += OnInterfaceEntriesChanged;
            OnPropertyChanged(nameof(InterfaceOptions));
        }
    }

    private void OnInterfaceEntriesChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(InterfaceOptions));

    public IEnumerable<InterfaceEntry> InterfaceOptions =>
        Enumerable.Repeat(
            new InterfaceEntry(null!, "") { IsDefaultSentinel = true }, 1)
        .Concat(_interfaceEntries);

    // Convenience view of only PacketItems (for Send, HexDump, etc.)
    public IEnumerable<PacketItem> Packets => Sequence
        .Where(s => s.Kind == SequenceItemKind.Packet)
        .Select(s => s.Packet!);

    public PacketItem? SelectedPacket
    {
        get => _selectedPacket;
        set => SetProperty(ref _selectedPacket, value);
    }

    public SequenceItem? SelectedSequenceItem
    {
        get => _selectedSequenceItem;
        set
        {
            SetProperty(ref _selectedSequenceItem, value);
            SelectedPacket = value?.Kind == SequenceItemKind.Packet ? value.Packet : null;
        }
    }

    public ICommand AddPacketCommand          { get; }
    public ICommand DeleteItemCommand         { get; }
    public ICommand DeleteCheckedItemsCommand { get; }
    public ICommand DuplicatePacketCommand    { get; }
    public ICommand MoveUpCommand             { get; }
    public ICommand MoveDownCommand           { get; }
    public ICommand AddDelayEventCommand    { get; }
    public ICommand AddRegWriteCommand      { get; }
    public ICommand AddRegReadCommand       { get; }
    public ICommand AddRegWaitForCommand    { get; }
    public ICommand AddFdbWriteCommand      { get; }
    public ICommand AddFdbReadCommand       { get; }
    public ICommand AddFdbWaitForCommand    { get; }
    public ICommand AddFdbFlushCommand      { get; }
    public ICommand AddCaptureVerifyCommand { get; }
    public ICommand AddSerialSendCommand    { get; }
    public ICommand AddSerialVerifyCommand  { get; }

    public PacketListViewModel()
    {
        AddPacketCommand          = new RelayCommand(AddPacket);
        DeleteItemCommand         = new RelayCommand(DeleteItem,         () => SelectedSequenceItem != null);
        DeleteCheckedItemsCommand = new RelayCommand(DeleteCheckedItems, () => Sequence.Any(s => s.IsChecked));
        DuplicatePacketCommand    = new RelayCommand(DuplicatePacket,    () => SelectedPacket != null);
        MoveUpCommand          = new RelayCommand(MoveUp,          CanMoveUp);
        MoveDownCommand        = new RelayCommand(MoveDown,        CanMoveDown);
        AddDelayEventCommand    = new RelayCommand(AddDelayEvent);
        AddRegWriteCommand      = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegWrite    }));
        AddRegReadCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegRead     }));
        AddRegWaitForCommand    = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegWaitFor  }));
        AddFdbWriteCommand      = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbWrite    }));
        AddFdbReadCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbRead     }));
        AddFdbWaitForCommand    = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbWaitFor  }));
        AddFdbFlushCommand      = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbFlush    }));
        AddCaptureVerifyCommand = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.CaptureVerify, TimeoutMs = 3000, CaptureExpected = 1 }));
        AddSerialSendCommand    = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.SerialSend   }));
        AddSerialVerifyCommand  = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.SerialVerify, TimeoutMs = 5000 }));

        Sequence.CollectionChanged += (_, _) => ReIndex();

        AddPacket();
    }

    private void ReIndex()
    {
        for (int i = 0; i < Sequence.Count; i++)
            Sequence[i].Index = i;
    }

    public void AddPacket()
    {
        int insertAt = SelectedSequenceItem != null
            ? Sequence.IndexOf(SelectedSequenceItem) + 1
            : Sequence.Count;

        var packet = new PacketItem { Name = $"Packet{insertAt}" };
        var item = new SequenceItem(packet);

        Sequence.Insert(insertAt, item);
        SelectedSequenceItem = item;
    }

    private void AddDelayEvent() =>
        InsertEvent(new SequenceEvent { DelayMs = 100 });

    private void InsertEvent(SequenceEvent ev)
    {
        var item     = new SequenceItem(ev);
        int insertAt = SelectedSequenceItem != null
            ? Sequence.IndexOf(SelectedSequenceItem) + 1
            : Sequence.Count;
        Sequence.Insert(insertAt, item);
        SelectedSequenceItem = item;
    }

    private void DeleteItem()
    {
        if (SelectedSequenceItem == null) return;
        int idx = Sequence.IndexOf(SelectedSequenceItem);
        Sequence.Remove(SelectedSequenceItem);

        if (Sequence.Count > 0)
            SelectedSequenceItem = Sequence[Math.Max(0, idx - 1)];
        else
            SelectedSequenceItem = null;
    }

    private void DeleteCheckedItems()
    {
        var toRemove = Sequence.Where(s => s.IsChecked).ToList();
        if (toRemove.Count == 0) return;

        int firstIdx = Sequence.IndexOf(toRemove[0]);
        foreach (var item in toRemove)
            Sequence.Remove(item);

        if (Sequence.Count > 0)
            SelectedSequenceItem = Sequence[Math.Min(firstIdx, Sequence.Count - 1)];
        else
            SelectedSequenceItem = null;
    }

    private void DuplicatePacket()
    {
        if (SelectedPacket == null) return;
        var clone = new PacketItem();
        foreach (var block in SelectedPacket.Blocks)
        {
            var newBlock = PacketItem.CreateBlock(block.Type);
            newBlock.ImportBytes(block.Bytes, 0);
            clone.Blocks.Add(newBlock);
        }
        int idx = Sequence.IndexOf(SelectedSequenceItem!);
        int insertAt = idx + 1;
        clone.Name = $"Packet{insertAt}";
        var item = new SequenceItem(clone);
        Sequence.Insert(insertAt, item);
        SelectedSequenceItem = item;
    }

    private void MoveUp()
    {
        if (SelectedSequenceItem == null) return;
        int idx = Sequence.IndexOf(SelectedSequenceItem);
        if (idx > 0) Sequence.Move(idx, idx - 1);
    }

    private void MoveDown()
    {
        if (SelectedSequenceItem == null) return;
        int idx = Sequence.IndexOf(SelectedSequenceItem);
        if (idx < Sequence.Count - 1) Sequence.Move(idx, idx + 1);
    }

    private bool CanMoveUp()   => SelectedSequenceItem != null && Sequence.IndexOf(SelectedSequenceItem) > 0;
    private bool CanMoveDown() => SelectedSequenceItem != null && Sequence.IndexOf(SelectedSequenceItem) < Sequence.Count - 1;

    public void LoadSequence(IEnumerable<SequenceItem> items)
    {
        Sequence.Clear();
        foreach (var item in items)
            Sequence.Add(item);
        SelectedSequenceItem = Sequence.FirstOrDefault(s => s.Kind == SequenceItemKind.Packet);
    }

    public void AddEventForApi(SequenceEvent ev)
    {
        var item = new SequenceItem(ev);
        Sequence.Add(item);
        SelectedSequenceItem = item;
    }

    public void RemoveEventForApi(int index)
    {
        if (index >= 0 && index < Sequence.Count)
            Sequence.RemoveAt(index);
    }

    public void ClearEventsForApi()
    {
        var toRemove = Sequence.Where(s => s.Kind == SequenceItemKind.Event).ToList();
        foreach (var item in toRemove) Sequence.Remove(item);
    }
}
