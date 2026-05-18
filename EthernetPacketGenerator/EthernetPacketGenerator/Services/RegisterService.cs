using System;
using System.Threading.Tasks;

namespace EthernetPacketGenerator.Services;

public class RegisterService
{
    private readonly SerialPortService _serial;

    // 변경 가능한 베이스 주소 — 기본값 0x44A0_0000
    public uint BaseAddress { get; set; } = 0x44A0_0000;

    public RegisterService(SerialPortService serial) => _serial = serial;

    public bool IsConnected => _serial.IsOpen;

    /// <summary>레지스터 읽기. 전송: "read 0x{BaseAddress+offset}"  응답: "OK 0x{value}"</summary>
    public async Task<uint> ReadAsync(uint offset, int timeoutMs = 2000)
    {
        var addr = BaseAddress + offset;
        var resp = await _serial.SendCommandAsync($"read 0x{addr:X}", timeoutMs);
        if (!resp.StartsWith("OK "))
            throw new InvalidOperationException($"읽기 실패: {resp}");

        return Convert.ToUInt32(resp[3..].Trim(), 16);
    }

    /// <summary>레지스터 쓰기. 전송: "write 0x{BaseAddress+offset} 0x{value}"  응답: "OK"</summary>
    public async Task WriteAsync(uint offset, uint value, int timeoutMs = 2000)
    {
        var addr = BaseAddress + offset;
        var resp = await _serial.SendCommandAsync(
            $"write 0x{addr:X} 0x{value:X8}", timeoutMs);
        if (!resp.StartsWith("OK"))
            throw new InvalidOperationException($"쓰기 실패: {resp}");
    }
}
