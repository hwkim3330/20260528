using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EthernetPacketGenerator.Models;

namespace EthernetPacketGenerator.Services;

public class FdbService
{
    private readonly RegisterService _reg;

    // ── FDB 레지스터 오프셋 (TGSW 베이스 0x44A0_0000 기준) ──────────────────
    private const uint FDB_BASE       = 0xA00;
    private const uint OFF_VERSION    = FDB_BASE + 0x00;   // 0xA00  버전
    private const uint OFF_FDB_LOAD   = FDB_BASE + 0x04;   // 0xA04  Default Load (write 1)
    private const uint OFF_ENABLE     = FDB_BASE + 0x0C;   // 0xA0C  [4]=AgeScan [1]=Learning [0]=Lookup
    private const uint OFF_AGE_PERIOD = FDB_BASE + 0x10;   // 0xA10  Age Period (ns)
    private const uint OFF_AGING_THR  = FDB_BASE + 0x14;   // 0xA14  Aging Threshold (×100ms)
    private const uint OFF_MCU_MAC0   = FDB_BASE + 0x18;   // 0xA18  MAC[31:0]
    private const uint OFF_MCU_MAC1   = FDB_BASE + 0x1C;   // 0xA1C  MAC[47:32]
    private const uint OFF_MCU_VLAN   = FDB_BASE + 0x20;   // 0xA20  [12]=Valid [11:0]=VLAN
    private const uint OFF_MCU_PORT   = FDB_BASE + 0x24;   // 0xA24  [8:0]=Port
    private const uint OFF_MCU_BUCKET = FDB_BASE + 0x28;   // 0xA28  [9:0]=Bucket [19:16]=SlotBitmap
    private const uint OFF_MCU_CMD    = FDB_BASE + 0x2C;   // 0xA2C  [7:4]=Table [3:0]=Cmd
    private const uint OFF_FDB_STATUS = FDB_BASE + 0x40;   // 0xA40  [0]=done_mac_table_init
    private const uint OFF_CMD_STATUS = FDB_BASE + 0x44;   // 0xA44  [2]=WR [1]=RD-Flood [0]=RD-MAC
    private const uint OFF_RD_BUCKET  = FDB_BASE + 0x48;   // 0xA48  [9:0]=Bucket [15:12]=Slot
    private const uint OFF_RD_PORT    = FDB_BASE + 0x4C;   // 0xA4C  [8:0]=Port
    private const uint OFF_RD_FLAGS   = FDB_BASE + 0x50;   // 0xA50  [15]=Valid [14]=Static [13:0]=Timestamp
    private const uint OFF_RD_MAC0    = FDB_BASE + 0x54;   // 0xA54  [15:0]=MAC[15:0]
    private const uint OFF_RD_MAC1    = FDB_BASE + 0x58;   // 0xA58  [15:0]=MAC[31:16]
    private const uint OFF_RD_MAC2    = FDB_BASE + 0x5C;   // 0xA5C  [15:0]=MAC[47:32]

    private const uint CMD_HASH_READ    = 0x12;   // MCU_MAC-Hash: MAC 주소로 해시 조회
    private const uint CMD_READ_BUCKET  = 0x13;
    private const uint CMD_HASH_WRITE   = 0x14;   // MCU_MAC-Hash: 해시로 버킷/슬롯 자동 배정
    private const uint CMD_WRITE_BUCKET = 0x15;
    private const uint CMD_HASH_DELETE  = 0x16;   // MCU_MAC-Hash: MAC 주소로 해시 삭제
    private const uint CMD_FLUSH_ALL    = 0x70;   // 전체 MAC 테이블 초기화

    private const uint STATUS_RD_MAC       = 0x1;   // 0xA44 [0] RD-MAC-Table Result Valid
    private const uint STATUS_WR_MAC       = 0x4;   // 0xA44 [2] WR-MAC-Table Command Result Valid
    private const uint STATUS_INIT_DONE    = 0x1;   // 0xA40 [0] done_mac_table_init

    public FdbService(RegisterService reg) => _reg = reg;

    // ── 컨트롤 레지스터 R/W ──────────────────────────────────────────────────
    public Task<uint> ReadVersionAsync()              => _reg.ReadAsync(OFF_VERSION);
    public Task<uint> ReadEnableAsync()               => _reg.ReadAsync(OFF_ENABLE);
    public Task       WriteEnableAsync(uint value)    => _reg.WriteAsync(OFF_ENABLE, value);
    public Task<uint> ReadAgePeriodAsync()            => _reg.ReadAsync(OFF_AGE_PERIOD);
    public Task<uint> ReadAgingThrAsync()             => _reg.ReadAsync(OFF_AGING_THR);
    public Task       FdbDefaultLoadAsync()           => _reg.WriteAsync(OFF_FDB_LOAD, 1);

    // ── FDB 엔트리 쓰기 (Hash Write, CMD=0x14) — 버킷/슬롯 하드웨어 자동 배정 ──
    public async Task WriteEntryByHashAsync(
        string mac, bool vlanValid, int vlanId, int port,
        CancellationToken ct = default)
    {
        var bytes    = ParseMac(mac);
        uint mac0    = (uint)(bytes[2] << 24 | bytes[3] << 16 | bytes[4] << 8 | bytes[5]);
        uint mac1    = (uint)(bytes[0] << 8  | bytes[1]);
        uint vlanReg = (vlanValid ? 0x1000u : 0u) | ((uint)(vlanId & 0xFFF));
        uint portReg = (uint)(port & 0x1FF);

        await _reg.WriteAsync(OFF_MCU_MAC0, mac0);           ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_MAC1, mac1);           ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_VLAN, vlanReg);        ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_PORT, portReg);        ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_CMD,  CMD_HASH_WRITE); ct.ThrowIfCancellationRequested();

        await PollStatusAsync(STATUS_WR_MAC, ct);
    }

    // ── FDB 엔트리 쓰기 (Bucket Write, CMD=0x15) — 버킷/슬롯 직접 지정 ─────────
    public async Task WriteEntryAsync(
        string mac, bool vlanValid, int vlanId,
        int port, int bucket, int slotBitmap,
        CancellationToken ct = default)
    {
        var bytes      = ParseMac(mac);
        uint mac0      = (uint)(bytes[2] << 24 | bytes[3] << 16 | bytes[4] << 8 | bytes[5]);
        uint mac1      = (uint)(bytes[0] << 8  | bytes[1]);
        uint vlanReg   = (vlanValid ? 0x1000u : 0u) | ((uint)(vlanId & 0xFFF));
        uint portReg   = (uint)(port & 0x1FF);
        uint bucketReg = ((uint)(slotBitmap & 0xF) << 16) | ((uint)(bucket & 0x3FF));

        await _reg.WriteAsync(OFF_MCU_MAC0,   mac0);             ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_MAC1,   mac1);             ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_VLAN,   vlanReg);          ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_PORT,   portReg);          ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_BUCKET, bucketReg);        ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_CMD,    CMD_WRITE_BUCKET); ct.ThrowIfCancellationRequested();

        await PollStatusAsync(STATUS_WR_MAC, ct);
    }

    // ── FDB 엔트리 읽기 (Hash Read, CMD=0x12) — MAC 주소로 해시 조회 ────────────
    public async Task<FdbEntry?> ReadEntryByMacAsync(
        string mac, bool vlanValid, int vlanId,
        CancellationToken ct = default)
    {
        var bytes    = ParseMac(mac);
        uint mac0    = (uint)(bytes[2] << 24 | bytes[3] << 16 | bytes[4] << 8 | bytes[5]);
        uint mac1    = (uint)(bytes[0] << 8  | bytes[1]);
        uint vlanReg = (vlanValid ? 0x1000u : 0u) | ((uint)(vlanId & 0xFFF));

        await _reg.WriteAsync(OFF_MCU_MAC0, mac0);           ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_MAC1, mac1);           ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_VLAN, vlanReg);        ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_CMD,  CMD_HASH_READ);  ct.ThrowIfCancellationRequested();

        await PollStatusAsync(STATUS_RD_MAC, ct);

        var flags    = await _reg.ReadAsync(OFF_RD_FLAGS);
        if ((flags & 0x8000) == 0) return null;   // Valid 비트 없음 → 미학습

        var rdPort   = await _reg.ReadAsync(OFF_RD_PORT);   ct.ThrowIfCancellationRequested();
        var rdBucket = await _reg.ReadAsync(OFF_RD_BUCKET); ct.ThrowIfCancellationRequested();

        return new FdbEntry
        {
            Mac        = mac,
            Port       = (int)(rdPort   & 0x1FF),
            Bucket     = (int)(rdBucket & 0x3FF),
            SlotBitmap = (int)((rdBucket >> 12) & 0xF),
            Timestamp  = (int)(flags & 0x3FFF),
            IsStatic   = (flags & 0x4000) != 0
        };
    }

    // ── FDB 엔트리 읽기 (Bucket Read, CMD=0x13) ──────────────────────────────
    public async Task<FdbEntry?> ReadEntryAsync(
        int bucket, int slotBitmap,
        CancellationToken ct = default)
    {
        uint bucketReg = ((uint)(slotBitmap & 0xF) << 16) | ((uint)(bucket & 0x3FF));

        await _reg.WriteAsync(OFF_MCU_BUCKET, bucketReg);       ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_CMD,    CMD_READ_BUCKET); ct.ThrowIfCancellationRequested();

        await PollStatusAsync(STATUS_RD_MAC, ct);

        var flags  = await _reg.ReadAsync(OFF_RD_FLAGS);
        if ((flags & 0x8000) == 0) return null;   // Valid 비트 없음 → 빈 슬롯

        var mac0   = await _reg.ReadAsync(OFF_RD_MAC0); ct.ThrowIfCancellationRequested();
        var mac1   = await _reg.ReadAsync(OFF_RD_MAC1); ct.ThrowIfCancellationRequested();
        var mac2   = await _reg.ReadAsync(OFF_RD_MAC2); ct.ThrowIfCancellationRequested();
        var rdPort = await _reg.ReadAsync(OFF_RD_PORT);

        return new FdbEntry
        {
            Bucket     = bucket,
            SlotBitmap = slotBitmap,
            Mac        = FormatMac(mac2 & 0xFFFF, mac1 & 0xFFFF, mac0 & 0xFFFF),
            Port       = (int)(rdPort & 0x1FF),
            IsStatic   = (flags & 0x4000) != 0,
            Timestamp  = (int)(flags & 0x3FFF)
        };
    }

    // ── FDB 엔트리 삭제 (Hash Delete, CMD=0x16) — MAC 주소로 해시 삭제 ─────────
    public async Task DeleteEntryByMacAsync(
        string mac, bool vlanValid, int vlanId,
        CancellationToken ct = default)
    {
        var bytes    = ParseMac(mac);
        uint mac0    = (uint)(bytes[2] << 24 | bytes[3] << 16 | bytes[4] << 8 | bytes[5]);
        uint mac1    = (uint)(bytes[0] << 8  | bytes[1]);
        uint vlanReg = (vlanValid ? 0x1000u : 0u) | ((uint)(vlanId & 0xFFF));

        await _reg.WriteAsync(OFF_MCU_MAC0, mac0);            ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_MAC1, mac1);            ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_VLAN, vlanReg);         ct.ThrowIfCancellationRequested();
        await _reg.WriteAsync(OFF_MCU_CMD,  CMD_HASH_DELETE); ct.ThrowIfCancellationRequested();

        await PollStatusAsync(STATUS_WR_MAC, ct);
    }

    // ── 전체 MAC 테이블 초기화 (CMD=0x70) ────────────────────────────────────
    // 완료 판정: 0xA40 FDB_STATUS bit[0] done_mac_table_init 가 1 이 될 때까지 폴링
    public async Task FlushAllAsync(CancellationToken ct = default)
    {
        await _reg.WriteAsync(OFF_MCU_CMD, CMD_FLUSH_ALL);
        ct.ThrowIfCancellationRequested();
        await PollFdbStatusAsync(STATUS_INIT_DONE, ct, timeoutMs: 2000);
    }

    // ── 0xA44 CMD_STATUS 폴링 (Read/Write 명령 완료 대기) ────────────────────
    private async Task PollStatusAsync(uint bitMask, CancellationToken ct, int timeoutMs = 500)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await _reg.ReadAsync(OFF_CMD_STATUS);
            if ((status & bitMask) != 0) return;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException($"CMD_STATUS(0xA44) 폴링 타임아웃 (mask=0x{bitMask:X})");
    }

    // ── 0xA40 FDB_STATUS 폴링 (Initialize 명령 완료 대기) ────────────────────
    private async Task PollFdbStatusAsync(uint bitMask, CancellationToken ct, int timeoutMs = 2000)
    {
        var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var status = await _reg.ReadAsync(OFF_FDB_STATUS);
            if ((status & bitMask) != 0) return;
            await Task.Delay(10, ct);
        }
        throw new TimeoutException($"FDB_STATUS(0xA40) 폴링 타임아웃 (done_mac_table_init 미세트)");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    private static byte[] ParseMac(string mac) =>
        mac.Split(':').Select(s => Convert.ToByte(s, 16)).ToArray();

    private static string FormatMac(uint hi16, uint mid16, uint lo16) =>
        $"{(hi16  >> 8) & 0xFF:X2}:{hi16  & 0xFF:X2}:" +
        $"{(mid16 >> 8) & 0xFF:X2}:{mid16 & 0xFF:X2}:" +
        $"{(lo16  >> 8) & 0xFF:X2}:{lo16  & 0xFF:X2}";
}
