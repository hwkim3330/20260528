using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

public class LedBitItem : ViewModelBase
{
    private bool _on;
    public string Label { get; }
    public bool IsOn { get => _on; set => SetProperty(ref _on, value); }
    public LedBitItem(string label) => Label = label;
}

public class LedClockViewModel : ViewModelBase
{
    private readonly RegisterService _reg;

    private const uint OFF_LED_OUT    = 0x060;
    private const uint OFF_EXT_SW_IN  = 0x064;
    private const uint OFF_CLK_LIM_0  = 0x068;
    private const uint OFF_CLK_LIM_1  = 0x06C;
    private const uint OFF_CLK_LIM_R0 = 0x0D0;

    // ── LED_REG_OUT ───────────────────────────────────────────────────────────
    // FPGA_LED_CTRL [9:8]: 0=CPU GPIO, 1=FPGA Auto, 3=Register
    private int _ledCtrl = 1;
    public int LedCtrl { get => _ledCtrl; set { SetProperty(ref _ledCtrl, value); NotifyLedMode(); } }
    public bool LedModeCpuGpio  { get => _ledCtrl == 0; set { if (value) LedCtrl = 0; } }
    public bool LedModeFpga     { get => _ledCtrl == 1; set { if (value) LedCtrl = 1; } }
    public bool LedModeRegister { get => _ledCtrl == 3; set { if (value) LedCtrl = 3; } }

    // FPGA 자동 모드 — 읽기 전용 8개 LED (각 의미 고정)
    public ObservableCollection<LedBitItem> FpgaLedBits { get; } = new();

    // 레지스터 직접 모드 — 쓰기 가능 8개 LED
    public ObservableCollection<LedBitItem> RegLedBits { get; } = new();

    // ── EXT_SW_IN ─────────────────────────────────────────────────────────────
    public ObservableCollection<LedBitItem> ExtSwBits { get; } = new();

    // ── CLK COUNT LIMIT ───────────────────────────────────────────────────────
    private double _sysClkMhz  = 200;
    private double _ahbClkMhz  = 100;
    private double _rgmii0Mhz  = 125;

    public double SysClkMhz  { get => _sysClkMhz;  set => SetProperty(ref _sysClkMhz,  value); }
    public double AhbClkMhz  { get => _ahbClkMhz;  set => SetProperty(ref _ahbClkMhz,  value); }
    public double Rgmii0Mhz  { get => _rgmii0Mhz;  set => SetProperty(ref _rgmii0Mhz,  value); }

    public double SysBlinkSec  => _sysClkMhz  > 0 ? Math.Round(1.0 / _sysClkMhz  * 200, 3) : 0;
    public double AhbBlinkSec  => _ahbClkMhz  > 0 ? Math.Round(1.0 / _ahbClkMhz  * 100, 3) : 0;
    public double Rgmii0BlinkSec => _rgmii0Mhz > 0 ? Math.Round(1.0 / _rgmii0Mhz * 125, 3) : 0;

    // ── Status ───────────────────────────────────────────────────────────────
    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ── Commands ─────────────────────────────────────────────────────────────
    public ICommand ReadAllCommand       { get; }
    public ICommand ReadLedCommand       { get; }
    public ICommand ApplyLedModeCommand  { get; }
    public ICommand ApplyRegLedCommand   { get; }
    public ICommand ReadExtSwCommand     { get; }
    public ICommand ReadClkLimCommand    { get; }
    public ICommand ApplyAllClkCommand   { get; }
    public ICommand ApplySysClkCommand   { get; }
    public ICommand ApplyAhbClkCommand   { get; }
    public ICommand ApplyRgmii0Command   { get; }

    private static readonly string[] FpgaLedLabels =
    {
        "System CLK Blink(400M)", "AHB CLK Blink(400M)", "RGMII CLK Blink(125M)", "Reset_n",
        "EXT_SW[0]", "EXT_SW[1]", "EXT_SW[2]", "EXT_SW[3]"
    };

    public LedClockViewModel(RegisterService reg)
    {
        _reg = reg;
        for (int i = 0; i < 8; i++) FpgaLedBits.Add(new LedBitItem(FpgaLedLabels[i]));
        for (int i = 0; i < 8; i++) RegLedBits.Add(new LedBitItem($"LED{i}"));
        for (int i = 0; i < 6; i++) ExtSwBits.Add(new LedBitItem($"SW{i}"));

        ReadAllCommand      = new RelayCommand(async () => await ReadAllAsync());
        ReadLedCommand      = new RelayCommand(async () => await ReadLedAsync());
        ApplyLedModeCommand = new RelayCommand(async () => await ApplyLedModeAsync());
        ApplyRegLedCommand  = new RelayCommand(async () => await ApplyRegLedAsync());
        ReadExtSwCommand    = new RelayCommand(async () => await ReadExtSwAsync());
        ReadClkLimCommand   = new RelayCommand(async () => await ReadClkLimAsync());
        ApplyAllClkCommand  = new RelayCommand(async () => await ApplyAllClkAsync());
        ApplySysClkCommand  = new RelayCommand(async () => await ApplyClkLimAsync(OFF_CLK_LIM_0,  SysClkMhz));
        ApplyAhbClkCommand  = new RelayCommand(async () => await ApplyClkLimAsync(OFF_CLK_LIM_1,  AhbClkMhz));
        ApplyRgmii0Command  = new RelayCommand(async () => await ApplyClkLimAsync(OFF_CLK_LIM_R0, Rgmii0Mhz));
    }

    private async Task ReadAllAsync()
    {
        await ReadLedAsync();
        await ReadExtSwAsync();
        await ReadClkLimAsync();
    }

    private async Task ApplyAllClkAsync()
    {
        await ApplyClkLimAsync(OFF_CLK_LIM_0,  SysClkMhz);
        await ApplyClkLimAsync(OFF_CLK_LIM_1,  AhbClkMhz);
        await ApplyClkLimAsync(OFF_CLK_LIM_R0, Rgmii0Mhz);
    }

    private void NotifyLedMode()
    {
        OnPropertyChanged(nameof(LedModeCpuGpio));
        OnPropertyChanged(nameof(LedModeFpga));
        OnPropertyChanged(nameof(LedModeRegister));
    }

    private async Task ReadLedAsync()
    {
        try
        {
            var v    = await _reg.ReadAsync(OFF_LED_OUT);
            LedCtrl  = (int)((v >> 8) & 0x3);
            var leds = (byte)(v & 0xFF);
            for (int i = 0; i < 8; i++)
            {
                bool on = ((leds >> i) & 1) != 0;
                FpgaLedBits[i].IsOn = on;
                RegLedBits[i].IsOn  = on;
            }
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyLedModeAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_LED_OUT);
            v = (v & ~0x300u) | ((uint)LedCtrl << 8);
            await _reg.WriteAsync(OFF_LED_OUT, v);
            Status = "LED 모드 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyRegLedAsync()
    {
        try
        {
            uint leds = 0;
            for (int i = 0; i < 8; i++)
                if (RegLedBits[i].IsOn) leds |= (1u << i);
            var v = await _reg.ReadAsync(OFF_LED_OUT);
            v = (v & ~0xFFu) | leds;
            await _reg.WriteAsync(OFF_LED_OUT, v);
            Status = "LED 출력 설정 완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadExtSwAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(OFF_EXT_SW_IN);
            for (int i = 0; i < 6; i++)
                ExtSwBits[i].IsOn = ((v >> i) & 1) != 0;
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ReadClkLimAsync()
    {
        try
        {
            SysClkMhz  = LimitToMhz(await _reg.ReadAsync(OFF_CLK_LIM_0));
            AhbClkMhz  = LimitToMhz(await _reg.ReadAsync(OFF_CLK_LIM_1));
            Rgmii0Mhz  = LimitToMhz(await _reg.ReadAsync(OFF_CLK_LIM_R0));
            OnPropertyChanged(nameof(SysBlinkSec));
            OnPropertyChanged(nameof(AhbBlinkSec));
            OnPropertyChanged(nameof(Rgmii0BlinkSec));
            Status = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task ApplyClkLimAsync(uint offset, double mhz)
    {
        try
        {
            await _reg.WriteAsync(offset, MhzToLimit(mhz));
            Status = $"클럭 설정 완료 ({mhz} MHz)";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    // limit = 실제클럭(MHz) × 10^6 / 2   (절반값 저장)
    private static double LimitToMhz(uint limit) =>
        limit > 0 ? Math.Round((double)limit * 2 / 1_000_000, 3) : 0;

    private static uint MhzToLimit(double mhz) =>
        (uint)(mhz * 1_000_000 / 2);
}
