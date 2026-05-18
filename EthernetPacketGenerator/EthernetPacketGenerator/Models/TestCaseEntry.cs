using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace EthernetPacketGenerator.Models;

public class TestCaseEntry : INotifyPropertyChanged
{
    private string _name       = "TC";
    private bool   _isSelected = false;

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

    public List<SequenceItemDto> Items { get; set; } = new();

    [JsonPropertyName("scenarioSteps")]
    public List<TestScenarioStep> ScenarioSteps { get; set; } = new();

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

    // CaptureVerify
    [JsonPropertyName("captureInterface")] public string CaptureInterface { get; set; } = "";
    [JsonPropertyName("captureFilter")]    public string CaptureFilter    { get; set; } = "";
    [JsonPropertyName("captureExpected")]  public int    CaptureExpected  { get; set; } = 1;

    // SerialSend / SerialVerify
    [JsonPropertyName("serialText")] public string SerialText { get; set; } = "";
    [JsonPropertyName("serialHex")]  public string SerialHex  { get; set; } = "";

    public class BlockDto
    {
        [JsonPropertyName("type")]  public string Type  { get; set; } = "";
        [JsonPropertyName("bytes")] public string Bytes { get; set; } = "";
    }
}
