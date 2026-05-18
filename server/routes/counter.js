'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }

// Counter read via serial port
// Sends "read_cnt" or "read_cnt <N>" command, waits for response, parses lines.
// Response format from firmware: REGISTER_NAME [A: 0xADDR, D: 0xVALUE]
// (matches C# CountViewerViewModel.LineRegex)

const LINE_RE = /^(\w+)\s+\[A:\s*(0x[\dA-Fa-f]+)\s*,\s*D:\s*(0x[\dA-Fa-f]+)\]/;

// ── GET /api/counter/read?port=all|0-5 ───────────────────────────────────────
router.get('/counter/read', async (req, res) => {
  const portParam = (req.query.port || 'all').toString().trim().toLowerCase();
  const cmd = portParam === 'all' ? 'read_cnt' : `read_cnt ${portParam}`;

  try {
    const localCmd = req.app.locals.localCmd;

    // Clear serial RX buffer first
    await localCmd('serialclear', {}, 3000).catch(() => {});

    // Send the command via serialwrite
    await localCmd('serialwrite', { text: cmd + '\r' }, 5000);

    // Poll for response — collect lines for up to 8 seconds
    const deadline = Date.now() + 8000;
    let accumulated = '';
    const counters = [];
    let lastCount = -1;

    while (Date.now() < deadline) {
      await new Promise(r => setTimeout(r, 200));
      const rx = await localCmd('serialread', {}, 3000).catch(() => null);
      if (rx && rx.hex && rx.hex.length > 0) {
        const bytes = Buffer.from(rx.hex, 'hex');
        accumulated += bytes.toString('utf8');
      }

      // Parse what we have
      const lines = accumulated.split(/\r?\n/);
      // Keep last partial line
      accumulated = lines.pop() || '';

      let parsed = 0;
      for (const line of lines) {
        const m = LINE_RE.exec(line.trim());
        if (!m) continue;
        const name   = m[1];
        const addr   = m[2];
        const valHex = m[3];
        const valDec = parseInt(valHex.replace(/^0x/i, ''), 16) || 0;

        // Group: prefix before first '_', or "FBR" for FBR_ prefix
        const underIdx = name.indexOf('_');
        let group = underIdx > 0 ? name.slice(0, underIdx) : name;
        if (group.toUpperCase().startsWith('FBR')) group = 'FBR';

        counters.push({ group, name, address: addr, value: valHex, valueDec: valDec });
        parsed++;
      }

      // Stop if we got data and nothing new is arriving
      if (counters.length > 0 && counters.length === lastCount) break;
      lastCount = counters.length;
    }

    // If port was specified, return port number
    const portNum = portParam === 'all' ? null : parseInt(portParam, 10);
    const result = { ok: true, counters };
    if (portNum !== null) result.port = portNum;
    res.json(result);
  } catch (e) { wErr(res, e); }
});

module.exports = router;
