using System.ComponentModel;
using System.Runtime.CompilerServices;
using SharpPcap;

namespace EthernetPacketGenerator.Models;

/// <summary>
/// SendViewModel의 인터페이스 목록 아이템.
/// IsActive 체크박스로 활성화 여부를, IsDefault 라디오버튼으로 기본 인터페이스를 선택한다.
/// </summary>
public class InterfaceEntry : INotifyPropertyChanged
{
    private bool _isActive = false;
    private bool _isDefault;

    public ILiveDevice? Device { get; }
    public string ShortName { get; }

    /// <summary>PacketList ComboBox에 "(Default)" 항목으로 표시하기 위한 sentinel 플래그.</summary>
    public bool IsDefaultSentinel { get; init; }

    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    public bool IsDefault
    {
        get => _isDefault;
        set { _isDefault = value; OnPropertyChanged(); }
    }

    public InterfaceEntry(ILiveDevice device, string shortName)
    {
        Device    = device;
        ShortName = shortName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
