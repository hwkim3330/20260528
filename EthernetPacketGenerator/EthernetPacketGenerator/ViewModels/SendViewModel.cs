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
    private Func<PacketItem, CancellationToken, Task<string?>>? _packetValidationCallback;
    private ILiveDevice? _selectedInterface;
    private ObservableCollection<SequenceItem>? _sequence;

    private bool   _isSendingSelected;
    private bool   _isSendingList;
    private bool   _repeatEnabled;
    private int    _cyclePeriodMs = 5000;

    private string _startTime = "-";
    private string _endTime   = "-";
    private string _cycleTime       = "-";
    private string _estimatedTimeMs = "-";   // expected one-pass duration
    private string _passResultLabel = "-";   // "초과" or "여유"
    private string _passResultValue = "-";   // actual time value
    private bool   _isOverrun;               // drives colour in XAML
    private long   _cumulativeOverrunMs;     // accumulated overrun across repeats
    private int    _sentPackets;
    private long   _sentBytes;

    private CancellationTokenSource? _ctsSelected;
    private CancellationTokenSource? _ctsList;
    private DateTime  _sendStart;
    private Stopwatch _cycleWatch = new();
    private DispatcherTimer? _uiTimer;

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
        if (System.Windows.Application.Current is not App app || app.LabServer == null) return;
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
        set { SetProperty(ref _isSendingSelected, value); OnPropertyChanged(nameof(IsSending)); OnPropertyChanged(nameof(SendSelectedLabel)); }
    }

    public bool IsSendingList
    {
        get => _isSendingList;
        set { SetProperty(ref _isSendingList, value); OnPropertyChanged(nameof(IsSending)); OnPropertyChanged(nameof(SendListLabel)); }
    }

    public bool IsSending => _isSendingSelected || _isSendingList;

    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set => SetProperty(ref _repeatEnabled, value);
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
    public void SetPacketValidationCallback(Func<PacketItem, CancellationToken, Task<string?>> callback) =>
        _packetValidationCallback = callback;

    /// <summary>Returns true if started, false if already running.</summary>
    public bool RunSequenceForApi()
    {
        if (IsSendingList) return false;
        if (_sequence == null || !_sequence.Any()) return false;
        ToggleSendList();
        return true;
    }

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
                  (_serial.IsOpen && (_sequence?.Any() ?? false)));
        SendListCommand = new RelayCommand(ToggleSendList,
            () => IsSendingList ||
                  (_serial.IsOpen && (_sequence?.Any() ?? false)));
        RefreshInterfacesCommand = new RelayCommand(LoadInterfaces);

        LoadInterfaces();
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
            EstimatedTimeMs = "-";
            return;
        }

        double totalMs = 0;
        foreach (var item in _sequence)
        {
            if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                totalMs += EthernetTiming.WireTimeMs(item.Packet.TotalLength);
            else if (item.Kind == SequenceItemKind.Event && item.Event != null)
                totalMs += item.Event.DelayMs;
        }

        EstimatedTimeMs = $"{totalMs:F3} ms";
    }

    // ── Send Selected ────────────────────────────────────────────────────────
    private void ToggleSendSelected()
    {
        if (IsSendingSelected) { StopSelected(); return; }
        if (GetCheckedItems().Count == 0) return;

        BeginSend(showCycle: false);
        IsSendingSelected = true;

        var token = (_ctsSelected = new CancellationTokenSource()).Token;
        Task.Run(async () =>
        {
            bool cancelled = false;
            do
            {
                var items = await System.Windows.Application.Current.Dispatcher
                    .InvokeAsync(() => GetCheckedItems());
                if (items.Count == 0) break;

                foreach (var item in items)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }

                    if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                    {
                        await SendPacketWithValidationAsync(item.Packet, token).ConfigureAwait(false);
                    }
                    else if (item.Kind == SequenceItemKind.Event && item.Event != null)
                        cancelled = !await ExecuteEventWithTerminalLogAsync(item.Event, token);
                }

            } while (!cancelled && RepeatEnabled && !token.IsCancellationRequested);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                IsSendingSelected = false;
                EndSendStats();
            });
        }, token);
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
        if (_sequence == null) return;

        BeginSend(showCycle: true);
        IsSendingList = true;

        _ctsList = new CancellationTokenSource();
        Task.Run(async () => await RunListLoop(_ctsList.Token), _ctsList.Token);
    }

    private async Task RunListLoop(CancellationToken token)
    {
        bool cancelled = false;
        var passSw = new Stopwatch();

        do
        {
            passSw.Restart();
            _cycleWatch.Restart();

            List<SequenceItem> items = await System.Windows.Application.Current.Dispatcher
                .InvokeAsync(() => _sequence!.ToList());

            foreach (var item in items)
            {
                if (token.IsCancellationRequested) { cancelled = true; break; }

                if (item.Kind == SequenceItemKind.Packet && item.Packet != null)
                {
                    await SendPacketWithValidationAsync(item.Packet, token).ConfigureAwait(false);
                }
                else if (item.Kind == SequenceItemKind.Event && item.Event != null)
                {
                    cancelled = !await ExecuteEventWithTerminalLogAsync(item.Event, token).ConfigureAwait(false);
                    if (cancelled) break;
                }
            }

            passSw.Stop();
            if (cancelled) break;

            // Compute overrun/margin for this pass
            var passMs    = passSw.ElapsedMilliseconds;
            var overrunMs = passMs - CyclePeriodMs;
            var overrun   = overrunMs > 0;

            if (overrun)
                _cumulativeOverrunMs += overrunMs;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
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

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            IsSendingList = false;
            EndSendStats();
        });
    }

    private async Task SendPacketWithValidationAsync(PacketItem packet, CancellationToken token)
    {
        var targets = await System.Windows.Application.Current.Dispatcher
            .InvokeAsync(() => GetSendTargets(packet.OutgoingInterfaceNames));
        var sentTargets = new List<string>();

        foreach (var entry in targets)
        {
            if (entry.Device == null) continue;
            _sendService.SendOnce(packet.FullBytes, entry.Device);
            sentTargets.Add(entry.ShortName);
        }

        Log($"[Packet TX] name={packet.Name}, dst={packet.DstMac}, len={packet.TotalLength}, iface={string.Join(", ", sentTargets)}");

        if (_packetValidationCallback == null) return;

        try
        {
            var validation = await _packetValidationCallback(packet, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(validation))
                Log(validation);
        }
        catch (OperationCanceledException)
        {
            Log($"[PacketCheck STOP] name={packet.Name}, canceled");
        }
        catch (Exception ex)
        {
            Log($"[PacketCheck FAIL] name={packet.Name}, error={ex.Message}");
        }
    }

    private async Task<bool> ExecuteEventWithTerminalLogAsync(SequenceEvent ev, CancellationToken token)
    {
        switch (ev.EventType)
        {
            case SequenceEventType.Delay:
                Log($"[Delay START] {ev.DelayMs} ms");
                try
                {
                    await Task.Delay(ev.DelayMs, token).ConfigureAwait(false);
                    Log($"[Delay PASS] {ev.DelayMs} ms");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    Log("[Delay STOP] canceled");
                    return false;
                }

            case SequenceEventType.RegWrite:
                if (!EnsureSerialOpen("RegWrite")) return true;
                try
                {
                    Log($"[RegWrite START] addr=0x{ev.Address:X8}, value=0x{ev.Value:X8}");
                    var r = await _serial.SendCommandAsync($"write 0x{ev.Address:X} 0x{ev.Value:X8}");
                    Log($"[RegWrite PASS] addr=0x{ev.Address:X8}, value=0x{ev.Value:X8}, response={r}");
                }
                catch (Exception ex)
                {
                    Log($"[RegWrite FAIL] addr=0x{ev.Address:X8}, error={ex.Message}");
                }
                return true;

            case SequenceEventType.RegRead:
                if (!EnsureSerialOpen("RegRead")) return true;
                try
                {
                    Log($"[RegRead START] addr=0x{ev.Address:X8}");
                    var r = await _serial.SendCommandAsync($"read 0x{ev.Address:X}");
                    Log($"[RegRead PASS] addr=0x{ev.Address:X8}, response={r}");
                }
                catch (Exception ex)
                {
                    Log($"[RegRead FAIL] addr=0x{ev.Address:X8}, error={ex.Message}");
                }
                return true;

            case SequenceEventType.RegWaitFor:
                if (!EnsureSerialOpen("RegWait")) return true;
                return await WaitRegisterConditionAsync(
                    "RegWait",
                    ev.Address,
                    ev.Mask,
                    ev.Expected,
                    ev.TimeoutMs,
                    token).ConfigureAwait(false);

            case SequenceEventType.FdbWrite:
                if (!EnsureSerialOpen("FdbWrite")) return true;
                try
                {
                    var portBits = Convert.ToString(ev.Port, 2).PadLeft(4, '0');
                    Log($"[FdbWrite START] mac={ev.MacAddress}, port=0b{portBits}, vlan={(ev.VlanValid ? ev.VlanId.ToString() : "none")}");
                    await _fdb.WriteEntryByHashAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, ev.Port, token);
                    Log($"[FdbWrite PASS] mac={ev.MacAddress}, port=0b{portBits}");
                }
                catch (OperationCanceledException)
                {
                    Log("[FdbWrite STOP] canceled");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"[FdbWrite FAIL] mac={ev.MacAddress}, error={ex.Message}");
                }
                return true;

            case SequenceEventType.FdbRead:
                if (!EnsureSerialOpen("FdbRead")) return true;
                try
                {
                    Log($"[FdbRead START] mac={ev.MacAddress}, vlan={(ev.VlanValid ? ev.VlanId.ToString() : "none")}");
                    var entry = await _fdb.ReadEntryByMacAsync(ev.MacAddress, ev.VlanValid, ev.VlanId, token);
                    if (entry != null)
                    {
                        var portBits = Convert.ToString(entry.Port, 2).PadLeft(4, '0');
                        Log($"[FdbRead PASS] mac={ev.MacAddress}, port=0b{portBits}, static={entry.StaticDisplay}");
                    }
                    else
                    {
                        Log($"[FdbRead MISS] mac={ev.MacAddress}, entry not found");
                    }
                }
                catch (OperationCanceledException)
                {
                    Log("[FdbRead STOP] canceled");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"[FdbRead FAIL] mac={ev.MacAddress}, error={ex.Message}");
                }
                return true;

            case SequenceEventType.FdbWaitFor:
                if (!EnsureSerialOpen("FdbWait")) return true;
                return await WaitRegisterConditionAsync(
                    "FdbWait",
                    ev.Address,
                    ev.Mask,
                    ev.Expected,
                    ev.TimeoutMs,
                    token).ConfigureAwait(false);

            case SequenceEventType.FdbFlush:
                if (!EnsureSerialOpen("FdbFlush")) return true;
                try
                {
                    Log("[FdbFlush START] clear all FDB entries");
                    await _fdb.FlushAllAsync(token);
                    Log("[FdbFlush PASS] all FDB entries cleared");
                }
                catch (OperationCanceledException)
                {
                    Log("[FdbFlush STOP] canceled");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"[FdbFlush FAIL] error={ex.Message}");
                }
                return true;

            case SequenceEventType.CaptureVerify:
                return await ExecuteCaptureVerifyWithLogAsync(ev, token);

            case SequenceEventType.SerialSend:
                Log($"[SerialSend START]");
                if (!string.IsNullOrWhiteSpace(ev.SerialHex))
                {
                    try
                    {
                        var hexClean = ev.SerialHex.Replace(" ", "").Replace("-", "");
                        if (hexClean.Length % 2 != 0) hexClean = "0" + hexClean;
                        var bytes = Enumerable.Range(0, hexClean.Length / 2)
                            .Select(i => Convert.ToByte(hexClean.Substring(i * 2, 2), 16)).ToArray();
                        _serial.SendBytes(bytes);
                        Log($"[SerialSend PASS] sent hex [{ev.SerialHex}]");
                    }
                    catch (Exception ex) { Log($"[SerialSend FAIL] hex send error: {ex.Message}"); }
                }
                else if (!string.IsNullOrWhiteSpace(ev.SerialText))
                {
                    try
                    {
                        _serial.SendLine(ev.SerialText);
                        Log($"[SerialSend PASS] sent \"{ev.SerialText}\"");
                    }
                    catch (Exception ex) { Log($"[SerialSend FAIL] text send error: {ex.Message}"); }
                }
                else { Log("[SerialSend SKIP] nothing to send"); }
                return true;

            case SequenceEventType.SerialVerify:
                Log($"[SerialVerify START] pattern=\"{ev.SerialText}\" timeout={ev.TimeoutMs}ms");
                {
                    var found = false;
                    var svTcs = new TaskCompletionSource<bool>();
                    System.Text.RegularExpressions.Regex? svRx = null;
                    try { svRx = new System.Text.RegularExpressions.Regex(ev.SerialText, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { }
                    void onLine(string line)
                    {
                        bool matched = svRx != null ? svRx.IsMatch(line) : line.Contains(ev.SerialText, StringComparison.OrdinalIgnoreCase);
                        if (matched) svTcs.TrySetResult(true);
                    }
                    _serial.LineReceived += onLine;
                    try
                    {
                        using var svCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        svCts.CancelAfter(ev.TimeoutMs);
                        try
                        {
                            found = await svTcs.Task.WaitAsync(svCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            if (token.IsCancellationRequested) { Log("[SerialVerify STOP] canceled"); return false; }
                        }
                    }
                    finally { _serial.LineReceived -= onLine; }
                    if (found) Log($"[SerialVerify PASS] pattern matched");
                    else       Log($"[SerialVerify FAIL] pattern not found within {ev.TimeoutMs}ms");
                }
                return true;

            default:
                Log($"[Event SKIP] unsupported event type: {ev.EventType}");
                return true;
        }
    }

    private async Task<bool> ExecuteCaptureVerifyWithLogAsync(SequenceEvent ev, CancellationToken token)
    {
        Log($"[CaptureVerify START] iface={( string.IsNullOrEmpty(ev.CaptureInterface) ? "any" : ev.CaptureInterface )} filter=\"{ev.CaptureFilter}\" expect>={ev.CaptureExpected} timeout={ev.TimeoutMs}ms");
        int count = 0;
        ILiveDevice? dev = null;
        if (string.IsNullOrWhiteSpace(ev.CaptureInterface))
            dev = Interfaces.FirstOrDefault();
        else
            dev = Interfaces.FirstOrDefault(d =>
                (d.Name ?? "").Contains(ev.CaptureInterface, StringComparison.OrdinalIgnoreCase) ||
                (d.Description ?? "").Contains(ev.CaptureInterface, StringComparison.OrdinalIgnoreCase));

        if (dev == null) { Log("[CaptureVerify FAIL] no matching interface found"); return true; }
        try
        {
            dev.Open(DeviceModes.None, 1000);
            if (!string.IsNullOrWhiteSpace(ev.CaptureFilter))
            {
                try { dev.Filter = ev.CaptureFilter; } catch { }
            }
            dev.OnPacketArrival += (_, _) => System.Threading.Interlocked.Increment(ref count);
            dev.StartCapture();
            try { await Task.Delay(ev.TimeoutMs, token); }
            catch (OperationCanceledException) { }
            try { dev.StopCapture(); dev.Close(); } catch { }
        }
        catch (Exception ex) { Log($"[CaptureVerify FAIL] capture error: {ex.Message}"); return true; }

        if (count >= ev.CaptureExpected)
            Log($"[CaptureVerify PASS] received={count} expected>={ev.CaptureExpected}");
        else
            Log($"[CaptureVerify FAIL] received={count} expected>={ev.CaptureExpected}");
        if (token.IsCancellationRequested) return false;
        return true;
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

            // ── RegWaitFor ─────────────────────────────────────────────────
            case SequenceEventType.RegWaitFor:
                if (!CheckPort("RegWaitFor")) return true;
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
                                    Log($"[RegWait ✓] 0x{ev.Address:X8} = 0x{val:X8}  (조건 충족)");
                                    return true;
                                }
                            }
                        }
                        catch { }
                        try { await Task.Delay(200, token); } catch (OperationCanceledException) { return false; }
                    }
                    Log($"[RegWait ✗] 0x{ev.Address:X8}  타임아웃 ({ev.TimeoutMs}ms) — 시나리오 중단");
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

            // ── FdbWaitFor ─────────────────────────────────────────────────
            case SequenceEventType.FdbWaitFor:
                if (!CheckPort("FdbWaitFor")) return true;
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
                                    Log($"[FdbWait ✓] 0x{ev.Address:X8} = 0x{val:X8}  (조건 충족)");
                                    return true;
                                }
                            }
                        }
                        catch { /* 일시적 오류는 무시하고 재시도 */ }
                        try { await Task.Delay(200, token); } catch (OperationCanceledException) { return false; }
                    }
                    Log($"[FdbWait ✗] 0x{ev.Address:X8}  타임아웃 ({ev.TimeoutMs}ms) — 시나리오 중단");
                    return false;
                }

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

            case SequenceEventType.CaptureVerify:
                return await ExecuteCaptureVerifyWithLogAsync(ev, token);

            case SequenceEventType.SerialSend:
                if (!string.IsNullOrWhiteSpace(ev.SerialHex))
                {
                    try
                    {
                        var hx = ev.SerialHex.Replace(" ", "").Replace("-", "");
                        if (hx.Length % 2 != 0) hx = "0" + hx;
                        _serial.SendBytes(Enumerable.Range(0, hx.Length / 2)
                            .Select(i => Convert.ToByte(hx.Substring(i * 2, 2), 16)).ToArray());
                    }
                    catch { }
                }
                else if (!string.IsNullOrWhiteSpace(ev.SerialText))
                {
                    try { _serial.SendLine(ev.SerialText); } catch { }
                }
                return true;

            case SequenceEventType.SerialVerify:
                {
                    var svTcs2 = new TaskCompletionSource<bool>();
                    System.Text.RegularExpressions.Regex? svRx2 = null;
                    try { svRx2 = new System.Text.RegularExpressions.Regex(ev.SerialText, System.Text.RegularExpressions.RegexOptions.IgnoreCase); }
                    catch { }
                    void onLine2(string line)
                    {
                        bool m = svRx2 != null ? svRx2.IsMatch(line) : line.Contains(ev.SerialText, StringComparison.OrdinalIgnoreCase);
                        if (m) svTcs2.TrySetResult(true);
                    }
                    _serial.LineReceived += onLine2;
                    try
                    {
                        using var svCts2 = CancellationTokenSource.CreateLinkedTokenSource(token);
                        svCts2.CancelAfter(ev.TimeoutMs);
                        try { await svTcs2.Task.WaitAsync(svCts2.Token); }
                        catch (OperationCanceledException)
                        {
                            if (token.IsCancellationRequested) return false;
                        }
                    }
                    finally { _serial.LineReceived -= onLine2; }
                }
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

    private void StopList()
    {
        _ctsList?.Cancel();
        IsSendingList = false;
        EndSendStats();
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
            var entry = new InterfaceEntry(dev, GetShortName(dev));
            entry.PropertyChanged += OnInterfaceEntryChanged;
            InterfaceEntries.Add(entry);
        }

        // 첫 번째를 Default로만 지정 (IsActive는 사용자가 직접 체크)
        if (InterfaceEntries.Count > 0)
        {
            InterfaceEntries[0].IsDefault = true;
        }

        var apiStatus = "";
        if (System.Windows.Application.Current is App app && app.LabServer != null)
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
