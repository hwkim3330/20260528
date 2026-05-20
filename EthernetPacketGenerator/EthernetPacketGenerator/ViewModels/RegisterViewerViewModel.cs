using EthernetPacketGenerator.Services;
using EthernetPacketGenerator.ViewModels.RegisterViewer;

namespace EthernetPacketGenerator.ViewModels;

public class RegisterViewerViewModel : ViewModelBase
{
    private readonly RegisterService _reg;
    private readonly FdbService _fdbService;

    public SysControlViewModel  SysCtrl      { get; }
    public InterruptViewModel   Interrupt    { get; }
    public TimestampViewModel   Timestamp    { get; }
    public LedClockViewModel    LedClock     { get; }
    public TestDataViewModel    TestData     { get; }
    public FdbViewModel         Fdb          { get; }
    public CountViewerViewModel CountViewer  { get; }
    public MdioViewModel        Mdio         { get; }

    // ── BaseAddress (UI 바인딩용) ─────────────────────────────────────────
    public string BaseAddressHex
    {
        get => $"0x{_reg.BaseAddress:X8}";
        set
        {
            try
            {
                var hex = value.Replace("0x", "").Replace("0X", "").Trim();
                _reg.BaseAddress = Convert.ToUInt32(hex, 16);
                OnPropertyChanged();
                BaseAddressError = string.Empty;
            }
            catch
            {
                BaseAddressError = "잘못된 주소";
                OnPropertyChanged(nameof(BaseAddressError));
            }
        }
    }

    private string _baseAddressError = string.Empty;
    public string BaseAddressError
    {
        get => _baseAddressError;
        private set => SetProperty(ref _baseAddressError, value);
    }

    public RegisterViewerViewModel(SerialPortService serial)
    {
        _reg        = new RegisterService(serial);
        SysCtrl     = new SysControlViewModel(_reg);
        Interrupt   = new InterruptViewModel(_reg);
        Timestamp   = new TimestampViewModel(_reg);
        LedClock    = new LedClockViewModel(_reg);
        TestData    = new TestDataViewModel(_reg);
        _fdbService = new FdbService(_reg);
        Fdb         = new FdbViewModel(_fdbService);
        CountViewer = new CountViewerViewModel(serial);
        Mdio        = new MdioViewModel(new MdioService(_reg));
    }

    public async Task<uint> ReadRegisterForApiAsync(uint offset) => await _reg.ReadAsync(offset);

    public async Task WriteRegisterForApiAsync(uint offset, uint value) => await _reg.WriteAsync(offset, value);

    public async Task<object?> FdbReadByMacForApiAsync(string mac, bool vlanValid, int vlanId) =>
        await _fdbService.ReadEntryByMacAsync(mac, vlanValid, vlanId);

    public async Task FdbWriteByMacForApiAsync(string mac, bool vlanValid, int vlanId, int port) =>
        await _fdbService.WriteEntryByHashAsync(mac, vlanValid, vlanId, port);

    public async Task FdbDeleteByMacForApiAsync(string mac, bool vlanValid, int vlanId) =>
        await _fdbService.DeleteEntryByMacAsync(mac, vlanValid, vlanId);

    public async Task FdbFlushForApiAsync() => await _fdbService.FlushAllAsync();
}
