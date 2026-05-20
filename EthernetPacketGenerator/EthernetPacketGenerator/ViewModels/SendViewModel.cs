using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using SharpPcap;

namespace EthernetPacketGenerator.ViewModels;

public class SendViewModel : ViewModelBase
{
    private readonly PacketSendService  _sendService;
    private readonly SerialPortService  _serial;
    private readonly RegisterService    _reg;
    private readonly FdbService         _fdb;
    private Action<string>?             _logCallback;
    private ILiveDevice? _selectedInterface;
    private ObservableCollection<SequenceItem>? _sequence;
    private CaptureViewModel? _capture;

    // 직전 FdbWrite MAC (RxVerify 폴백용)
    private string _lastFdbMac = string.Empty;

    // 시퀀스 내 FdbWrite 누적 맵 (MAC → Port 비트마스크). FdbFlush 시 초기화.
    private readonly Dictionary<string, int> _fdbMap = new(StringComparer.OrdinalIgnoreCase);

    // 직전 Packet TX에 사용된 송신 NIC 이름 집합 (Capture 측 InterfaceName 포맷).
    // RxVerify에서 송신 인터페이스에 캡처된 패킷은 검증 대상에서 제외.
    private readonly HashSet<string> _lastTxIfaceNames = new(StringComparer.OrdinalIgnoreCase);

    // 패킷 송신 직전 캡처 패킷 수 — 다음 스텝이 RxVerify일 때 송신 전에 기록.
    // RxVerify가 이 값을 baseCount로 사용하여 송신 전에 이미 들어온 패킷을 제외.
    private int? _preRxVerifyBaseCount;

    private bool   _isSendingSelected;
    private bool   _isSendingList;
    private bool   _repeatEnabled;
    private bool   _continueOnFail;
    private int    _cyclePeriodMs = 5000;

    private string _startTime = "-";
    private string _endTime   = "-";
    private string _cycleTime       = "-";
    private string _estimatedTimeMs = "-";   // expected one-pass duration
    private string _passResultLabel = "-";   // "초과" or "여유"
    private string _passResultValue = "-";   // actual time value
    private bool   _isOverrun;               // drives colour in XAML
    private bool   _isDropWarning;
    private double _estimatedTimeMsRaw;   // numeric estimated ms for comparison
    private long   _cumulativeOverrunMs;     // accumulated overrun across repeats
    private int    _sentPackets;
    private long   _sentBytes;

    private CancellationTokenSource? _ctsSelected;
    private CancellationTokenSource? _ctsList;
    private DateTime  _sendStart;
    private Stopwatch _cycleWatch = new();
    private DispatcherTimer? _uiTimer;

    /// <summary>Send List 한 번의 패스(또는 비반복 전체)가 완료/중단됐을 때 발생.</summary>
    public event EventHandler? SendListCompleted;

    // ── Interfaces ──────────────────────────────────────────────────────────
    public ObservableCollection<ILiveDevice> Interfaces { get; } = new();

    /// <summary>체크박스+Default 라디오 포함 인터페이스 목록 (C 옵션)</summary>
    public ObservableCollection<InterfaceEntry> InterfaceEntries { get; } = new();

    public ILiveDevice? SelectedInterface
    {
        get => _selectedInterface;
        set
        {
            SetProperty(ref _selectedInterface, value);
            OnPropertyChanged(nameof(SelectedInterfaceName));
            if (value != null) _sendService.OpenDevice(value);
            SyncLabServer();
        }
    }

    public string SelectedInterfaceName => GetShortName(_selectedInterface);

    /// <summary>활성(IsActive=true) 인터페이스 목록을 반환한다.</summary>
    public IReadOnlyList<InterfaceEntry> ActiveEntries =>
        InterfaceEntries.Where(e => e.IsActive).ToList();

    /// <summary>ShortName으로 InterfaceEntry 룩업. 없으면 Default 반환.</summary>
    public InterfaceEntry? FindEntry(string? shortName) =>
        string.IsNullOrEmpty(shortName)
            ? InterfaceEntries.FirstOrDefault(e => e.IsDefault)
            : InterfaceEntries.FirstOrDefault(e => e.ShortName == shortName)
              ?? InterfaceEntries.FirstOrDefault(e => e.IsDefault);

    /// <summary>
    /// 패킷을 전송할 대상 인터페이스 목록을 반환한다.
    /// - names 비어있음 : 체크(IsActive)된 전체 인터페이스. 없으면 Default 하나만.
    /// - names 지정     : 해당 이름들의 인터페이스. 찾지 못한 이름은 Default로 폴백.
    /// </summary>
    private IReadOnlyList<InterfaceEntry> GetSendTargets(HashSet<string> names)
    {
        if (names.Count > 0)
        {
            var result = names
                .Select(n => InterfaceEntries.FirstOrDefault(e => e.ShortName == n))
                .Where(e => e != null)
                .Cast<InterfaceEntry>()
                .ToList();
            if (result.Count > 0) return result;
            // 이름이 있지만 매칭 실패 → Default
            var def2 = InterfaceEntries.FirstOrDefault(e => e.IsDefault);
            return def2 != null ? new[] { def2 } : Array.Empty<InterfaceEntry>();
        }

        // 비어있으면 IsActive 전체
        var active = InterfaceEntries.Where(e => e.IsActive).ToList();
        if (active.Count > 0) return active;

        // IsActive 없으면 Default 하나
        var def = InterfaceEntries.FirstOrDefault(e => e.IsDefault);
        return def != null ? new[] { def } : Array.Empty<InterfaceEntry>();
    }

    private void SyncLabServer()
    {
        if (System.Windows.Application.Current is not App app) return;
        var def = InterfaceEntries.FirstOrDefault(e => e.IsDefault);
        app.LabServer.SelectedInterfaceName  = GetShortName(def?.Device ?? _selectedInterface);
        app.LabServer.ActiveDevice           = def?.Device ?? _selectedInterface;
        app.LabServer.ActiveInterfaceEntries = InterfaceEntries.ToList();
    }

    public static string GetShortName(ILiveDevice? dev)
    {
        if (dev == null) return "(no interface)";
        var desc = dev.Description ?? dev.Name ?? string.Empty;
        var idx  = desc.LastIndexOf('{');
        if (idx > 0) desc = desc[..idx].TrimEnd(' ', '\\', '_');
        return desc.Length > 0 ? desc : (dev.Name ?? "(unknown)");
    }

    // ── Send state ───────────────────────────────────────────────────────────
    public bool IsSendingSelected
    {
        get => _isSendingSelected;
        set
        {
            SetProperty(ref _isSendingSelected, value);
            OnPropertyChanged(nameof(IsSending));
            OnPropertyChanged(nameof(SendSelectedLabel));
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                CommandManager.InvalidateRequerySuggested,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public bool IsSendingList
    {
        get => _isSendingList;
        set
        {
            SetProperty(ref _isSendingList, value);
            OnPropertyChanged(nameof(IsSending));
            OnPropertyChanged(nameof(SendListLabel));
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                CommandManager.InvalidateRequerySuggested,
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public bool IsSending => _isSendingSelected || _isSendingList;

    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set => SetProperty(ref _repeatEnabled, value);
    }

    public bool ContinueOnFail
    {
        get => _continueOnFail;
        set => SetProperty(ref _continueOnFail, value);
    }

    public int CyclePeriodMs
    {
        get => _cyclePeriodMs;
        set { SetProperty(ref _cyclePeriodMs, Math.Max(1, value)); RecalcEstimatedTime(); }
    }

    // ── Button labels ────────────────────────────────────────────────────────
    public string SendSelectedLabel => IsSendingSelected ? "■  Stop" : "▶  Send Selected";
    public string SendListLabel     => IsSendingList     ? "■  Stop" : "▶  Send List";

    // ── Status ───────────────────────────────────────────────────────────────
    public string StartTime
    {
        get => _startTime;
        set => SetProperty(ref _startTime, value);
    }

    public string EndTime
    {
        get => _endTime;
        set => SetProperty(ref _endTime, value);
    }

    public string CycleTime
    {
        get => _cycleTime;
        set => SetProperty(ref _cycleTime, value);
    }

    // Estimated one-pass duration (packets wire time + delay events)
    public string EstimatedTimeMs
    {
        get => _estimatedTimeMs;
        set => SetProperty(ref _estimatedTimeMs, value);
    }

    // "초과" or "여유"
    public string PassResultLabel
    {
        get => _passResultLabel;
        set => SetProperty(ref _passResultLabel, value);
    }

    // Time value next to the label
    public string PassResultValue
    {
        get => _passResultValue;
        set => SetProperty(ref _passResultValue, value);
    }

    // False = green (여유), True = red (초과)
    public bool IsOverrun
    {
        get => _isOverrun;
        set => SetProperty(ref _isOverrun, value);
    }

    public bool IsDropWarning
    {
        get => _isDropWarning;
        private set => SetProperty(ref _isDropWarning, value);
    }

    public int SentPackets
    {
        get => _sentPackets;
        set => SetProperty(ref _sentPackets, value);
    }

    public long SentBytes
    {
        get => _sentBytes;
        set => SetProperty(ref _sentBytes, value);
    }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand SendSelectedCommand      { get; }
    public ICommand SendListCommand          { get; }
    public ICommand RefreshInterfacesCommand { get; }

    public void SetLogCallback(Action<string> log) => _logCallback = log;

    public SendViewModel(SerialPortService serial)
    {
        _serial      = serial;
        _reg         = new RegisterService(serial) { BaseAddress = 0x44A0_0000 };
        _fdb         = new FdbService(_reg);
        _sendService = new PacketSendService();
        _sendService.PacketSent += (_, len) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                SentPackets++;
                SentBytes += len;
            });
        _sendService.SendError += (_, msg) =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                StartTime = $"Error: {msg}");

        SendSelectedCommand = new RelayCommand(ToggleSendSelected,
            () => IsSendingSelected ||
                  (!IsSendingList && (_sequence?.Any(s => s.IsChecked) ?? false)));
        SendListCommand = new RelayCommand(ToggleSendList,
            () => IsSendingList ||
                  (!IsSendingSelected && (_sequence?.Any() ?? false)));
        RefreshInterfacesCommand = new RelayCommand(LoadInterfaces);

        LoadInterfaces();
    }

    public void AttachCapture(CaptureViewModel capture)
    {
        _capture = capture;
    }

    public void SetSequence(ObservableCollection<SequenceItem> seq)
    {
        // Unsubscribe old
        if (_sequence != null)
        {
            _sequence.CollectionChanged -= OnSequenceChanged;
            foreach (var item in _sequence) UnsubscribeItem(item);
        }

        _sequence = seq;
        _sequence.CollectionChanged += OnSequenceChanged;
        foreach (var item in _sequence) SubscribeItem(item);
        RecalcEstimatedTime();
    }

    // ── Sequence change tracking for EstimatedTime ───────────────────────────
    private void OnSequenceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (SequenceItem i in e.OldItems) UnsubscribeItem(i);
        if (e.NewItems != null) foreach (SequenceItem i in e.NewItems) SubscribeItem(i);
        RecalcEstimatedTime();
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
            CommandManager.InvalidateRequerySuggested,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SubscribeItem(SequenceItem item)
    {
        item.PropertyChanged += OnSequenceItemChanged;
        if (item.Packet != null) item.Packet.PropertyChanged += OnPacketChanged;
        if (item.Event  != null) item.Event.PropertyChanged  += OnEventChanged;
    }

    private void UnsubscribeItem(SequenceItem item)
    {
        item.PropertyChanged -= OnSequenceItemChanged;
        if (item.Packet != null) item.Packet.PropertyChanged -= OnPacketChanged;
        if (item.Event  != null) item.Event.PropertyChanged  -= OnEventChanged;
    }

    private void OnSequenceItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RecalcEstimatedTime();
        if (e.PropertyName == nameof(SequenceItem.IsChecked))
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(
                CommandManager.InvalidateRequerySuggested,
                System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnPacketChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PacketItem.TotalLength))
            RecalcEstimatedTime();
    }

    private void OnEventChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SequenceEvent.DelayMs))
            RecalcEstimatedTime();
    }

    // ── Estimated time calculation ───────────────────────────────────────────
    // Wire time per packet (1 Gbps): preamble(8) + max(frame,64) + FCS(4) + IFG(12) bytes
    // Delay events: their DelayMs value
    private void RecalcEstimatedTime()
    {
        if (_sequence == null || _sequence.Count == 0)
        {
            _estimatedTimeMsRaw = 0;
            EstimatedTimeMs = "-";
            UpdateDropWarning();
            return;
        }

        double totalMs = 0;
        foreach (var item in _sequence)
        {
            if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                totalMs += EthernetTiming.WireTimeMs(item.Packet.TotalLength);
            else if (item.Kind == SequenceItemKind.Event && item.Event != null)
            {
                totalMs += item.Event.EventType switch
                {
                    SequenceEventType.Delay    => item.Event.DelayMs,
                    SequenceEventType.RegVerify   => item.Event.TimeoutMs,
                    SequenceEventType.RxVerify => item.Event.TimeoutMs,
                    _                          => 50   // RegWrite/Read/FdbWrite/Read/Flush 추정
                };
            }
        }

        _estimatedTimeMsRaw = totalMs;
        EstimatedTimeMs = $"{totalMs:F3} ms";
        UpdateDropWarning();
    }

    private void UpdateDropWarning()
    {
        IsDropWarning = _estimatedTimeMsRaw > 0 && CyclePeriodMs < _estimatedTimeMsRaw;
    }

    // ── Send Selected ────────────────────────────────────────────────────────
    private void ToggleSendSelected()
    {
        if (IsSendingSelected) { StopSelected(); return; }
        if (GetCheckedItems().Count == 0) return;

        BeginSend(showCycle: false);
        IsSendingSelected = true;

        var cts = _ctsSelected = new CancellationTokenSource();
        var token = cts.Token;

        // token을 Task.Run에 넘기지 않음 — cancel 시 finally 블록이 반드시 실행되도록
        Task.Run(async () =>
        {
            try
            {
                bool cancelled = false;
                do
                {
                    var items = await System.Windows.Application.Current.Dispatcher
                        .InvokeAsync(() => GetCheckedItems());
                    if (items.Count == 0) break;


                    for (int i = 0; i < items.Count; i++)
                    {
                        if (token.IsCancellationRequested) { cancelled = true; break; }
                        var item = items[i];

                        if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                        {
                            // 다음 스텝이 RxVerify면 송신 직전 캡처 수 기록
                            bool nextIsRxVerify = i + 1 < items.Count &&
                                items[i + 1].Kind == SequenceItemKind.Event &&
                                items[i + 1].Event?.EventType == SequenceEventType.RxVerify;
                            if (nextIsRxVerify && _capture != null)
                                _preRxVerifyBaseCount = await System.Windows.Application.Current.Dispatcher
                                    .InvokeAsync(() => _capture.Packets.Count);
                            await SendPacketAsync(item, token).ConfigureAwait(false);
                            if (token.IsCancellationRequested) { cancelled = true; break; }
                        }
                        else if (item.Kind == SequenceItemKind.Event && item.Event != null)
                        {
                            cancelled = !await ExecuteEventWithTerminalLogAsync(item.Event, token, item).ConfigureAwait(false);
                            if (cancelled) break;
                        }
                    }

                } while (!cancelled && RepeatEnabled && !token.IsCancellationRequested);
            }
            catch (OperationCanceledException) { /* 정상 취소 */ }
            catch (Exception ex) { Log($"[SendSelected ERROR] {ex.Message}"); }
            finally
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsSendingSelected = false;
                    EndSendStats();
                });
            }
        });
    }

    // ── 패킷 전송 — 원본 바이트 그대로 전송, 즉시 Pass 마킹 ──────────────────
    private async Task SendPacketAsync(SequenceItem item, CancellationToken token)
    {
        if (item.Packet == null) return;
        token.ThrowIfCancellationRequested();

        var targets = await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => GetSendTargets(item.Packet.OutgoingInterfaceNames));

        var txIfaceNames = targets
            .Select(e => e.Device != null ? GetShortName(e.Device) : "")
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // RxVerify에서 송신 NIC을 제외하기 위해 Capture 포맷 NIC 이름으로도 기록
        _lastTxIfaceNames.Clear();
        if (_capture != null)
        {
            foreach (var entry in targets)
            {
                var name = ResolveCaptureIfaceName(entry.Device);
                if (!string.IsNullOrEmpty(name)) _lastTxIfaceNames.Add(name);
            }
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => item.TestResult = PacketTestResult.Running);

        foreach (var entry in targets)
            if (entry.Device != null)
                _sendService.SendOnce(item.Packet.FullBytes, entry.Device);

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => item.TestResult = PacketTestResult.Pass);

        Log($"[Packet TX] idx={item.Index}, tx=[{string.Join(",", txIfaceNames)}]");
    }

    private List<SequenceItem> GetCheckedItems() =>
        _sequence?.Where(s => s.IsChecked).ToList() ?? new();

    private void StopSelected()
    {
        _ctsSelected?.Cancel();
        IsSendingSelected = false;
        EndSendStats();
    }

    // ── Send List ────────────────────────────────────────────────────────────
    private void ToggleSendList()
    {
        if (IsSendingList) { StopList(); return; }
        if (_sequence == null || _sequence.Count == 0) return;

        BeginSend(showCycle: true);
        IsSendingList = true;

        var cts = _ctsList = new CancellationTokenSource();

        // token을 Task.Run에 넘기지 않음 — finally 블록이 반드시 실행되도록
        Task.Run(async () =>
        {
            try   { await RunListLoop(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 정상 취소 */ }
            catch (Exception ex) { Log($"[SendList ERROR] {ex.Message}"); }
            finally
            {
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    IsSendingList = false;
                    EndSendStats();
                    SendListCompleted?.Invoke(this, EventArgs.Empty);
                });
            }
        });
    }

    private async Task RunListLoop(CancellationToken token)
    {
        var passSw = new Stopwatch();

        do
        {
            passSw.Restart();
            _cycleWatch.Restart();

            List<SequenceItem> items = await System.Windows.Application.Current.Dispatcher
                .InvokeAsync(() => _sequence!.ToList());

            bool cancelled = false;
            for (int i = 0; i < items.Count; i++)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }
                var item = items[i];

                if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                {
                    // 다음 스텝이 RxVerify면 송신 직전 캡처 수 기록
                    bool nextIsRxVerify = i + 1 < items.Count &&
                        items[i + 1].Kind == SequenceItemKind.Event &&
                        items[i + 1].Event?.EventType == SequenceEventType.RxVerify;
                    if (nextIsRxVerify && _capture != null)
                        _preRxVerifyBaseCount = await System.Windows.Application.Current.Dispatcher
                            .InvokeAsync(() => _capture.Packets.Count);
                    await SendPacketAsync(item, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                }
                else if (item.Kind == SequenceItemKind.Event && item.Event != null)
                {
                    cancelled = !await ExecuteEventWithTerminalLogAsync(item.Event, token, item).ConfigureAwait(false);
                    if (cancelled) break;
                }
            }

            passSw.Stop();
            if (cancelled || token.IsCancellationRequested) break;

            // Compute overrun/margin for this pass
            var passMs    = passSw.ElapsedMilliseconds;
            var overrunMs = passMs - CyclePeriodMs;
            var overrun   = overrunMs > 0;

            if (overrun) _cumulativeOverrunMs += overrunMs;

            _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
            {
                IsOverrun = overrun;
                if (overrun)
                {
                    PassResultLabel = "초과";
                    PassResultValue = RepeatEnabled
                        ? $"+{overrunMs}ms  (누적 +{_cumulativeOverrunMs}ms)"
                        : $"+{overrunMs}ms";
                }
                else
                {
                    PassResultLabel = "여유";
                    PassResultValue = $"{CyclePeriodMs - passMs}ms";
                }
            });

            if (!RepeatEnabled) break;

            // Wait for remaining cycle time; if overrun → start immediately
            var remainMs = (int)(CyclePeriodMs - passMs);
            if (remainMs > 0)
            {
                try { await Task.Delay(remainMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

        } while (!token.IsCancellationRequested);
    }

    private async Task<bool> ExecuteEventWithTerminalLogAsync(SequenceEvent ev, CancellationToken token, SequenceItem? seqItem = null)
    {
        if (seqItem != null)
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => seqItem.TestResult = PacketTestResult.Running);

        async Task SetResult(bool pass)
        {
            if (seqItem != null)
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => seqItem.TestResult = pass ? PacketTestResult.Pass : PacketTestResult.Fail);
        }

        switch (ev.EventType)
        {
            case SequenceEventType.Delay:
                Log($"[Delay START] {ev.DelayMs} ms");
                try
                {
                    await Task.Delay(ev.DelayMs, token).ConfigureAwait(false);
                    Log($"[Delay PASS] {ev.DelayMs} ms");
                    await SetResult(true);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    Log("[Delay STOP] canceled");
                    await SetResult(false);
                    return false;
                }

            case SequenceEventType.RegWrite:
                if (!EnsureSerialOpen("RegWrite")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        Log($"[RegWrite START] addr=0x{ev.Address:X8}, value=0x{ev.Value:X8}");
                        var r = await _serial.SendCommandAsync($"write 0x{ev.Address:X} 0x{ev.Value:X8}");
                        Log($"[RegWrite PASS] addr=0x{ev.Address:X8}, value=0x{ev.Value:X8}, response={r}");
                        await SetResult(true);
                        passed = true;
                    }
                    catch (Exception ex)
                    {
                        Log($"[RegWrite FAIL] addr=0x{ev.Address:X8}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.RegRead:
                if (!EnsureSerialOpen("RegRead")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        Log($"[RegRead START] addr=0x{ev.Address:X8}");
                        var r = await _serial.SendCommandAsync($"read 0x{ev.Address:X}");
                        Log($"[RegRead PASS] addr=0x{ev.Address:X8}, response={r}");
                        await SetResult(true);
                        passed = true;
                    }
                    catch (Exception ex)
                    {
                        Log($"[RegRead FAIL] addr=0x{ev.Address:X8}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.RegVerify:
                if (!EnsureSerialOpen("RegVerify")) { await SetResult(false); return ContinueOnFail; }
                {
                    var ok = await WaitRegisterConditionAsync("RegVerify", ev.Address, ev.Mask, ev.Expected, ev.TimeoutMs, token).ConfigureAwait(false);
                    await SetResult(ok);
                    return ok || ContinueOnFail;
                }

            case SequenceEventType.FdbWrite:
                if (!EnsureSerialOpen("FdbWrite")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        var portBits = Convert.ToString(ev.Port, 2).PadLeft(6, '0');
                        Log($"[FdbWrite START] mac={ev.MacAddress}, port=0b{portBits}, vlan={(ev.VlanValid ? ev.VlanId.ToString() : "none")}");
                        await _fdb.WriteEntryByHashAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, ev.Port, token);
                        _lastFdbMac = ev.MacAddress;
                        _fdbMap[ev.MacAddress] = ev.Port;
                        Log($"[FdbWrite PASS] mac={ev.MacAddress}, port=0b{portBits}");
                        await SetResult(true);
                        passed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[FdbWrite STOP] canceled");
                        await SetResult(false);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FdbWrite FAIL] mac={ev.MacAddress}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.FdbRead:
                if (!EnsureSerialOpen("FdbRead")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        Log($"[FdbRead START] mac={ev.MacAddress}, vlan={(ev.VlanValid ? ev.VlanId.ToString() : "none")}");
                        var entry = await _fdb.ReadEntryByMacAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, token);
                        if (entry != null)
                        {
                            var portBits = Convert.ToString(entry.Port, 2).PadLeft(4, '0');
                            Log($"[FdbRead PASS] mac={ev.MacAddress}, port=0b{portBits}, static={entry.StaticDisplay}");
                            await SetResult(true);
                            passed = true;
                        }
                        else
                        {
                            Log($"[FdbRead MISS] mac={ev.MacAddress}, entry not found");
                            await SetResult(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[FdbRead STOP] canceled");
                        await SetResult(false);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FdbRead FAIL] mac={ev.MacAddress}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.FdbWriteBucket:
                if (!EnsureSerialOpen("FdbWriteBucket")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        var portBitsB = Convert.ToString(ev.Port, 2).PadLeft(6, '0');
                        Log($"[FdbWriteBucket START] mac={ev.MacAddress}, port=0b{portBitsB}, B:{ev.Bucket} S:0x{ev.SlotBitmap:X}");
                        await _fdb.WriteEntryAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, ev.Port, ev.Bucket, ev.SlotBitmap, token);
                        _lastFdbMac = ev.MacAddress;
                        _fdbMap[ev.MacAddress] = ev.Port;
                        Log($"[FdbWriteBucket PASS] mac={ev.MacAddress}, port=0b{portBitsB}");
                        await SetResult(true);
                        passed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[FdbWriteBucket STOP] canceled");
                        await SetResult(false);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FdbWriteBucket FAIL] mac={ev.MacAddress}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.FdbReadBucket:
                if (!EnsureSerialOpen("FdbReadBucket")) { await SetResult(false); return ContinueOnFail; }
                try
                {
                    bool hasExpected = !string.IsNullOrWhiteSpace(ev.FdbExpectedMac);
                    Log($"[FdbReadBucket START] B:{ev.Bucket} S:0x{ev.SlotBitmap:X}{(hasExpected ? $", exp={ev.FdbExpectedMac}" : "")}");
                    var entryB = await _fdb.ReadEntryAsync(ev.Bucket, ev.SlotBitmap, token);
                    if (entryB != null)
                    {
                        if (!hasExpected)
                        {
                            Log($"[FdbReadBucket READ] mac={entryB.Mac}, port=0b{Convert.ToString(entryB.Port, 2).PadLeft(6, '0')}");
                            await SetResult(true);
                        }
                        else
                        {
                            var macMatch = string.Equals(entryB.Mac, ev.FdbExpectedMac, StringComparison.OrdinalIgnoreCase);
                            if (macMatch)
                            {
                                Log($"[FdbReadBucket PASS] mac={entryB.Mac} — 기대값 일치");
                                await SetResult(true);
                            }
                            else
                            {
                                Log($"[FdbReadBucket FAIL] mac={entryB.Mac} (exp:{ev.FdbExpectedMac}) — 기대값 불일치");
                                await SetResult(false);
                                return ContinueOnFail;
                            }
                        }
                    }
                    else
                    {
                        Log($"[FdbReadBucket {(hasExpected ? "FAIL" : "MISS")}] 빈 슬롯 — B:{ev.Bucket} S:0x{ev.SlotBitmap:X}");
                        await SetResult(hasExpected ? false : true);
                        if (hasExpected) return ContinueOnFail;
                    }
                }
                catch (OperationCanceledException)
                {
                    Log("[FdbReadBucket STOP] canceled");
                    await SetResult(false);
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"[FdbReadBucket FAIL] error={ex.Message}");
                    await SetResult(false);
                }
                return true;

            case SequenceEventType.FdbWaitFor:
                if (!EnsureSerialOpen("FdbWaitFor")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        var portBitsW = Convert.ToString(ev.Port, 2).PadLeft(6, '0');
                        Log($"[FdbWaitFor START] mac={ev.MacAddress}, port=0b{portBitsW}, timeout={ev.TimeoutMs}ms");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        bool found = false;
                        while (sw.ElapsedMilliseconds < ev.TimeoutMs)
                        {
                            token.ThrowIfCancellationRequested();
                            var e = await _fdb.ReadEntryByMacAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, token);
                            if (e != null && e.Port == ev.Port) { found = true; break; }
                            await Task.Delay(50, token);
                        }
                        if (found)
                        {
                            Log($"[FdbWaitFor PASS] mac={ev.MacAddress}, port=0b{portBitsW}");
                            await SetResult(true);
                            passed = true;
                        }
                        else
                        {
                            Log($"[FdbWaitFor FAIL] mac={ev.MacAddress} — 타임아웃 {ev.TimeoutMs}ms");
                            await SetResult(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[FdbWaitFor STOP] canceled");
                        await SetResult(false);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FdbWaitFor FAIL] mac={ev.MacAddress}, error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.FdbFlush:
                if (!EnsureSerialOpen("FdbFlush")) { await SetResult(false); return ContinueOnFail; }
                {
                    bool passed = false;
                    try
                    {
                        Log("[FdbFlush START] clear all FDB entries");
                        await _fdb.FlushAllAsync(token);
                        _fdbMap.Clear();
                        Log("[FdbFlush PASS] all FDB entries cleared");
                        await SetResult(true);
                        passed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        Log("[FdbFlush STOP] canceled");
                        await SetResult(false);
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Log($"[FdbFlush FAIL] error={ex.Message}");
                        await SetResult(false);
                    }
                    return passed || ContinueOnFail;
                }

            case SequenceEventType.RxVerify:
            {
                if (_capture == null)
                {
                    Log("[RxVerify SKIP] capture not attached");
                    return true;
                }

                // ── 준비 ───────────────────────────────────────────────────────
                if (seqItem != null)
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => seqItem.TestResult = PacketTestResult.Running);

                // 검사할 DA: 이벤트에 명시 → 없으면 직전 FdbWrite MAC
                var targetMac = string.IsNullOrWhiteSpace(ev.ExpectedDstMac)
                    ? _lastFdbMac
                    : ev.ExpectedDstMac.Trim().ToUpperInvariant();

                // DA MAC → NIC 이름 (Port 컬럼 불필요: DA 자체가 수신 포트를 특정)
                string? expectedIfaceName = MacToInterfaceName(targetMac);

                if (string.IsNullOrEmpty(targetMac) || targetMac == "00:00:00:00:00:00")
                {
                    Log("[RxVerify SKIP] 대상 DA 없음 — FdbWrite 이후에 RxVerify를 배치하거나 ExpectedDstMac을 설정하세요");
                    if (seqItem != null)
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                            () => seqItem.TestResult = PacketTestResult.None);
                    return true;
                }

                int timeoutMs = Math.Max(ev.TimeoutMs, 2000);
                int expectedPort = _fdbMap.TryGetValue(targetMac, out var fdbPort) ? fdbPort : -1;
                string expectedPortStr = expectedPort >= 0 ? expectedPort.ToString() : "매핑없음";
                Log($"[RxVerify START] DA={targetMac}  기대포트={expectedPortStr}(NIC:{expectedIfaceName ?? "매핑없음"})  timeout={timeoutMs}ms");

                // ── baseCount: 송신 직전에 미리 찍어둔 값 우선 사용 ────────────
                // (송신과 RxVerify 사이에 RX가 먼저 도착해도 누락 방지)
                int baseCount;
                if (_preRxVerifyBaseCount.HasValue)
                {
                    baseCount = _preRxVerifyBaseCount.Value;
                    _preRxVerifyBaseCount = null;
                }
                else
                {
                    baseCount = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => _capture.Packets.Count);
                }

                // timeout 동안 대기 (패킷은 CaptureViewModel이 백그라운드에서 계속 수집)
                try { await Task.Delay(timeoutMs, token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (seqItem != null)
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                            () => seqItem.TestResult = PacketTestResult.None);
                    return false;
                }

                // ── timeout 종료 후: 수집된 새 패킷에서 DA 일치 분류 ─────────
                var newPackets = await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => _capture.Packets.Skip(baseCount).ToList());

                // DA(DstMac)가 targetMac과 일치하는 패킷만 추출
                //   + 송신 NIC에서 캡처된 패킷은 자기 송신 에코이므로 제외
                var matchedAll = newPackets
                    .Where(p => p.DstMac.Equals(targetMac, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var matched = matchedAll
                    .Where(p => !_lastTxIfaceNames.Contains(p.InterfaceName))
                    .ToList();
                int excludedTx = matchedAll.Count - matched.Count;
                if (excludedTx > 0)
                    Log($"  └ 송신NIC({string.Join(", ", _lastTxIfaceNames)}) 캡처 {excludedTx}개 제외");

                // NIC별 수신 개수
                var byIface = matched
                    .GroupBy(p => p.InterfaceName, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

                // ── 판정 ───────────────────────────────────────────────────────
                // 1) 아무 NIC에서도 DA 일치 패킷 없음 → FAIL (drop)
                // 2) 올바른 NIC에서만 수신         → PASS
                // 3) 다른 NIC에서도 수신            → FAIL (flooding)
                string verdict;
                string reason;

                if (matched.Count == 0)
                {
                    verdict = "FAIL";
                    reason  = $"drop — DA={targetMac} 패킷이 어느 NIC에서도 캡처되지 않음 (timeout={timeoutMs}ms)";
                }
                else if (expectedIfaceName == null)
                {
                    // 포트-NIC 매핑을 모르는 경우: 수신은 됐으나 포트 판별 불가
                    verdict = "PASS";
                    reason  = $"DA={targetMac} {matched.Count}개 수신 (NIC:{string.Join(", ", byIface.Keys)}) — 포트 매핑 없어 NIC 검증 생략";
                }
                else
                {
                    bool correctHit   = byIface.Keys.Any(k => k.Equals(expectedIfaceName, StringComparison.OrdinalIgnoreCase));
                    bool floodingHit  = byIface.Keys.Any(k => !k.Equals(expectedIfaceName, StringComparison.OrdinalIgnoreCase));

                    if (correctHit && !floodingHit)
                    {
                        verdict = "PASS";
                        reason  = $"NIC:{expectedIfaceName}에서만 {byIface[expectedIfaceName]}개 수신";
                    }
                    else if (!correctHit)
                    {
                        verdict = "FAIL";
                        reason  = $"기대 NIC({expectedIfaceName}) 수신 없음, 실제 수신 NIC: {string.Join(", ", byIface.Keys)}";
                    }
                    else
                    {
                        var floodNics = byIface.Keys
                            .Where(k => !k.Equals(expectedIfaceName, StringComparison.OrdinalIgnoreCase));
                        verdict = "FAIL";
                        reason  = $"flooding — 기대 외 NIC에서도 수신: {string.Join(", ", floodNics)}";
                    }
                }

                Log($"[RxVerify {verdict}] {reason}");

                // NIC별 상세 로그
                foreach (var (iface, cnt) in byIface)
                    Log($"  └ NIC={iface}  DA일치={cnt}개");

                var testResult = verdict == "PASS" ? PacketTestResult.Pass : PacketTestResult.Fail;

                if (seqItem != null)
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                        () => seqItem.TestResult = testResult);

                return verdict == "PASS" || ContinueOnFail;
            }

            default:
                Log($"[Event SKIP] unsupported event type: {ev.EventType}");
                return true;
        }
    }

    private async Task<bool> WaitRegisterConditionAsync(
        string tag,
        uint address,
        uint mask,
        uint expected,
        int timeoutMs,
        CancellationToken token)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        uint? lastValue = null;
        Log($"[{tag} START] addr=0x{address:X8}, mask=0x{mask:X8}, expected=0x{expected:X8}, timeout={timeoutMs}ms");

        while (DateTime.Now < deadline)
        {
            if (token.IsCancellationRequested)
            {
                Log($"[{tag} STOP] canceled");
                return false;
            }

            try
            {
                var r = await _serial.SendCommandAsync($"read 0x{address:X}");
                if (r.StartsWith("OK "))
                {
                    var val = Convert.ToUInt32(r[3..].Trim(), 16);
                    lastValue = val;
                    if ((val & mask) == expected)
                    {
                        Log($"[{tag} PASS] addr=0x{address:X8}, value=0x{val:X8}");
                        return true;
                    }
                }
                else
                {
                    Log($"[{tag} READ] addr=0x{address:X8}, response={r}");
                }
            }
            catch (Exception ex)
            {
                Log($"[{tag} RETRY] addr=0x{address:X8}, error={ex.Message}");
            }

            try { await Task.Delay(200, token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Log($"[{tag} STOP] canceled");
                return false;
            }
        }

        var last = lastValue.HasValue ? $"0x{lastValue.Value:X8}" : "no valid read";
        Log($"[{tag} FAIL] addr=0x{address:X8}, timeout={timeoutMs}ms, last={last}");
        return false;
    }

    private bool EnsureSerialOpen(string tag)
    {
        if (_serial.IsOpen) return true;
        Log($"[{tag} FAIL] serial port is not open. Connect HyperTerminal first.");
        return false;
    }

    /// <summary>이벤트 실행. 정상완료=true, 취소/타임아웃중단=false</summary>
    private async Task<bool> ExecuteEventAsync(SequenceEvent ev, CancellationToken token)
    {
        switch (ev.EventType)
        {
            // ── Delay ──────────────────────────────────────────────────────
            case SequenceEventType.Delay:
                try   { await Task.Delay(ev.DelayMs, token).ConfigureAwait(false); return true; }
                catch (OperationCanceledException)                                 { return false; }

            // ── RegWrite ───────────────────────────────────────────────────
            case SequenceEventType.RegWrite:
                if (!CheckPort("RegWrite")) return true;
                try
                {
                    var r = await _serial.SendCommandAsync($"write 0x{ev.Address:X} 0x{ev.Value:X8}");
                    Log($"[RegWrite] 0x{ev.Address:X8} = 0x{ev.Value:X8}  →  {r}");
                }
                catch (Exception ex) { Log($"[RegWrite 실패] {ex.Message}"); }
                return true;

            // ── RegRead ────────────────────────────────────────────────────
            case SequenceEventType.RegRead:
                if (!CheckPort("RegRead")) return true;
                try
                {
                    var r = await _serial.SendCommandAsync($"read 0x{ev.Address:X}");
                    Log($"[RegRead]  0x{ev.Address:X8}  →  {r}");
                }
                catch (Exception ex) { Log($"[RegRead 실패] {ex.Message}"); }
                return true;

            // ── Verify ────────────────────────────────────────────────────
            case SequenceEventType.RegVerify:
                if (!CheckPort("RegVerify")) return true;
                {
                    var deadline = DateTime.Now.AddMilliseconds(ev.TimeoutMs);
                    while (DateTime.Now < deadline)
                    {
                        if (token.IsCancellationRequested) return false;
                        try
                        {
                            var r = await _serial.SendCommandAsync($"read 0x{ev.Address:X}");
                            if (r.StartsWith("OK "))
                            {
                                var val = Convert.ToUInt32(r[3..].Trim(), 16);
                                if ((val & ev.Mask) == ev.Expected)
                                {
                                    Log($"[Verify ✓] 0x{ev.Address:X8} = 0x{val:X8}  (조건 충족)");
                                    return true;
                                }
                            }
                        }
                        catch { }
                        try { await Task.Delay(200, token); } catch (OperationCanceledException) { return false; }
                    }
                    Log($"[Verify ✗] 0x{ev.Address:X8}  타임아웃 ({ev.TimeoutMs}ms) — 시나리오 중단");
                    return false;
                }

            // ── FdbWrite ───────────────────────────────────────────────────
            case SequenceEventType.FdbWrite:
                if (!CheckPort("FdbWrite")) return true;
                try
                {
                    await _fdb.WriteEntryByHashAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, ev.Port, token);
                    Log($"[FdbWrite ✓] {ev.MacAddress}  Port:{ev.Port}");
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbWrite 실패] {ex.Message}"); }
                return true;

            // ── FdbRead ────────────────────────────────────────────────────
            case SequenceEventType.FdbRead:
                if (!CheckPort("FdbRead")) return true;
                try
                {
                    var entry = await _fdb.ReadEntryByMacAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, token);
                    if (entry != null)
                        Log($"[FdbRead ✓] {ev.MacAddress}  Port:{entry.Port}  {entry.StaticDisplay}");
                    else
                        Log($"[FdbRead]   {ev.MacAddress}  → 미학습 (테이블에 없음)");
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbRead 실패] {ex.Message}"); }
                return true;

            // ── FdbWriteBucket ─────────────────────────────────────────────
            case SequenceEventType.FdbWriteBucket:
                if (!CheckPort("FdbWriteBucket")) return true;
                try
                {
                    await _fdb.WriteEntryAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, ev.Port, ev.Bucket, ev.SlotBitmap, token);
                    Log($"[FdbWriteBucket ✓] {ev.MacAddress}  B:{ev.Bucket} S:0x{ev.SlotBitmap:X}");
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbWriteBucket 실패] {ex.Message}"); }
                return true;

            // ── FdbReadBucket ──────────────────────────────────────────────
            case SequenceEventType.FdbReadBucket:
                if (!CheckPort("FdbReadBucket")) return true;
                try
                {
                    var entryB = await _fdb.ReadEntryAsync(ev.Bucket, ev.SlotBitmap, token);
                    bool hasExp = !string.IsNullOrWhiteSpace(ev.FdbExpectedMac);
                    if (entryB != null)
                    {
                        if (!hasExp)
                            Log($"[FdbReadBucket ✓] B:{ev.Bucket} S:0x{ev.SlotBitmap:X}  mac={entryB.Mac}");
                        else if (string.Equals(entryB.Mac, ev.FdbExpectedMac, StringComparison.OrdinalIgnoreCase))
                            Log($"[FdbReadBucket PASS] mac={entryB.Mac} — 기대값 일치");
                        else
                        {
                            Log($"[FdbReadBucket FAIL] mac={entryB.Mac} (exp:{ev.FdbExpectedMac}) — 기대값 불일치");
                            return ContinueOnFail;
                        }
                    }
                    else
                    {
                        Log($"[FdbReadBucket {(hasExp ? "FAIL" : "MISS")}] 빈 슬롯");
                        if (hasExp) return ContinueOnFail;
                    }
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbReadBucket 실패] {ex.Message}"); }
                return true;

            // ── FdbWaitFor ─────────────────────────────────────────────────
            case SequenceEventType.FdbWaitFor:
                if (!CheckPort("FdbWaitFor")) return true;
                try
                {
                    var deadline2 = DateTime.Now.AddMilliseconds(ev.TimeoutMs);
                    bool found2 = false;
                    while (DateTime.Now < deadline2)
                    {
                        if (token.IsCancellationRequested) return false;
                        var e2 = await _fdb.ReadEntryByMacAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, token);
                        if (e2 != null && e2.Port == ev.Port) { found2 = true; break; }
                        try { await Task.Delay(50, token); } catch (OperationCanceledException) { return false; }
                    }
                    if (found2)
                        Log($"[FdbWaitFor ✓] {ev.MacAddress}  Port:{ev.Port}");
                    else
                    {
                        Log($"[FdbWaitFor ✗] {ev.MacAddress}  타임아웃 ({ev.TimeoutMs}ms)");
                        return ContinueOnFail;
                    }
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbWaitFor 실패] {ex.Message}"); }
                return true;

            // ── FdbFlush ───────────────────────────────────────────────────
            case SequenceEventType.FdbFlush:
                if (!CheckPort("FdbFlush")) return true;
                try
                {
                    await _fdb.FlushAllAsync(token);
                    Log("[FdbFlush ✓] 전체 MAC 테이블 초기화 완료");
                }
                catch (OperationCanceledException) { return false; }
                catch (Exception ex) { Log($"[FdbFlush 실패] {ex.Message}"); }
                return true;

            default:
                return true;
        }
    }

    private bool CheckPort(string tag)
    {
        if (_serial.IsOpen) return true;
        Log($"[{tag} 실패] 포트가 열려있지 않습니다.");
        return false;
    }

    private void Log(string msg) => _logCallback?.Invoke(msg);

    // 송신용 ILiveDevice → CaptureViewModel.Interfaces의 Name (캡처 측 InterfaceName 포맷)
    private string? ResolveCaptureIfaceName(ILiveDevice? dev)
    {
        if (dev == null || _capture == null) return null;
        var byRef = _capture.Interfaces.FirstOrDefault(i => ReferenceEquals(i.Device, dev));
        if (byRef != null) return byRef.Name;
        var mac = dev.MacAddress?.ToString();
        if (string.IsNullOrEmpty(mac)) return null;
        return _capture.Interfaces.FirstOrDefault(i =>
            string.Equals(i.Device?.MacAddress?.ToString(), mac, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    // DA MAC → CaptureViewModel의 NIC Name 변환
    private string? MacToInterfaceName(string mac)
    {
        if (_capture == null) return null;

        var targetRaw = mac.Replace(":", "").ToUpperInvariant();

        var hit = System.Windows.Application.Current.Dispatcher.Invoke(() =>
            _capture.Interfaces.FirstOrDefault(i =>
            {
                var devMac = i.Device?.MacAddress?.ToString()?.ToUpperInvariant() ?? "";
                return devMac == targetRaw;
            }));

        return hit?.Name;
    }

    private static void DiagLog(string msg)
    {
        System.Diagnostics.Debug.WriteLine(msg);
        try { System.IO.File.AppendAllText(@"C:\Users\tht12\plv_diag.txt", msg + "\n"); } catch { }
    }

    /// <summary>UI 버튼 클릭 — Send Selected 토글 (CanExecute 우회, 항상 직접 실행).</summary>
    public void ToggleSendSelectedDirect()
    {
        DiagLog($"[SVM] ToggleSendSelectedDirect  IsSendingSelected={IsSendingSelected}  seq={_sequence?.Count ?? -1}  checked={GetCheckedItems().Count}");
        ToggleSendSelected();
    }

    /// <summary>UI 버튼 클릭 — Send List 토글 (CanExecute 우회, 항상 직접 실행).</summary>
    public void ToggleSendListDirect()
    {
        DiagLog($"[SVM] ToggleSendListDirect  IsSendingList={IsSendingList}  seq={_sequence?.Count ?? -1}");
        ToggleSendList();
    }

    /// <summary>외부에서 Send List를 시작한다. 이미 실행 중이면 무시.</summary>
    public void StartListExternal()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!IsSendingList) ToggleSendList();
        });
    }

    private void StopList() => StopListCore();

    public void StopListExternal()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(StopListCore);
    }

    private void StopListCore()
    {
        _ctsList?.Cancel();
        IsSendingList = false;
        EndSendStats();
        SendListCompleted?.Invoke(this, EventArgs.Empty);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void BeginSend(bool showCycle)
    {
        SentPackets          = 0;
        SentBytes            = 0;
        _sendStart           = DateTime.Now;
        StartTime            = _sendStart.ToString("HH:mm:ss");
        EndTime              = "-";
        CycleTime            = showCycle ? "0.0s / -" : "-";
        PassResultLabel      = "-";
        PassResultValue      = "-";
        IsOverrun            = false;
        _cumulativeOverrunMs = 0;

        _cycleWatch.Reset();

        _uiTimer?.Stop();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) =>
        {
            if (showCycle && _cycleWatch.IsRunning)
                CycleTime = $"{_cycleWatch.Elapsed.TotalSeconds:F1}s / {CyclePeriodMs / 1000.0:F1}s";
        };
        _uiTimer.Start();
    }

    private void EndSendStats()
    {
        _uiTimer?.Stop();
        _uiTimer = null;
        _cycleWatch.Stop();
        EndTime   = DateTime.Now.ToString("HH:mm:ss");
        CycleTime = "-";
    }

    private static readonly Dictionary<string, string> _hardcodedMacNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "9C:6B:00:49:3A:32", "Realtek Gaming 2.5GbE Family Controller #2" },
            { "C8:4D:44:25:2D:37", "Realtek USB 2.5GbE Family Controller" },
            { "A0:36:9F:A8:E4:A7", "Intel(R) Ethernet Server Adapter I350-T4 #4" },
            { "A0:36:9F:A8:E4:A5", "Intel(R) Ethernet Server Adapter I350-T4" },
            { "A0:36:9F:A8:E4:A4", "Intel(R) Ethernet Server Adapter I350-T4 #3" },
            { "A0:36:9F:A8:E4:A6", "Intel(R) Ethernet Server Adapter I350-T4 #2" },
        };

    private void LoadInterfaces()
    {
        // 기존 추가 인터페이스 닫기
        foreach (var e in InterfaceEntries.Where(e => !e.IsDefault))
            if (e.Device != null) _sendService.CloseExtra(e.Device);

        Interfaces.Clear();
        InterfaceEntries.Clear();

        var (devices, error) = NetworkInterfaceService.GetInterfaces();
        foreach (var dev in devices)
        {
            Interfaces.Add(dev);
            var autoName = GetShortName(dev);
            // 하드코딩 MAC 폴백: SharpPcap이 NIC 이름을 GUID로만 반환할 때 친숙한 이름으로 대체
            var macStr = dev.MacAddress?.ToString() ?? "";
            // SharpPcap MacAddress.ToString() 형식은 "9C6B00493A32" (구분자 없음)
            var macColon = macStr.Length == 12
                ? string.Join(":", Enumerable.Range(0, 6).Select(i => macStr.Substring(i * 2, 2)))
                : macStr;
            if (_hardcodedMacNames.TryGetValue(macColon, out var friendlyName))
                autoName = friendlyName;
            var entry = new InterfaceEntry(dev, autoName);
            entry.PropertyChanged += OnInterfaceEntryChanged;
            InterfaceEntries.Add(entry);
        }

        // 첫 번째를 Default로만 지정 (IsActive는 사용자가 직접 체크)
        if (InterfaceEntries.Count > 0)
        {
            InterfaceEntries[0].IsDefault = true;
        }

        var apiStatus = "";
        if (System.Windows.Application.Current is App app)
            apiStatus = app.LabServer.IsRunning
                ? $" | API :{app.LabServer.Port} ●"
                : " | API 시작 실패 ✕";

        if (error != null)
            StartTime = error + apiStatus;
        else if (InterfaceEntries.Count > 0)
        {
            SelectedInterface = InterfaceEntries[0].Device;
            StartTime = $"Ready — {InterfaceEntries.Count} interface(s){apiStatus}";
        }
    }

    /// <summary>
    /// 패킷의 SrcMAC을 보고 OutgoingInterfaceNames를 자동 설정한다.
    /// 하드코딩 맵에 있는 MAC이면 해당 인터페이스로 지정, 없으면 비워서 Default로 폴백.
    /// </summary>
    public void AutoMapSrcMacToInterface(IEnumerable<PacketItem> packets)
    {
        foreach (var pkt in packets)
        {
            var srcMac = pkt.SrcMac;
            if (string.IsNullOrEmpty(srcMac) || srcMac == "-") continue;

            if (_hardcodedMacNames.TryGetValue(srcMac, out var targetName))
            {
                var entry = InterfaceEntries.FirstOrDefault(e =>
                    string.Equals(e.ShortName, targetName, StringComparison.OrdinalIgnoreCase));
                if (entry != null && !pkt.OutgoingInterfaceNames.Contains(entry.ShortName))
                {
                    pkt.OutgoingInterfaceNames.Clear();
                    pkt.OutgoingInterfaceNames.Add(entry.ShortName);
                    pkt.OnOutgoingInterfaceChanged();
                }
            }
        }
    }

    private void OnInterfaceEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not InterfaceEntry changed) return;

        if (e.PropertyName == nameof(InterfaceEntry.IsDefault) && changed.IsDefault)
        {
            // 라디오 버튼처럼 동작: 다른 항목의 IsDefault 해제
            foreach (var entry in InterfaceEntries)
                if (entry != changed) entry.IsDefault = false;

            // 기본 인터페이스 변경 → _sendService 및 LabServer 동기화
            SelectedInterface = changed.Device;
        }

        if (e.PropertyName == nameof(InterfaceEntry.IsActive))
        {
            if (changed.Device != null)
            {
                if (changed.IsActive)
                    _sendService.OpenExtra(changed.Device);
                else
                    _sendService.CloseExtra(changed.Device);
            }
            SyncLabServer();
        }
    }
}
