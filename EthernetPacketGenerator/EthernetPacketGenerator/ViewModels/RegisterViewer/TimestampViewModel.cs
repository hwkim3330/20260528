using System;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

public class TimestampViewModel : ViewModelBase
{
    private readonly RegisterService _reg;

    private const uint OFF_NS       = 0x020;
    private const uint OFF_SEC_LO   = 0x024;
    private const uint OFF_SEC_HI   = 0x028;
    private const uint OFF_CTRL_0   = 0x02C;
    private const uint OFF_CTRL_1   = 0x030;
    private const uint OFF_INCDEC_0 = 0x034;
    private const uint OFF_INCDEC_1 = 0x038;

    // ── 현재 시각 표시 ────────────────────────────────────────────────────────
    private string _currentTime = "—";
    public string CurrentTime { get => _currentTime; set => SetProperty(ref _currentTime, value); }

    // ── 시간 설정 입력값 ──────────────────────────────────────────────────────
    private int _setYear  = DateTime.Now.Year;
    private int _setMonth = DateTime.Now.Month;
    private int _setDay   = DateTime.Now.Day;
    private int _setHour  = DateTime.Now.Hour;
    private int _setMin   = DateTime.Now.Minute;
    private int _setSec   = DateTime.Now.Second;
    private uint _setNs   = 0;

    public int  SetYear  { get => _setYear;  set => SetProperty(ref _setYear,  value); }
    public int  SetMonth { get => _setMonth; set => SetProperty(ref _setMonth, value); }
    public int  SetDay   { get => _setDay;   set => SetProperty(ref _setDay,   value); }
    public int  SetHour  { get => _setHour;  set => SetProperty(ref _setHour,  value); }
    public int  SetMin   { get => _setMin;   set => SetProperty(ref _setMin,   value); }
    public int  SetSec   { get => _setSec;   set => SetProperty(ref _setSec,   value); }
    public uint SetNs    { get => _setNs;    set => SetProperty(ref _setNs,    value); }

    // ── 클럭 설정 ─────────────────────────────────────────────────────────────
    private double _clockMhz = 200.0;
    public double ClockMhz
    {
        get => _clockMhz;
        set => SetProperty(ref _clockMhz, value);
    }

    // ── PPS 설정 ──────────────────────────────────────────────────────────────
    // PPS_OUT_SRC [17:16]: 0=Disable, 1=Internal, 2/3=GPS
    private int _ppsSrc = 1;
    public int PpsSrc { get => _ppsSrc; set => SetProperty(ref _ppsSrc, value); }
    public bool PpsDisable  { get => _ppsSrc == 0; set { if (value) PpsSrc = 0; } }
    public bool PpsInternal { get => _ppsSrc == 1; set { if (value) PpsSrc = 1; } }
    public bool PpsGps      { get => _ppsSrc >= 2; set { if (value) PpsSrc = 2; } }

    // PPS_WIDTH [31:24]: 2ms 단위, 0x32=100ms 기본값
    private int _ppsWidthMs = 100;
    public int PpsWidthMs
    {
        get => _ppsWidthMs;
        set => SetProperty(ref _ppsWidthMs, Math.Clamp(value, 0, 510));
    }

    // ── 시간 보정 ─────────────────────────────────────────────────────────────
    private int _nsAdjMs = 0;
    private int _secAdj  = 0;
    public int NsAdjMs { get => _nsAdjMs; set => SetProperty(ref _nsAdjMs, value); }
    public int SecAdj  { get => _secAdj;  set => SetProperty(ref _secAdj,  value); }

    // ── Status ───────────────────────────────────────────────────────────────
    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand ReadAllCommand        { get; }
    public ICommand ReadTimeCommand       { get; }
    public ICommand SetTimeCommand        { get; }
    public ICommand ReadClockCommand      { get; }
    public ICommand ApplyClockCommand     { get; }
    public ICommand ApplyClkPpsCommand    { get; }
    public ICommand NsAdjIncCommand       { get; }
    public ICommand NsAdjDecCommand       { get; }
    public ICommand SecAdjIncCommand      { get; }
    public ICommand SecAdjDecCommand      { get; }
    public ICommand ReadPpsCommand        { get; }
    public ICommand ApplyPpsCommand       { get; }

    public TimestampViewModel(RegisterService reg)
    {
        _reg = reg;
        ReadAllCommand    = new RelayCommand(async () => { await ReadTimeAsync(); await ReadClockAsync(); await ReadPpsAsync(); });
        ReadTimeCommand   = new RelayCommand(async () => await ReadTimeAsync());
        SetTimeCommand    = new RelayCommand(async () => await SetTimeAsync());
        ReadClockCommand  = new RelayCommand(async () => await ReadClockAsync());
        ApplyClockCommand = new RelayCommand(async () => await ApplyClockAsync());
        ApplyClkPpsCommand = new RelayCommand(async () => { await ApplyClockAsync(); await ApplyPpsAsync(); });
        NsAdjIncCommand   = new RelayCommand(async () => await AdjNsAsync(inc: true));
        NsAdjDecCommand   = new RelayCommand(async () => await AdjNsAsync(inc: false));
        SecAdjIncCommand  = new RelayCommand(async () => await AdjSecAsync(inc: true));
        SecAdjDecCommand  = new RelayCommand(async () => await AdjSecAsync(inc: false));
        ReadPpsCommand    = new RelayCommand(async () => await ReadPpsAsync());
        ApplyPpsCommand   = new RelayCommand(async () => await ApplyPpsAsync());
    }

    // ── 시간 읽기 (Snapshot: NS → SEC_LO → SEC_HI 순) ─────────────────────
    private async Task ReadTimeAsync()
    {
        try
        {
            // NS를 먼저 읽으면 SEC가 자동 캡처됨
            var ns    = await _reg.ReadAsync(OFF_NS);
            var secLo = await _reg.ReadAsync(OFF_SEC_LO);
            var secHi = await _reg.ReadAsync(OFF_SEC_HI);

            ulong sec = ((ulong)(secHi & 0xFFFF) << 32) | secLo;
            var dto = DateTimeOffset.FromUnixTimeSeconds((long)sec)
                                    .ToLocalTime();
            CurrentTime = $"{dto:yyyy-MM-dd  HH:mm:ss}.{ns:D9} ns";
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── 시간 쓰기 (NS → SEC_LO → SEC_HI(Trigger) 순) ────────────────────
    private async Task SetTimeAsync()
    {
        try
        {
            var dto    = new DateTimeOffset(SetYear, SetMonth, SetDay,
                                            SetHour, SetMin,   SetSec,
                                            TimeZoneInfo.Local.GetUtcOffset(DateTime.Now));
            var unixSec = (ulong)dto.ToUnixTimeSeconds();
            var secLo   = (uint)(unixSec & 0xFFFFFFFF);
            var secHi   = (uint)((unixSec >> 32) & 0xFFFF);

            await _reg.WriteAsync(OFF_NS,     SetNs);
            await _reg.WriteAsync(OFF_SEC_LO, secLo);
            await _reg.WriteAsync(OFF_SEC_HI, secHi);   // Trigger
            Status = "시간 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── 클럭 읽기 (CTRL_0 ADDEND + CTRL_1 INCREMENT → MHz 역산) ─────────
    private async Task ReadClockAsync()
    {
        try
        {
            var addend    = await _reg.ReadAsync(OFF_CTRL_0);
            var ctrl1     = await _reg.ReadAsync(OFF_CTRL_1);
            var increment = ctrl1 & 0xFFFF;

            // 틱당 ns = (increment + addend/2^32) × 10^9 / 2^32
            double scaledPerTick = increment + (double)addend / 4294967296.0;
            double nsPerTick     = scaledPerTick * 1e9 / 4294967296.0;
            ClockMhz = nsPerTick > 0 ? Math.Round(1000.0 / nsPerTick, 6) : 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── 클럭 쓰기 (MHz → ADDEND + INCREMENT 계산) ───────────────────────
    private async Task ApplyClockAsync()
    {
        try
        {
            double periodNs  = ClockMhz > 0 ? 1000.0 / ClockMhz : 0;
            double exactIncr = periodNs * 4294967296.0 / 1e9;
            uint   increment = (uint)Math.Floor(exactIncr);
            uint   addend    = (uint)Math.Round((exactIncr - increment) * 4294967296.0);

            var ctrl1 = await _reg.ReadAsync(OFF_CTRL_1);
            ctrl1 = (ctrl1 & 0xFFFF_0000) | (increment & 0xFFFF);

            await _reg.WriteAsync(OFF_CTRL_0, addend);
            await _reg.WriteAsync(OFF_CTRL_1, ctrl1);
            Status = $"클럭 설정 완료 (INCREMENT={increment}, ADDEND=0x{addend:X8})";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── NS 오프셋 보정 ────────────────────────────────────────────────────
    private async Task AdjNsAsync(bool inc)
    {
        try
        {
            uint nsVal = (uint)((long)NsAdjMs * 1_000_000);   // ms → ns
            uint v     = (nsVal & 0x3FFF_FFFF) | (inc ? 0x4000_0000u : 0x8000_0000u);
            await _reg.WriteAsync(OFF_INCDEC_0, v);
            Status = $"NS {(inc ? "+" : "-")}{NsAdjMs}ms 보정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── SEC 오프셋 보정 ───────────────────────────────────────────────────
    private async Task AdjSecAsync(bool inc)
    {
        try
        {
            uint v = ((uint)Math.Abs(SecAdj) & 0x3FFF_FFFF)
                   | (inc ? 0x4000_0000u : 0x8000_0000u);
            await _reg.WriteAsync(OFF_INCDEC_1, v);
            Status = $"SEC {(inc ? "+" : "-")}{Math.Abs(SecAdj)}초 보정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── PPS 읽기 ──────────────────────────────────────────────────────────
    private async Task ReadPpsAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_CTRL_1);
            PpsSrc     = (int)((v >> 16) & 0x3);
            PpsWidthMs = (int)((v >> 24) & 0xFF) * 2;
            OnPropertyChanged(nameof(PpsDisable));
            OnPropertyChanged(nameof(PpsInternal));
            OnPropertyChanged(nameof(PpsGps));
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // ── PPS 쓰기 ──────────────────────────────────────────────────────────
    private async Task ApplyPpsAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_CTRL_1);
            v &= ~0xFF03_0000u;                           // PPS_WIDTH, PPS_OUT_SRC 클리어
            v |= ((uint)PpsSrc << 16);
            v |= (((uint)(PpsWidthMs / 2) & 0xFF) << 24);
            await _reg.WriteAsync(OFF_CTRL_1, v);
            Status = "PPS 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }
}
