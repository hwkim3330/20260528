'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }
function hasWorker(req) { return req.app.locals.workerHub.hasWorker(req.app.locals.localWorkerId); }

const LINE_RE = /^(\w+)\s+\[A:\s*(0x[\dA-Fa-f]+)\s*,\s*D:\s*(0x[\dA-Fa-f]+)\]/;

async function readCountersFromSerial({ localCmd, workerHub, localWorkerId, serialBridge }, portParam) {
  const cmd = portParam === 'all' ? 'read_cnt' : `read_cnt ${portParam}`;

  // Send the command — works for both C# worker and native serialBridge
  if (workerHub.hasWorker(localWorkerId)) {
    await localCmd('serialWrite', { text: cmd + '\r' }, 5000);
  } else {
    const sid = serialBridge.getSession(null);
    if (!sid) throw Object.assign(new Error('Serial port not open'), { workerError: true });
    await serialBridge.write(sid, { text: cmd + '\r' });
  }

  // Collect serial-rx events for up to 8 s (same regardless of source)
  const eventSource  = workerHub.hasWorker(localWorkerId) ? workerHub.events : serialBridge.events;
  const eventName    = workerHub.hasWorker(localWorkerId) ? `event:${localWorkerId}` : 'serial';
  const deadline     = Date.now() + 8000;
  let accumulated    = '';
  const counters     = [];
  let lastCount      = -1;

  await new Promise((resolve) => {
    const onEvent = (payload) => {
      if (payload?.kind !== 'serial') return;
      if (payload.rxType === 'rx' && payload.hex) {
        accumulated += Buffer.from(payload.hex, 'hex').toString('utf8');
      }
    };
    eventSource.on(eventName, onEvent);

    const tick = setInterval(() => {
      const lines = accumulated.split(/\r?\n/);
      accumulated = lines.pop() || '';
      for (const line of lines) {
        const m = LINE_RE.exec(line.trim());
        if (!m) continue;
        const name   = m[1];
        const addr   = m[2];
        const valHex = m[3];
        const valDec = parseInt(valHex.replace(/^0x/i, ''), 16) || 0;
        const underIdx = name.indexOf('_');
        let group = underIdx > 0 ? name.slice(0, underIdx) : name;
        if (group.toUpperCase().startsWith('FBR')) group = 'FBR';
        counters.push({ group, name, address: addr, value: valHex, valueDec: valDec });
      }
      const done = Date.now() >= deadline ||
                   (counters.length > 0 && counters.length === lastCount);
      lastCount = counters.length;
      if (done) {
        clearInterval(tick);
        eventSource.off(eventName, onEvent);
        resolve();
      }
    }, 200);

    setTimeout(() => {
      clearInterval(tick);
      eventSource.off(eventName, onEvent);
      resolve();
    }, 8500);
  });

  return counters;
}

// GET /api/counter/read?port=all|0-5
router.get('/counter/read', async (req, res) => {
  const portParam = (req.query.port || 'all').toString().trim().toLowerCase();
  try {
    const counters = await readCountersFromSerial(req.app.locals, portParam);
    const result = { ok: true, counters };
    if (portParam !== 'all') result.port = parseInt(portParam, 10);
    res.json(result);
  } catch (e) { wErr(res, e); }
});

module.exports = router;
