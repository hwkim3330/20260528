using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.ViewModels;

public class AutomationViewModel : ViewModelBase
{
    private readonly PacketFlowMonitorViewModel _pfm;

    public PacketListViewModel      PacketListVM  { get; }
    public TestCaseManagerViewModel TestCaseMgrVM { get; }

    // Expose PFM for interface configuration binding in AutomationView
    public PacketFlowMonitorViewModel PfmVM => _pfm;

    // Expose PFM shared collections
    public ObservableCollection<PacketFlowAutoTestRow> AutoTestRows => _pfm.AutoTestRows;

    private string _statusText = "Ready — select interfaces in Capture tab, then run a test.";
    private bool   _isRunning;

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            SetProperty(ref _isRunning, value);
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                System.Windows.Input.CommandManager.InvalidateRequerySuggested,
                DispatcherPriority.Background);
        }
    }

    // Expose latest result from PFM
    public string FinalResult => _pfm.FinalResult;
    public string FinalReason => _pfm.FinalReason;

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand RunTxSanityCheckCommand  { get; }
    public ICommand RunFdbAutoTestCommand    { get; }
    public ICommand RunFloodingCheckCommand  { get; }
    public ICommand ClearResultsCommand      { get; }

    public AutomationViewModel(
        PacketListViewModel      packetListVM,
        TestCaseManagerViewModel testCaseMgrVM,
        PacketFlowMonitorViewModel pfm)
    {
        PacketListVM  = packetListVM;
        TestCaseMgrVM = testCaseMgrVM;
        _pfm          = pfm;

        _pfm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PacketFlowMonitorViewModel.IsMonitoring))
            {
                IsRunning = _pfm.IsMonitoring;
            }
            if (e.PropertyName is nameof(PacketFlowMonitorViewModel.FinalResult)
                               or nameof(PacketFlowMonitorViewModel.FinalReason))
            {
                OnPropertyChanged(nameof(FinalResult));
                OnPropertyChanged(nameof(FinalReason));
            }
        };

        RunTxSanityCheckCommand = new RelayCommand(
            async () => await RunWithStatusAsync("TX Sanity Check", _pfm.RunTxSanityCheckAsync),
            () => !IsRunning);

        RunFdbAutoTestCommand = new RelayCommand(
            async () => await RunWithStatusAsync("FDB Auto Test (3 steps)", _pfm.RunFdbAutoTestAsync),
            () => !IsRunning);

        RunFloodingCheckCommand = new RelayCommand(
            async () => await RunWithStatusAsync("Flooding Check", _pfm.RunFloodingCheckAsync),
            () => !IsRunning);

        ClearResultsCommand = new RelayCommand(() =>
        {
            _pfm.ClearResultCommand.Execute(null);
            StatusText = "Cleared.";
        });
    }

    /// <summary>외부 HTTP API (POST /api/auto/run) 에서 호출 — UI 스레드에서 실행됨.</summary>
    public Task RunTestAsync(string test) => test switch
    {
        "tx-sanity"   => RunWithStatusAsync("TX Sanity Check",         _pfm.RunTxSanityCheckAsync),
        "fdb-test"    => RunWithStatusAsync("FDB Auto Test (3 steps)",  _pfm.RunFdbAutoTestAsync),
        "flood-check" => RunWithStatusAsync("Flooding Check",           _pfm.RunFloodingCheckAsync),
        _ => Task.FromException(new ArgumentException($"Unknown test: {test}"))
    };

    /// <summary>외부 HTTP API (GET /api/auto/results) 에서 호출 — UI 스레드에서 실행됨.</summary>
    public List<Models.PacketFlowAutoTestRow> GetResultsSnapshot() => AutoTestRows.ToList();

    private async Task RunWithStatusAsync(string testName, Func<Task> action)
    {
        StatusText = $"Running: {testName}...";
        try
        {
            await action();
            string res = _pfm.FinalResult;
            StatusText = string.IsNullOrEmpty(res)
                ? $"{testName} complete."
                : $"{testName}: {res}";
        }
        catch (Exception ex)
        {
            StatusText = $"{testName} failed: {ex.Message}";
        }
    }
}
