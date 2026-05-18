using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Models;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

// ── 테이블 한 행 ─────────────────────────────────────────────────────────────
public class FdbResultRow : INotifyPropertyChanged
{
    public string Bucket      { get; init; } = "-";
    public string Slot        { get; init; } = "-";
    public string Mac         { get; init; } = "-";
    public string Port        { get; init; } = "-";
    public string EntryStatus { get; init; } = "없음";
    public string Timestamp   { get; init; } = "-";
    public string WrStatus    { get; init; } = "-";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── ViewModel ────────────────────────────────────────────────────────────────
public class FdbViewModel : ViewModelBase
{
    private readonly FdbService _fdb;

    // ── FDB CONTROL 프로퍼티 ──────────────────────────────────────────────
    private string _version        = "-";
    private bool   _ageScanEnable;
    private bool   _learningEnable;
    private bool   _lookupEnable;
    private string _agePeriodNs   = "0";
    private string _agingThrVal   = "0";
    private string _controlStatus = string.Empty;

    public string Version        { get => _version;        set => SetProperty(ref _version,        value); }
    public bool   AgeScanEnable  { get => _ageScanEnable;  set => SetProperty(ref _ageScanEnable,  value); }
    public bool   LearningEnable { get => _learningEnable; set => SetProperty(ref _learningEnable, value); }
    public bool   LookupEnable   { get => _lookupEnable;   set => SetProperty(ref _lookupEnable,   value); }
    public string AgePeriodNs    { get => _agePeriodNs;    set => SetProperty(ref _agePeriodNs,    value); }
    public string AgingThrVal    { get => _agingThrVal;    set => SetProperty(ref _agingThrVal,    value); }
    public string ControlStatus  { get => _controlStatus;  set => SetProperty(ref _controlStatus,  value); }

    // ── Command Input ─────────────────────────────────────────────────────
    private string _macInput    = "00:00:00:00:00:00";
    private string _vlanInput   = "0";
    private bool   _vlanValid;
    private string _portInput   = "0";
    private string _bucketInput = "0";
    private string _slotInput   = "1";   // hex bitmap

    public string MacInput    { get => _macInput;    set => SetProperty(ref _macInput,    value); }
    public string VlanInput   { get => _vlanInput;   set => SetProperty(ref _vlanInput,   value); }
    public bool   VlanValid   { get => _vlanValid;   set => SetProperty(ref _vlanValid,   value); }
    public string PortInput   { get => _portInput;   set => SetProperty(ref _portInput,   value); }
    public string BucketInput { get => _bucketInput; set => SetProperty(ref _bucketInput, value); }
    public string SlotInput   { get => _slotInput;   set => SetProperty(ref _slotInput,   value); }

    // ── Result 테이블 ─────────────────────────────────────────────────────
    public ObservableCollection<FdbResultRow> Results { get; } = new();

    private string _cmdResult = string.Empty;
    public string CmdResult { get => _cmdResult; set => SetProperty(ref _cmdResult, value); }

    // ── Commands ──────────────────────────────────────────────────────────
    public ICommand ReadConfigCommand   { get; }
    public ICommand ApplyEnableCommand  { get; }
    public ICommand FdbLoadCommand      { get; }
    public ICommand ReadByHashCommand    { get; }
    public ICommand ReadByBucketCommand  { get; }
    public ICommand WriteCommand         { get; }
    public ICommand WriteByBucketCommand { get; }
    public ICommand DeleteCommand        { get; }
    public ICommand InitAllCommand       { get; }

    public FdbViewModel(FdbService fdb)
    {
        _fdb = fdb;
        ReadConfigCommand    = new RelayCommand(async () => await ReadConfigAsync());
        ApplyEnableCommand   = new RelayCommand(async () => await ApplyEnableAsync());
        FdbLoadCommand       = new RelayCommand(async () => await FdbLoadAsync());
        ReadByHashCommand    = new RelayCommand(async () => await ReadByHashAsync());
        ReadByBucketCommand  = new RelayCommand(async () => await ReadByBucketAsync());
        WriteCommand         = new RelayCommand(async () => await WriteAsync());
        WriteByBucketCommand = new RelayCommand(async () => await WriteByBucketAsync());
        DeleteCommand        = new RelayCommand(async () => await DeleteAsync());
        InitAllCommand       = new RelayCommand(async () => await InitAllAsync());
    }

    // ── FDB CONTROL ───────────────────────────────────────────────────────
    private async Task ReadConfigAsync()
    {
        try
        {
            ControlStatus = "읽는 중...";
            var ver = await _fdb.ReadVersionAsync();
            Version = $"0x{ver:X8}";

            var en = await _fdb.ReadEnableAsync();
            AgeScanEnable  = (en & (1u << 4)) != 0;
            LearningEnable = (en & (1u << 1)) != 0;
            LookupEnable   = (en & (1u << 0)) != 0;

            AgePeriodNs = (await _fdb.ReadAgePeriodAsync()).ToString();
            AgingThrVal = (await _fdb.ReadAgingThrAsync()).ToString();

            ControlStatus = "읽기 완료";
        }
        catch (Exception ex) { ControlStatus = $"오류: {ex.Message}"; }
    }

    private async Task ApplyEnableAsync()
    {
        try
        {
            uint en = 0;
            if (AgeScanEnable)  en |= (1u << 4);
            if (LearningEnable) en |= (1u << 1);
            if (LookupEnable)   en |= (1u << 0);
            await _fdb.WriteEnableAsync(en);
            ControlStatus = "ENABLE 적용 완료";
        }
        catch (Exception ex) { ControlStatus = $"오류: {ex.Message}"; }
    }

    private async Task FdbLoadAsync()
    {
        try
        {
            await _fdb.FdbDefaultLoadAsync();
            ControlStatus = "Default Load 완료";
        }
        catch (Exception ex) { ControlStatus = $"오류: {ex.Message}"; }
    }

    // ── Read by Hash (MAC + VLAN) ─────────────────────────────────────────
    private async Task ReadByHashAsync()
    {
        try
        {
            CmdResult = "읽는 중...";
            if (!TryParseVlan(out int vlan)) return;

            Results.Clear();
            var entry = await _fdb.ReadEntryByMacAsync(MacInput, VlanValid, vlan);
            Results.Add(new FdbResultRow
            {
                Mac         = entry?.Mac ?? MacInput,
                Port        = entry?.Port.ToString() ?? "-",
                Bucket      = entry?.Bucket.ToString() ?? "-",
                Slot        = entry != null ? $"0x{entry.SlotBitmap:X}" : "-",
                Timestamp   = entry?.Timestamp.ToString() ?? "-",
                EntryStatus = entry == null ? "없음 (미학습)" : (entry.IsStatic ? "Static" : "Dynamic")
            });
            CmdResult = entry != null ? "엔트리 발견" : "미학습 (테이블에 없음)";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Read by Bucket (슬롯 지정) ────────────────────────────────────────
    private async Task ReadByBucketAsync()
    {
        try
        {
            CmdResult = "읽는 중...";
            if (!TryParseBucket(out int bucket) || !TryParseSlot(out int slot)) return;

            Results.Clear();
            var entry = await _fdb.ReadEntryAsync(bucket, slot);
            Results.Add(new FdbResultRow
            {
                Bucket      = bucket.ToString(),
                Slot        = $"0x{slot:X}",
                Mac         = entry?.Mac         ?? "-",
                Port        = entry?.Port.ToString() ?? "-",
                Timestamp   = entry?.Timestamp.ToString() ?? "-",
                EntryStatus = entry == null ? "없음" : (entry.IsStatic ? "Static" : "Dynamic")
            });
            CmdResult = entry != null ? "엔트리 발견" : "슬롯 비어있음";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Write by Hash (MAC + VLAN + Port, CMD=0x14) ───────────────────────
    private async Task WriteAsync()
    {
        try
        {
            CmdResult = "쓰는 중...";
            if (!TryParsePort(out int port) || !TryParseVlan(out int vlan)) return;

            await _fdb.WriteEntryByHashAsync(MacInput, VlanValid, vlan, port);
            CmdResult = "쓰기 완료";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Write by Bucket (MAC + VLAN + Port + Bucket + Slot, CMD=0x15) ────
    private async Task WriteByBucketAsync()
    {
        try
        {
            CmdResult = "쓰는 중...";
            if (!TryParsePort(out int port) || !TryParseVlan(out int vlan)
                || !TryParseBucket(out int bucket) || !TryParseSlot(out int slot)) return;

            await _fdb.WriteEntryAsync(MacInput, VlanValid, vlan, port, bucket, slot);
            CmdResult = $"쓰기 완료  Bkt:{bucket}  Slot:0x{slot:X}";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Delete by Hash (MAC + VLAN, CMD=0x16) ────────────────────────────
    private async Task DeleteAsync()
    {
        try
        {
            CmdResult = "삭제 중...";
            if (!TryParseVlan(out int vlan)) return;

            await _fdb.DeleteEntryByMacAsync(MacInput, VlanValid, vlan);
            Results.Clear();
            CmdResult = $"삭제 완료 ({MacInput})";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Init All Tables (CMD=0x70) ────────────────────────────────────────
    private async Task InitAllAsync()
    {
        try
        {
            CmdResult = "전체 초기화 중...";
            await _fdb.FlushAllAsync();
            Results.Clear();
            CmdResult = "전체 초기화 완료";
        }
        catch (Exception ex) { CmdResult = $"오류: {ex.Message}"; }
    }

    // ── Parse Helpers ─────────────────────────────────────────────────────
    private bool TryParseVlan(out int vlan)
    {
        if (int.TryParse(VlanInput, out vlan) && vlan is >= 0 and <= 4095) return true;
        CmdResult = "VLAN 값 오류 (0~4095)"; vlan = 0; return false;
    }

    private bool TryParsePort(out int port)
    {
        if (int.TryParse(PortInput, out port) && port is >= 0 and <= 511) return true;
        CmdResult = "Port 값 오류 (0~511)"; port = 0; return false;
    }

    private bool TryParseBucket(out int bucket)
    {
        bucket = 0;
        try
        {
            var s = BucketInput.Trim();
            bucket = Convert.ToInt32(s, s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
            if (bucket is >= 0 and <= 1023) return true;
            CmdResult = "Bucket 범위 오류 (0~1023)"; return false;
        }
        catch { CmdResult = "Bucket 파싱 오류"; return false; }
    }

    private bool TryParseSlot(out int slot)
    {
        slot = 0;
        try
        {
            var s = SlotInput.Trim();
            slot = Convert.ToInt32(s, s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
            if (slot is >= 1 and <= 0xF) return true;
            CmdResult = "Slot bitmap 오류 (0x1~0xF)"; return false;
        }
        catch { CmdResult = "Slot 파싱 오류"; return false; }
    }
}
