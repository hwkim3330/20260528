using SharpPcap;

namespace EthernetPacketGenerator.Services;

public class PacketSendService : IDisposable
{
    private ILiveDevice? _device;
    private System.Timers.Timer? _repeatTimer;
    private bool _disposed;

    public event EventHandler<string>? SendError;
    public event EventHandler<int>? PacketSent;

    public void OpenDevice(ILiveDevice device)
    {
        _device?.Close();
        _device = device;
        _device.Open(DeviceModes.None);
    }

    public void SendOnce(byte[] packet) => SendOnce(packet, null);

    /// <param name="device">null이면 OpenDevice로 열어둔 기본 디바이스 사용</param>
    public void SendOnce(byte[] packet, ILiveDevice? device)
    {
        var target = device ?? _device;
        if (target == null)
        {
            SendError?.Invoke(this, "No network interface selected.");
            return;
        }

        try
        {
            if (device != null && device != _device)
            {
                // 별도 인터페이스는 이미 열려 있어야 함 (OpenExtra로 열림)
                device.SendPacket(packet);
            }
            else
            {
                target.SendPacket(packet);
            }
            PacketSent?.Invoke(this, packet.Length);
        }
        catch (Exception ex)
        {
            SendError?.Invoke(this, ex.Message);
        }
    }

    /// <summary>기본 디바이스 외 추가 인터페이스를 오픈한다.</summary>
    public void OpenExtra(ILiveDevice device)
    {
        try { device.Open(DeviceModes.None); }
        catch { }
    }

    /// <summary>기본 디바이스 외 추가 인터페이스를 닫는다.</summary>
    public void CloseExtra(ILiveDevice device)
    {
        try { device.Close(); } catch { }
    }

    public void StartRepeat(byte[] packet, int intervalMs, int count = 0, Action? onDone = null)
    {
        StopRepeat();
        var snapshot = (byte[])packet.Clone();
        int sent = 0;

        _repeatTimer = new System.Timers.Timer(intervalMs);
        _repeatTimer.Elapsed += (_, _) =>
        {
            SendOnce(snapshot);
            sent++;
            if (count > 0 && sent >= count)
            {
                StopRepeat();
                onDone?.Invoke();
            }
        };
        _repeatTimer.AutoReset = true;
        _repeatTimer.Start();
    }

    public void StopRepeat()
    {
        _repeatTimer?.Stop();
        _repeatTimer?.Dispose();
        _repeatTimer = null;
    }

    public bool IsSending => _repeatTimer != null && _repeatTimer.Enabled;

    public void Dispose()
    {
        if (_disposed) return;
        StopRepeat();
        _device?.Close();
        _disposed = true;
    }
}
