using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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
    private TestCaseEntry? _selectedTc;
    private TestCaseGroup? _selectedGroup;
    private TestScenarioStep? _selectedScenarioRow;
    private string _status = "";
    private string _rxPortMappingText = "RX mapping: select capture interfaces first.";
    private System.Windows.Threading.DispatcherTimer? _autoSaveTimer;

    private static readonly string AutoSavePath = Path.Combine(
        AppContext.BaseDirectory, "test_cases.json");

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
    public ICommand AddTcCommand        { get; }              // 툴바 [+ TC] — 현재 선택 그룹에 추가
    public ICommand AddTcToGroupCommand { get; }              // param: TestCaseGroup (인라인 [+] 버튼)
    public ICommand ToggleGroupCommand  { get; }              // param: TestCaseGroup
    public ICommand SelectTcCommand     { get; }              // param: TestCaseEntry
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

    public TestCaseManagerViewModel(PacketListViewModel packetList)
    {
        _packetList = packetList;

        AddGroupCommand     = new RelayCommand(AddGroup);
        AddTcCommand        = new RelayCommand(() => { if (_selectedGroup != null) AddTcToGroup(_selectedGroup); }, () => _selectedGroup != null);
        AddTcToGroupCommand = new RelayCommand<TestCaseGroup>(AddTcToGroup);
        ToggleGroupCommand  = new RelayCommand<TestCaseGroup>(g => { if (g != null) g.IsExpanded = !g.IsExpanded; });
        SelectTcCommand     = new RelayCommand<TestCaseEntry>(SelectTc);
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

        // 시퀀스 변경 시 현재 TC에 자동저장 (디바운스 1.5s)
        _autoSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            _autoSaveTimer.Stop();
            FlushCurrentTc();
        };
        _packetList.Sequence.CollectionChanged += (_, _) => RestartAutoSaveTimer();

        AutoLoad();
    }

    private void RestartAutoSaveTimer()
    {
        _autoSaveTimer?.Stop();
        _autoSaveTimer?.Start();
    }

    // 현재 시퀀스를 선택된 TC에 즉시 반영하고 파일에 저장
    private void FlushCurrentTc()
    {
        if (_selectedTc == null) return;
        _selectedTc.Items = TestCaseSerializer.TakeSnapshot(_packetList.Sequence);
        _selectedTc.ScenarioSteps = ScenarioRows.ToList();
        AutoSave();
        Status = $"자동저장 {DateTime.Now:HH:mm:ss}";
    }

    public void AttachCapture(CaptureViewModel capture)
    {
        _capture = capture;
        UpdateRxPortMappingText();
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task<string?> ValidatePacketAfterSendAsync(PacketItem packet, CancellationToken token)
    {
        if (_capture == null)
            return null;

        await Task.Delay(250, token).ConfigureAwait(false);

        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            UpdateRxPortMappingText();
            var orderedRows = ScenarioRows
                .OrderBy(r => r.TC_ID)
                .ThenBy(r => r.Test_Scenario_ID)
                .ThenBy(r => r.Index)
                .ToList();

            string? activeExpected = null;
            TestScenarioStep? targetRow = null;
            var packetDst = ResolveScenarioMac(packet.DstMac);

            foreach (var row in orderedRows)
            {
                if (row.Action.Equals("FdbFlush", StringComparison.OrdinalIgnoreCase))
                {
                    activeExpected = "0b1110";
                    continue;
                }

                if (row.Action.Equals("FdbWrite", StringComparison.OrdinalIgnoreCase))
                {
                    activeExpected = row.Expected;
                    continue;
                }

                if (!row.Action.Equals("Packet", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rowDst = ResolveScenarioMac(row.Value);
                if (row.Name.Equals(packet.Name, StringComparison.OrdinalIgnoreCase) ||
                    rowDst.Equals(packetDst, StringComparison.OrdinalIgnoreCase))
                {
                    targetRow = row;
                    break;
                }
            }

            if (targetRow == null)
                return null;

            var packets = _capture.GetPacketsSnapshot(5000);
            var rxMap = GetRxPortMap();
            return ValidatePacketRow(targetRow, activeExpected, packets, rxMap);
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

        // 새 TC는 현재 시퀀스를 그대로 복사해서 시작 (빈 시퀀스 대신)
        var snapshot = TestCaseSerializer.TakeSnapshot(_packetList.Sequence);
        var tc = new TestCaseEntry
        {
            Name  = $"TC{group.TestCases.Count + 1}",
            Items = snapshot
        };
        group.TestCases.Add(tc);
        SelectTc(tc);
    }

    // ── TC 선택 → PACKET LIST 로드 ───────────────────────────────────────────
    private void SelectTc(TestCaseEntry? tc)
    {
        // 전환 전 현재 시퀀스 + 시나리오 rows 를 즉시 저장
        if (_selectedTc != null)
        {
            _autoSaveTimer?.Stop();
            _selectedTc.Items = TestCaseSerializer.TakeSnapshot(_packetList.Sequence);
            _selectedTc.ScenarioSteps = ScenarioRows.ToList();
            AutoSave();
        }

        if (_selectedTc != null) _selectedTc.IsSelected = false;
        _selectedTc = tc;
        if (tc != null)
        {
            tc.IsSelected = true;
            _selectedGroup = FindGroupOf(tc) ?? _selectedGroup;
            var items = TestCaseSerializer.RestoreSequence(tc.Items);
            _packetList.LoadSequence(items);
            ScenarioRows.Clear();
            foreach (var step in tc.ScenarioSteps) ScenarioRows.Add(step);
            Status = $"{tc.Name}  로드됨";
        }
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

    // ── 현재 PACKET LIST → 선택된 TC에 즉시 저장 ────────────────────────────
    private void SaveTc()
    {
        _autoSaveTimer?.Stop();
        FlushCurrentTc();
    }

    // ── 파일 저장/로드 ────────────────────────────────────────────────────────
    private void SaveToFile()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Test Case Suite (*.json)|*.json|All Files (*.*)|*.*",
            DefaultExt = "json"
        };
        if (dlg.ShowDialog() != true) return;
        try   { TestCaseSerializer.SaveToFile(Groups, dlg.FileName); Status = "파일 저장 완료"; }
        catch (Exception ex) { Status = $"저장 실패: {ex.Message}"; }
    }

    private void LoadFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Test Case Suite (*.json)|*.json|All Files (*.*)|*.*"
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
        if (!Groups.Any(g => g.TestCases.Any()))
            return;

        try
        {
            TestCaseSerializer.SaveToFile(Groups, AutoSavePath);
        }
        catch { }
    }

    private void AutoLoad()
    {
        // ① .json 파일 로드
        var tcsPath = FindBestTestCaseFile();
        if (tcsPath != null)
        {
            try { ApplyLoadedGroups(TestCaseSerializer.LoadFromFile(tcsPath)); }
            catch { }
        }

        // ② TestScenarios/ 폴더의 CSV 자동 임포트
        AutoLoadCsvScenarios();
    }

    // TestScenarios 폴더를 스캔해 TC CSV 를 그룹으로 임포트.
    // 이미 같은 이름의 그룹이 있으면 건너뜀(중복 방지).
    private void AutoLoadCsvScenarios()
    {
        var scenarioDir = Path.Combine(AppContext.BaseDirectory, "TestScenarios");
        if (!Directory.Exists(scenarioDir)) return;

        // Packets CSV 분리
        var allCsvs   = Directory.GetFiles(scenarioDir, "*.csv", SearchOption.TopDirectoryOnly);
        var packetCsv = allCsvs.FirstOrDefault(f =>
            Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase));
        var tcCsvs    = allCsvs
            .Where(f => !Path.GetFileName(f).Contains("packet", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        var existingGroupNames = Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tcFile in tcCsvs)
        {
            var groupName = Path.GetFileNameWithoutExtension(tcFile);
            if (existingGroupNames.Contains(groupName)) continue; // 이미 로드됨

            try
            {
                var imported = TestCaseSerializer.ImportFromCsv(tcFile, packetCsv);
                foreach (var g in imported) Groups.Add(g);
                existingGroupNames.Add(groupName);
            }
            catch { }
        }

        // 로드 후 첫 TC 선택 (아직 선택된 TC 없을 때만)
        if (_selectedTc == null)
        {
            var first = Groups.SelectMany(g => g.TestCases).FirstOrDefault();
            if (first != null) SelectTc(first);
        }
    }

    private static string? FindBestTestCaseFile()
    {
        var candidates = new List<string>();
        if (File.Exists(AutoSavePath))
            candidates.Add(AutoSavePath);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            try
            {
                candidates.AddRange(Directory.GetFiles(dir.FullName, "test_cases.json", SearchOption.AllDirectories));
            }
            catch { }
        }

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new { Path = path, Score = CountSavedTestCases(path), Size = new FileInfo(path).Length })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Size)
            .Select(x => x.Path)
            .FirstOrDefault();
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
        Groups.Clear();
        foreach (var g in loaded) Groups.Add(g);
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
            foreach (var row in ScenarioRows.OrderBy(r => r.TC_ID).ThenBy(r => r.Test_Scenario_ID).ThenBy(r => r.Index))
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

            ScenarioRows.Clear();
            foreach (var line in lines.Skip(1))
            {
                var c = ParseCsvLine(line);
                if (c.Count < 11) continue;
                ScenarioRows.Add(new TestScenarioStep
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
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 0, Test_Scenario_ID = 0, Name = $"Port{port}_LinkUp_Wait", Action = "RegWaitFor", Address = Hex(bmsrAddress), Mask = "0x80000004", Expected = "0x00000004", Timeout = "100ms" });
    }

    private void AddFdbFlowRows()
    {
        const string mac = "self";
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 1, Test_Scenario_ID = 1, Name = "FDB_Flush_Unknown_Unicast", Action = "FdbFlush", Note = "Unknown unicast should flood to IVN ports 1/2/3." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 1, Test_Scenario_ID = 1, Name = "Send_Unknown_Unicast", Action = "Packet", Value = mac, Note = "Dst MAC packet from port 0." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 2, Test_Scenario_ID = 1, Name = "FDB_Write_Port1_Ethernet10", Action = "FdbWrite", Value = mac, Expected = "0b0010", Note = "Only IVN port 1 / Ethernet 10 should receive." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 2, Test_Scenario_ID = 1, Name = "Send_Port1_Check", Action = "Packet", Value = mac });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 3, Test_Scenario_ID = 1, Name = "FDB_Write_Port2_Ethernet8", Action = "FdbWrite", Value = mac, Expected = "0b0100", Note = "Only IVN port 2 / Ethernet 8 should receive." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 3, Test_Scenario_ID = 1, Name = "Send_Port2_Check", Action = "Packet", Value = mac });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 4, Test_Scenario_ID = 1, Name = "FDB_Write_Port3_Ethernet5", Action = "FdbWrite", Value = mac, Expected = "0b1000", Note = "Only IVN port 3 / Ethernet 5 should receive." });
        ScenarioRows.Add(new TestScenarioStep { TC_ID = 4, Test_Scenario_ID = 1, Name = "Send_Port3_Check", Action = "Packet", Value = mac });
    }

    private void ApplyScenarioSheetToSequence()
    {
        var items = ScenarioRows.OrderBy(r => r.TC_ID).ThenBy(r => r.Test_Scenario_ID).ThenBy(r => r.Index)
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

        foreach (var row in ScenarioRows.OrderBy(r => r.TC_ID).ThenBy(r => r.Test_Scenario_ID).ThenBy(r => r.Index))
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

            ValidatePacketRow(row, activeExpected, packets, rxMap);

            if (row.Result == "PASS") pass++;
            else fail++;
        }

        Status = $"Capture validation completed. PASS {pass}, FAIL {fail}, packets scanned {packets.Count}.";
    }

    private string ValidatePacketRow(
        TestScenarioStep row,
        string? activeExpected,
        IReadOnlyList<CaptureRow> packets,
        Dictionary<int, string> rxMap)
    {
        var mac = ResolveScenarioMac(row.Value);
        var matches = packets
            .Where(p => p.DstMac.Equals(mac, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var observedInterfaces = matches
            .Select(p => p.InterfaceName)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(i => i)
            .ToList();

        var packetCounts = matches
            .Where(p => !string.IsNullOrWhiteSpace(p.InterfaceName))
            .GroupBy(p => p.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key)
            .Select(g => $"{ShortName(g.Key)}:{g.Count()}")
            .ToList();

        var expected = ExpectedInterfaceTokens(activeExpected, rxMap);
        var monitored = rxMap.Values.ToList();
        var expectedHits = expected
            .Where(token => observedInterfaces.Any(i => InterfaceMatches(i, token)))
            .ToList();
        var unexpectedHits = observedInterfaces
            .Where(i => monitored.Any(token => InterfaceMatches(i, token)) &&
                        !expected.Any(token => InterfaceMatches(i, token)))
            .ToList();

        var expectedText = expected.Count == 0 ? "(any RX)" : string.Join(", ", expected.Select(ShortName));
        var observedText = observedInterfaces.Count == 0
            ? "none"
            : string.Join(", ", observedInterfaces.Select(ShortName));
        var countText = packetCounts.Count == 0 ? "0 packets" : string.Join(" | ", packetCounts);
        var lastPacket = matches.LastOrDefault();
        var hexText = lastPacket == null ? "hex=none" : $"hex={lastPacket.HexPreview}";

        row.Observed = $"Expected {expectedText}; RX {observedText}; {countText}; Dst {mac}; {hexText}";

        if (expected.Count == 0)
            row.Result = matches.Count > 0 ? "PASS" : "FAIL";
        else if (expectedHits.Count == expected.Count && unexpectedHits.Count == 0)
            row.Result = "PASS";
        else
            row.Result = "FAIL";

        return $"[PacketCheck {row.Result}] {row.Name} dst={mac}, expected={expectedText}, observed={observedText}, {countText}, {hexText}";
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

    private static bool InterfaceMatches(string observed, string expected)
    {
        if (observed.Equals(expected, StringComparison.OrdinalIgnoreCase)) return true;
        if (observed.Contains(expected, StringComparison.OrdinalIgnoreCase)) return true;
        if (expected.Contains(observed, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private SequenceItem? ConvertRowToSequenceItem(TestScenarioStep row)
    {
        var action = (row.Action ?? "").Trim();
        if (action.Equals("RegWrite", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegWrite, Address = ParseHex(row.Address), Value = ParseHex(row.Value) });
        if (action.Equals("RegRead", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegRead, Address = ParseHex(row.Address) });
        if (action.Equals("RegWaitFor", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.RegWaitFor, Address = ParseHex(row.Address), Mask = ParseHex(row.Mask), Expected = ParseHex(row.Expected), TimeoutMs = ParseTimeout(row.Timeout, 1000) });
        if (action.Equals("FdbWrite", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbWrite, MacAddress = ResolveScenarioMac(row.Value), Port = ParsePort(row.Expected) });
        if (action.Equals("FdbRead", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbRead, MacAddress = ResolveScenarioMac(row.Value) });
        if (action.Equals("FdbFlush", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.FdbFlush });
        if (action.Equals("Delay", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(new SequenceEvent { EventType = SequenceEventType.Delay, DelayMs = ParseTimeout(row.Timeout, 100) });
        if (action.Equals("Packet", StringComparison.OrdinalIgnoreCase))
            return new SequenceItem(CreateEthernetTestPacket(row.Name, ResolveScenarioMac(row.Value)));
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

    private static int ParseTimeout(string? value, int fallback)
    {
        var clean = (value ?? "").Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase);
        return int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
    }

    private string ResolveScenarioMac(string? value)
    {
        var clean = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean) ||
            clean.Equals("self", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("local", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("me", StringComparison.OrdinalIgnoreCase))
        {
            return GetSelectedCaptureMac() ?? "00:00:00:00:00:00";
        }

        return NormalizeMac(clean);
    }

    private string? GetSelectedCaptureMac()
    {
        var mac = _capture?.Interfaces
            .Where(i => i.IsSelected)
            .Select(i => i.Device.MacAddress?.GetAddressBytes())
            .FirstOrDefault(bytes => bytes is { Length: 6 });

        return mac == null ? null : string.Join(":", mac.Select(b => b.ToString("X2")));
    }

    private static string NormalizeMac(string? value)
    {
        var mac = (value ?? "").Trim().Replace("-", ":").ToUpperInvariant();
        return string.IsNullOrWhiteSpace(mac) ? "00:00:00:00:00:00" : mac;
    }

    // ── CSV Import ────────────────────────────────────────────────────────────
    // TC CSV 파일을 선택하면, 같은 폴더에 TC_Packets.csv 가 있으면 자동으로 함께 로드
    private void ImportTcCsv()
    {
        var defaultDir = Path.Combine(AppContext.BaseDirectory, "TestScenarios");
        var dlg = new OpenFileDialog
        {
            Title       = "TC CSV 파일 선택 (TC.csv 또는 TC1.csv 형식)",
            Filter      = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
            Multiselect = true,
            InitialDirectory = Directory.Exists(defaultDir) ? defaultDir : AppContext.BaseDirectory
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 선택된 파일 분류: Packets CSV vs TC CSV
            string? packetCsv = null;
            var tcCsvFiles = new List<string>();

            foreach (var f in dlg.FileNames)
            {
                var fn = Path.GetFileName(f).ToLowerInvariant();
                if (fn.Contains("packet"))
                    packetCsv = f;
                else
                    tcCsvFiles.Add(f);
            }

            // 단일 파일이고 패킷 파일이 없으면 같은 폴더에서 *packet*.csv 자동 탐색
            if (packetCsv == null && tcCsvFiles.Count > 0)
            {
                var dir = Path.GetDirectoryName(tcCsvFiles[0]);
                if (dir != null)
                {
                    var auto = Directory.GetFiles(dir, "*packet*.csv", SearchOption.TopDirectoryOnly)
                                        .FirstOrDefault();
                    if (auto != null) packetCsv = auto;
                }
            }

            var imported = new List<TestCaseGroup>();
            foreach (var tcFile in tcCsvFiles)
                imported.AddRange(TestCaseSerializer.ImportFromCsv(tcFile, packetCsv));

            if (imported.Count == 0)
            {
                Status = "임포트된 TC가 없습니다. CSV 포맷을 확인하세요.";
                return;
            }

            foreach (var g in imported) Groups.Add(g);
            var firstTc = imported.SelectMany(g => g.TestCases).FirstOrDefault();
            if (firstTc != null) SelectTc(firstTc);

            var tcCount = imported.Sum(g => g.TestCases.Count);
            Status = $"CSV 임포트 완료: {tcCount}개 TC (패킷파일: {(packetCsv != null ? Path.GetFileName(packetCsv) : "없음")})";
        }
        catch (Exception ex)
        {
            Status = $"CSV 임포트 실패: {ex.Message}";
        }
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
