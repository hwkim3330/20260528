'use strict';
/**
 * switchProtocol.js — Register/FDB text protocol over serial.
 * Protocol: "read 0x{ADDR}" → "OK 0x{VALUE}" / "write 0x{ADDR} 0x{VALUE}" → "OK"
 * Used as fallback when C# worker is not available (Linux / headless).
 */

const serialBridge = require('./serialBridge');

// Default base address for register operations
let BASE_ADDRESS = 0x44A00000;

function hex8(n)  { return '0x' + (n >>> 0).toString(16).toUpperCase().padStart(8, '0'); }
function parseHex(s) { return parseInt(String(s ?? '0').replace(/^0x/i, ''), 16) || 0; }

/**
 * 절대 주소(>= BASE_ADDRESS)와 상대 오프셋을 모두 허용.
 * TC.csv 등에서 0x44A00080 같은 절대 주소가 오면 그대로 사용하고,
 * 레지스터 뷰어에서 0x030 같은 작은 오프셋이 오면 BASE_ADDRESS를 더한다.
 */
function resolveAddr(val) {
  const v = parseHex(val);
  return v >= BASE_ADDRESS ? (v >>> 0) : ((BASE_ADDRESS + v) >>> 0);
}

// ── Register primitives ────────────────────────────────────────────────────────

async function readRegister(session, offset) {
  const addr = (BASE_ADDRESS + offset) >>> 0;
  const resp = await serialBridge.command(session, `read ${hex8(addr)}`, 3000);
  return parseHex(resp);
}

async function writeRegister(session, offset, value) {
  const addr = (BASE_ADDRESS + offset) >>> 0;
  await serialBridge.command(session, `write ${hex8(addr)} ${hex8(value >>> 0)}`, 3000);
}

async function readAbsolute(session, addr) {
  const cmd = `read ${hex8(addr >>> 0)}`;
  console.log(`[serial →] ${cmd}  (session=${session})`);
  const resp = await serialBridge.command(session, cmd, 3000);
  console.log(`[serial ←] ${resp}`);
  return parseHex(resp);
}

async function writeAbsolute(session, addr, value) {
  const cmd = `write ${hex8(addr >>> 0)} ${hex8(value >>> 0)}`;
  console.log(`[serial →] ${cmd}  (session=${session})`);
  await serialBridge.command(session, cmd, 3000);
  console.log(`[serial ←] OK`);
}

// ── Public: register API (offset relative to BASE_ADDRESS) ────────────────────

async function registerStatus() {
  const s = serialBridge.getStatus();
  return { baseAddress: hex8(BASE_ADDRESS), connected: s.open, session: s.session };
}

async function registerRead(payload) {
  const sid  = serialBridge.getSession(payload.session);
  const addr = resolveAddr(payload.offset ?? payload.address ?? '0');
  const value = await readAbsolute(sid, addr);
  return { value: hex8(value), raw: value, offset: hex8(addr) };
}

async function registerWrite(payload) {
  const sid   = serialBridge.getSession(payload.session);
  const addr  = resolveAddr(payload.offset ?? payload.address ?? '0');
  const value = parseHex(payload.value ?? '0');
  await writeAbsolute(sid, addr, value);
  return { ok: true, offset: hex8(addr), value: hex8(value) };
}

// ── FDB register offsets ───────────────────────────────────────────────────────
const FDB = {
  OFF_MCU_MAC0:   0xA18,
  OFF_MCU_MAC1:   0xA1C,
  OFF_MCU_VLAN:   0xA20,
  OFF_MCU_PORT:   0xA24,
  OFF_MCU_BUCKET: 0xA28,
  OFF_MCU_CMD:    0xA2C,
  OFF_FDB_STATUS: 0xA40,
  OFF_CMD_STATUS: 0xA44,
  OFF_RD_BUCKET:  0xA48,
  OFF_RD_PORT:    0xA4C,
  OFF_RD_FLAGS:   0xA50,
  OFF_RD_MAC0:    0xA54,
  OFF_RD_MAC1:    0xA58,
  OFF_RD_MAC2:    0xA5C,
};

const CMD = {
  HASH_READ:    0x12,
  READ_BUCKET:  0x13,
  HASH_WRITE:   0x14,
  WRITE_BUCKET: 0x15,
  HASH_DELETE:  0x16,
  FLUSH_ALL:    0x70,
};

function parseMac(mac) {
  const b = mac.replace(/[:\-]/g, '');
  return Buffer.from(b.padStart(12, '0'), 'hex');
}

function macToWords(mac) {
  const b = parseMac(mac);
  // Hardware layout: MAC0 = b[2..5] big-endian, MAC1 = b[0..1] big-endian
  const lo = ((b[2] << 24) | (b[3] << 16) | (b[4] << 8) | b[5]) >>> 0;
  const hi = ((b[0] << 8) | b[1]) >>> 0;
  return { lo, hi };
}

function wordsToMac(lo, hi) {
  // lo = RD_MAC0|(RD_MAC1<<16): b[5]|b[4]<<8|b[3]<<16|b[2]<<24
  // hi = RD_MAC2&0xFFFF:        b[0]<<8|b[1]
  const b = [
    (hi >> 8) & 0xFF,
    hi & 0xFF,
    (lo >> 24) & 0xFF,
    (lo >> 16) & 0xFF,
    (lo >> 8) & 0xFF,
    lo & 0xFF,
  ];
  return b.map(x => x.toString(16).padStart(2, '0')).join(':');
}

async function pollStatus(sid, mask, timeoutMs = 500) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 10));
    const v = await readRegister(sid, FDB.OFF_CMD_STATUS);
    if (v & mask) return v;
  }
  throw new Error('FDB status timeout');
}

async function setMacAddress(sid, mac) {
  const { lo, hi } = macToWords(mac);
  await writeRegister(sid, FDB.OFF_MCU_MAC0, lo);
  await writeRegister(sid, FDB.OFF_MCU_MAC1, hi);
}

// ── Public: FDB API ───────────────────────────────────────────────────────────

async function fdbRead(payload) {
  const sid     = serialBridge.getSession(payload.session);
  const mac     = payload.mac || '00:00:00:00:00:00';
  const vlanId  = payload.vlanId ?? 0;
  const valid   = payload.vlanValid ?? (vlanId > 0);

  await setMacAddress(sid, mac);
  await writeRegister(sid, FDB.OFF_MCU_VLAN,
    (valid ? 0x1000 : 0) | (vlanId & 0xFFF));
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.HASH_READ);

  const st = await pollStatus(sid, 0x1, 500); // STATUS_RD_MAC
  const bucket = await readRegister(sid, FDB.OFF_RD_BUCKET);
  const port   = await readRegister(sid, FDB.OFF_RD_PORT);
  const flags  = await readRegister(sid, FDB.OFF_RD_FLAGS);
  const mac0   = await readRegister(sid, FDB.OFF_RD_MAC0);
  const mac1   = await readRegister(sid, FDB.OFF_RD_MAC1);
  const mac2   = await readRegister(sid, FDB.OFF_RD_MAC2);
  const rdMac  = wordsToMac((mac0 | (mac1 << 16)) >>> 0, mac2 & 0xFFFF);

  return {
    found:  !!(flags & 0x8000),
    mac:    rdMac,
    port:   port & 0x1FF,
    vlanId: vlanId,
    static: !!(flags & 0x4000),
    bucket: bucket & 0x3FF,
  };
}

async function fdbWrite(payload) {
  const sid    = serialBridge.getSession(payload.session);
  const mac    = payload.mac || '00:00:00:00:00:00';
  const port   = payload.port ?? 0;
  const vlanId = payload.vlanId ?? 0;
  const valid  = payload.vlanValid ?? (vlanId > 0);

  await setMacAddress(sid, mac);
  await writeRegister(sid, FDB.OFF_MCU_VLAN,
    (valid ? 0x1000 : 0) | (vlanId & 0xFFF));
  await writeRegister(sid, FDB.OFF_MCU_PORT, port & 0x1FF);
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.HASH_WRITE);

  await pollStatus(sid, 0x4, 500); // STATUS_WR_MAC
  return { ok: true };
}

async function fdbDelete(payload) {
  const sid    = serialBridge.getSession(payload.session);
  const mac    = payload.mac || '00:00:00:00:00:00';
  const vlanId = payload.vlanId ?? 0;
  const valid  = vlanId > 0;

  await setMacAddress(sid, mac);
  await writeRegister(sid, FDB.OFF_MCU_VLAN,
    (valid ? 0x1000 : 0) | (vlanId & 0xFFF));
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.HASH_DELETE);
  await pollStatus(sid, 0x4, 500);
  return { ok: true };
}

async function fdbFlush(payload) {
  const sid = serialBridge.getSession(payload?.session);
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.FLUSH_ALL);

  // Poll OFF_FDB_STATUS bit 0 (done_mac_table_init)
  const deadline = Date.now() + 2000;
  while (Date.now() < deadline) {
    await new Promise(r => setTimeout(r, 10));
    const v = await readRegister(sid, FDB.OFF_FDB_STATUS);
    if (v & 0x1) return { ok: true };
  }
  throw new Error('FDB flush timeout');
}

// ── FdbReadBucket: bucket 인덱스로 특정 슬롯 항목 읽기 ──────────────────────────
async function fdbReadBucket(payload) {
  const sid    = serialBridge.getSession(payload?.session);
  const bucket = payload.bucket ?? 0;
  const slot   = payload.slot   ?? 0;   // 슬롯 비트마스크 (0x1, 0x2, 0x4, 0x8)

  await writeRegister(sid, FDB.OFF_MCU_BUCKET, bucket & 0x3FF);
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.READ_BUCKET);

  const st = await pollStatus(sid, 0x1, 500); // STATUS_RD_MAC
  const port  = await readRegister(sid, FDB.OFF_RD_PORT);
  const flags = await readRegister(sid, FDB.OFF_RD_FLAGS);
  const mac0  = await readRegister(sid, FDB.OFF_RD_MAC0);
  const mac1  = await readRegister(sid, FDB.OFF_RD_MAC1);
  const mac2  = await readRegister(sid, FDB.OFF_RD_MAC2);
  const rdMac = wordsToMac((mac0 | (mac1 << 16)) >>> 0, mac2 & 0xFFFF);

  return {
    found:  !!(flags & 0x8000),
    mac:    rdMac,
    port:   port & 0x1FF,
    bucket: bucket,
    slot:   slot,
    static: !!(flags & 0x4000),
  };
}

async function fdbWriteBucket(payload) {
  const sid    = serialBridge.getSession(payload.session);
  const mac    = payload.mac || '00:00:00:00:00:00';
  const port   = payload.port ?? 0;
  const vlanId = payload.vlanId ?? 0;
  const valid  = payload.vlanValid ?? (vlanId > 0);
  const bucket = payload.bucket ?? 0;
  const slot   = payload.slot   ?? 0x1;  // 슬롯 비트마스크 (0x1~0xF)

  await setMacAddress(sid, mac);
  await writeRegister(sid, FDB.OFF_MCU_VLAN,
    (valid ? 0x1000 : 0) | (vlanId & 0xFFF));
  await writeRegister(sid, FDB.OFF_MCU_PORT, port & 0x1FF);
  await writeRegister(sid, FDB.OFF_MCU_BUCKET, ((slot & 0xF) << 16) | (bucket & 0x3FF));
  await writeRegister(sid, FDB.OFF_MCU_CMD, CMD.WRITE_BUCKET);

  await pollStatus(sid, 0x4, 500); // STATUS_WR_MAC
  return { ok: true, bucket: bucket & 0x3FF, slot: slot & 0xF };
}

module.exports = {
  registerStatus, registerRead, registerWrite,
  fdbRead, fdbWrite, fdbWriteBucket, fdbDelete, fdbFlush, fdbReadBucket,
  readRegister, writeRegister,
  setBaseAddress(addr) { BASE_ADDRESS = parseHex(addr); },
};
