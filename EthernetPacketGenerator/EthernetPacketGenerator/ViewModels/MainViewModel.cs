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
    private readonly StringBuilder _seqTerminalBuffer = new();
    private string _sequenceTerminalLog = "";

    // Tab indices: 0=PacketGenerator, 1=ScenarioLab, 2=NDJSONBridge, 3=Capture, 4=HyperTerminal, 5=Settings

    public PacketListViewModel         PacketListVM         { get; } = new();
    public BlockBuilderViewModel       BlockBuilderVM       { get; } = new();
    public HexDumpViewModel            HexDumpVM            { get; } = new();
    public TreeDecodeViewModel         TreeDecodeVM         { get; } = new();
    public SendViewModel               SendVM               { get; }
    public HyperTerminalViewModel      HyperTerminalVM      { get; }
    public TestCaseManagerViewModel    TestCaseMgrVM        { get; }
    public NdjsonBridgeViewModel       NdjsonBridgeVM       { get; }
    public PacketFlowMonitorViewModel  PacketFlowMonitorVM  { get; } = new();
    public CaptureViewModel            CaptureVM            { get; } = new();
    public AutomationViewModel         AutomationVM         { get; }
    public SettingsViewModel           SettingsVM           { get; }

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
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string SequenceTerminalLog
    {
        get => _sequenceTerminalLog;
        private set => SetProperty(ref _sequenceTerminalLog, value);
    }

    public ICommand SaveCommand                    { get; }
    public ICommand LoadCommand                    { get; }
    public ICommand ClearSequenceTerminalCommand   { get; }

    public MainViewModel()
    {
        HyperTerminalVM = new HyperTerminalViewModel(_serial);
        SendVM          = new SendViewModel(_serial);
        TestCaseMgrVM   = new TestCaseManagerViewModel(PacketListVM);
        TestCaseMgrVM.AttachCapture(CaptureVM);
        NdjsonBridgeVM  = new NdjsonBridgeViewModel(PacketListVM, SendVM, CaptureVM, TestCaseMgrVM);
        AutomationVM    = new AutomationViewModel(PacketListVM, TestCaseMgrVM, PacketFlowMonitorVM);
        SettingsVM      = new SettingsViewModel();

        SaveCommand = new RelayCommand(Save, () => PacketListVM.Packets.Any());
        LoadCommand = new RelayCommand(Load);
        ClearSequenceTerminalCommand = new RelayCommand(() =>
        {
            _seqTerminalBuffer.Clear();
            SequenceTerminalLog = "";
        });

        SendVM.SetLogCallback(line =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                HyperTerminalVM.AppendTerminal(line);
                AppendSequenceTerminal(line);
            }));
        SendVM.SetPacketValidationCallback(TestCaseMgrVM.ValidatePacketAfterSendAsync);

        PacketListVM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PacketListViewModel.SelectedPacket))
                OnSelectedPacketChanged(PacketListVM.SelectedPacket);
        };

        SendVM.SetSequence(PacketListVM.Sequence);
        PacketListVM.InterfaceEntries = SendVM.InterfaceEntries;

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
