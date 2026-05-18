using System;
using System.Threading;
using System.Threading.Tasks;

namespace EthernetPacketGenerator.Services;

public class MdioService
{
    private readonly RegisterService _reg;

    // MDIO blocks: TGSW_BASE + 0x0080, stride 0x40 per port
    private const uint MDIO_BASE   = 0x0080;
    private const uint MDIO_STRIDE = 0x0040;

    private const uint OFF_SETUP     = 0x0000;
    private const uint OFF_TIME      = 0x0004;
    private const uint OFF_INTERRUPT = 0x0008;
    private const uint OFF_ACC_DATA  = 0x0010;

    // PHY addresses per port (Port 0~5)
    public static readonly uint[] PhyAddrs = { 0x00, 0x04, 0x05, 0x08, 0x0A, 0x0C };

    public MdioService(RegisterService reg) => _reg = reg;

    private uint BlockBase(int port) => MDIO_BASE + (uint)port * MDIO_STRIDE;

    // ── MDIO_SETUP ───────────────────────────────────────────────────────
    public Task<uint> ReadSetupAsync(int port)              => _reg.ReadAsync(BlockBase(port) + OFF_SETUP);
    public Task       WriteSetupAsync(int port, uint value) => _reg.WriteAsync(BlockBase(port) + OFF_SETUP, value);

    // ── MDIO_TIME ────────────────────────────────────────────────────────
    public Task<uint> ReadTimeAsync(int port)              => _reg.ReadAsync(BlockBase(port) + OFF_TIME);
    public Task       WriteTimeAsync(int port, uint value) => _reg.WriteAsync(BlockBase(port) + OFF_TIME, value);

    // ── PHY 레지스터 읽기 (ACC_DATA 3단계) ───────────────────────────────
    public async Task<ushort?> ReadPhyRegAsync(
        int port, uint phyAddr, uint regAddr,
        CancellationToken ct = default)
    {
        uint acc = BlockBase(port) + OFF_ACC_DATA;

        // bit31=ACCESS_EN, OP_TYPE=0(read), PHY_ADDR[25:21], REG_ADDR[20:16]
        uint cmd = 0x8000_0000u
                 | ((phyAddr & 0x1Fu) << 21)
                 | ((regAddr & 0x1Fu) << 16);

        await _reg.WriteAsync(acc, cmd);

        var deadline = DateTime.Now.AddMilliseconds(1000);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var v = await _reg.ReadAsync(acc);
            if ((v & 0x8000_0000u) == 0)
                return (ushort)(v & 0xFFFF);
            await Task.Delay(10, ct);
        }
        return null; // timeout
    }

    // ── PHY 레지스터 쓰기 ─────────────────────────────────────────────────
    public async Task WritePhyRegAsync(
        int port, uint phyAddr, uint regAddr, ushort data,
        CancellationToken ct = default)
    {
        uint acc = BlockBase(port) + OFF_ACC_DATA;

        uint cmd = 0x8000_0000u
                 | (1u << 26)                  // OP_TYPE=1(write)
                 | ((phyAddr & 0x1Fu) << 21)
                 | ((regAddr & 0x1Fu) << 16)
                 | data;

        await _reg.WriteAsync(acc, cmd);

        var deadline = DateTime.Now.AddMilliseconds(1000);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var v = await _reg.ReadAsync(acc);
            if ((v & 0x8000_0000u) == 0) return;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException("MDIO write timeout");
    }

    // ── 링크 상태 (BMSR bit2) ─────────────────────────────────────────────
    public async Task<bool?> ReadLinkStatusAsync(int port, CancellationToken ct = default)
    {
        var bmsr = await ReadPhyRegAsync(port, PhyAddrs[port], 0x01, ct);
        if (bmsr == null) return null;
        return (bmsr.Value & 0x0004) != 0;
    }
}
