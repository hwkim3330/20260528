'use strict';
const { Router } = require('express');
const router = Router();

function workerErr(res, err) {
  res.status(err.workerError ? 502 : 503).json({ ok: false, error: err.message });
}

// GET /api/tty/list  →  { ok, ttys: [{path, name, usbProduct, ...}] }
router.get('/tty/list', async (req, res) => {
  try {
    const data = await req.app.locals.localCmd('serialList', {}, 5000);
    res.json({ ok: true, ttys: data?.ttys ?? [] });
  } catch (err) { workerErr(res, err); }
});

// POST /api/tty/open  →  { ok, sessionId }
router.post('/tty/open', async (req, res) => {
  try {
    const { path, port, baudRate = 115200, dataBits = 8, stopBits = 1, parity = 'none', hwFlow = false } = req.body || {};
    const portName = path || port || '';
    const data = await req.app.locals.localCmd('serialOpen', { path: portName, port: portName, baudRate, dataBits, stopBits, parity, rts: hwFlow }, 8000);
    const sessionId = data?.sessionId ?? data?.session ?? portName;
    res.json({ ok: true, sessionId, session: sessionId, ...(data || {}) });
  } catch (err) { workerErr(res, err); }
});

// GET /api/tty/stream?session=ID  — NDJSON stream of received bytes
// Lines: { type:'rx', hex:'...' } | { type:'error', message } | { type:'closed' }
router.get('/tty/stream', (req, res) => {
  const { workerHub, localWorkerId } = req.app.locals;
  const session = req.query.session || '';

  res.setHeader('Content-Type', 'application/x-ndjson');
  res.setHeader('Cache-Control', 'no-cache');
  res.setHeader('Connection', 'keep-alive');
  res.setHeader('X-Accel-Buffering', 'no');
  res.flushHeaders();

  const write = (obj) => { try { res.write(JSON.stringify(obj) + '\n'); } catch {} };

  const onEvent = (payload) => {
    if (payload?.kind !== 'serial') return;
    if (session && payload.session && payload.session !== session) return;
    if (payload.rxType === 'rx' && payload.hex) {
      write({ type: 'rx', hex: payload.hex, session: payload.session });
    } else if (payload.type === 'error') {
      write({ type: 'error', message: payload.message });
    } else if (payload.type === 'closed') {
      write({ type: 'closed' });
    }
  };

  workerHub.events.on(`event:${localWorkerId}`, onEvent);
  write({ connected: true, session });

  const keepalive = setInterval(() => { try { res.write('\n'); } catch {} }, 15000);

  req.on('close', () => {
    clearInterval(keepalive);
    workerHub.events.off(`event:${localWorkerId}`, onEvent);
  });
});

// POST /api/tty/write  — body: { sessionId, hex }
router.post('/tty/write', async (req, res) => {
  try {
    const { sessionId, session, hex, data: hexData, text } = req.body || {};
    const s = sessionId || session;
    const d = await req.app.locals.localCmd('serialWrite', { session: s, hex: hex ?? hexData, text }, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

// POST /api/tty/control  — body: { sessionId, rts, dtr, break }
router.post('/tty/control', async (req, res) => {
  try {
    const { sessionId, session, ...rest } = req.body || {};
    const d = await req.app.locals.localCmd('serialControl', { session: sessionId || session, ...rest }, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

// POST /api/tty/close
router.post('/tty/close', async (req, res) => {
  try {
    const { sessionId, session } = req.body || {};
    const d = await req.app.locals.localCmd('serialClose', { session: sessionId || session }, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

// ── legacy /api/serial/* aliases ──────────────────────────────────────────────

router.get('/serial/status', async (req, res) => {
  try {
    const ports = await req.app.locals.localCmd('serialList', {}, 5000);
    const info  = await req.app.locals.localCmd('serialStatus', {}, 5000).catch(() => ({}));
    res.json({ ok: true, ttys: ports?.ttys ?? [], ports: ports?.ttys ?? [], ...(info || {}) });
  } catch (err) { workerErr(res, err); }
});

router.post('/serial/connect', async (req, res) => {
  try {
    const d = await req.app.locals.localCmd('serialOpen', req.body || {}, 8000);
    const sessionId = d?.sessionId ?? d?.session;
    res.json({ ok: true, sessionId, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

router.post('/serial/disconnect', async (req, res) => {
  try {
    const d = await req.app.locals.localCmd('serialClose', {}, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

router.post('/serial/send', async (req, res) => {
  try {
    const d = await req.app.locals.localCmd('serialWrite', req.body || {}, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

router.post('/serial/clear', async (req, res) => {
  try {
    const d = await req.app.locals.localCmd('serialClear', {}, 5000);
    res.json({ ok: true, ...(d || {}) });
  } catch (err) { workerErr(res, err); }
});

module.exports = router;
