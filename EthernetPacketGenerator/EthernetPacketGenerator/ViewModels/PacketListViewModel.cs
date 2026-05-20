using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;

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
    private string _activeScenarioName = "";

    public string ActiveScenarioName
    {
        get => _activeScenarioName;
        set
        {
            SetProperty(ref _activeScenarioName, value);
            OnPropertyChanged(nameof(PacketListTitle));
            OnPropertyChanged(nameof(TestSequenceTitle));
        }
    }

    private bool _isScenarioMode;
    public bool IsScenarioMode
    {
        get => _isScenarioMode;
        set
        {
            SetProperty(ref _isScenarioMode, value);
            OnPropertyChanged(nameof(PacketListTitle));
            OnPropertyChanged(nameof(TestSequenceTitle));
        }
    }

    /// <summary>
    /// 시나리오 패킷 리스트에서 표시할 예상 시간 레이블.
    /// 외부(TestCaseManagerViewModel)에서 주입. null이면 SendViewModel.EstimatedTimeMs 사용.
    /// </summary>
    private string? _injectedEstimatedTimeMs;
    public string? InjectedEstimatedTimeMs
    {
        get => _injectedEstimatedTimeMs;
        set
        {
            SetProperty(ref _injectedEstimatedTimeMs, value);
            OnPropertyChanged(nameof(EstimatedLabelText));
            OnPropertyChanged(nameof(HasInjectedEstimated));
        }
    }

    /// <summary>InjectedEstimatedTimeMs가 있으면 true → PacketListView가 바인딩 전환.</summary>
    public bool HasInjectedEstimated => !string.IsNullOrEmpty(_injectedEstimatedTimeMs);

    /// <summary>표시용 예상 시간 문자열 (주입값 우선).</summary>
    public string EstimatedLabelText => _injectedEstimatedTimeMs ?? "-";

    /// <summary>
    /// TestSequence에 TC가 있는지 여부 — PacketListView 레이블 "예상" vs "합산 예상" 전환용.
    /// 외부에서 주입.
    /// </summary>
    private bool _suppressRebuild;   // LoadSequence 중 CollectionChanged → RebuildEthernetSequence 일괄 억제
    private bool _hasTestSequence;
    public bool HasTestSequence
    {
        get => _hasTestSequence;
        set => SetProperty(ref _hasTestSequence, value);
    }

    // PacketGenerator 탭용 (이더넷 전용 뷰)
    public string PacketListTitle =>
        _isScenarioMode
            ? (string.IsNullOrEmpty(_activeScenarioName) ? "Packet List" : $"Packet List - {_activeScenarioName}")
            : "Packet List";

    // ScenarioLab 탭용 (전체 시퀀스 뷰)
    public string TestSequenceTitle =>
        _isScenarioMode
            ? (string.IsNullOrEmpty(_activeScenarioName) ? "TEST SEQUENCE" : $"TEST SEQUENCE - {_activeScenarioName}")
            : "TEST SEQUENCE";

    // Flat sequence: packets + events interleaved (full, used by Scenario Lab)
    public ObservableCollection<SequenceItem> Sequence { get; } = new();

    // Ethernet-only view (used by Packet Generator tab)
    public ObservableCollection<SequenceItem> EthernetSequence { get; } = new();

    // 이더넷 패킷만 기준으로 계산한 예상 전송 시간 (패킷 제너레이터 탭 전용)
    private string _ethernetEstimatedTimeMs = "-";
    public string EthernetEstimatedTimeMs
    {
        get => _ethernetEstimatedTimeMs;
        private set => SetProperty(ref _ethernetEstimatedTimeMs, value);
    }

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
    public ICommand AddDelayEventCommand      { get; }
    public ICommand AddRegWriteCommand        { get; }
    public ICommand AddRegReadCommand         { get; }
    public ICommand AddRegVerifyCommand          { get; }
    public ICommand AddFdbWriteCommand        { get; }
    public ICommand AddFdbWriteBucketCommand  { get; }
    public ICommand AddFdbReadCommand         { get; }
    public ICommand AddFdbReadBucketCommand   { get; }
    public ICommand AddFdbFlushCommand        { get; }
    public ICommand AddRxVerifyCommand        { get; }

    public PacketListViewModel()
    {
        AddPacketCommand          = new RelayCommand(AddPacket);
        DeleteItemCommand         = new RelayCommand(DeleteItem,         () => SelectedSequenceItem != null);
        DeleteCheckedItemsCommand = new RelayCommand(DeleteCheckedItems, () => Sequence.Any(s => s.IsChecked));
        DuplicatePacketCommand    = new RelayCommand(DuplicatePacket,    () => SelectedPacket != null);
        MoveUpCommand          = new RelayCommand(MoveUp,          CanMoveUp);
        MoveDownCommand        = new RelayCommand(MoveDown,        CanMoveDown);
        AddDelayEventCommand     = new RelayCommand(AddDelayEvent);
        AddRegWriteCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegWrite       }));
        AddRegReadCommand        = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegRead        }));
        AddRegVerifyCommand         = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RegVerify         }));
        AddFdbWriteCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbWrite       }));
        AddFdbWriteBucketCommand = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbWriteBucket }));
        AddFdbReadCommand        = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbRead        }));
        AddFdbReadBucketCommand  = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbReadBucket  }));
        AddFdbFlushCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.FdbFlush       }));
        AddRxVerifyCommand       = new RelayCommand(() => InsertEvent(new SequenceEvent { EventType = SequenceEventType.RxVerify,  TimeoutMs = 2000 }));

        Sequence.CollectionChanged += (_, _) =>
        {
            if (_suppressRebuild) return;
            ReIndex();
            RebuildEthernetSequence();
        };

        AddPacket();
    }

    private void ReIndex()
    {
        for (int i = 0; i < Sequence.Count; i++)
            Sequence[i].Index = i;
    }

    private void RebuildEthernetSequence()
    {
        // Unsubscribe old packet change listeners
        foreach (var item in EthernetSequence)
            if (item.Packet != null) item.Packet.PropertyChanged -= OnEthernetPacketChanged;

        EthernetSequence.Clear();
        foreach (var item in Sequence.Where(s => s.Kind == SequenceItemKind.Packet))
        {
            EthernetSequence.Add(item);
            if (item.Packet != null)
                item.Packet.PropertyChanged += OnEthernetPacketChanged;
        }
        RecalcEthernetEstimatedTime();
    }

    private void OnEthernetPacketChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketItem.TotalLength))
            RecalcEthernetEstimatedTime();
    }

    private void RecalcEthernetEstimatedTime()
    {
        if (EthernetSequence.Count == 0) { EthernetEstimatedTimeMs = "-"; return; }
        double totalMs = EthernetSequence
            .Where(s => s.Packet != null)
            .Sum(s => EthernetTiming.WireTimeMs(s.Packet!.TotalLength));
        EthernetEstimatedTimeMs = $"{totalMs:F3} ms";
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
        // 리빌드 억제: Clear + Add × N 반복으로 EthernetSequence가 N+1번 초기화되는 것을 방지
        _suppressRebuild = true;
        try
        {
            Sequence.Clear();
            foreach (var item in items)
                Sequence.Add(item);
        }
        finally
        {
            _suppressRebuild = false;
        }
        // 전체 로드 완료 후 한 번만 재인덱스 + 리빌드
        ReIndex();
        RebuildEthernetSequence();
        SelectedSequenceItem = Sequence.FirstOrDefault(s => s.Kind == SequenceItemKind.Packet);
    }
}
