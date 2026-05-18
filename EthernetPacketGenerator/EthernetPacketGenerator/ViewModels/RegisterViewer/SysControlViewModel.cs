using System;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

public class SysControlViewModel : ViewModelBase
{
    private readonly RegisterService _reg;

    private const uint OFF_VERSION    = 0x000;
    private const uint OFF_DEF_LOAD   = 0x004;
    private const uint OFF_ENABLE     = 0x008;
    private const uint OFF_HOST_IF    = 0x00C;

    // ── VERSION ──────────────────────────────────────────────────────────────
    private string _versionDisplay = "—";
    public string VersionDisplay
    {
        get => _versionDisplay;
        set => SetProperty(ref _versionDisplay, value);
    }

    // ── ENABLE_REG ───────────────────────────────────────────────────────────
    private bool _tsgwEnable = true;
    public bool TsgwEnable { get => _tsgwEnable; set => SetProperty(ref _tsgwEnable, value); }

    // PORT_ENABLE [15:8] — 8비트, 각 포트별
    private bool _p0 = true, _p1 = true, _p2 = true, _p3 = true,
                 _p4 = true, _p5 = true, _p6 = true, _p7 = true;
    public bool P0 { get => _p0; set => SetProperty(ref _p0, value); }
    public bool P1 { get => _p1; set => SetProperty(ref _p1, value); }
    public bool P2 { get => _p2; set => SetProperty(ref _p2, value); }
    public bool P3 { get => _p3; set => SetProperty(ref _p3, value); }
    public bool P4 { get => _p4; set => SetProperty(ref _p4, value); }
    public bool P5 { get => _p5; set => SetProperty(ref _p5, value); }
    public bool P6 { get => _p6; set => SetProperty(ref _p6, value); }
    public bool P7 { get => _p7; set => SetProperty(ref _p7, value); }

    // ── HOST_IF_CTRL ─────────────────────────────────────────────────────────
    private int _ahbWrWait = 15;
    private int _ahbRdWait = 15;
    public int AhbWrWait { get => _ahbWrWait; set => SetProperty(ref _ahbWrWait, Math.Clamp(value, 0, 15)); }
    public int AhbRdWait { get => _ahbRdWait; set => SetProperty(ref _ahbRdWait, Math.Clamp(value, 0, 15)); }

    // ── Status ───────────────────────────────────────────────────────────────
    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand ReadAllCommand        { get; }
    public ICommand LoadDefaultsCommand   { get; }
    public ICommand ReadEnableCommand     { get; }
    public ICommand ApplyEnableCommand    { get; }
    public ICommand ReadHostIfCommand     { get; }
    public ICommand ApplyHostIfCommand    { get; }

    public SysControlViewModel(RegisterService reg)
    {
        _reg = reg;
        ReadAllCommand      = new RelayCommand(async () => await ReadAllAsync());
        LoadDefaultsCommand = new RelayCommand(async () => await LoadDefaultsAsync());
        ReadEnableCommand   = new RelayCommand(async () => await ReadEnableAsync());
        ApplyEnableCommand  = new RelayCommand(async () => await ApplyEnableAsync());
        ReadHostIfCommand   = new RelayCommand(async () => await ReadHostIfAsync());
        ApplyHostIfCommand  = new RelayCommand(async () => await ApplyHostIfAsync());
    }

    private async Task ReadAllAsync()
    {
        await ReadVersionAsync();
        await ReadEnableAsync();
        await ReadHostIfAsync();
    }

    private async Task ReadVersionAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_VERSION);
            VersionDisplay = ParseVersion(v);
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private static string ParseVersion(uint v)
    {
        var major = (v >> 24) & 0xFF;
        var year  = (v >> 16) & 0xFF;
        var month = (v >> 12) & 0xF;
        var day   = (v >>  4) & 0xFF;
        var minor =  v        & 0xF;

        var majorName = major == 0x52 ? "TSGW" : $"0x{major:X2}";
        // YEAR, DAY는 BCD 인코딩: 0x26 → 26, 0x14 → 14
        var yearVal  = ((year >> 4) & 0xF) * 10 + (year & 0xF);
        var dayVal   = ((day  >> 4) & 0xF) * 10 + (day  & 0xF);
        var monthStr = month switch
        {
            0xA => "10월", 0xB => "11월", 0xC => "12월",
            _   => $"{month}월"
        };

        return $"{majorName}  20{yearVal:D2}년 {monthStr} {dayVal:D2}일  v{minor}";
    }

    private async Task LoadDefaultsAsync()
    {
        try
        {
            await _reg.WriteAsync(OFF_DEF_LOAD, 0x1);
            Status = "기본값 로드 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadEnableAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_ENABLE);
            TsgwEnable = (v & 0x1) != 0;
            var ports  = (v >> 8) & 0xFF;
            P0 = (ports & 0x01) != 0; P1 = (ports & 0x02) != 0;
            P2 = (ports & 0x04) != 0; P3 = (ports & 0x08) != 0;
            P4 = (ports & 0x10) != 0; P5 = (ports & 0x20) != 0;
            P6 = (ports & 0x40) != 0; P7 = (ports & 0x80) != 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyEnableAsync()
    {
        try
        {
            uint ports = 0;
            if (P0) ports |= 0x01; if (P1) ports |= 0x02;
            if (P2) ports |= 0x04; if (P3) ports |= 0x08;
            if (P4) ports |= 0x10; if (P5) ports |= 0x20;
            if (P6) ports |= 0x40; if (P7) ports |= 0x80;
            uint v = (TsgwEnable ? 1u : 0u) | (ports << 8);
            await _reg.WriteAsync(OFF_ENABLE, v);
            Status = "Enable 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadHostIfAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_HOST_IF);
            AhbWrWait = (int)(v & 0xF);
            AhbRdWait = (int)((v >> 4) & 0xF);
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyHostIfAsync()
    {
        try
        {
            uint v = ((uint)AhbRdWait << 4) | (uint)AhbWrWait;
            await _reg.WriteAsync(OFF_HOST_IF, v);
            Status = "AHB 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }
}
