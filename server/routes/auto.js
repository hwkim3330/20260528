'use strict';
const { Router } = require('express');
const router = Router();

function wErr(res, e) { res.status(e.workerError ? 502 : 503).json({ ok: false, error: e.message }); }
function hasWorker(req) { return req.app.locals.workerHub.hasWorker(req.app.locals.localWorkerId); }

// GET /api/auto/status
router.get('/auto/status', async (req, res) => {
  try {
    if (hasWorker(req)) {
      return res.json({ ok: true, ...(await req.app.locals.localCmd('autostatus', {}, 5000) || {}) });
    }
    res.json({ ok: true, ...req.app.locals.autoEngine.getStatus() });
  } catch (e) { wErr(res, e); }
});

// GET /api/auto/results
router.get('/auto/results', async (req, res) => {
  try {
    if (hasWorker(req)) {
      return res.json({ ok: true, ...(await req.app.locals.localCmd('autoresults', {}, 10000) || {}) });
    }
    res.json({ ok: true, rows: req.app.locals.autoEngine.getResults() });
  } catch (e) { wErr(res, e); }
});

// POST /api/auto/run  { test: "name or id" }
router.post('/auto/run', async (req, res) => {
  try {
    if (hasWorker(req)) {
      return res.json({ ok: true, ...(await req.app.locals.localCmd('autorun', req.body || {}, 60000) || {}) });
    }
    const test = req.body?.test;
    if (!test) return res.status(400).json({ ok: false, error: 'test required' });
    // Fire and forget — client polls /auto/status
    req.app.locals.autoEngine.runTest(test).catch(() => {});
    res.json({ ok: true, test, status: 'started' });
  } catch (e) { wErr(res, e); }
});

// POST /api/auto/stop
router.post('/auto/stop', async (req, res) => {
  try {
    if (hasWorker(req)) {
      return res.json({ ok: true, ...(await req.app.locals.localCmd('autostop', {}, 5000) || {}) });
    }
    req.app.locals.autoEngine.stopTest();
    res.json({ ok: true });
  } catch (e) { wErr(res, e); }
});

module.exports = router;
