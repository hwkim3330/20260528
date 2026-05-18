namespace EthernetPacketGenerator.Models;

/// <summary>FDB Read 결과 단일 엔트리</summary>
public class FdbEntry
{
    public int    Bucket     { get; init; }
    public int    SlotBitmap { get; init; }
    public string Mac        { get; init; } = string.Empty;
    public int    VlanId     { get; init; }
    public bool   VlanValid  { get; init; }
    public int    Port       { get; init; }
    public bool   IsStatic   { get; init; }
    public int    Timestamp  { get; init; }

    public string VlanDisplay   => VlanValid ? VlanId.ToString() : "-";
    public string StaticDisplay => IsStatic  ? "Static" : "Dynamic";
}
