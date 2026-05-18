'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }

// MDIO register map (relative to BaseAddress = 0x44A00000)
// Block base = 0x0080 + port * 0x0040
// Offsets within block:
//   0x0000: MDIO_SETUP
//   0x0004: MDIO_TIME
//   0x0010: MDIO_ACC_DATA
const MDIO_BASE   = 0x0080;
const MDIO_STRIDE = 0x0040;
const OFF_SETUP   = 0x0000;
const OFF_TIME    = 0x0004;
const OFF_ACC     = 0x0010;

// PHY addresses per port (matches MdioService.PhyAddrs)
const PHY_ADDRS = [0x00, 0x04, 0x05, 0x08, 0x0A, 0x0C];

function blockBase(port) {
  return MDIO_BASE + port * MDIO_STRIDE;
}

function toHexOffset(num) {
  return '0x' + num.toString(16).toUpperCase().padStart(8, '0');
}

function parseHex(str) {
  if (str === null || str === undefined) return NaN;
  return parseInt(String(str).replace(/^0x/i, ''), 16);
}

async function regRead(req, offset) {
  const d = await req.app.locals.localCmd('registerread', { offset: toHexOffset(offset) }, 5000);
  // value field is "0xHEXHEX" string
  return parseHex(d.value);
}

async function regWrite(req, offset, value) {
  await req.app.locals.localCmd('registerwrite', {
    offset: toHexOffset(offset),
    value:  '0x' + (value >>> 0).toString(16).toUpperCase().padStart(8, '0')
  }, 5000);
}

// ── POST /api/mdio/read ───────────────────────────────────────────────────────
// body: { port, phyAddr, regAddr }
// ACC_DATA bit31=EN, bit26=WR=0(read), [25:21]=PHY, [20:16]=REG
router.post('/mdio/read', async (req, res) => {
  try {
    const port    = Number(req.body.port ?? 0);
    const phyAddr = parseHex(req.body.phyAddr ?? '0x00');
    const regAddr = parseHex(req.body.regAddr ?? '0x01');

    if (port < 0 || port > 5) throw new Error('port must be 0-5');

    const acc = blockBase(port) + OFF_ACC;
    const cmd = 0x80000000 | ((phyAddr & 0x1F) << 21) | ((regAddr & 0x1F) << 16);
    await regWrite(req, acc, cmd);

    // Poll up to 2000ms, 50ms interval
    const deadline = Date.now() + 2000;
    let raw = cmd; // seed with EN=1 so loop runs at least once
    while (Date.now() < deadline) {
      await new Promise(r => setTimeout(r, 50));
      raw = await regRead(req, acc);
      if ((raw & 0x80000000) === 0) break;
    }
    if ((raw & 0x80000000) !== 0) throw new Error('MDIO read timeout');

    const value = raw & 0xFFFF;
    res.json({ ok: true, value: '0x' + value.toString(16).toUpperCase().padStart(4, '0'), raw: value });
  } catch (e) { wErr(res, e); }
});

// ── POST /api/mdio/write ──────────────────────────────────────────────────────
// body: { port, phyAddr, regAddr, value }
// ACC_DATA bit31=EN, bit26=WR=1(write), [25:21]=PHY, [20:16]=REG, [15:0]=DATA
router.post('/mdio/write', async (req, res) => {
  try {
    const port    = Number(req.body.port ?? 0);
    const phyAddr = parseHex(req.body.phyAddr ?? '0x00');
    const regAddr = parseHex(req.body.regAddr ?? '0x01');
    const data    = parseHex(req.body.value   ?? '0x0000') & 0xFFFF;

    if (port < 0 || port > 5) throw new Error('port must be 0-5');

    const acc = blockBase(port) + OFF_ACC;
    const cmd = 0x80000000 | (1 << 26) | ((phyAddr & 0x1F) << 21) | ((regAddr & 0x1F) << 16) | data;
    await regWrite(req, acc, cmd);

    // Poll for completion
    const deadline = Date.now() + 2000;
    let raw = cmd;
    while (Date.now() < deadline) {
      await new Promise(r => setTimeout(r, 50));
      raw = await regRead(req, acc);
      if ((raw & 0x80000000) === 0) break;
    }
    if ((raw & 0x80000000) !== 0) throw new Error('MDIO write timeout');

    res.json({ ok: true });
  } catch (e) { wErr(res, e); }
});

// ── GET /api/mdio/link-status ─────────────────────────────────────────────────
// Reads BMSR (reg 0x01) bit2 for all 6 ports
router.get('/mdio/link-status', async (req, res) => {
  try {
    const ports = [];
    for (let port = 0; port < 6; port++) {
      const phyAddr = PHY_ADDRS[port];
      const acc     = blockBase(port) + OFF_ACC;
      // Read trigger: BMSR = reg 0x01
      const cmd = 0x80000000 | ((phyAddr & 0x1F) << 21) | ((0x01) << 16);
      await regWrite(req, acc, cmd);

      const deadline = Date.now() + 1000;
      let raw = cmd;
      let timedOut = false;
      while (Date.now() < deadline) {
        await new Promise(r => setTimeout(r, 50));
        raw = await regRead(req, acc);
        if ((raw & 0x80000000) === 0) break;
        if (Date.now() >= deadline) { timedOut = true; break; }
      }

      const linkUp = timedOut ? null : Boolean(raw & 0x0004);
      ports.push({ port, linkUp });
    }
    res.json({ ok: true, ports });
  } catch (e) { wErr(res, e); }
});

// ── POST /api/mdio/setup ──────────────────────────────────────────────────────
// body: { port, enable, preDisable, interruptEnable, targetMhz }
// Matches C# MdioViewModel.ApplyAsync logic
router.post('/mdio/setup', async (req, res) => {
  try {
    const port       = Number(req.body.port ?? 0);
    const enable     = Boolean(req.body.enable);
    const preDisable = Boolean(req.body.preDisable);
    const intrEnable = Boolean(req.body.interruptEnable);
    const targetMhz  = Number(req.body.targetMhz ?? 2.5);

    if (port < 0 || port > 5) throw new Error('port must be 0-5');

    const setupOff = blockBase(port) + OFF_SETUP;
    const timeOff  = blockBase(port) + OFF_TIME;

    // 1. Disable first
    let setupDis = 0x00600000; // TA=b10, SOF=b01 base
    if (preDisable) setupDis |= 0x01000000;
    if (intrEnable) setupDis |= 0x80000000;
    await regWrite(req, setupOff, setupDis);
    await new Promise(r => setTimeout(r, 10));

    // 2. Compute clock dividers (AHB = 100 MHz)
    const ahbMhz = 100.0;
    const clk = Math.max(1, Math.min(255, Math.round(ahbMhz / (2.0 * targetMhz))));
    const ms  = Math.max(1, Math.min(4095, Math.round(targetMhz * 1000.0)));
    const unit = 100;
    const timeReg = ((unit & 0xFFF) << 20) | ((ms & 0xFFF) << 8) | (clk & 0xFF);
    await regWrite(req, timeOff, timeReg);

    // 3. Apply SETUP with requested enable state
    let setupFin = 0x00600000;
    if (enable)     setupFin |= 0x00010000;
    if (preDisable) setupFin |= 0x01000000;
    if (intrEnable) setupFin |= 0x80000000;
    await regWrite(req, setupOff, setupFin);

    res.json({ ok: true, setup: '0x' + setupFin.toString(16).toUpperCase().padStart(8, '0'), time: '0x' + timeReg.toString(16).toUpperCase().padStart(8, '0'), clk, ms });
  } catch (e) { wErr(res, e); }
});

module.exports = router;
