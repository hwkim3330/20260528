using SharpPcap;

namespace EthernetPacketGenerator.Services;

public static class NetworkInterfaceService
{
    public static (IReadOnlyList<ILiveDevice> Devices, string? Error) GetInterfaces()
    {
        try
        {
            var list = CaptureDeviceList.Instance.OfType<ILiveDevice>().ToList();
            return (list, list.Count == 0 ? "No interfaces found. Is Npcap installed?" : null);
        }
        catch (Exception ex)
        {
            return (Array.Empty<ILiveDevice>(), $"Failed to load interfaces: {ex.Message}");
        }
    }
}
