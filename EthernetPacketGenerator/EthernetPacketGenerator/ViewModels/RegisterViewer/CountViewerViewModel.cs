using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

/// <summary>포트 선택 옵션 — Label은 표시 이름, Command는 전송할 UART 커맨드</summary>
public record CountPortOption(string Label, string Command)
{
    public override string ToString() => Label;
}

public class CountItem
{
    public string Group    { get; init; } = "";
    public string Name     { get; init; } = "";
    public string Address  { get; init; } = "";
    public string ValueHex { get; init; } = "";
    public uint   ValueDec { get; init; }
}

public class CountViewerViewModel : ViewModelBase
{
    // 응답 형식: REGISTER_NAME [A: 0xADDR, D: 0xVALUE]
    private static readonly Regex LineRegex =
        new(@"^(\w+)\s+\[A:\s*(0x[\dA-Fa-f]+)\s*,\s*D:\s*(0x[\dA-Fa-f]+)\]",
            RegexOptions.Compiled);

    private readonly SerialPortService _serial;

    public ObservableCollection<CountItem> Items { get; } = new();

    public IReadOnlyList<CountPortOption> PortOptions { get; } = new List<CountPortOption>
    {
        new("ALL",    "read_cnt"),
        new("Port 0", "read_cnt 0"),
        new("Port 1", "read_cnt 1"),
        new("Port 2", "read_cnt 2"),
        new("Port 3", "read_cnt 3"),
        new("Port 4", "read_cnt 4"),
        new("Port 5", "read_cnt 5"),
    };

    private CountPortOption _selectedPort;
    public CountPortOption SelectedPort
    {
        get => _selectedPort;
        set => SetProperty(ref _selectedPort, value);
    }

    private string _status = "";
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            SetProperty(ref _isBusy, value);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ICommand ReadCountCommand { get; }
    public ICommand ClearCommand     { get; }

    public CountViewerViewModel(SerialPortService serial)
    {
        _serial       = serial;
        _selectedPort = PortOptions[0];

        ReadCountCommand = new RelayCommand(
            async () => await ReadCountAsync(),
            () => _serial.IsOpen && !IsBusy);

        ClearCommand = new RelayCommand(() =>
        {
            Items.Clear();
            Status = "";
        });
    }

    private async Task ReadCountAsync()
    {
        IsBusy = true;
        Status = "읽는 중...";
        try
        {
            var lines = await _serial.SendCommandMultiLineAsync(
                SelectedPort.Command, timeoutMs: 8000);

            Items.Clear();
            int parsed = 0;
            foreach (var line in lines)
            {
                var m = LineRegex.Match(line);
                if (!m.Success) continue;

                var name   = m.Groups[1].Value;
                var addr   = m.Groups[2].Value;
                var valHex = m.Groups[3].Value;
                uint valDec;
                try { valDec = Convert.ToUInt32(valHex.Replace("0x", "").Replace("0X", ""), 16); }
                catch { valDec = 0; }

                // 그룹: RX0, RX1... TX0... FBR_로 시작하는 것은 FBR
                var underIdx = name.IndexOf('_');
                var group = underIdx > 0 ? name[..underIdx] : name;
                if (group.StartsWith("FBR", StringComparison.OrdinalIgnoreCase)) group = "FBR";

                Items.Add(new CountItem
                {
                    Group    = group,
                    Name     = name,
                    Address  = addr,
                    ValueHex = valHex,
                    ValueDec = valDec,
                });
                parsed++;
            }

            Status = parsed > 0
                ? $"{parsed}개 항목  (포트: {SelectedPort.Label})"
                : "데이터 없음 — 응답 형식을 확인하세요";
        }
        catch (Exception ex)
        {
            Status = $"오류: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
