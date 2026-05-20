using System.Text;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using Microsoft.Win32;

namespace EthernetPacketGenerator.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly Services.SerialPortService _serial = new();

    private ProtocolBlock? _selectedBlock;
    private int _selectedTabIndex;
    private bool _suppressPacketListSync = false;
    private bool _isSyncingSelection     = false;  // 선택 동기화 재귀 방지
    private PacketItem? _activePacket;              // 마지막으로 선택한 패킷 — 탭 전환 복원용
    private readonly StringBuilder _seqTerminalBuffer = new();
    private string _sequenceTerminalLog = "";

    // Tab indices: 0=PacketGenerator, 1=ScenarioLab, 2=Capture, 3=HyperTerminal, 4=Settings

    /// <summary>패킷 제너레이터 탭 전용 패킷 리스트</summary>
    public PacketListViewModel         PacketListVM         { get; } = new();
    /// <summary>시나리오 랩 탭 전용 패킷 리스트 (패킷 제너레이터 탭과 완전 분리)</summary>
    public PacketListViewModel         ScenarioPacketListVM { get; } = new();
    public BlockBuilderViewModel       BlockBuilderVM       { get; } = new();
    public HexDumpViewModel            HexDumpVM            { get; } = new();
    public TreeDecodeViewModel         TreeDecodeVM         { get; } = new();
    /// <summary>패킷 제너레이터 탭 전용 — EthernetSequence(이더넷 패킷만) 기반</summary>
    public SendViewModel               PacketGenSendVM      { get; }
    /// <summary>시나리오 랩 탭 전용 — 전체 Sequence(이벤트 포함) 기반</summary>
    public SendViewModel               ScenarioSendVM       { get; }
    public HyperTerminalViewModel      HyperTerminalVM      { get; }
    public TestCaseManagerViewModel    TestCaseMgrVM        { get; }
    public PacketFlowMonitorViewModel  PacketFlowMonitorVM  { get; } = new();
    public CaptureViewModel            CaptureVM            { get; } = new();
    public AutomationViewModel         AutomationVM         { get; }

    public ProtocolBlock? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            SetProperty(ref _selectedBlock, value);
            BlockBuilderVM.SelectedBlock = value;
            HexDumpVM.SetHighlightedBlock(value);
        }
    }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            // Suppress spurious PacketListVM.SelectedPacket fires that occur when
            // the Packet Generator tab's PacketListView re-enters the visual tree
            // (WPF fires Loaded → ApplySequenceBinding → ItemsSource reset → SelectedItem reset)
            _suppressPacketListSync = true;
            SetProperty(ref _selectedTabIndex, value);

            var newTab         = value;
            var scenarioPacket = ScenarioPacketListVM.SelectedPacket;
            var pgPacket       = PacketListVM.SelectedPacket;

            // After all Loaded/Render events fire (which reset SelectedItem and override
            // BlockBuilderVM), re-apply the correct packet in Background priority.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() =>
                {
                    _suppressPacketListSync = false;
                    // _activePacket = 어느 탭에서든 마지막으로 선택한 패킷.
                    // 이것을 기준으로 BlockBuilder를 복원하고 목적지 탭 VM 선택도 동기화.
                    var restore = _activePacket
                        ?? (newTab == 1 ? scenarioPacket : (scenarioPacket ?? pgPacket));

                    if (restore != null)
                    {
                        // 목적지 VM에서 같은 패킷 객체를 찾아 선택 동기화 (공유된 경우만 적용)
                        _suppressPacketListSync = true;
                        if (newTab == 1)
                        {
                            var item = ScenarioPacketListVM.Sequence
                                .FirstOrDefault(s => s.Kind == SequenceItemKind.Packet && s.Packet == restore);
                            if (item != null && ScenarioPacketListVM.SelectedPacket != restore)
                                ScenarioPacketListVM.SelectedSequenceItem = item;
                        }
                        else
                        {
                            var item = PacketListVM.Sequence
                                .FirstOrDefault(s => s.Kind == SequenceItemKind.Packet && s.Packet == restore);
                            if (item != null && PacketListVM.SelectedPacket != restore)
                                PacketListVM.SelectedSequenceItem = item;
                        }
                        _suppressPacketListSync = false;
                        OnSelectedPacketChanged(restore);
                    }
                }));
        }
    }

    public string SequenceTerminalLog
    {
        get => _sequenceTerminalLog;
        private set => SetProperty(ref _sequenceTerminalLog, value);
    }

    public ICommand SaveCommand                    { get; }
    public ICommand LoadCommand                    { get; }
    public ICommand ClearSequenceTerminalCommand   { get; }
    public ICommand LoadTcIntoGeneratorCommand     { get; }

    public MainViewModel()
    {
        HyperTerminalVM  = new HyperTerminalViewModel(_serial);

        // 패킷 제너레이터: 이더넷 패킷만 전송
        PacketGenSendVM  = new SendViewModel(_serial);
        PacketGenSendVM.AttachCapture(CaptureVM);

        // 시나리오 랩: 전체 시퀀스(이벤트 포함) 전송 — 패킷 제너레이터 탭과 완전 분리
        ScenarioSendVM   = new SendViewModel(_serial);
        ScenarioSendVM.AttachCapture(CaptureVM);

        // TestCaseMgrVM은 ScenarioPacketListVM을 사용 (패킷 제너레이터 탭과 분리)
        TestCaseMgrVM   = new TestCaseManagerViewModel(ScenarioPacketListVM);
        TestCaseMgrVM.AttachCapture(CaptureVM);
        TestCaseMgrVM.AttachSendViewModel(ScenarioSendVM);
        AutomationVM    = new AutomationViewModel(ScenarioPacketListVM, TestCaseMgrVM, PacketFlowMonitorVM);

        SaveCommand = new RelayCommand(Save, () => PacketListVM.Packets.Any());
        LoadCommand = new RelayCommand(Load);
        ClearSequenceTerminalCommand = new RelayCommand(() =>
        {
            _seqTerminalBuffer.Clear();
            SequenceTerminalLog = "";
        });
        // TC 선택기: ScenarioPacketListVM에 먼저 로드 후 동일 객체 참조를 PacketListVM에 공유
        // → 패킷 제너레이터에서 편집하면 Scenario Lab에도 즉시 반영됨
        LoadTcIntoGeneratorCommand = new RelayCommand<TestCaseEntry>(tc =>
        {
            if (tc == null) return;
            TestCaseMgrVM.SelectTestCase(tc);                         // ScenarioPacketListVM에 로드
            PacketListVM.LoadSequence(ScenarioPacketListVM.Sequence); // 동일 객체를 PacketListVM에도 공유
        });

        Action<string> logCallback = line =>
            System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                HyperTerminalVM.AppendTerminal(line);
                AppendSequenceTerminal(line);
            });
        PacketGenSendVM.SetLogCallback(logCallback);
        ScenarioSendVM.SetLogCallback(logCallback);

        // ScenarioSendVM.IsSending이 변해도 IsRunningSequence는 건드리지 않음
        // — IsRunningSequence는 RunTestSequenceCommand에서만 제어됨

        // PG 탭에서 패킷 선택 → BlockBuilder 갱신 + Scenario Lab 선택 동기화
        PacketListVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PacketListViewModel.SelectedPacket)) return;
            if (_suppressPacketListSync || _isSyncingSelection) return;

            var pkt = PacketListVM.SelectedPacket;
            if (pkt != null) _activePacket = pkt;
            OnSelectedPacketChanged(pkt);

            // Scenario Lab의 선택도 동일 패킷으로 맞춤 (공유 객체인 경우에만 적용)
            if (pkt != null && ScenarioPacketListVM.SelectedPacket != pkt)
            {
                var item = ScenarioPacketListVM.Sequence
                    .FirstOrDefault(s => s.Kind == SequenceItemKind.Packet && s.Packet == pkt);
                if (item != null)
                {
                    _isSyncingSelection = true;
                    ScenarioPacketListVM.SelectedSequenceItem = item;
                    _isSyncingSelection = false;
                }
            }
        };

        // Scenario Lab 탭에서 패킷 선택 → BlockBuilder 갱신 + PG 선택 동기화
        ScenarioPacketListVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PacketListViewModel.SelectedPacket)) return;
            if (_isSyncingSelection) return;

            var pkt = ScenarioPacketListVM.SelectedPacket;
            if (pkt == null) return;

            if (_selectedTabIndex == 1)
            {
                _activePacket = pkt;
                OnSelectedPacketChanged(pkt);
            }

            // PG의 선택도 동일 패킷으로 맞춤 (공유 객체인 경우에만 적용)
            if (PacketListVM.SelectedPacket != pkt)
            {
                var item = PacketListVM.Sequence
                    .FirstOrDefault(s => s.Kind == SequenceItemKind.Packet && s.Packet == pkt);
                if (item != null)
                {
                    _isSyncingSelection = true;
                    PacketListVM.SelectedSequenceItem = item;
                    _isSyncingSelection = false;
                }
            }
        };

        // 패킷 제너레이터: EthernetSequence(이더넷 패킷만)
        PacketGenSendVM.SetSequence(PacketListVM.EthernetSequence);
        // 시나리오 랩: ScenarioPacketListVM의 전체 Sequence(이벤트 포함)
        ScenarioSendVM.SetSequence(ScenarioPacketListVM.Sequence);

        // 인터페이스 목록: 패킷 제너레이터는 PacketGenSendVM, 시나리오 랩은 ScenarioSendVM
        PacketListVM.InterfaceEntries         = PacketGenSendVM.InterfaceEntries;
        ScenarioPacketListVM.InterfaceEntries = ScenarioSendVM.InterfaceEntries;

        if (PacketListVM.SelectedPacket != null)
            OnSelectedPacketChanged(PacketListVM.SelectedPacket);
    }

    private void AppendSequenceTerminal(string line)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        _seqTerminalBuffer.AppendLine($"[{ts}] {line}");
        if (_seqTerminalBuffer.Length > 80_000)
        {
            var text = _seqTerminalBuffer.ToString();
            var cutAt = text.IndexOf('\n', text.Length / 2);
            _seqTerminalBuffer.Clear();
            if (cutAt >= 0) _seqTerminalBuffer.Append(text[(cutAt + 1)..]);
        }
        SequenceTerminalLog = _seqTerminalBuffer.ToString();
    }

    private void OnSelectedPacketChanged(PacketItem? packet)
    {
        BlockBuilderVM.SetPacket(packet);
        HexDumpVM.SetPacket(packet);
        TreeDecodeVM.SetPacket(packet);
        SelectedBlock = null;
    }

    private void Save()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Packet Generator Files (*.epg)|*.epg|All Files (*.*)|*.*",
            DefaultExt = "epg"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            PacketSerializationService.Save(PacketListVM.Packets, dlg.FileName);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Save failed: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void Load()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Packet Generator Files (*.epg)|*.epg|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var packets = PacketSerializationService.Load(dlg.FileName);
            PacketListVM.Sequence.Clear();
            foreach (var p in packets)
                PacketListVM.Sequence.Add(new SequenceItem(p));

            if (PacketListVM.Packets.Any())
                PacketListVM.SelectedSequenceItem =
                    PacketListVM.Sequence.First(s => s.Kind == SequenceItemKind.Packet);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Load failed: {ex.Message}", "Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}
