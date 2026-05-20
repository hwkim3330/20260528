using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

// ── 포트별 링크 상태 행 ──────────────────────────────────────────────────────
public class MdioPortStatus : INotifyPropertyChanged
{
    public string Label { get; init; } = "";

    private bool? _linkUp;
    public bool? LinkUp
    {
        get => _linkUp;
        set
        {
            _linkUp = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LinkLabel));
            OnPropertyChanged(nameof(DotColor));
        }
    }

    public string LinkLabel => LinkUp == null ? "—" : (LinkUp.Value ? "Link UP" : "Link DOWN");
    public string DotColor  => LinkUp == null ? "#555555" : (LinkUp.Value ? "#44FF88" : "#FF4444");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── ViewModel ────────────────────────────────────────────────────────────────
public class MdioViewModel : ViewModelBase
{
    private readonly MdioService _mdio;

    // ── 포트 선택 ─────────────────────────────────────────────────────────
    public IReadOnlyList<string> PortOptions { get; } = new[]
    {
        "Port 0  (MDIO0)", "Port 1  (MDIO1)", "Port 2  (MDIO2)",
        "Port 3  (MDIO3)", "Port 4  (MDIO4)", "Port 5  (MDIO5)"
    };

    private int _selectedPortIndex;
    public int SelectedPortIndex
    {
        get => _selectedPortIndex;
        set { SetProperty(ref _selectedPortIndex, value); SyncPhyAddr(); }
    }

    // ── MDIO_SETUP ────────────────────────────────────────────────────────
    private bool _mdioEnable;
    private bool _preDisable;
    private bool _intrEnable;
    private string _setupStatus = "";

    public bool   MdioEnable  { get => _mdioEnable;  set => SetProperty(ref _mdioEnable,  value); }
    public bool   PreDisable  { get => _preDisable;  set => SetProperty(ref _preDisable,  value); }
    public bool   IntrEnable  { get => _intrEnable;  set => SetProperty(ref _intrEnable,  value); }
    public string SetupStatus { get => _setupStatus; set => SetProperty(ref _setupStatus, value); }

    // ── MDIO_TIME ─────────────────────────────────────────────────────────
    private string _targetMdcMhz   = "2.5";
    private string _clkDivide      = "20";
    private string _millisecDivide = "2500";
    private string _unitDivide     = "100";
    private string _timeStatus     = "";

    public string TargetMdcMhz   { get => _targetMdcMhz;   set => SetProperty(ref _targetMdcMhz,   value); }
    public string ClkDivide      { get => _clkDivide;      set => SetProperty(ref _clkDivide,      value); }
    public string MillisecDivide { get => _millisecDivide; set => SetProperty(ref _millisecDivide, value); }
    public string UnitDivide     { get => _unitDivide;     set => SetProperty(ref _unitDivide,     value); }
    public string TimeStatus     { get => _timeStatus;     set => SetProperty(ref _timeStatus,     value); }

    // ── 수동 PHY 접근 ─────────────────────────────────────────────────────
    private string _phyAddr   = "0x00";
    private string _regAddr   = "0x01";
    private string _accData   = "0x0000";
    private string _accStatus = "";

    public string PhyAddr   { get => _phyAddr;   set => SetProperty(ref _phyAddr,   value); }
    public string RegAddr   { get => _regAddr;   set => SetProperty(ref _regAddr,   value); }
    public string AccData   { get => _accData;   set => SetProperty(ref _accData,   value); }
    public string AccStatus { get => _accStatus; set => SetProperty(ref _accStatus, value); }

    // ── 링크 상태 ─────────────────────────────────────────────────────────
    public ObservableCollection<MdioPortStatus> PortStatuses { get; } = new(new[]
    {
        new MdioPortStatus { Label = "Port 0" },
        new MdioPortStatus { Label = "Port 1" },
        new MdioPortStatus { Label = "Port 2" },
        new MdioPortStatus { Label = "Port 3" },
        new MdioPortStatus { Label = "Port 4" },
        new MdioPortStatus { Label = "Port 5" },
    });

    private string _linkStatus = "";
    public string LinkStatus { get => _linkStatus; set => SetProperty(ref _linkStatus, value); }

    // ── Busy ─────────────────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { SetProperty(ref _isBusy, value); CommandManager.InvalidateRequerySuggested(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────
    public ICommand CalcMdcCommand           { get; }
    public ICommand ApplyCommand             { get; }
    public ICommand ReadPhyRegCommand        { get; }
    public ICommand WritePhyRegCommand       { get; }
    public ICommand ReadAllLinkStatusCommand { get; }

    public MdioViewModel(MdioService mdio)
    {
        _mdio = mdio;

        CalcMdcCommand           = new RelayCommand(CalcMdc);
        ApplyCommand             = new RelayCommand(async () => await ApplyAsync(),             () => !IsBusy);
        ReadPhyRegCommand        = new RelayCommand(async () => await ReadPhyRegAsync(),        () => !IsBusy);
        WritePhyRegCommand       = new RelayCommand(async () => await WritePhyRegAsync(),       () => !IsBusy);
        ReadAllLinkStatusCommand = new RelayCommand(async () => await ReadAllLinkStatusAsync(), () => !IsBusy);
    }

    private void SyncPhyAddr()
    {
        if (_selectedPortIndex >= 0 && _selectedPortIndex < MdioService.PhyAddrs.Length)
            PhyAddr = $"0x{MdioService.PhyAddrs[_selectedPortIndex]:X2}";
    }

    // ── 목표 MDC 주파수 → CLK_DIVIDE / MILLISEC_DIVIDE 자동 계산 ─────────
    private void CalcMdc()
    {
        if (!double.TryParse(TargetMdcMhz,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double mhz) || mhz <= 0)
        {
            TimeStatus = "주파수 형식 오류 (예: 2.5)";
            return;
        }

        const double ahbMhz = 100.0;
        uint clk = Math.Clamp((uint)Math.Round(ahbMhz / (2.0 * mhz)), 1, 255);
        uint ms  = Math.Clamp((uint)Math.Round(mhz * 1000.0), 1, 4095);

        ClkDivide      = clk.ToString();
        MillisecDivide = ms.ToString();

        double actual = ahbMhz / (2.0 * clk);
        TimeStatus = $"f_MDC ≈ {actual:F3} MHz  (CLK={clk}, MILLISEC={ms})";
    }

    // ── SETUP + TIME 통합 적용 ───────────────────────────────────────────
    private async Task ApplyAsync()
    {
        IsBusy = true;
        try
        {
            // 1. MDIO_ENABLE=0 (비활성화)
            await _mdio.WriteSetupAsync(_selectedPortIndex, BuildSetupValue(false));
            await Task.Delay(10);

            // 2. MDIO_TIME 설정
            uint clk  = uint.TryParse(ClkDivide,      out var c) ? c :    4u;
            uint ms   = uint.TryParse(MillisecDivide, out var m) ? m : 2500u;
            uint unit = uint.TryParse(UnitDivide,     out var u) ? u :  100u;
            uint timeReg = (unit << 20) | (ms << 8) | clk;
            await _mdio.WriteTimeAsync(_selectedPortIndex, timeReg);

            // 3. MDIO_SETUP 최종 적용 (ENABLE 포함)
            uint setupReg = BuildSetupValue(MdioEnable);
            await _mdio.WriteSetupAsync(_selectedPortIndex, setupReg);

            SetupStatus = $"SETUP=0x{setupReg:X8}  TIME=0x{timeReg:X8}";
            TimeStatus  = string.Empty;
        }
        catch (Exception ex) { SetupStatus = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private uint BuildSetupValue(bool enable)
    {
        uint v = 0x0060_0000u; // TA=b10, SOF=b01 기본값
        if (enable)     v |= 0x0001_0000u;
        if (PreDisable) v |= 0x0100_0000u;
        if (IntrEnable) v |= 0x8000_0000u;
        return v;
    }

    // ── 수동 PHY 접근 ─────────────────────────────────────────────────────
    private async Task ReadPhyRegAsync()
    {
        IsBusy = true;
        try
        {
            if (!TryParseHex(PhyAddr, out uint phy) || !TryParseHex(RegAddr, out uint reg))
            { AccStatus = "주소 형식 오류 (hex)"; return; }

            var result = await _mdio.ReadPhyRegAsync(_selectedPortIndex, phy, reg);
            if (result == null)
            { AccData = "TIMEOUT"; AccStatus = "MDIO 타임아웃"; }
            else
            { AccData = $"0x{result.Value:X4}"; AccStatus = $"PHY[0x{phy:X2}] Reg[0x{reg:X2}] = 0x{result.Value:X4}"; }
        }
        catch (Exception ex) { AccStatus = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task WritePhyRegAsync()
    {
        IsBusy = true;
        try
        {
            if (!TryParseHex(PhyAddr, out uint phy) || !TryParseHex(RegAddr, out uint reg)
                || !TryParseHex(AccData, out uint data))
            { AccStatus = "주소/데이터 형식 오류 (hex)"; return; }

            await _mdio.WritePhyRegAsync(_selectedPortIndex, phy, reg, (ushort)(data & 0xFFFF));
            AccStatus = $"PHY[0x{phy:X2}] Reg[0x{reg:X2}] ← 0x{data:X4}  완료";
        }
        catch (Exception ex) { AccStatus = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    // ── 전체 포트 링크 상태 읽기 ──────────────────────────────────────────
    private async Task ReadAllLinkStatusAsync()
    {
        IsBusy = true;
        LinkStatus = "읽는 중...";
        try
        {
            for (int port = 0; port < 6; port++)
            {
                var up = await _mdio.ReadLinkStatusAsync(port);
                PortStatuses[port].LinkUp = up;
            }
            LinkStatus = $"갱신 완료  {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { LinkStatus = $"오류: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private static bool TryParseHex(string text, out uint result)
    {
        var clean = text.Replace("0x", "").Replace("0X", "").Replace("_", "").Trim();
        return uint.TryParse(clean, NumberStyles.HexNumber, null, out result);
    }
}
