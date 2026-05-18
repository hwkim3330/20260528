using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using EthernetPacketGenerator.Commands;
using EthernetPacketGenerator.Services;

namespace EthernetPacketGenerator.ViewModels.RegisterViewer;

public class TestRegItem : ViewModelBase
{
    private readonly RegisterService _reg;
    private readonly uint _offset;

    private string _valueHex = "0x00000000";
    private string _status   = string.Empty;

    public string Name     { get; }
    public string ValueHex { get => _valueHex; set => SetProperty(ref _valueHex, value); }
    public string Status   { get => _status;   set => SetProperty(ref _status,   value); }

    public ICommand ReadCommand  { get; }
    public ICommand WriteCommand { get; }

    public TestRegItem(RegisterService reg, string name, uint offset)
    {
        _reg    = reg;
        Name    = name;
        _offset = offset;

        ReadCommand  = new RelayCommand(async () => await ReadAsync());
        WriteCommand = new RelayCommand(async () => await WriteAsync());
    }

    private async Task ReadAsync()
    {
        try
        {
            var v = await _reg.ReadAsync(_offset);
            ValueHex = $"0x{v:X8}";
            Status   = string.Empty;
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }

    private async Task WriteAsync()
    {
        try
        {
            var v = Convert.ToUInt32(ValueHex.Replace("0x", "").Replace("0X", ""), 16);
            await _reg.WriteAsync(_offset, v);
            Status = "완료";
        }
        catch (Exception ex) { Status = $"오류: {ex.Message}"; }
    }
}

public class TestDataViewModel : ViewModelBase
{
    private readonly RegisterService _reg;

    public ObservableCollection<TestRegItem> Items { get; } = new();

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    public ICommand ReadAllCommand  { get; }
    public ICommand WriteAllCommand { get; }

    public TestDataViewModel(RegisterService reg)
    {
        _reg = reg;
        for (int i = 0; i < 8; i++)
            Items.Add(new TestRegItem(reg, $"TEST_DATA_{i}", (uint)(0x040 + i * 4)));

        ReadAllCommand  = new RelayCommand(async () => await ReadAllAsync());
        WriteAllCommand = new RelayCommand(async () => await WriteAllAsync());
    }

    private async Task ReadAllAsync()
    {
        foreach (var item in Items)
            await item.ReadCommand.ExecuteAsync();
        Status = "전체 읽기 완료";
    }

    private async Task WriteAllAsync()
    {
        foreach (var item in Items)
            await item.WriteCommand.ExecuteAsync();
        Status = "전체 쓰기 완료";
    }
}

// ICommand 확장 — async 실행 헬퍼
file static class CommandExtensions
{
    public static Task ExecuteAsync(this ICommand cmd)
    {
        cmd.Execute(null);
        return Task.CompletedTask;
    }
}
