using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;
using Microsoft.Win32;

namespace EthernetPacketGenerator.ViewModels;

public class TestCaseManagerViewModel : ViewModelBase
{
    private readonly PacketListViewModel _packetList;
    private CaptureViewModel? _capture;
    private SendViewModel? _sendVm;
    private TestCaseEntry? _selectedTc;
    private TestCaseGroup? _selectedGroup;
    private TestScenarioStep? _selectedScenarioRow;
    private string _status = "";
    private string _rxPortMappingText = "RX mapping: select capture interfaces first.";

    // 시나리오 실행 시퀀스 관련
    public ObservableCollection<TestCaseEntry> TestSequence { get; } = new();

    private bool _isRunningSequence;
    private bool _autoProgressLoad;   // RunTestSequenceAsync가 TC를 자동으로 로드할 때 true
    private int _currentSequenceIndex;
    private CancellationTokenSource? _ctsTestSequence;
    private string _seqStartTime = "-";
    private string _seqEndTime   = "-";

    public bool IsRunningSequence
    {
        get => _isRunningSequence;
        set
        {
            SetProperty(ref _isRunningSequence, value);
            OnPropertyChanged(nameof(SequenceProgressText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SeqStartTime
    {
        get => _seqStartTime;
        set => SetProperty(ref _seqStartTime, value);
    }

    public string SeqEndTime
    {
        get => _seqEndTime;
        set => SetProperty(ref _seqEndTime, value);
    }

    public int CurrentSequenceIndex
    {
        get => _currentSequenceIndex;
        set { SetProperty(ref _currentSequenceIndex, value); OnPropertyChanged(nameof(SequenceProgressText)); }
    }

    public string SequenceProgressText =>
        TestSequence.Count == 0
            ? ""
            : $"{Math.Min(_currentSequenceIndex + 1, TestSequence.Count)} / {TestSequence.Count}";

    /// <summary>
    /// TestSequence가 비어있으면 선택된 TC 1개의 예상 시간,
    /// 채워져 있으면 모든 TC의 합산 예상 시간.
    /// </summary>
    public string TotalSequenceEstimatedTimeMs
    {
        get
        {
            if (TestSequence.Count == 0)
            {
                if (_selectedTc == null) return "-";
                double single = CalcTcEstimatedMs(_selectedTc);
                return single <= 0 ? "-" : $"{single:F3} ms";
            }
            double total = TestSequence.Sum(CalcTcEstimatedMs);
            return total <= 0 ? "-" : $"{total:F3} ms";
        }
    }

    /// <summary>TestSequence가 1개 이상이면 true → 헤더 레이블을 "합산 예상"으로 표시.</summary>
    public bool HasTestSequence => TestSequence.Count > 0;

    // 패킷 wire time + 모든 이벤트 ms를 합산
    private static double CalcTcEstimatedMs(TestCaseEntry tc)
    {
        double ms = 0;
        foreach (var dto in tc.Items)
        {
            if (string.Equals(dto.Kind, "Packet", StringComparison.OrdinalIgnoreCase))
            {
                if (dto.Blocks == null || dto.Blocks.Count == 0) continue;
                int totalBytes = dto.Blocks.Sum(b =>
                {
                    if (string.IsNullOrWhiteSpace(b.Bytes)) return 0;
                    return (b.Bytes.Replace(" ", "").Length + 1) / 2;
                });
                ms += EthernetTiming.WireTimeMs(totalBytes);
            }
            else if (string.Equals(dto.Kind, "Event", StringComparison.OrdinalIgnoreCase))
            {
                ms += (dto.EventType ?? "") switch
                {
                    var t when string.Equals(t, "Delay",    StringComparison.OrdinalIgnoreCase) => dto.DelayMs,
                    var t when string.Equals(t, "RegVerify", StringComparison.OrdinalIgnoreCase) => dto.TimeoutMs,
                    var t when string.Equals(t, "Verify",    StringComparison.OrdinalIgnoreCase) => dto.TimeoutMs, // backwards compat
                    var t when string.Equals(t, "RegWaitFor", StringComparison.OrdinalIgnoreCase) => dto.TimeoutMs, // backwards compat
                    var t when string.Equals(t, "RxVerify", StringComparison.OrdinalIgnoreCase) => dto.TimeoutMs,
                    _ => 50   // RegWrite / RegRead / FdbWrite / FdbRead / FdbFlush 추정
                };
            }
        }
        return ms;
    }

    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;

    private static string AutoSavePath =>
        Path.GetFullPath(Path.Combine(ScenarioDir, "..", "test_cases.tcs"));

    public ObservableCollection<TestCaseGroup> Groups { get; } = new();
    public ObservableCollection<TestScenarioStep> ScenarioRows { get; } = new();

    public TestCaseEntry? SelectedTc => _selectedTc;

    public TestScenarioStep? SelectedScenarioRow
    {
        get => _selectedScenarioRow;
        set
        {
            SetProperty(ref _selectedScenarioRow, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public string RxPortMappingText
    {
        get => _rxPortMappingText;
        private set => SetProperty(ref _rxPortMappingText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand AddGroupCommand     { get; }
    public ICommand AddTcToGroupCommand { get; }              // param: TestCaseGroup
    public ICommand ToggleGroupCommand  { get; }              // param: TestCaseGroup
    public ICommand SelectTcCommand           { get; }  // param: TestCaseEntry — TEST CASES 클릭
    public ICommand SelectTcFromSequenceCommand { get; }  // param: TestCaseEntry — TEST SEQUENCE 클릭
    public ICommand DeleteTcCommand     { get; }              // param: TestCaseEntry  (우클릭 메뉴)
    public ICommand DeleteGroupCommand  { get; }              // param: TestCaseGroup  (우클릭 메뉴)
    public ICommand DeleteCommand       { get; }              // 툴바 [−] 버튼용
    public ICommand SaveTcCommand       { get; }
    public ICommand SaveFileCommand     { get; }
    public ICommand LoadFileCommand     { get; }
    public ICommand AddScenarioRowCommand { get; }
    public ICommand DeleteScenarioRowCommand { get; }
    public ICommand LoadFdbScenarioSheetCommand { get; }
    public ICommand ApplyScenarioSheetCommand { get; }
    public ICommand SaveScenarioSheetCommand { get; }
    public ICommand LoadScenarioSheetCommand { get; }
    public ICommand ValidateCaptureCommand { get; }
    public ICommand ImportTcCsvCommand    { get; }
    public ICommand ReloadCsvCommand      { get; }
    public ICommand AddCheckedToSequenceCommand    { get; }
    public ICommand RemoveFromTestSequenceCommand  { get; }    // param: TestCaseEntry
    public ICommand RunTestSequenceCommand         { get; }
    public ICommand ResetSequenceResultsCommand    { get; }

    public TestCaseManagerViewModel(PacketListViewModel packetList)
    {
        _packetList = packetList;

        AddGroupCommand     = new RelayCommand(AddGroup);
        AddTcToGroupCommand = new RelayCommand<TestCaseGroup>(AddTcToGroup);
        ToggleGroupCommand  = new RelayCommand<TestCaseGroup>(g => { if (g != null) g.IsExpanded = !g.IsExpanded; });
        SelectTcCommand             = new RelayCommand<TestCaseEntry>(tc => SelectTc(tc, false));
        SelectTcFromSequenceCommand = new RelayCommand<TestCaseEntry>(tc => SelectTc(tc, true));
        DeleteTcCommand     = new RelayCommand<TestCaseEntry>(DeleteTc);
        DeleteGroupCommand  = new RelayCommand<TestCaseGroup>(DeleteGroup);
        DeleteCommand       = new RelayCommand(Delete, CanDelete);
        SaveTcCommand       = new RelayCommand(SaveTc,   () => _selectedTc != null);
        SaveFileCommand     = new RelayCommand(SaveToFile, () => Groups.Any());
        LoadFileCommand     = new RelayCommand(LoadFromFile);
        AddScenarioRowCommand = new RelayCommand(AddScenarioRow);
        DeleteScenarioRowCommand = new RelayCommand(DeleteScenarioRow, () => SelectedScenarioRow != null);
        LoadFdbScenarioSheetCommand = new RelayCommand(LoadFdbScenarioSheet);
        ApplyScenarioSheetCommand = new RelayCommand(ApplyScenarioSheetToSequence, () => ScenarioRows.Any());
        SaveScenarioSheetCommand = new RelayCommand(SaveScenarioSheet, () => ScenarioRows.Any());
        LoadScenarioSheetCommand = new RelayCommand(LoadScenarioSheet);
        ValidateCaptureCommand = new RelayCommand(ValidateCaptureResults, () => ScenarioRows.Any() && _capture != null);
        ImportTcCsvCommand     = new RelayCommand(ImportTcCsv);
        ReloadCsvCommand       = new RelayCommand(ReloadCsv);
        AddCheckedToSequenceCommand = new RelayCommand(AddCheckedToSequence,
            () => Groups.SelectMany(g => g.TestCases).Any(tc => tc.IsChecked));
        RemoveFromTestSequenceCommand = new RelayCommand<TestCaseEntry>(tc =>
        {
            if (tc != null) TestSequence.Remove(tc);
            OnPropertyChanged(nameof(SequenceProgressText));
            OnPropertyChanged(nameof(TotalSequenceEstimatedTimeMs));
        });
        RunTestSequenceCommand = new RelayCommand(
            ToggleRunTestSequence,
            () => IsRunningSequence ||
                  (TestSequence.Count > 0 &&
                   !(_sendVm?.IsSendingList ?? false) &&
                   !(_sendVm?.IsSendingSelected ?? false)));

        ResetSequenceResultsCommand = new RelayCommand(
            ResetSequenceResults,
            () => !IsRunningSequence && TestSequence.Any(tc => tc.IsDone || tc.IsFailed));

        TestSequence.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SequenceProgressText));
            OnPropertyChanged(nameof(TotalSequenceEstimatedTimeMs));
            OnPropertyChanged(nameof(HasTestSequence));
            SyncEstimatedToPacketList();
            CommandManager.InvalidateRequerySuggested();
        };

        AutoLoad();
        StartWatcher();
    }

    // ── FileSystemWatcher: TestScenarios/ 폴더 및 서브폴더 감시 ─────────────
    private void StartWatcher()
    {
        var dir = ScenarioDir;
        try { Directory.CreateDirectory(dir); } catch { }
        if (!Directory.Exists(dir)) return;

        _watcher = new FileSystemWatcher(dir)
        {
            Filter    = "*.*",   // Renamed 시 변경 전 이름도 잡기 위해 전체 감시
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            IncludeSubdirectories = true,
            EnableRaisingEvents   = true
        };
        _watcher.Changed += OnCsvChanged;
        _watcher.Created += OnCsvChanged;
        _watcher.Deleted += OnCsvChanged;
        _watcher.Renamed += OnCsvChanged;
    }

    private void OnCsvChanged(object sender, FileSystemEventArgs e)
    {
        // CSV 파일 또는 디렉토리 변경만 처리
        var ext = Path.GetExtension(e.FullPath);
        bool isCsvOrDir = string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase)
                       || string.IsNullOrEmpty(ext);  // 디렉토리 이벤트는 확장자 없음
        // Renamed의 경우 변경 전 이름(OldFullPath)도 csv였을 수 있음
        if (!isCsvOrDir && e is RenamedEventArgs re)
            isCsvOrDir = string.Equals(Path.GetExtension(re.OldFullPath), ".csv", StringComparison.OrdinalIgnoreCase);
        if (!isCsvOrDir) return;

        // 디바운스: 파일 저장 시 이벤트가 여러 번 오는 것 방지 (500ms)
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(_ =>
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ReloadChangedCsv(e.FullPath, e.ChangeType);
            });
        }, null, 500, Timeout.Infinite);
    }

    private void ReloadChangedCsv(string fullPath, WatcherChangeTypes changeType)
    {
        SyncCsvToGroups(forceReparse: true);
        Status = $"CSV 변경 감지, 재동기화: {Path.GetFileName(fullPath)}";
    }

    private void ReloadCsv()
    {
        SyncCsvToGroups(forceReparse: true);
        Status = $"TestScenarios 재로드 완료  {DateTime.Now:HH:mm:ss}";
    }

    private static string ScenarioDir
    {
        get
        {
            var exeDir = AppContext.BaseDirectory;
            var projectDir = Path.GetFullPath(Path.Combine(exeDir, @"..\..\..\"));
            var sourceDir = Path.Combine(projectDir, "TestScenarios");
            if (Directory.Exists(sourceDir)) return sourceDir;
            return Path.Combine(exeDir, "TestScenarios");
        }
    }

    // 폴더 내 Packet CSV 탐색 (파일명에 "packet" 포함)
    private static string? FindPacketCsvInDir(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, "*.csv", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase));
    }

    public void AttachCapture(CaptureViewModel capture)
    {
        _capture = capture;
        UpdateRxPortMappingText();
        CommandManager.InvalidateRequerySuggested();
    }

    public bool ContinueOnFail
    {
        get => _sendVm?.ContinueOnFail ?? false;
        set { if (_sendVm != null) _sendVm.ContinueOnFail = value; OnPropertyChanged(); }
    }

    public void AttachSendViewModel(SendViewModel sendVm)
    {
        if (_sendVm != null)
            _sendVm.PropertyChanged -= OnSendVmPropertyChanged;
        _sendVm = sendVm;
        _sendVm.PropertyChanged += OnSendVmPropertyChanged;
    }

    private void OnSendVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SendViewModel.IsSendingList) or nameof(SendViewModel.IsSendingSelected))
            CommandManager.InvalidateRequerySuggested();
        if (e.PropertyName == nameof(SendViewModel.ContinueOnFail))
            OnPropertyChanged(nameof(ContinueOnFail));
    }

    // ── 테스트 시퀀스 실행 (TC 순서대로 Send List 자동 진행) ─────────────────
    private void ToggleRunTestSequence()
    {
        if (IsRunningSequence)
        {
            // Stop
            _ctsTestSequence?.Cancel();
            _sendVm?.StopListExternal();
            IsRunningSequence = false;
            SeqEndTime = DateTime.Now.ToString("HH:mm:ss");
            foreach (var tc in TestSequence) tc.IsRunning = false;
            Status = "테스트 시퀀스 중단됨";
            return;
        }

        if (TestSequence.Count == 0 || _sendVm == null) return;

        IsRunningSequence = true;
        CurrentSequenceIndex = 0;
        SeqStartTime = DateTime.Now.ToString("HH:mm:ss");
        SeqEndTime   = "-";
        Status = "테스트 시퀀스 실행 중...";
        foreach (var tc in TestSequence)
        {
            tc.IsRunning      = false;
            tc.IsDone         = false;
            tc.IsFailed       = false;
            tc.LastRunResults = null;
        }

        var cts = _ctsTestSequence = new CancellationTokenSource();
        var token = cts.Token;

        Task.Run(async () => await RunTestSequenceAsync(token).ConfigureAwait(false));
    }

    private async Task RunTestSequenceAsync(CancellationToken token)
    {
        var sendVm = _sendVm!;

        for (int i = 0; i < TestSequence.Count; i++)
        {
            if (token.IsCancellationRequested) break;

            var tc = TestSequence[i];

            // UI 스레드에서 TC 선택 (시퀀스 로드) — _autoProgressLoad=true로 강제 교체
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CurrentSequenceIndex = i;
                _autoProgressLoad = true;
                try { SelectTc(tc); }
                finally { _autoProgressLoad = false; }
                tc.IsRunning = true;
                tc.IsDone    = false;  // 실행 중에는 이전 결과 동그라미 숨김
                Status = $"TC {i + 1}/{TestSequence.Count}: {tc.Name} 실행 중...";
            });

            if (token.IsCancellationRequested) break;

            // Send List 완료를 기다리는 TCS
            // 구독을 StartListExternal 호출 전에 등록해야 레이스 컨디션 방지
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCompleted(object? s, EventArgs e) => tcs.TrySetResult(true);
            sendVm.SendListCompleted += OnCompleted;

            try
            {
                // CancellationToken이 취소되면 TCS도 완료
                using var reg = token.Register(() => tcs.TrySetResult(false));

                // Send List 시작 — 구독 이후에 호출
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (!sendVm.IsSendingList)
                        sendVm.StartListExternal();
                });

                // Send List 완료 대기 (타임아웃 없음 — token으로만 취소)
                await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                sendVm.SendListCompleted -= OnCompleted;
            }

            if (token.IsCancellationRequested) break;

            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                // RxVerify/RegVerify 등 시퀀스 스텝별 결과 (SequenceItem.TestResult) 확인
                bool failed = _packetList.Sequence
                    .Any(s => s.TestResult == PacketTestResult.Fail);

                // 결과 캐시 저장 — TC 전환 후 돌아와도 Result 칼럼 복원 가능
                tc.LastRunResults = _packetList.Sequence
                    .Select(s => s.TestResult)
                    .ToArray();

                tc.IsRunning = false;
                tc.IsDone    = true;
                tc.IsFailed  = failed;
                Status = $"TC {i + 1}/{TestSequence.Count}: {tc.Name} {(failed ? "FAIL" : "PASS")}";
            });
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsRunningSequence = false;
            SeqEndTime = DateTime.Now.ToString("HH:mm:ss");
            if (!token.IsCancellationRequested)
            {
                CurrentSequenceIndex = TestSequence.Count - 1;
                Status = $"테스트 시퀀스 완료 ({TestSequence.Count}개 TC)";
            }
        });
    }

    // ── 그룹 추가 ────────────────────────────────────────────────────────────
    private void AddGroup()
    {
        var group = new TestCaseGroup { Name = $"Group{Groups.Count + 1}" };
        Groups.Add(group);
        _selectedGroup = group;
        CommandManager.InvalidateRequerySuggested();
    }

    // ── TC 추가 (그룹 내) ────────────────────────────────────────────────────
    private void AddTcToGroup(TestCaseGroup? group)
    {
        if (group == null) return;
        _selectedGroup = group;
        group.IsExpanded = true;

        var tc = new TestCaseEntry { Name = $"TC{group.TestCases.Count + 1}" };
        group.TestCases.Add(tc);
        SelectTc(tc);
    }

    /// <summary>패킷 제너레이터 탭 TC 선택기에서 TC를 선택할 때 외부에서 호출</summary>
    public void SelectTestCase(TestCaseEntry tc) => SelectTc(tc);

    // ── TC 선택 → TEST SEQUENCE 로드 (모든 아이템) ───────────────────────────
    private void SelectTc(TestCaseEntry? tc, bool restoreResults = true)
    {
        if (_selectedTc != null && _selectedTc != tc) _selectedTc.IsSelected = false;
        _selectedTc = tc;
        if (tc != null)
        {
            tc.IsSelected = true;
            _selectedGroup = FindGroupOf(tc) ?? _selectedGroup;
            // Send 실행 중에는 Sequence를 교체하지 않음 (진행 중인 전송 보호)
            // 자동 시퀀스 진행(_autoProgressLoad)은 예외: 다음 TC 로드를 허용
            if (!(_sendVm?.IsSendingList ?? false) || _autoProgressLoad)
            {
                var items = TestCaseSerializer.RestoreSequence(tc.Items);
                _packetList.IsScenarioMode = true;
                _packetList.LoadSequence(items);
                _packetList.ActiveScenarioName = tc.Name;
                // SrcMAC 기반 인터페이스 자동 매핑
                _sendVm?.AutoMapSrcMacToInterface(_packetList.Sequence
                    .Where(s => s.Kind == Models.SequenceItemKind.Packet && s.Packet != null)
                    .Select(s => s.Packet!));
                // 결과 초기화 후 이전 실행 결과 복원 — restoreResults=true일 때만 (TEST SEQUENCE 클릭)
                var seq = _packetList.Sequence;
                foreach (var s in seq) s.TestResult = PacketTestResult.None;
                if (restoreResults && tc.LastRunResults != null)
                {
                    for (int idx = 0; idx < Math.Min(seq.Count, tc.LastRunResults.Length); idx++)
                        seq[idx].TestResult = tc.LastRunResults[idx];
                }
            }
            Status = $"{tc.Name}  로드됨";
        }
        OnPropertyChanged(nameof(TotalSequenceEstimatedTimeMs));
        SyncEstimatedToPacketList();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SyncEstimatedToPacketList()
    {
        _packetList.HasTestSequence      = TestSequence.Count > 0;
        _packetList.InjectedEstimatedTimeMs = TotalSequenceEstimatedTimeMs;
    }

    // ── 시퀀스 결과 초기화 ──────────────────────────────────────────────────
    private void ResetSequenceResults()
    {
        foreach (var tc in TestSequence)
        {
            tc.LastRunResults = null;
            tc.IsDone         = false;
            tc.IsFailed       = false;
        }
        // 현재 표시 중인 시퀀스 아이템 결과도 초기화
        foreach (var s in _packetList.Sequence)
            s.TestResult = PacketTestResult.None;

        Status = "시퀀스 결과 초기화 완료";
        CommandManager.InvalidateRequerySuggested();
    }

    // ── 체크된 TC들을 테스트 시퀀스에 추가 ─────────────────────────────────
    private void AddCheckedToSequence()
    {
        var checked_ = Groups.SelectMany(g => g.TestCases).Where(tc => tc.IsChecked).ToList();
        foreach (var tc in checked_)
        {
            if (!TestSequence.Contains(tc))
            {
                // 시퀀스에 새로 추가 시 이전 실행 결과 초기화
                tc.LastRunResults = null;
                tc.IsDone         = false;
                tc.IsFailed       = false;
                TestSequence.Add(tc);
            }
        }
        OnPropertyChanged(nameof(SequenceProgressText));
        Status = $"{checked_.Count}개 TC가 테스트 시퀀스에 추가됨";
        CommandManager.InvalidateRequerySuggested();
    }

    // ── 우클릭 메뉴용 삭제 ────────────────────────────────────────────────────
    private void DeleteTc(TestCaseEntry? tc)
    {
        if (tc == null) return;
        var group = FindGroupOf(tc);
        if (tc == _selectedTc)
        {
            _selectedTc.IsSelected = false;
            _selectedTc = null;
        }
        group?.TestCases.Remove(tc);
        Status = "삭제됨";
        CommandManager.InvalidateRequerySuggested();
    }

    private void DeleteGroup(TestCaseGroup? group)
    {
        if (group == null) return;
        if (group == _selectedGroup) _selectedGroup = null;
        if (_selectedTc != null && group.TestCases.Contains(_selectedTc))
        {
            _selectedTc.IsSelected = false;
            _selectedTc = null;
        }
        Groups.Remove(group);
        Status = "그룹 삭제됨";
        CommandManager.InvalidateRequerySuggested();
    }

    // ── 삭제: TC 선택 시 TC, 아니면 마지막 활성 그룹 ────────────────────────
    private void Delete()
    {
        if (_selectedTc != null)
        {
            var group = FindGroupOf(_selectedTc);
            _selectedTc.IsSelected = false;
            group?.TestCases.Remove(_selectedTc);
            _selectedTc = null;
            Status = "삭제됨";
        }
        else if (_selectedGroup != null)
        {
            Groups.Remove(_selectedGroup);
            _selectedGroup = null;
            Status = "그룹 삭제됨";
        }
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanDelete() => _selectedTc != null || _selectedGroup != null;

    // ── 현재 PACKET LIST → 선택된 TC에 저장 ─────────────────────────────────
    private void SaveTc()
    {
        if (_selectedTc == null) return;
        _selectedTc.Items = TestCaseSerializer.TakeSnapshot(_packetList.Sequence);
        TrySaveAutoTcs();
        Status = $"저장됨  {DateTime.Now:HH:mm:ss}";
    }

    // ── 파일 저장/로드 ────────────────────────────────────────────────────────
    private void SaveToFile()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Test Case Suite (*.tcs)|*.tcs|All Files (*.*)|*.*",
            DefaultExt = "tcs"
        };
        if (dlg.ShowDialog() != true) return;
        try   { TestCaseSerializer.SaveToFile(Groups, dlg.FileName); Status = "파일 저장 완료"; }
        catch (Exception ex) { Status = $"저장 실패: {ex.Message}"; }
    }

    private void LoadFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Test Case Suite (*.tcs)|*.tcs|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            ApplyLoadedGroups(TestCaseSerializer.LoadFromFile(dlg.FileName));
            Status = $"로드 완료 ({Groups.Count}개 그룹)";
        }
        catch (Exception ex) { Status = $"로드 실패: {ex.Message}"; }
    }

    // ── 자동 저장/로드 ────────────────────────────────────────────────────────
    public void AutoSave()
    {
        if (Groups.Any(g => g.TestCases.Any()))
            TrySaveAutoTcs();
    }

    private void AutoLoad()
    {
        // ① .tcs 로드 (수동 저장 그룹 복원용)
        var tcsPath = FindBestTestCaseFile();
        if (tcsPath != null)
        {
            try { ApplyLoadedGroups(TestCaseSerializer.LoadFromFile(tcsPath)); }
            catch { }
        }

        // ② CSV 스캔: CSV 기반 그룹은 항상 최신 파일로 재파싱 (캐시 무시)
        SyncCsvToGroups(forceReparse: true);
    }

    // TestScenarios/ 폴더 구조를 그룹 트리로 동기화.
    // 규칙:
    //   - TestScenarios/<폴더명>/<파일>.csv → 그룹 <폴더명>, 엔트리 <파일명>
    //   - TestScenarios/<파일>.csv (루트 직속, packet 제외) → 그룹 "(root)", 엔트리 <파일명>
    //   - *packet*.csv 는 패킷 정의 파일 (엔트리 생성 안 함)
    //   - 루트의 *packet*.csv 는 모든 그룹에서 공유 (서브폴더 내 packet csv 우선)
    private void SyncCsvToGroups(bool forceReparse = false)
    {
        var rootDir = ScenarioDir;
        if (!Directory.Exists(rootDir)) return;

        // 루트 패킷 CSV (공유용)
        var rootPacketCsv = Directory.GetFiles(rootDir, "*.csv", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase));

        // ── 폴더별로 그룹 스펙 수집 ─────────────────────────────────────────
        // key: 그룹 이름, value: (폴더 경로, TC CSV 목록, Packet CSV)
        var groupSpecs = new Dictionary<string, (string folderPath, List<string> tcCsvs, string? packetCsv)>(
            StringComparer.OrdinalIgnoreCase);

        // 1. 서브폴더 → 각 폴더명이 그룹명 (패킷 CSV는 항상 루트만 사용)
        foreach (var subDir in Directory.GetDirectories(rootDir).OrderBy(d => d))
        {
            var groupName = Path.GetFileName(subDir);
            var allCsvs   = Directory.GetFiles(subDir, "*.csv", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
            var tcCsvs    = allCsvs.Where(f => !Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase)).ToList();
            if (tcCsvs.Count > 0)
                groupSpecs[groupName] = (subDir, tcCsvs, rootPacketCsv);
        }

        // 2. 루트 직속 TC CSV → "(root)" 그룹
        {
            var rootCsvs  = Directory.GetFiles(rootDir, "*.csv", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
            var packetCsv = rootCsvs.FirstOrDefault(f => Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase));
            var tcCsvs    = rootCsvs.Where(f => !Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase)).ToList();
            if (tcCsvs.Count > 0)
                groupSpecs["(root)"] = (rootDir, tcCsvs, packetCsv);
        }

        // ── 각 그룹 스펙을 Groups에 반영 ──────────────────────────────────
        // Groups에 있지만 스펙에 없는 CSV 기반 그룹은 제거
        var csvGroupsToRemove = Groups
            .Where(g => g.CsvSourcePath != null && !groupSpecs.ContainsKey(g.Name))
            .ToList();
        foreach (var g in csvGroupsToRemove) Groups.Remove(g);

        bool anyChanged = false;

        foreach (var (groupName, (folderPath, tcCsvs, packetCsv)) in groupSpecs)
        {
            // TC csv와 packet csv 모두 수정 시각 비교
            var allCsvsForModify = packetCsv != null
                ? tcCsvs.Append(packetCsv).ToList()
                : tcCsvs;
            var latestModify = allCsvsForModify
                .Where(File.Exists)
                .Select(f => File.GetLastWriteTimeUtc(f))
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            // 기존 그룹 찾기 (이름 일치 또는 CsvSourcePath 폴더 일치)
            var existingGroup = Groups.FirstOrDefault(g =>
                string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase) ||
                (g.CsvSourcePath != null &&
                 string.Equals(Path.GetDirectoryName(g.CsvSourcePath), folderPath, StringComparison.OrdinalIgnoreCase)));

            // 최신 상태이면 경로만 교정하고 스킵 (forceReparse 시 항상 재파싱)
            if (!forceReparse
                && existingGroup != null
                && existingGroup.CsvLastModifiedUtc != null
                && latestModify <= existingGroup.CsvLastModifiedUtc.Value)
            {
                if (existingGroup.CsvSourcePath != null &&
                    !string.Equals(Path.GetDirectoryName(existingGroup.CsvSourcePath), folderPath, StringComparison.OrdinalIgnoreCase))
                    existingGroup.CsvSourcePath = tcCsvs[0];
                continue;
            }

            // 재파싱
            var newGroup = new TestCaseGroup
            {
                Name               = groupName,
                IsExpanded         = existingGroup?.IsExpanded ?? true,
                CsvSourcePath      = tcCsvs[0],
                CsvLastModifiedUtc = latestModify
            };

            var parsedEntries = new List<EthernetPacketGenerator.Models.TestCaseEntry>();
            foreach (var tcFile in tcCsvs)
            {
                try
                {
                    var entry = TestCaseSerializer.ImportCsvAsEntry(tcFile, packetCsv);
                    if (entry != null) parsedEntries.Add(entry);
                }
                catch (Exception ex)
                {
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        Status = $"CSV 파싱 오류 [{Path.GetFileName(tcFile)}]: {ex.Message}");
                }
            }
            foreach (var entry in parsedEntries
                .OrderBy(e => e.TestScenarioId)
                .ThenBy(e => e.TcId)
                .ThenBy(e => e.Name))
                newGroup.TestCases.Add(entry);

            // 파싱 결과가 없으면 기존 그룹 유지 (삭제하지 않음)
            if (newGroup.TestCases.Count == 0) continue;

            int insertIdx = existingGroup != null ? Groups.IndexOf(existingGroup) : -1;
            if (existingGroup != null) Groups.Remove(existingGroup);

            if (insertIdx >= 0 && insertIdx <= Groups.Count)
                Groups.Insert(insertIdx, newGroup);
            else
                Groups.Add(newGroup);

            anyChanged = true;
        }

        if (anyChanged) TrySaveAutoTcs();

        // Groups를 각 그룹의 최소 TestScenarioId → 그룹명 순으로 정렬
        var sortedGroups = Groups
            .OrderBy(g => g.TestCases.Count > 0 ? g.TestCases.Min(e => e.TestScenarioId) : int.MaxValue)
            .ThenBy(g => g.Name)
            .ToList();
        for (int i = 0; i < sortedGroups.Count; i++)
        {
            int cur = Groups.IndexOf(sortedGroups[i]);
            if (cur != i) Groups.Move(cur, i);
        }

        // 재파싱으로 _selectedTc 객체가 교체됐을 수 있으므로 이름 기준으로 재선택
        var prevName = _selectedTc?.Name;
        if (anyChanged && prevName != null)
        {
            var replacement = Groups.SelectMany(g => g.TestCases)
                .FirstOrDefault(tc => string.Equals(tc.Name, prevName, StringComparison.OrdinalIgnoreCase));
            if (replacement != null && replacement != _selectedTc)
                SelectTc(replacement);
        }
        else if (_selectedTc == null)
        {
            var first = Groups.SelectMany(g => g.TestCases).FirstOrDefault();
            if (first != null) SelectTc(first);
        }
    }

    private void TrySaveAutoTcs()
    {
        try { TestCaseSerializer.SaveToFile(Groups, AutoSavePath); }
        catch { }
    }

    private static string? FindBestTestCaseFile()
    {
        // 항상 Debug 실행 파일 옆 test_cases.tcs만 사용
        return File.Exists(AutoSavePath) ? AutoSavePath : null;
    }

    private static int CountSavedTestCases(string path)
    {
        try
        {
            return TestCaseSerializer.LoadFromFile(path).Sum(g => g.TestCases.Count);
        }
        catch
        {
            return 0;
        }
    }

    private void ApplyLoadedGroups(System.Collections.Generic.List<TestCaseGroup> loaded)
    {
        // 그룹 내부: TC_ID 순 정렬
        foreach (var g in loaded)
        {
            var sorted = g.TestCases.OrderBy(tc => tc.TcId).ToList();
            g.TestCases.Clear();
            foreach (var tc in sorted) g.TestCases.Add(tc);
        }

        // 그룹 순서: 그룹 내 min(TestScenarioId) 순 정렬
        var sortedGroups = loaded
            .OrderBy(g => g.TestCases.Count > 0 ? g.TestCases.Min(tc => tc.TestScenarioId) : int.MaxValue)
            .ToList();

        Groups.Clear();
        foreach (var g in sortedGroups) Groups.Add(g);

        // TestSequence 동기화 — 이름 기준으로 새 TC 객체로 교체 (결과 캐시 이전)
        var allNew = Groups.SelectMany(g => g.TestCases).ToList();
        for (int i = 0; i < TestSequence.Count; i++)
        {
            var old = TestSequence[i];
            var replacement = allNew.FirstOrDefault(tc =>
                string.Equals(tc.Name, old.Name, StringComparison.OrdinalIgnoreCase));
            if (replacement != null && !ReferenceEquals(replacement, old))
            {
                // 이전 실행 결과 캐시 이전
                replacement.LastRunResults = old.LastRunResults;
                replacement.IsDone         = old.IsDone;
                replacement.IsFailed       = old.IsFailed;
                TestSequence[i] = replacement;
            }
        }

        var first = Groups.SelectMany(g => g.TestCases).FirstOrDefault();
        if (first != null) SelectTc(first);
    }

    // ── 헬퍼 ────────────────────────────────────────────────────────────────
    private void AddScenarioRow()
    {
        var nextIndex = ScenarioRows.Count == 0 ? 0 : ScenarioRows.Max(r => r.Index) + 1;
        var row = new TestScenarioStep { TC_ID = 0, Test_Scenario_ID = 0, Index = nextIndex, Name = $"Step{nextIndex}", Action = "RegWrite", Timeout = "100ms" };
        ScenarioRows.Add(row);
        SelectedScenarioRow = row;
        Status = "Scenario row added.";
    }

    private void DeleteScenarioRow()
    {
        if (SelectedScenarioRow == null) return;
        ScenarioRows.Remove(SelectedScenarioRow);
        SelectedScenarioRow = ScenarioRows.FirstOrDefault();
        ReindexScenarioRows();
        Status = "Scenario row deleted.";
    }

    private void LoadFdbScenarioSheet()
    {
        ScenarioRows.Clear();
        AddLinkCheckRows(0, 0x44A00080, 0x44A00090, 0x80010000);
        AddLinkCheckRows(1, 0x44A000C0, 0x44A000D0, 0x80810000);
        AddLinkCheckRows(2, 0x44A00100, 0x44A00110, 0x80A10000);
        AddLinkCheckRows(3, 0x44A00140, 0x44A00150, 0x81010000);
        AddLinkCheckRows(4, 0x44A00180, 0x44A00190, 0x81410000);
        AddLinkCheckRows(5, 0x44A001C0, 0x44A001D0, 0x81810000);
        AddFdbFlowRows();
        ReindexScenarioRows();
        Status = "Loaded FDB forwarding sheet. Edit cells, then Apply to Sequence.";
    }

    private void SaveScenarioSheet()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Scenario Sheet CSV (*.csv)|*.csv|All Files (*.*)|*.*",
            DefaultExt = "csv",
            FileName = "scenario_sheet.csv"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("TC_ID,Test_Scenario_ID,Index,Name,Action,Address,Value,Mask,Expected,Timeout,Observed,Result,Note");
            foreach (var row in ScenarioRows.OrderBy(r => r.Test_Scenario_ID).ThenBy(r => r.TC_ID).ThenBy(r => r.Index))
            {
                sb.AppendLine(string.Join(",",
                    Csv(row.TC_ID.ToString(CultureInfo.InvariantCulture)),
                    Csv(row.Test_Scenario_ID.ToString(CultureInfo.InvariantCulture)),
                    Csv(row.Index.ToString(CultureInfo.InvariantCulture)),
                    Csv(row.Name),
                    Csv(row.Action),
                    Csv(row.Address),
                    Csv(row.Value),
                    Csv(row.Mask),
                    Csv(row.Expected),
                    Csv(row.Timeout),
                    Csv(row.Observed),
                    Csv(row.Result),
                    Csv(row.Note)));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Status = $"Scenario sheet saved: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            Status = $"Scenario save failed: {ex.Message}";
        }
    }

    private void LoadScenarioSheet()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Scenario Sheet CSV (*.csv)|*.csv|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count == 0) return;

            var rows = new List<TestScenarioStep>();
            foreach (var line in lines.Skip(1))
            {
                var c = ParseCsvLine(line);
                if (c.Count < 11) continue;
                rows.Add(new TestScenarioStep
                {
                    TC_ID = ParseInt(c[0]),
                    Test_Scenario_ID = ParseInt(c[1]),
                    Index = ParseInt(c[2]),
                    Name = c[3],
                    Action = c[4],
                    Address = c[5],
                    Value = c[6],
                    Mask = c[7],
                    Expected = c[8],
                    Timeout = c[9],
                    Observed = c.Count > 11 ? c[10] : "",
                    Result = c.Count > 12 ? c[11] : "",
                    Note = c.Count > 12 ? c[12] : c[10]
                });
            }
            ScenarioRows.Clear();
            foreach (var row in rows.OrderBy(r => r.Test_Scenario_ID).ThenBy(r => r.TC_ID).ThenBy(r => r.Index))
                ScenarioRows.Add(row);
            Status = $"Scenario sheet loaded: {ScenarioRows.Count} rows.";
        }
        catch (Exception ex)
        {
            Status = $"Scenario load failed: {ex.Message}";
        }
    }

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(ch);
            }
        }
        result.Add(sb.ToString());
        return result;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private void AddLinkCheckRows(int port, uint mdioAddress, uint bmsrAddress, uint triggerValue)
    {
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 0, Test_Scenario_ID = 0, Name = $"Port{port}_MDIO_Enable", Action = "RegWrite", Address = Hex(mdioAddress), Value = "0x00610000", Note = "Setup - Link Status" });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 0, Test_Scenario_ID = 0, Name = $"Port{port}_BMSR_Read_Trigger", Action = "RegWrite", Address = Hex(bmsrAddress), Value = Hex(triggerValue) });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 0, Test_Scenario_ID = 0, Name = $"Port{port}_LinkUp_Wait", Action = "RegVerify", Address = Hex(bmsrAddress), Mask = "0x80000004", Expected = "0x00000004", Timeout = "100ms" });
    }

    // 포트 인덱스 → MAC 매핑 (환경 고정값)
    // key: 비트마스크 (0b000001~0b100000)
    private static readonly IReadOnlyDictionary<int, string> PortMacMap =
        new Dictionary<int, string>
        {
            { 0b000001, "9C:6B:00:49:3A:32" },
            { 0b000010, "C8:4D:44:25:2D:37" },
            { 0b000100, "A0:36:9F:A8:E4:A7" },
            { 0b001000, "A0:36:9F:A8:E4:A5" },
            { 0b010000, "A0:36:9F:A8:E4:A4" },
            { 0b100000, "A0:36:9F:A8:E4:A6" },
        };

    private static string PortMac(int bitmask) =>
        PortMacMap.TryGetValue(bitmask, out var m) ? m : "00:00:00:00:00:00";

    private void AddFdbFlowRows()
    {
        // TC1: FDB flush → 전체 flood 확인
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 1, Test_Scenario_ID = 1, Name = "FDB_Flush", Action = "FdbFlush", Note = "Unknown unicast floods to all ports." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 1, Test_Scenario_ID = 1, Name = "Send_Flood_Check", Action = "Packet", Value = PortMac(0b000010), Note = "Expect flood to port 1~5." });

        // TC2~6: 각 포트(1~5)에 FDB static write 후 unicast 포워딩 검증
        for (int i = 1; i <= 5; i++)
        {
            int bitmask = 1 << i;
            var mac = PortMac(bitmask);
            ScenarioRows.Add(new TestScenarioStep
            {
                TC_ID = i + 1, Test_Scenario_ID = 1,
                Name   = $"FDB_Write_Port{i}",
                Action = "FdbWrite",
                Value  = mac,
                Expected = $"0b{Convert.ToString(bitmask, 2).PadLeft(6, '0')}",
                Note   = $"Static entry: MAC={mac} → Port 0b{Convert.ToString(bitmask, 2).PadLeft(6, '0')}"
            });
            ScenarioRows.Add(new TestScenarioStep
            {
                TC_ID = i + 1, Test_Scenario_ID = 1,
                Name   = $"Send_Port{i}_Check",
                Action = "Packet",
                Value  = mac,
                Note   = $"Expect unicast only to port 0b{Convert.ToString(bitmask, 2).PadLeft(6, '0')}."
            });
        }
    }

    private void ApplyScenarioSheetToSequence()
    {
        var items = ScenarioRows.OrderBy(r => r.Test_Scenario_ID).ThenBy(r => r.TC_ID).ThenBy(r => r.Index)
            .Select(ConvertRowToSequenceItem).Where(i => i != null).Cast<SequenceItem>().ToList();
        _packetList.LoadSequence(items);
        Status = $"Applied {items.Count} sheet rows to Test Sequence.";
    }

    private void ValidateCaptureResults()
    {
        if (_capture == null)
        {
            Status = "Capture is not attached.";
            return;
        }

        UpdateRxPortMappingText();
        var packets = _capture.GetPacketsSnapshot(5000);
        var pass = 0;
        var fail = 0;
        string? activeExpected = null;
        var rxMap = GetRxPortMap();

        foreach (var row in ScenarioRows.OrderBy(r => r.Test_Scenario_ID).ThenBy(r => r.TC_ID).ThenBy(r => r.Index))
        {
            row.Observed = string.Empty;
            row.Result = string.Empty;

            if (row.Action.Equals("FdbFlush", StringComparison.OrdinalIgnoreCase))
            {
                activeExpected = "0b1110";
                row.Observed = "Expect flood: Ethernet 10, Ethernet 8, Ethernet 5";
                continue;
            }

            if (row.Action.Equals("FdbWrite", StringComparison.OrdinalIgnoreCase))
            {
                activeExpected = row.Expected;
                row.Observed = $"Expect {DescribeExpectedPorts(row.Expected)}";
                continue;
            }

            if (!row.Action.Equals("Packet", StringComparison.OrdinalIgnoreCase))
                continue;

            var mac = NormalizeMac(row.Value);
            var matches = packets
                .Where(p => p.DstMac.Equals(mac, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var observedInterfaces = matches
                .Select(p => p.InterfaceName)
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(i => i)
                .ToList();

            var expected = ExpectedInterfaceTokens(activeExpected, rxMap);
            var expectedHits = expected
                .Where(token => observedInterfaces.Any(i => i.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var unexpectedHits = observedInterfaces
                .Where(i => MonitoredInterfaceTokens().Any(token => i.Contains(token, StringComparison.OrdinalIgnoreCase)) &&
                            !expected.Any(token => i.Contains(token, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            row.Observed = observedInterfaces.Count == 0
                ? $"No capture for {mac}"
                : string.Join(" | ", observedInterfaces);

            if (expected.Count == 0)
            {
                row.Result = matches.Count > 0 ? "PASS" : "FAIL";
            }
            else if (expectedHits.Count == expected.Count && unexpectedHits.Count == 0)
            {
                row.Result = "PASS";
            }
            else
            {
                row.Result = "FAIL";
            }

            if (row.Result == "PASS") pass++;
            else fail++;
        }

        Status = $"Capture validation completed. PASS {pass}, FAIL {fail}, packets scanned {packets.Count}.";
    }

    private Dictionary<int, string> GetRxPortMap()
    {
        var selected = _capture?.Interfaces
            .Where(i => i.IsSelected)
            .Select(i => i.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList() ?? new List<string>();

        if (selected.Count == 0)
            selected = new List<string> { "이더넷 10", "이더넷 8", "이더넷 5" };

        return new Dictionary<int, string>
        {
            [0b0010] = selected.ElementAtOrDefault(0) ?? "이더넷 10",
            [0b0100] = selected.ElementAtOrDefault(1) ?? "이더넷 8",
            [0b1000] = selected.ElementAtOrDefault(2) ?? "이더넷 5"
        };
    }

    private void UpdateRxPortMappingText()
    {
        var rxMap = GetRxPortMap();
        RxPortMappingText =
            $"RX map from Capture checkboxes: 0b0010 -> {ShortName(rxMap[0b0010])}, 0b0100 -> {ShortName(rxMap[0b0100])}, 0b1000 -> {ShortName(rxMap[0b1000])}";
    }

    private static List<string> ExpectedInterfaceTokens(string? expected, Dictionary<int, string> rxMap)
    {
        var port = ParsePort(expected);
        var list = new List<string>();
        if ((port & 0b0010) != 0) list.Add(rxMap[0b0010]);
        if ((port & 0b0100) != 0) list.Add(rxMap[0b0100]);
        if ((port & 0b1000) != 0) list.Add(rxMap[0b1000]);
        return list;
    }

    private List<string> MonitoredInterfaceTokens() => GetRxPortMap().Values.ToList();

    private static string DescribeExpectedPorts(string? expected)
    {
        var port = ParsePort(expected);
        var parts = new List<string>();
        if ((port & 0b0010) != 0) parts.Add("RX1");
        if ((port & 0b0100) != 0) parts.Add("RX2");
        if ((port & 0b1000) != 0) parts.Add("RX3");
        return parts.Count == 0 ? "no monitored RX port" : string.Join(", ", parts);
    }

    private static string ShortName(string name)
    {
        if (name.Length <= 26) return name;
        var trimmed = name.Replace("\\Device\\NPF_", "", StringComparison.OrdinalIgnoreCase);
        return trimmed.Length <= 26 ? trimmed : trimmed[..26] + "...";
    }

    private SequenceItem? ConvertRowToSequenceItem(TestScenarioStep row)
    {
        var action = (row.Action ?? "").Trim();
        if (action.Equals("RegWrite", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegWrite, Address = ParseHex(row.Address), Value = ParseHex(row.Value) });
        if (action.Equals("RegRead", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegRead, Address = ParseHex(row.Address) });
        if (action.Equals("RegWaitFor", StringComparison.OrdinalIgnoreCase) ||
            action.Equals("RegVerify", StringComparison.OrdinalIgnoreCase) || action.Equals("Verify", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegVerify, Address = ParseHex(row.Address), Mask = ParseHex(row.Mask), Expected = ParseHex(row.Expected), TimeoutMs = ParseTimeout(row.Timeout, 1000) });
        if (action.Equals("FdbWrite", StringComparison.OrdinalIgnoreCase))
        {
            // Expected 컬럼: 포트 비트마스크 (0b000001~0b100000 또는 1~32)
            var portBitmask = ParsePort(row.Expected);
            string mac;
            if (string.IsNullOrWhiteSpace(row.Value) || row.Value.Trim() == "-")
                mac = PortMacMap.TryGetValue(portBitmask, out var m1) ? m1 : "00:00:00:00:00:00";
            else
                mac = NormalizeMac(row.Value);
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbWrite, MacAddress = mac, Port = portBitmask });
        }
        if (action.Equals("FdbRead", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbRead, MacAddress = NormalizeMac(row.Value) });
        if (action.Equals("FdbFlush", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbFlush });
        if (action.Equals("Delay", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.Delay, DelayMs = ParseTimeout(row.Timeout, 100) });
        if (action.Equals("Packet", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(CreateEthernetTestPacket(row.Name, NormalizeMac(row.Value)));
        return null;
    }

    private static PacketItem CreateEthernetTestPacket(string name, string dstMac)
    {
        var packet = new PacketItem { Name = string.IsNullOrWhiteSpace(name) ? "FDB_Test_Packet" : name };
        packet.Blocks.Add(new EthernetBlock { DstMac = string.IsNullOrWhiteSpace(dstMac) ? "C8:4D:44:25:2D:37" : dstMac, SrcMac = "02:00:00:00:00:01", EtherType = 0x88B5 });
        var payload = new RawPayloadBlock();
        payload.ImportBytes(System.Text.Encoding.ASCII.GetBytes("KETI-FDB-FORWARDING-TEST"), 0);
        packet.Blocks.Add(payload);
        return packet;
    }

    private void ReindexScenarioRows()
    {
        for (int i = 0; i < ScenarioRows.Count; i++)
            ScenarioRows[i].Index = i;
    }

    private static string Hex(uint value) => $"0x{value:X8}";

    private static uint ParseHex(string? value)
    {
        var clean = (value ?? "").Replace("0x", "", StringComparison.OrdinalIgnoreCase).Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static int ParsePort(string? value)
    {
        var clean = (value ?? "").Trim();
        if (clean.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
            return Convert.ToInt32(clean[2..], 2);
        return int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    // 비트마스크(0x01=0, 0x02=1, 0x04=2, 0x08=3, 0x10=4, 0x20=5)를 포트 인덱스로 변환.
    // 이미 포트 인덱스(0~5)면 그대로 반환.
    private static int BitMaskToPortIndex(int value)
    {
        if (value <= 5) return value;   // 이미 포트 인덱스
        // 비트마스크: LSB 위치 반환 (단일 비트 가정)
        for (int i = 0; i < 6; i++)
            if ((value & (1 << i)) != 0) return i;
        return 0;
    }

    private static int ParseTimeout(string? value, int fallback)
    {
        var clean = (value ?? "").Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private static string NormalizeMac(string? value)
    {
        var mac = (value ?? "").Trim().Replace("-", ":").ToUpperInvariant();
        return string.IsNullOrWhiteSpace(mac) ? "C8:4D:44:25:2D:37" : mac;
    }

    // ── CSV Import ────────────────────────────────────────────────────────────
    // CSV 파일 선택 → TestScenarios/<그룹명>/ 폴더에 복사 → SyncCsvToGroups 호출
    private void ImportTcCsv()
    {
        var defaultDir = ScenarioDir;
        var dlg = new OpenFileDialog
        {
            Title       = "TC CSV 파일 선택 (여러 개 선택 가능)",
            Filter      = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : AppContext.BaseDirectory
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var files = dlg.FileNames.ToList();
            if (files.Count == 0) return;

            // 파일들이 이미 TestScenarios/ 하위에 있으면 그냥 Reload
            var allInScenarioDir = files.All(f =>
                f.StartsWith(ScenarioDir, StringComparison.OrdinalIgnoreCase));

            if (!allInScenarioDir)
            {
                // 그룹명 입력: 기본값은 선택 파일들의 공통 접두사
                var tcFiles   = files.Where(f => !Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase)).ToList();
                var groupName = tcFiles.Count > 0
                    ? CommonPrefix(tcFiles.Select(f => Path.GetFileNameWithoutExtension(f)).ToList())
                    : "Imported";

                var destDir = Path.Combine(ScenarioDir, groupName);
                Directory.CreateDirectory(destDir);

                foreach (var f in files)
                    File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true);

                Status = $"CSV {files.Count}개를 TestScenarios/{groupName}/ 에 복사함";
            }

            SyncCsvToGroups();
            TrySaveAutoTcs();
        }
        catch (Exception ex)
        {
            Status = $"CSV 임포트 실패: {ex.Message}";
        }
    }

    private static string CommonPrefix(List<string> names)
    {
        if (names.Count == 0) return "Scenarios";
        if (names.Count == 1) return names[0];
        var prefix = names[0];
        foreach (var n in names.Skip(1))
        {
            int len = 0;
            while (len < prefix.Length && len < n.Length &&
                   char.ToUpperInvariant(prefix[len]) == char.ToUpperInvariant(n[len]))
                len++;
            prefix = prefix[..len];
        }
        prefix = prefix.TrimEnd('_', '-', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return string.IsNullOrEmpty(prefix) ? "Scenarios" : prefix;
    }

    private TestCaseGroup? FindGroupOf(TestCaseEntry tc) =>
        Groups.FirstOrDefault(g => g.TestCases.Contains(tc));

    public object GetSnapshotForApi() => new
    {
        status = Status,
        selected = _selectedTc?.Name ?? string.Empty,
        sequence = TestCaseSerializer.TakeSnapshot(_packetList.Sequence),
        groups = Groups.Select((g, gi) => new
        {
            index = gi,
            name = g.Name,
            isExpanded = g.IsExpanded,
            testCases = g.TestCases.Select((tc, ti) => new
            {
                groupIndex = gi,
                index = ti,
                name = tc.Name,
                selected = tc.IsSelected,
                itemCount = tc.Items.Count
            }).ToList()
        }).ToList()
    };

    public void AddGroupForApi(string? name)
    {
        var group = new TestCaseGroup { Name = string.IsNullOrWhiteSpace(name) ? $"Group{Groups.Count + 1}" : name.Trim() };
        Groups.Add(group);
        _selectedGroup = group;
        Status = $"Group added: {group.Name}";
    }

    public void AddTestCaseForApi(int groupIndex, string? name)
    {
        var group = Groups.ElementAtOrDefault(groupIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(groupIndex));
        var tc = new TestCaseEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"TC{group.TestCases.Count + 1}" : name.Trim(),
            Items = TestCaseSerializer.TakeSnapshot(_packetList.Sequence)
        };
        group.TestCases.Add(tc);
        SelectTc(tc);
        Status = $"Test case added: {tc.Name}";
    }

    public void SelectTestCaseForApi(int groupIndex, int testCaseIndex)
    {
        var group = Groups.ElementAtOrDefault(groupIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(groupIndex));
        var tc = group.TestCases.ElementAtOrDefault(testCaseIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(testCaseIndex));
        SelectTc(tc);
    }

    public void SaveCurrentToSelectedForApi()
    {
        if (_selectedTc == null)
            throw new InvalidOperationException("No selected test case");
        SaveTc();
    }

    public void DeleteTestCaseForApi(int groupIndex, int testCaseIndex)
    {
        var group = Groups.ElementAtOrDefault(groupIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(groupIndex));
        var tc = group.TestCases.ElementAtOrDefault(testCaseIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(testCaseIndex));
        DeleteTc(tc);
    }

    public void DeleteGroupForApi(int groupIndex)
    {
        var group = Groups.ElementAtOrDefault(groupIndex)
            ?? throw new ArgumentOutOfRangeException(nameof(groupIndex));
        DeleteGroup(group);
    }
}
