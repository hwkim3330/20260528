using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EthernetPacketGenerator.Models;

public class TestScenarioStep : INotifyPropertyChanged
{
    private int _tcId;
    private int _scenarioId;
    private int _index;
    private string _name = "";
    private string _action = "RegWrite";
    private string _address = "";
    private string _value = "";
    private string _mask = "";
    private string _expected = "";
    private string _timeout = "";
    private string _observed = "";
    private string _result = "";
    private string _note = "";

    public int TC_ID { get => _tcId; set { _tcId = value; OnPropertyChanged(); } }
    public int Test_Scenario_ID { get => _scenarioId; set { _scenarioId = value; OnPropertyChanged(); } }
    public int Index { get => _index; set { _index = value; OnPropertyChanged(); } }
    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Action { get => _action; set { _action = value; OnPropertyChanged(); } }
    public string Address { get => _address; set { _address = value; OnPropertyChanged(); } }
    public string Value { get => _value; set { _value = value; OnPropertyChanged(); } }
    public string Mask { get => _mask; set { _mask = value; OnPropertyChanged(); } }
    public string Expected { get => _expected; set { _expected = value; OnPropertyChanged(); } }
    public string Timeout { get => _timeout; set { _timeout = value; OnPropertyChanged(); } }
    public string Observed { get => _observed; set { _observed = value; OnPropertyChanged(); } }
    public string Result { get => _result; set { _result = value; OnPropertyChanged(); } }
    public string Note { get => _note; set { _note = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
