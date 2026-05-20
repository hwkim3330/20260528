using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public class TestCaseGroup : INotifyPropertyChanged
{
    private string _name       = "Group";
    private bool   _isExpanded = true;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    /// <summary>CSV 파일에서 임포트된 경우 해당 파일 경로 (null이면 수동 생성)</summary>
    public string? CsvSourcePath { get; set; }

    /// <summary>이 그룹을 파싱했을 당시 CSV 파일의 LastWriteTimeUtc</summary>
    public DateTime? CsvLastModifiedUtc { get; set; }

    public ObservableCollection<TestCaseEntry> TestCases { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
