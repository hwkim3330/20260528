using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace EthernetPacketGenerator.Models;

public class TestCaseEntry : INotifyPropertyChanged
{
    private string _name       = "TC";
    private bool   _isSelected = false;
    private bool   _isChecked  = false;
    private bool   _isRunning  = false;
    private bool   _isDone     = false;
    private bool   _isFailed   = false;

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    public bool IsChecked
    {
        get => _isChecked;
        set { _isChecked = value; OnPropertyChanged(); }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set { _isRunning = value; OnPropertyChanged(); }
    }

    public bool IsDone
    {
        get => _isDone;
        set { _isDone = value; OnPropertyChanged(); }
    }

    public bool IsFailed
    {
        get => _isFailed;
        set { _isFailed = value; OnPropertyChanged(); }
    }

    public int TestScenarioId { get; set; }
    public int TcId           { get; set; }

    /// <summary>마지막 실행 결과 캐시 (인덱스 순서, JSON 직렬화 제외)</summary>
    [JsonIgnore]
    public PacketTestResult[]? LastRunResults { get; set; }

    public List<SequenceItemDto> Items { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class SequenceItemDto
{
    [JsonPropertyName("kind")]       public string Kind       { get; set; } = "Packet";
    [JsonPropertyName("checked")]    public bool   IsChecked  { get; set; }

    // Packet
    [JsonPropertyName("packetName")] public string? PacketName { get; set; }
    [JsonPropertyName("blocks")]     public List<BlockDto>? Blocks { get; set; }

    // Event — common
    [JsonPropertyName("eventType")]  public string? EventType  { get; set; }
    [JsonPropertyName("eventLabel")] public string? EventLabel { get; set; }
    [JsonPropertyName("delayMs")]    public int     DelayMs    { get; set; }

    // Reg*
    [JsonPropertyName("address")]    public string  Address    { get; set; } = "0x00000000";
    [JsonPropertyName("value")]      public string  Value      { get; set; } = "0x00000000";
    [JsonPropertyName("mask")]       public string  Mask       { get; set; } = "0xFFFFFFFF";
    [JsonPropertyName("expected")]   public string  Expected   { get; set; } = "0x00000000";
    [JsonPropertyName("timeoutMs")]  public int     TimeoutMs  { get; set; } = 1000;

    // Fdb*
    [JsonPropertyName("macAddress")] public string MacAddress { get; set; } = "00:00:00:00:00:00";
    [JsonPropertyName("vlanValid")]  public bool   VlanValid  { get; set; }
    [JsonPropertyName("vlanId")]     public int    VlanId     { get; set; }
    [JsonPropertyName("port")]       public int    Port       { get; set; }
    [JsonPropertyName("bucket")]     public int    Bucket     { get; set; }
    [JsonPropertyName("slotBitmap")] public int    SlotBitmap { get; set; } = 1;

    // FdbReadBucket verify
    [JsonPropertyName("fdbExpectedMac")] public string FdbExpectedMac { get; set; } = string.Empty;

    // RxVerify
    [JsonPropertyName("expectedDstMac")] public string ExpectedDstMac { get; set; } = string.Empty;
    [JsonPropertyName("expectedPort")]   public int    ExpectedPort   { get; set; } = -1;

    public class BlockDto
    {
        [JsonPropertyName("type")]  public string Type  { get; set; } = "";
        [JsonPropertyName("bytes")] public string Bytes { get; set; } = "";
    }
}
